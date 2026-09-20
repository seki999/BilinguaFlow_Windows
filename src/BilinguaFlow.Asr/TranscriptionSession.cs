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
    private readonly SpeechSegmentationOptions _segmentationOptions;
    private readonly AudioPreprocessor _preprocessor = new();
    private Channel<AudioChunk>? _channel;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private SourceLanguage _language;
    private int _queueLength;
    private long _dropped;
    private long _captured;
    private long _speechDetected;
    private long _rejected;
    private long _buffered;
    private long _submitted;
    private long _completed;

    private int _queueWarningIssued;

    public TranscriptionSession(CaptureSource source, ISpeechRecognitionService recognizer,
        ILogger<TranscriptionSession> logger, SpeechSegmentationOptions? segmentationOptions = null)
    { Source = source; _recognizer = recognizer; _logger = logger; _segmentationOptions = segmentationOptions ?? new(); }

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
        Interlocked.Increment(ref _captured);
        var channel = _channel;
        if (channel is null || _worker is null) return false;
        if (channel.Writer.TryWrite(chunk))
        {
            Interlocked.Increment(ref _queueLength);
            WarnIfQueueIsGrowing();
            PublishDiagnostics();
            return true;
        }
        Interlocked.Increment(ref _dropped);
        PublishDiagnostics();
        return false;
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        var segmenter = new EnergyVadSegmenter(_segmentationOptions);
        segmenter.Diagnostic += OnSegmentationDiagnostic;
        try
        {
            var reader = _channel!.Reader;
            while (true)
            {
                AudioChunk chunk;
                using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pollCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                try { chunk = await reader.ReadAsync(pollCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var expired in segmenter.FlushExpired())
                        await RecognizeAsync(expired, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (ChannelClosedException) { break; }

                await ProcessChunkAsync(chunk, segmenter, cancellationToken).ConfigureAwait(false);
                while (reader.TryRead(out var queuedChunk))
                    await ProcessChunkAsync(queuedChunk, segmenter, cancellationToken).ConfigureAwait(false);
            }
            foreach (var pending in segmenter.Flush()) await RecognizeAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ASR worker failed for {Source}", Source);
            StatusChanged?.Invoke(this, $"ASR worker error: {ex.Message}");
        }
        finally { segmenter.Diagnostic -= OnSegmentationDiagnostic; }
    }

    private async Task ProcessChunkAsync(AudioChunk chunk, EnergyVadSegmenter segmenter, CancellationToken cancellationToken)
    {
        Interlocked.Decrement(ref _queueLength);
        WarnIfQueueIsGrowing();
        var samples = _preprocessor.ConvertToMono16Khz(chunk);
        foreach (var utterance in segmenter.Process(samples))
            await RecognizeAsync(utterance, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecognizeAsync(SpeechSegment segment, CancellationToken cancellationToken)
    {
        var duration = segment.Duration;
        Interlocked.Increment(ref _submitted);
        var languageCode = _language == SourceLanguage.Japanese ? "ja" : "en";
        _logger.LogInformation(
            "Submitting {Source} segment to SenseVoice: duration {Duration:F2}s, RMS {Rms:F5}, language {Language} ({LanguageCode}), reason {Reason}",
            Source, duration.TotalSeconds, segment.Rms, _language, languageCode, SubmissionReasonLabel(segment.SubmissionReason));
        PublishDiagnostics();
        StatusChanged?.Invoke(this, "Recognizing...");
        var stopwatch = Stopwatch.StartNew();
        var text = await _recognizer.RecognizeAsync(segment.Samples, _language, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        Interlocked.Increment(ref _completed);
        PublishDiagnostics();
        _logger.LogInformation("Recognized {Source} {Language} utterance ({AudioMs} ms) in {ProcessingMs} ms, RTF {Rtf:F2}, merged {MergedCount}",
            Source, _language, duration.TotalMilliseconds, stopwatch.ElapsedMilliseconds,
            stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds, segment.OriginalSegmentCount);
        if (!string.IsNullOrWhiteSpace(text))
            ResultAvailable?.Invoke(this, new RecognitionResult(DateTimeOffset.Now, Source,
                _language == SourceLanguage.Japanese ? "Japanese" : "English", text, true, duration,
                stopwatch.Elapsed, segment.WasMerged, segment.OriginalSegmentCount));
        StatusChanged?.Invoke(this, "Listening...");
    }

    private void OnSegmentationDiagnostic(object? sender, SegmentationEvent e)
    {
        switch (e.Kind)
        {
            case SegmentationEventKind.SpeechDetected: Interlocked.Increment(ref _speechDetected); break;
            case SegmentationEventKind.Rejected: Interlocked.Increment(ref _rejected); break;
            case SegmentationEventKind.Buffered: Interlocked.Increment(ref _buffered); break;
        }
        if (e.Kind == SegmentationEventKind.Rejected) _logger.LogDebug("{Source}: {Message}", Source, e.Message);
        else _logger.LogInformation("{Source}: {Message}", Source, e.Message);
        PublishDiagnostics();
    }

    private static string SubmissionReasonLabel(SegmentSubmissionReason reason) => reason switch
    {
        SegmentSubmissionReason.VadEnd => "VAD-end",
        SegmentSubmissionReason.Timeout => "timeout",
        SegmentSubmissionReason.MaxLength => "max-length",
        SegmentSubmissionReason.StopFlush => "Stop-flush",
        SegmentSubmissionReason.DebugChunk => "debug-chunk",
        _ => reason.ToString()
    };

    private void WarnIfQueueIsGrowing()
    {
        var length = Volatile.Read(ref _queueLength);
        if (length >= Capacity * 3 / 4 && Interlocked.Exchange(ref _queueWarningIssued, 1) == 0)
            _logger.LogWarning("{Source} ASR queue is growing: {QueueLength}/{Capacity}", Source, length, Capacity);
        else if (length < Capacity / 2)
            Interlocked.Exchange(ref _queueWarningIssued, 0);
    }

    private void PublishDiagnostics() => DiagnosticsChanged?.Invoke(this,
        new TranscriptionDiagnostics(Math.Max(0, Volatile.Read(ref _queueLength)), Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _captured), Interlocked.Read(ref _speechDetected), Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _buffered), Interlocked.Read(ref _submitted), Interlocked.Read(ref _completed)));

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
