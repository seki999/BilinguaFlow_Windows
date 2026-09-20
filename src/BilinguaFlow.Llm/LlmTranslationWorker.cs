using System.Diagnostics;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Llm;

public sealed class LlmTranslationWorker(ILlmService service, QwenSettings settings, ILogger<LlmTranslationWorker> logger)
    : IAsyncDisposable
{
    private sealed record Queued(LlmTranslationRequest Request, long EnqueuedTimestamp);
    private readonly Queue<Queued> _queue = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _stopping;
    private long _completed;
    private long _failed;
    private long _dropped;

    public event EventHandler<LlmTranslationResult>? ResultAvailable;
    public event EventHandler<LlmDiagnostics>? DiagnosticsChanged;

    public void Start(CancellationToken cancellationToken)
    {
        if (_worker is not null) return;
        _stopping = false;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
    }

    public bool TryEnqueue(LlmTranslationRequest request)
    {
        Queued? dropped = null;
        lock (_gate)
        {
            if (_worker is null) return false;
            if (_queue.Count >= settings.LlmQueueCapacity)
            {
                dropped = _queue.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _queue.Enqueue(new Queued(request, Stopwatch.GetTimestamp()));
        }
        if (dropped is not null)
        {
            logger.LogWarning("LLM queue full; dropped oldest sequence {SequenceId}", dropped.Request.SequenceId);
            ResultAvailable?.Invoke(this, Failure(dropped.Request, "Translation dropped: LLM queue full."));
        }
        _signal.Release();
        PublishDiagnostics();
        return true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try { await _signal.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            Queued? item;
            lock (_gate)
            {
                if (_stopping && _queue.Count == 0) break;
                item = _queue.Count > 0 ? _queue.Dequeue() : null;
            }
            if (item is null) continue;
            PublishDiagnostics();
            var queueWait = Stopwatch.GetElapsedTime(item.EnqueuedTimestamp);
            LlmTranslationResult result;
            try { result = await service.CorrectAndTranslateAsync(item.Request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM worker isolated a failure for sequence {SequenceId}", item.Request.SequenceId);
                result = Failure(item.Request, $"Translation error: {ex.Message}");
            }
            result = result with { QueueWaitTime = queueWait, EndToEndLatency = DateTimeOffset.Now - item.Request.SegmentCompletedAt };
            if (result.Success) Interlocked.Increment(ref _completed); else Interlocked.Increment(ref _failed);
            logger.LogInformation("LLM sequence {SequenceId}: queue {QueueMs} ms, generation {GenerationMs} ms, end-to-end {TotalMs} ms",
                result.SequenceId, queueWait.TotalMilliseconds, result.ProcessingTime.TotalMilliseconds, result.EndToEndLatency.TotalMilliseconds);
            ResultAvailable?.Invoke(this, result);
            PublishDiagnostics();
        }
    }

    public void ClearContextAndQueue()
    {
        lock (_gate) _queue.Clear();
        PublishDiagnostics();
    }

    public async Task StopAsync(bool finishCurrent = true)
    {
        var worker = _worker;
        if (worker is null) return;
        Queued[] canceled;
        lock (_gate)
        {
            canceled = [.. _queue];
            _queue.Clear();
        }
        foreach (var item in canceled)
            ResultAvailable?.Invoke(this, Failure(item.Request, "Translation canceled on Stop."));
        _stopping = true;
        if (finishCurrent) _signal.Release(); else _cancellation?.Cancel();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(finishCurrent ? 15 : 2)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            logger.LogWarning("Timed out stopping LLM worker");
            _cancellation?.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        catch (OperationCanceledException) { }
        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
        PublishDiagnostics();
    }

    private LlmTranslationResult Failure(LlmTranslationRequest request, string message) =>
        new(request.SequenceId, request.OriginalText, request.OriginalText, "", TimeSpan.Zero, TimeSpan.Zero,
            DateTimeOffset.Now - request.SegmentCompletedAt, false, false, message);

    private void PublishDiagnostics()
    {
        int length;
        lock (_gate) length = _queue.Count;
        DiagnosticsChanged?.Invoke(this, new LlmDiagnostics(length, Interlocked.Read(ref _completed),
            Interlocked.Read(ref _failed), Interlocked.Read(ref _dropped)));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(false).ConfigureAwait(false);
        _signal.Dispose();
    }
}
