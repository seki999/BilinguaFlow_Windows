using System.Collections.ObjectModel;
using System.Windows;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAudioDeviceService _devices;
    private readonly IAudioCaptureSessionFactory _sessions;
    private readonly IRecordingPathFactory _paths;
    private readonly ISystemClock _clock;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<IAudioCaptureSession> _activeSessions = [];
    private CancellationTokenSource? _captureCancellation;
    private SelectionOption<CaptureMode>? _selectedMode;
    private SelectionOption<SourceLanguage>? _selectedLanguage;
    private AudioDevice? _selectedOutputDevice;
    private AudioDevice? _selectedInputDevice;
    private bool _isCapturing;
    private string _status = "Ready";
    private float _systemAudioLevel;
    private float _microphoneAudioLevel;

    public MainViewModel(IAudioDeviceService devices, IAudioCaptureSessionFactory sessions, IRecordingPathFactory paths, ISystemClock clock, ILogger<MainViewModel> logger)
    {
        _devices = devices; _sessions = sessions; _paths = paths; _clock = clock; _logger = logger;
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCapturing);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsCapturing);
        SelectedMode = Modes[0]; SelectedLanguage = Languages[0];
    }

    public IReadOnlyList<SelectionOption<CaptureMode>> Modes { get; } = [new(CaptureMode.Movie, "Movie Mode"), new(CaptureMode.Meeting, "Meeting Mode"), new(CaptureMode.Microphone, "Microphone Mode")];
    public IReadOnlyList<SelectionOption<SourceLanguage>> Languages { get; } = [new(SourceLanguage.English, "English"), new(SourceLanguage.Japanese, "Japanese")];
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    public ObservableCollection<AudioDevice> InputDevices { get; } = [];
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public SelectionOption<CaptureMode>? SelectedMode { get => _selectedMode; set { if (SetProperty(ref _selectedMode, value)) { OnPropertyChanged(nameof(NeedsSystemAudio)); OnPropertyChanged(nameof(NeedsMicrophone)); RaiseCommands(); } } }
    public SelectionOption<SourceLanguage>? SelectedLanguage { get => _selectedLanguage; set => SetProperty(ref _selectedLanguage, value); }
    public AudioDevice? SelectedOutputDevice { get => _selectedOutputDevice; set { if (SetProperty(ref _selectedOutputDevice, value)) RaiseCommands(); } }
    public AudioDevice? SelectedInputDevice { get => _selectedInputDevice; set { if (SetProperty(ref _selectedInputDevice, value)) RaiseCommands(); } }
    public bool IsCapturing { get => _isCapturing; private set { if (SetProperty(ref _isCapturing, value)) RaiseCommands(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public float SystemAudioLevel { get => _systemAudioLevel; private set => SetProperty(ref _systemAudioLevel, value); }
    public float MicrophoneAudioLevel { get => _microphoneAudioLevel; private set => SetProperty(ref _microphoneAudioLevel, value); }
    public bool NeedsSystemAudio => SelectedMode?.Value is CaptureMode.Movie or CaptureMode.Meeting;
    public bool NeedsMicrophone => SelectedMode?.Value is CaptureMode.Microphone or CaptureMode.Meeting;
    public Task InitializeAsync() => RefreshDevicesAsync();

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

    private bool CanStart() => !IsCapturing && SelectedMode is not null && (!NeedsSystemAudio || SelectedOutputDevice is not null) && (!NeedsMicrophone || SelectedInputDevice is not null);

    private async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (IsCapturing) return;
            _captureCancellation = new CancellationTokenSource(); var token = _captureCancellation.Token; var timestamp = _clock.Now;
            if (NeedsSystemAudio) await StartSessionAsync(CaptureSource.System, SelectedOutputDevice!, timestamp, token);
            if (NeedsMicrophone) await StartSessionAsync(CaptureSource.Microphone, SelectedInputDevice!, timestamp, token);
            IsCapturing = true;
            Status = SelectedMode?.Value == CaptureMode.Meeting ? "Listening to system audio and microphone..." : "Listening...";
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to start audio capture"); Status = $"Capture error: {ex.Message}"; await StopSessionsCoreAsync(); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartSessionAsync(CaptureSource source, AudioDevice device, DateTimeOffset timestamp, CancellationToken token)
    {
        var session = _sessions.Create(source); session.LevelChanged += OnLevelChanged; session.CaptureStopped += OnCaptureStopped; _activeSessions.Add(session);
        await session.StartAsync(device, _paths.Create(source, timestamp), token);
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCapturing && _activeSessions.Count == 0) return;
            Status = "Stopping..."; _captureCancellation?.Cancel(); await StopSessionsCoreAsync(); Status = "Stopped — recordings saved";
        }
        catch (Exception ex) { _logger.LogError(ex, "Unable to stop audio capture cleanly"); Status = $"Stop error: {ex.Message}"; }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopSessionsCoreAsync()
    {
        var sessions = _activeSessions.ToArray(); _activeSessions.Clear();
        foreach (var session in sessions)
        {
            session.LevelChanged -= OnLevelChanged; session.CaptureStopped -= OnCaptureStopped;
            try { await session.StopAsync(); await session.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing {Source} session", session.Source); }
        }
        _captureCancellation?.Dispose(); _captureCancellation = null; IsCapturing = false; SystemAudioLevel = 0; MicrophoneAudioLevel = 0;
    }

    private void OnLevelChanged(object? sender, AudioLevelChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => { if (e.Source == CaptureSource.System) SystemAudioLevel = e.Level * 100; else MicrophoneAudioLevel = e.Level * 100; });
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        if (e.Error is null || !IsCapturing) return;
        Application.Current.Dispatcher.BeginInvoke(() => { Status = $"{e.Source} device disconnected: {e.Error.Message}"; _ = StopAsync(); });
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
    private void RaiseCommands() { StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); RefreshDevicesCommand.RaiseCanExecuteChanged(); }
    public async ValueTask DisposeAsync() { await StopAsync(); _lifecycleGate.Dispose(); }
}
