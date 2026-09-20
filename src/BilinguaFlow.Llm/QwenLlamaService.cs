using System.Diagnostics;
using System.Text;
using BilinguaFlow.Core.Speech;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.Llm;

public sealed class QwenLlamaService(QwenSettings settings, ILogger<QwenLlamaService> logger) : ILlmService
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly QwenPromptBuilder _promptBuilder = new(settings);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;

    public bool IsReady => _executor is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsReady) return;
        if (!File.Exists(settings.ModelPath))
            throw new FileNotFoundException($"Qwen model not found. Expected: {settings.ModelPath}", settings.ModelPath);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady) return;
            var stopwatch = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                var parameters = new ModelParams(settings.ModelPath)
                {
                    ContextSize = settings.ContextSize,
                    Threads = settings.EffectiveThreads,
                    GpuLayerCount = 0
                };
                _weights = LLamaWeights.LoadFromFile(parameters);
                _executor = new StatelessExecutor(_weights, parameters);
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Qwen model loaded in {ElapsedMs} ms from {ModelPath} with {Threads} CPU threads",
                stopwatch.ElapsedMilliseconds, settings.ModelPath, settings.EffectiveThreads);
        }
        finally { _initializationGate.Release(); }
    }

    public async Task<LlmTranslationResult> CorrectAndTranslateAsync(
        LlmTranslationRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            var prompt = _promptBuilder.Build(request);
            var inference = new InferenceParams
            {
                MaxTokens = settings.MaxOutputTokens,
                AntiPrompts = ["<|im_end|>", "<|im_start|>"],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = settings.Temperature,
                    TopP = settings.TopP
                }
            };
            var output = new StringBuilder();
            await foreach (var text in _executor!.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
                output.Append(text);
            stopwatch.Stop();
            var raw = output.ToString();
            logger.LogInformation("Qwen generated {Characters} characters in {ElapsedMs} ms for sequence {SequenceId}",
                raw.Length, stopwatch.ElapsedMilliseconds, request.SequenceId);
            if (!LlmJsonParser.TryParse(raw, out var parsed))
            {
                logger.LogWarning("Invalid Qwen JSON for sequence {SequenceId}: {RawOutput}", request.SequenceId, raw);
                return Failure(request, stopwatch.Elapsed, "Translation error: invalid JSON output.");
            }
            return new LlmTranslationResult(request.SequenceId, request.OriginalText, parsed!.CorrectedText,
                parsed.TranslatedText, stopwatch.Elapsed, TimeSpan.Zero,
                DateTimeOffset.Now - request.SegmentCompletedAt,
                !string.Equals(request.OriginalText, parsed.CorrectedText, StringComparison.Ordinal), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Qwen translation failed for sequence {SequenceId}", request.SequenceId);
            return Failure(request, stopwatch.Elapsed, $"Translation error: {ex.Message}");
        }
    }

    private static LlmTranslationResult Failure(LlmTranslationRequest request, TimeSpan elapsed, string error) =>
        new(request.SequenceId, request.OriginalText, request.OriginalText, "", elapsed, TimeSpan.Zero,
            DateTimeOffset.Now - request.SegmentCompletedAt, false, false, error);

    public ValueTask DisposeAsync()
    {
        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
