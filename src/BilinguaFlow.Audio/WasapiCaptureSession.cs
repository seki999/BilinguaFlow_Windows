using BilinguaFlow.Core.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BilinguaFlow.Audio;

public sealed class WasapiCaptureSession(CaptureSource source, ILogger<WasapiCaptureSession> logger)
    : IAudioCaptureSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _stopping;

    public CaptureSource Source { get; } = source;
    public bool IsCapturing => _capture is not null;
    public string? RecordingPath { get; private set; }
    public event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;
    public event EventHandler<AudioChunk>? AudioAvailable;
    public event EventHandler<CaptureStoppedEventArgs>? CaptureStopped;

    public async Task StartAsync(AudioDevice device, string recordingPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
                throw new InvalidOperationException($"A {Source} capture session is already active.");

            Directory.CreateDirectory(Path.GetDirectoryName(recordingPath)!);
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = enumerator.GetDevice(device.Id);
            WasapiCapture capture = Source == CaptureSource.System
                ? new WasapiLoopbackCapture(endpoint)
                : new WasapiCapture(endpoint);

            var writer = new WaveFileWriter(recordingPath, capture.WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            _writer = writer;
            RecordingPath = recordingPath;
            Interlocked.Exchange(ref _stopping, 0);
            _cancellationRegistration = cancellationToken.Register(() => _ = StopAsync());
            capture.StartRecording();
            logger.LogInformation("Started {Source} capture from {Device} to {Path}", Source, device.Name, recordingPath);
        }
        catch
        {
            Cleanup();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is null) return;
            _capture.StopRecording();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            var level = CalculatePeak(e.Buffer.AsSpan(0, e.BytesRecorded), _capture!.WaveFormat);
            LevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(Source, level));
            if (AudioAvailable is not null)
            {
                var format = _capture.WaveFormat;
                AudioAvailable.Invoke(this, new AudioChunk(Source, e.Buffer.AsSpan(0, e.BytesRecorded).ToArray(),
                    format.SampleRate, format.Channels, format.BitsPerSample,
                    format.Encoding == WaveFormatEncoding.IeeeFloat ? AudioSampleEncoding.IeeeFloat : AudioSampleEncoding.Pcm));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed while processing {Source} audio", Source);
            _capture?.StopRecording();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            logger.LogError(e.Exception, "{Source} capture stopped unexpectedly", Source);
        else
            logger.LogInformation("Stopped {Source} capture", Source);

        Cleanup();
        CaptureStopped?.Invoke(this, new CaptureStoppedEventArgs(Source, e.Exception));
    }

    internal static float CalculatePeak(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.IsEmpty) return 0;
        float peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 3 < buffer.Length; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(i, 4))));
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 1 < buffer.Length; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer.Slice(i, 2)) / 32768f));
        }
        return Math.Clamp(peak, 0, 1);
    }

    private void Cleanup()
    {
        _cancellationRegistration.Dispose();
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
        _writer?.Dispose();
        _writer = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Cleanup();
        _gate.Dispose();
    }
}
