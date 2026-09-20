namespace BilinguaFlow.Core.Audio;

public enum CaptureSource { System, Microphone }

public sealed record AudioLevelChangedEventArgs(CaptureSource Source, float Level);

public sealed record CaptureStoppedEventArgs(CaptureSource Source, Exception? Error = null);

public enum AudioSampleEncoding { Pcm, IeeeFloat }

public sealed record AudioChunk(CaptureSource Source, byte[] Data, int SampleRate, int Channels, int BitsPerSample, AudioSampleEncoding Encoding);

public interface IAudioDeviceService
{
    IReadOnlyList<AudioDevice> GetInputDevices();
    IReadOnlyList<AudioDevice> GetOutputDevices();
}

public interface IAudioCaptureSession : IAsyncDisposable
{
    CaptureSource Source { get; }
    bool IsCapturing { get; }
    string? RecordingPath { get; }
    event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;
    event EventHandler<AudioChunk>? AudioAvailable;
    event EventHandler<CaptureStoppedEventArgs>? CaptureStopped;
    Task StartAsync(AudioDevice device, string recordingPath, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IAudioCaptureSessionFactory
{
    IAudioCaptureSession Create(CaptureSource source);
}
