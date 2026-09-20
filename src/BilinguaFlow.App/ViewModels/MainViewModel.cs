using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using BilinguaFlow.Infrastructure;
using BilinguaFlow.Llm;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int TranscriptLimit = 500;
    private readonly IAudioDeviceService _devices;
    private readonly IAudioCaptureSessionFactory _audioSessionFactory;
    private readonly ITranscriptionSessionFactory _transcriptionSessionFactory;
    private readonly ISpeechRecognitionService _recognizer;
    private readonly ILlmService _llmService;
    private readonly LlmTranslationWorker _llmWorker;
    private readonly QwenSettings _qwenSettings;
    private readonly IRecordingPathFactory _paths;
    private readonly ISystemClock _clock;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<IAudioCaptureSession> _activeAudioSessions = [];
    private readonly ConcurrentDictionary<CaptureSource, ITranscriptionSession> _activeTranscriptionSessions = [];
    private readonly Dictionary<CaptureSource, TranscriptionDiagnostics> _diagnosticsBySource = [];
    private readonly SenseVoiceModelFiles _modelFiles;
    private readonly List<string> _recentContext = [];
    private long _sequence;
    private CancellationTokenSource? _captureCancellation;
    private SelectionOption<CaptureMode>? _selectedMode;
    private SelectionOption<SourceLanguage>? _selectedLanguage;
    private AudioDevice? _selectedOutputDevice;
    private AudioDevice? _selectedInputDevice;
    private bool _isCapturing;
    private bool _movieAsrDebugMode;
    private bool _showRawAsr = true;
    private ContextProfile? _selectedProfile;
    private string _additionalContext = "";
    private LlmDiagnostics _llmDiagnostics = new(0, 0, 0, 0);
    private string _status = "Ready";
    private string _diagnostics = "ASR idle";
    private float _systemAudioLevel;
    private float _microphoneAudioLevel;

    public MainViewModel(IAudioDeviceService devices, IAudioCaptureSessionFactory audioSessionFactory,
        ITranscriptionSessionFactory transcriptionSessionFactory, ISpeechRecognitionService recognizer,
        ILlmService llmService, LlmTranslationWorker llmWorker, QwenSettings qwenSettings,
        IRecordingPathFactory paths, ISystemClock clock, ILogger<MainViewModel> logger)
    {
        _devices = devices; _audioSessionFactory = audioSessionFactory; _transcriptionSessionFactory = transcriptionSessionFactory;
        _recognizer = recognizer; _llmService = llmService; _llmWorker = llmWorker; _qwenSettings = qwenSettings;
        _paths = paths; _clock = clock; _logger = logger;
        _modelFiles = SenseVoiceModelLocator.Locate(AppContext.BaseDirectory);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCapturing);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsCapturing);
        ClearTranscriptCommand = new AsyncRelayCommand(ClearTranscriptAsync, () => TranscriptItems.Count > 0);
        SelectedMode = Modes[0]; SelectedLanguage = Languages[0];
        _llmWorker.ResultAvailable += OnLlmResult;
        _llmWorker.DiagnosticsChanged += OnLlmDiagnostics;
    }

    public IReadOnlyList<SelectionOption<CaptureMode>> Modes { get; } = [new(CaptureMode.Movie, "Movie Mode"), new(CaptureMode.Meeting, "Meeting Mode"), new(CaptureMode.Microphone, "Microphone Mode")];
    public IReadOnlyList<SelectionOption<SourceLanguage>> Languages { get; } = [new(SourceLanguage.English, "English"), new(SourceLanguage.Japanese, "Japanese")];
    public IReadOnlyList<ContextProfile> Profiles { get; } = ContextProfiles.All;
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    public ObservableCollection<AudioDevice> InputDevices { get; } = [];
    public ObservableCollection<TranscriptItemViewModel> TranscriptItems { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand ClearTranscriptCommand { get; }

    public SelectionOption<CaptureMode>? SelectedMode { get => _selectedMode; set { if (SetProperty(ref _selectedMode, value)) { OnPropertyChanged(nameof(NeedsSystemAudio)); OnPropertyChanged(nameof(NeedsMicrophone)); SelectDefaultProfile(); RaiseCommands(); } } }
    public SelectionOption<SourceLanguage>? SelectedLanguage { get => _selectedLanguage; set { if (SetProperty(ref _selectedLanguage, value)) SelectDefaultProfile(); } }
    public AudioDevice? SelectedOutputDevice { get => _selectedOutputDevice; set { if (SetProperty(ref _selectedOutputDevice, value)) RaiseCommands(); } }
    public AudioDevice? SelectedInputDevice { get => _selectedInputDevice; set { if (SetProperty(ref _selectedInputDevice, value)) RaiseCommands(); } }
    public bool IsCapturing { get => _isCapturing; private set { if (SetProperty(ref _isCapturing, value)) RaiseCommands(); } }
    public bool CanChangeSettings => !IsCapturing;
    public bool MovieAsrDebugMode { get => _movieAsrDebugMode; set => SetProperty(ref _movieAsrDebugMode, value); }
    public bool ShowRawAsr { get => _showRawAsr; set => SetProperty(ref _showRawAsr, value); }
    public ContextProfile? SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }
    public string AdditionalContext { get => _additionalContext; set => SetProperty(ref _additionalContext, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string ModelDirectory => _modelFiles.Directory;
    public bool IsModelAvailable => _modelFiles.Exists;
    public string QwenModelPath => _qwenSettings.ModelPath;
    public bool IsQwenModelAvailable => File.Exists(_qwenSettings.ModelPath);
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
            if (!_llmService.IsReady)
            {
                if (!IsQwenModelAvailable) Status = $"Qwen model not found. Expected: {QwenModelPath}";
                else
                {
                    Status = "Loading Qwen model...";
                    try { await _llmService.InitializeAsync(token); Status = "Models ready."; }
                    catch (Exception ex) { _logger.LogError(ex, "Unable to initialize Qwen"); Status = "Qwen unavailable — running transcription only."; }
                }
            }
            _llmWorker.Start(token);

            if (NeedsSystemAudio) await StartTranscriptionAsync(CaptureSource.System, token);
            if (NeedsMicrophone) await StartTranscriptionAsync(CaptureSource.Microphone, token);
            var timestamp = _clock.Now;
            if (NeedsSystemAudio) await StartAudioSessionAsync(CaptureSource.System, SelectedOutputDevice!, timestamp, token);
            if (NeedsMicrophone) await StartAudioSessionAsync(CaptureSource.Microphone, SelectedInputDevice!, timestamp, token);

            IsCapturing = true;
            var listening = SelectedMode?.Value == CaptureMode.Meeting ? "Listening to system audio and microphone..." : "Listening...";
            Status = _llmService.IsReady ? listening : $"{listening} Qwen unavailable — transcription only.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start transcription");
            Status = FriendlyError(ex);
            await StopTranscriptionsCoreAsync(false); await _llmWorker.StopAsync(false); await StopAudioSessionsCoreAsync();
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartTranscriptionAsync(CaptureSource source, CancellationToken token)
    {
        var session = _transcriptionSessionFactory.Create(source, SelectedMode!.Value, MovieAsrDebugMode);
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
            await _llmWorker.StopAsync(true);
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

    private void OnRecognitionResult(object? sender, RecognitionResult result)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var profile = SelectedProfile ?? ContextProfiles.DefaultFor(SelectedMode!.Value, SelectedLanguage!.Value);
        List<string> recent;
        lock (_recentContext) recent = [.. _recentContext];
        var request = new LlmTranslationRequest(sequence, result.Text, SelectedLanguage!.Value, SelectedMode!.Value,
            result.Source, profile, AdditionalContext, recent, result.Timestamp - result.ProcessingTime,
            result.AudioDuration, result.ProcessingTime);
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            TranscriptItems.Add(TranscriptItemViewModel.FromResult(sequence, result));
            while (TranscriptItems.Count > TranscriptLimit) TranscriptItems.RemoveAt(0);
            ClearTranscriptCommand.RaiseCanExecuteChanged();
        });
        if (_llmService.IsReady) _llmWorker.TryEnqueue(request);
        else OnLlmResult(this, new LlmTranslationResult(sequence, result.Text, result.Text, "", TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, false, false, "Qwen unavailable — raw ASR only."));
    }

    private void OnLlmResult(object? sender, LlmTranslationResult result) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        TranscriptItems.FirstOrDefault(item => item.SequenceId == result.SequenceId)?.Apply(result);
        if (!result.Success) return;
        lock (_recentContext)
        {
            _recentContext.Add(result.CorrectedText);
            while (_recentContext.Count > _qwenSettings.RecentContextCount) _recentContext.RemoveAt(0);
        }
    });

    private void OnLlmDiagnostics(object? sender, LlmDiagnostics diagnostics)
    {
        _llmDiagnostics = diagnostics;
        RefreshDiagnostics();
    }

    private void OnAsrStatusChanged(object? sender, string message) => Application.Current.Dispatcher.BeginInvoke(() => Status = message);

    private void OnDiagnosticsChanged(object? sender, TranscriptionDiagnostics diagnostics)
    {
        if (sender is not ITranscriptionSession session) return;
        lock (_diagnosticsBySource)
        {
            _diagnosticsBySource[session.Source] = diagnostics;
            var queued = _diagnosticsBySource.Values.Sum(value => value.QueueLength);
            var dropped = _diagnosticsBySource.Values.Sum(value => value.DroppedAudioChunks);
            var captured = _diagnosticsBySource.Values.Sum(value => value.CapturedAudioChunks);
            var speech = _diagnosticsBySource.Values.Sum(value => value.SpeechSegmentsDetected);
            var rejected = _diagnosticsBySource.Values.Sum(value => value.RejectedSegments);
            var buffered = _diagnosticsBySource.Values.Sum(value => value.BufferedSegments);
            var submitted = _diagnosticsBySource.Values.Sum(value => value.SubmittedSegments);
            var completed = _diagnosticsBySource.Values.Sum(value => value.CompletedRecognitions);
            Application.Current.Dispatcher.BeginInvoke(() => Diagnostics = FormatDiagnostics(captured, speech, rejected,
                buffered, submitted, completed, queued, dropped));
        }
    }

    private string FormatDiagnostics(long captured, long speech, long rejected, long buffered, long submitted,
        long completed, int queued, long dropped) =>
        $"Captured: {captured} · Speech detected: {speech} · Rejected: {rejected}\n" +
        $"Buffered: {buffered} · Submitted to ASR: {submitted} · ASR completed: {completed}\n" +
        $"ASR queue: {queued} · Dropped chunks: {dropped} · LLM queue: {_llmDiagnostics.QueueLength}\n" +
        $"LLM completed: {_llmDiagnostics.Completed} · Failed: {_llmDiagnostics.Failed} · Dropped: {_llmDiagnostics.MergedOrDropped}";

    private void RefreshDiagnostics()
    {
        lock (_diagnosticsBySource)
        {
            var values = _diagnosticsBySource.Values;
            var text = FormatDiagnostics(values.Sum(x => x.CapturedAudioChunks), values.Sum(x => x.SpeechSegmentsDetected),
                values.Sum(x => x.RejectedSegments), values.Sum(x => x.BufferedSegments),
                values.Sum(x => x.SubmittedSegments), values.Sum(x => x.CompletedRecognitions),
                values.Sum(x => x.QueueLength), values.Sum(x => x.DroppedAudioChunks));
            Application.Current.Dispatcher.BeginInvoke(() => Diagnostics = text);
        }
    }

    private void OnLevelChanged(object? sender, AudioLevelChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (e.Source == CaptureSource.System) SystemAudioLevel = e.Level * 100; else MicrophoneAudioLevel = e.Level * 100; });
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        if (e.Error is null || !IsCapturing) return;
        Application.Current.Dispatcher.BeginInvoke(() => { Status = $"Audio device disconnected: {e.Error.Message}"; _ = StopAsync(); });
    }

    private Task ClearTranscriptAsync() { TranscriptItems.Clear(); lock (_recentContext) _recentContext.Clear(); _llmWorker.ClearContextAndQueue(); ClearTranscriptCommand.RaiseCanExecuteChanged(); return Task.CompletedTask; }
    private void SelectDefaultProfile()
    {
        if (_selectedMode is null || _selectedLanguage is null) return;
        SelectedProfile = ContextProfiles.DefaultFor(_selectedMode.Value, _selectedLanguage.Value);
    }
    private static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => ex.Message,
        InvalidOperationException when ex.Message.Contains("native", StringComparison.OrdinalIgnoreCase) => ex.Message,
        _ => $"ASR initialization failed: {ex.Message}"
    };
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
    private void RaiseCommands() { OnPropertyChanged(nameof(CanChangeSettings)); StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); RefreshDevicesCommand.RaiseCanExecuteChanged(); ClearTranscriptCommand.RaiseCanExecuteChanged(); }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _llmWorker.ResultAvailable -= OnLlmResult; _llmWorker.DiagnosticsChanged -= OnLlmDiagnostics;
        await _llmWorker.DisposeAsync(); await _llmService.DisposeAsync(); await _recognizer.DisposeAsync(); _lifecycleGate.Dispose();
    }
}
