using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using BilinguaFlow.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int TranscriptLimit = 500;
    private readonly IAudioDeviceService _devices;
    private readonly IAudioCaptureSessionFactory _audioSessionFactory;
    private readonly ITranscriptionSessionFactory _transcriptionSessionFactory;
    private readonly ISpeechRecognitionService _recognizer;
    private readonly IRecordingPathFactory _paths;
    private readonly ISystemClock _clock;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<IAudioCaptureSession> _activeAudioSessions = [];
    private readonly ConcurrentDictionary<CaptureSource, ITranscriptionSession> _activeTranscriptionSessions = [];
    private readonly Dictionary<CaptureSource, TranscriptionDiagnostics> _diagnosticsBySource = [];
    private readonly SenseVoiceModelFiles _modelFiles;
    private CancellationTokenSource? _captureCancellation;
    private SelectionOption<CaptureMode>? _selectedMode;
    private SelectionOption<SourceLanguage>? _selectedLanguage;
    private AudioDevice? _selectedOutputDevice;
    private AudioDevice? _selectedInputDevice;
    private bool _isCapturing;
    private string _status = "Ready";
    private string _diagnostics = "ASR idle";
    private float _systemAudioLevel;
    private float _microphoneAudioLevel;

    public MainViewModel(IAudioDeviceService devices, IAudioCaptureSessionFactory audioSessionFactory,
        ITranscriptionSessionFactory transcriptionSessionFactory, ISpeechRecognitionService recognizer,
        IRecordingPathFactory paths, ISystemClock clock, ILogger<MainViewModel> logger)
    {
        _devices = devices; _audioSessionFactory = audioSessionFactory; _transcriptionSessionFactory = transcriptionSessionFactory;
        _recognizer = recognizer; _paths = paths; _clock = clock; _logger = logger;
        _modelFiles = SenseVoiceModelLocator.Locate(AppContext.BaseDirectory);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCapturing);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsCapturing);
        ClearTranscriptCommand = new AsyncRelayCommand(ClearTranscriptAsync, () => TranscriptItems.Count > 0);
        SelectedMode = Modes[0]; SelectedLanguage = Languages[0];
    }

    public IReadOnlyList<SelectionOption<CaptureMode>> Modes { get; } = [new(CaptureMode.Movie, "Movie Mode"), new(CaptureMode.Meeting, "Meeting Mode"), new(CaptureMode.Microphone, "Microphone Mode")];
    public IReadOnlyList<SelectionOption<SourceLanguage>> Languages { get; } = [new(SourceLanguage.English, "English"), new(SourceLanguage.Japanese, "Japanese")];
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    public ObservableCollection<AudioDevice> InputDevices { get; } = [];
    public ObservableCollection<TranscriptItemViewModel> TranscriptItems { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand ClearTranscriptCommand { get; }

    public SelectionOption<CaptureMode>? SelectedMode { get => _selectedMode; set { if (SetProperty(ref _selectedMode, value)) { OnPropertyChanged(nameof(NeedsSystemAudio)); OnPropertyChanged(nameof(NeedsMicrophone)); RaiseCommands(); } } }
    public SelectionOption<SourceLanguage>? SelectedLanguage { get => _selectedLanguage; set => SetProperty(ref _selectedLanguage, value); }
    public AudioDevice? SelectedOutputDevice { get => _selectedOutputDevice; set { if (SetProperty(ref _selectedOutputDevice, value)) RaiseCommands(); } }
    public AudioDevice? SelectedInputDevice { get => _selectedInputDevice; set { if (SetProperty(ref _selectedInputDevice, value)) RaiseCommands(); } }
    public bool IsCapturing { get => _isCapturing; private set { if (SetProperty(ref _isCapturing, value)) RaiseCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string ModelDirectory => _modelFiles.Directory;
    public bool IsModelAvailable => _modelFiles.Exists;
    public float SystemAudioLevel { get => _systemAudioLevel; private set => SetProperty(ref _systemAudioLevel, value); }
    public float MicrophoneAudioLevel { get => _microphoneAudioLevel; private set => SetProperty(ref _microphoneAudioLevel, value); }
    public bool NeedsSystemAudio => SelectedMode?.Value is CaptureMode.Movie or CaptureMode.Meeting;
    public bool NeedsMicrophone => SelectedMode?.Value is CaptureMode.Microphone or CaptureMode.Meeting;

    public async Task InitializeAsync()
    {
        await RefreshDevicesAsync();
        if (!IsModelAvailable) Status = $"SenseVoice model not found. Expected: {ModelDirectory}";
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            Status = "Detecting audio devices...";
            var outputs = await Task.Run(_devices.GetOutputDevices); var inputs = await Task.Run(_devices.GetInputDevices);
            Replace(OutputDevices, outputs); Replace(InputDevices, inputs);
            SelectedOutputDevice = OutputDevices.FirstOrDefault(); SelectedInputDevice = InputDevices.FirstOrDefault();
            Status = outputs.Count + inputs.Count == 0 ? "No active audio devices found" : "Ready";
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to enumerate audio devices"); Status = $"Device error: {ex.Message}"; }
    }

    private bool CanStart() => !IsCapturing && SelectedMode is not null && SelectedLanguage is not null &&
        (!NeedsSystemAudio || SelectedOutputDevice is not null) && (!NeedsMicrophone || SelectedInputDevice is not null);

    private async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (IsCapturing) return;
            if (!IsModelAvailable) { Status = $"SenseVoice model not found. Expected: {ModelDirectory}"; return; }
            Status = "Loading SenseVoice model...";
            _captureCancellation = new CancellationTokenSource();
            var token = _captureCancellation.Token;
            var initializationTime = await _recognizer.InitializeAsync(_modelFiles, SelectedLanguage!.Value, token);
            Diagnostics = initializationTime == TimeSpan.Zero ? "SenseVoice ready (cached)" : $"ASR initialized in {initializationTime.TotalSeconds:F1}s";
            Status = "SenseVoice ready.";

            if (NeedsSystemAudio) await StartTranscriptionAsync(CaptureSource.System, token);
            if (NeedsMicrophone) await StartTranscriptionAsync(CaptureSource.Microphone, token);
            var timestamp = _clock.Now;
            if (NeedsSystemAudio) await StartAudioSessionAsync(CaptureSource.System, SelectedOutputDevice!, timestamp, token);
            if (NeedsMicrophone) await StartAudioSessionAsync(CaptureSource.Microphone, SelectedInputDevice!, timestamp, token);

            IsCapturing = true;
            Status = SelectedMode?.Value == CaptureMode.Meeting ? "Listening to system audio and microphone..." : "Listening...";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start transcription");
            Status = FriendlyError(ex);
            await StopTranscriptionsCoreAsync(false); await StopAudioSessionsCoreAsync();
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartTranscriptionAsync(CaptureSource source, CancellationToken token)
    {
        var session = _transcriptionSessionFactory.Create(source);
        session.ResultAvailable += OnRecognitionResult;
        session.StatusChanged += OnAsrStatusChanged;
        session.DiagnosticsChanged += OnDiagnosticsChanged;
        if (!_activeTranscriptionSessions.TryAdd(source, session))
            throw new InvalidOperationException($"A {source} transcription session is already active.");
        await session.StartAsync(SelectedLanguage!.Value, token);
    }

    private async Task StartAudioSessionAsync(CaptureSource source, AudioDevice device, DateTimeOffset timestamp, CancellationToken token)
    {
        var session = _audioSessionFactory.Create(source);
        session.LevelChanged += OnLevelChanged; session.AudioAvailable += OnAudioAvailable; session.CaptureStopped += OnCaptureStopped;
        _activeAudioSessions.Add(session);
        await session.StartAsync(device, _paths.Create(source, timestamp), token);
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCapturing && _activeAudioSessions.Count == 0 && _activeTranscriptionSessions.Count == 0) return;
            Status = "Finishing pending speech...";
            foreach (var session in _activeAudioSessions) session.AudioAvailable -= OnAudioAvailable;
            await StopTranscriptionsCoreAsync(true);
            _captureCancellation?.Cancel();
            await StopAudioSessionsCoreAsync();
            Status = "Stopped — recordings and transcript saved in memory";
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to stop cleanly"); Status = $"Stop error: {ex.Message}"; }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopTranscriptionsCoreAsync(bool flush)
    {
        var sessions = _activeTranscriptionSessions.Values.ToArray(); _activeTranscriptionSessions.Clear();
        foreach (var session in sessions)
        {
            try { await session.StopAsync(flush); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping {Source} ASR", session.Source); }
            session.ResultAvailable -= OnRecognitionResult; session.StatusChanged -= OnAsrStatusChanged; session.DiagnosticsChanged -= OnDiagnosticsChanged;
            try { await session.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing {Source} ASR", session.Source); }
        }
        _diagnosticsBySource.Clear();
    }

    private async Task StopAudioSessionsCoreAsync()
    {
        var sessions = _activeAudioSessions.ToArray(); _activeAudioSessions.Clear();
        foreach (var session in sessions)
        {
            session.LevelChanged -= OnLevelChanged; session.AudioAvailable -= OnAudioAvailable; session.CaptureStopped -= OnCaptureStopped;
            try { await session.StopAsync(); await session.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing {Source} session", session.Source); }
        }
        _captureCancellation?.Dispose(); _captureCancellation = null; IsCapturing = false; SystemAudioLevel = 0; MicrophoneAudioLevel = 0;
    }

    private void OnAudioAvailable(object? sender, AudioChunk chunk)
    {
        if (_activeTranscriptionSessions.TryGetValue(chunk.Source, out var pipeline)) pipeline.TryEnqueue(chunk);
    }

    private void OnRecognitionResult(object? sender, RecognitionResult result) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        TranscriptItems.Add(TranscriptItemViewModel.FromResult(result));
        while (TranscriptItems.Count > TranscriptLimit) TranscriptItems.RemoveAt(0);
        ClearTranscriptCommand.RaiseCanExecuteChanged();
    });

    private void OnAsrStatusChanged(object? sender, string message) => Application.Current.Dispatcher.BeginInvoke(() => Status = message);

    private void OnDiagnosticsChanged(object? sender, TranscriptionDiagnostics diagnostics)
    {
        if (sender is not ITranscriptionSession session) return;
        lock (_diagnosticsBySource)
        {
            _diagnosticsBySource[session.Source] = diagnostics;
            var queued = _diagnosticsBySource.Values.Sum(value => value.QueueLength);
            var dropped = _diagnosticsBySource.Values.Sum(value => value.DroppedAudioChunks);
            Application.Current.Dispatcher.BeginInvoke(() => Diagnostics = $"ASR queue: {queued} · Dropped chunks: {dropped}");
        }
    }

    private void OnLevelChanged(object? sender, AudioLevelChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (e.Source == CaptureSource.System) SystemAudioLevel = e.Level * 100; else MicrophoneAudioLevel = e.Level * 100; });
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        if (e.Error is null || !IsCapturing) return;
        Application.Current.Dispatcher.BeginInvoke(() => { Status = $"Audio device disconnected: {e.Error.Message}"; _ = StopAsync(); });
    }

    private Task ClearTranscriptAsync() { TranscriptItems.Clear(); ClearTranscriptCommand.RaiseCanExecuteChanged(); return Task.CompletedTask; }
    private static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => ex.Message,
        InvalidOperationException when ex.Message.Contains("native", StringComparison.OrdinalIgnoreCase) => ex.Message,
        _ => $"ASR initialization failed: {ex.Message}"
    };
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
    private void RaiseCommands() { StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); RefreshDevicesCommand.RaiseCanExecuteChanged(); ClearTranscriptCommand.RaiseCanExecuteChanged(); }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(); await _recognizer.DisposeAsync(); _lifecycleGate.Dispose();
    }
}
