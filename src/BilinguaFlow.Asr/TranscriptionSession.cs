using System.Diagnostics;
using System.Threading.Channels;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Asr;

public sealed class TranscriptionSession : ITranscriptionSession
{
    private const int Capacity = 64;
    private readonly ISpeechRecognitionService _recognizer;
    private readonly ILogger<TranscriptionSession> _logger;
    private readonly AudioPreprocessor _preprocessor = new();
    private Channel<AudioChunk>? _channel;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private SourceLanguage _language;
    private int _queueLength;
    private long _dropped;

    public TranscriptionSession(CaptureSource source, ISpeechRecognitionService recognizer, ILogger<TranscriptionSession> logger)
    { Source = source; _recognizer = recognizer; _logger = logger; }

    public CaptureSource Source { get; }
    public event EventHandler<RecognitionResult>? ResultAvailable;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;

    public Task StartAsync(SourceLanguage language, CancellationToken cancellationToken)
    {
        if (_worker is not null) throw new InvalidOperationException($"A {Source} transcription session is already active.");
        _language = language;
        _channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunWorkerAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public bool TryEnqueue(AudioChunk chunk)
    {
        var channel = _channel;
        if (channel is null || _worker is null) return false;
        if (channel.Writer.TryWrite(chunk))
        {
            Interlocked.Increment(ref _queueLength);
            PublishDiagnostics();
            return true;
        }
        Interlocked.Increment(ref _dropped);
        PublishDiagnostics();
        return false;
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        var segmenter = new EnergyVadSegmenter();
        try
        {
            await foreach (var chunk in _channel!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueLength);
                var samples = _preprocessor.ConvertToMono16Khz(chunk);
                foreach (var utterance in segmenter.Process(samples)) await RecognizeAsync(utterance, cancellationToken).ConfigureAwait(false);
            }
            var pending = segmenter.Flush();
            if (pending is not null) await RecognizeAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ASR worker failed for {Source}", Source);
            StatusChanged?.Invoke(this, $"ASR worker error: {ex.Message}");
        }
    }

    private async Task RecognizeAsync(float[] utterance, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds((double)utterance.Length / AudioPreprocessor.TargetSampleRate);
        StatusChanged?.Invoke(this, "Recognizing...");
        var stopwatch = Stopwatch.StartNew();
        var text = await _recognizer.RecognizeAsync(utterance, _language, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        _logger.LogInformation("Recognized {Source} utterance ({AudioMs} ms) in {ProcessingMs} ms, RTF {Rtf:F2}",
            Source, duration.TotalMilliseconds, stopwatch.ElapsedMilliseconds, stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds);
        if (!string.IsNullOrWhiteSpace(text))
            ResultAvailable?.Invoke(this, new RecognitionResult(DateTimeOffset.Now, Source,
                _language == SourceLanguage.Japanese ? "Japanese" : "English", text, true, duration, stopwatch.Elapsed));
        StatusChanged?.Invoke(this, "Listening...");
    }

    private void PublishDiagnostics() => DiagnosticsChanged?.Invoke(this,
        new TranscriptionDiagnostics(Math.Max(0, Volatile.Read(ref _queueLength)), Interlocked.Read(ref _dropped)));

    public async Task StopAsync(bool flushPending = true, CancellationToken cancellationToken = default)
    {
        var worker = _worker;
        if (worker is null) return;
        _channel?.Writer.TryComplete();
        if (!flushPending) _cancellation?.Cancel();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out stopping {Source} transcription worker", Source);
            _cancellation?.Cancel();
        }
        finally
        {
            _worker = null; _channel = null; _cancellation?.Dispose(); _cancellation = null;
            Interlocked.Exchange(ref _queueLength, 0); PublishDiagnostics();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(false).ConfigureAwait(false);
}
