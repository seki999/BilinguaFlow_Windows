using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Core.Speech;

public sealed record SenseVoiceModelFiles(string Directory, string ModelPath, string TokensPath)
{
    public bool Exists => File.Exists(ModelPath) && File.Exists(TokensPath);
}

public sealed record RecognitionResult(DateTimeOffset Timestamp, CaptureSource Source, string Language, string Text,
    bool IsFinal, TimeSpan AudioDuration, TimeSpan ProcessingTime, bool WasMerged = false, int OriginalSegmentCount = 1,
    AsrEngine AsrEngine = AsrEngine.SenseVoice, bool IsComparison = false,
    float? AverageLogProbability = null, float? NoSpeechProbability = null)
{
    public double RealTimeFactor => AudioDuration.TotalSeconds <= 0 ? 0 : ProcessingTime.TotalSeconds / AudioDuration.TotalSeconds;
}

public sealed record TranscriptionDiagnostics(
    int QueueLength,
    long DroppedAudioChunks,
    long CapturedAudioChunks = 0,
    long SpeechSegmentsDetected = 0,
    long RejectedSegments = 0,
    long BufferedSegments = 0,
    long SubmittedSegments = 0,
    long CompletedRecognitions = 0,
    TimeSpan? InitializationTime = null);

public interface ISpeechRecognitionService : IAsyncDisposable
{
    AsrEngine Engine { get; }
    bool IsInitialized { get; }
    Task<TimeSpan> InitializeAsync(SenseVoiceModelFiles files, SourceLanguage language, CancellationToken cancellationToken);
    Task<string> RecognizeAsync(float[] samples, SourceLanguage language, CancellationToken cancellationToken);
}

public interface ITranscriptionSession : IAsyncDisposable
{
    CaptureSource Source { get; }
    event EventHandler<RecognitionResult>? ResultAvailable;
    event EventHandler<string>? StatusChanged;
    event EventHandler<TranscriptionDiagnostics>? DiagnosticsChanged;
    Task StartAsync(SourceLanguage language, CancellationToken cancellationToken);
    bool TryEnqueue(AudioChunk chunk);
    Task StopAsync(bool flushPending = true, CancellationToken cancellationToken = default);
}

public interface ITranscriptionSessionFactory
{
    ITranscriptionSession Create(CaptureSource source, CaptureMode mode, AsrEngine engine,
        bool movieDebugMode = false, bool compareMode = false);
}

public sealed record LlmTranslationRequest(
    long SequenceId,
    string OriginalText,
    SourceLanguage SourceLanguage,
    CaptureMode Mode,
    CaptureSource Source,
    ContextProfile Profile,
    string AdditionalContext,
    IReadOnlyList<string> RecentUtterances,
    DateTimeOffset SegmentCompletedAt,
    TimeSpan AudioDuration,
    TimeSpan AsrProcessingTime,
    AsrEngine AsrEngine = AsrEngine.SenseVoice);

public sealed record LlmTranslationResult(
    long SequenceId,
    string OriginalText,
    string CorrectedText,
    string TranslatedText,
    TimeSpan ProcessingTime,
    TimeSpan QueueWaitTime,
    TimeSpan EndToEndLatency,
    bool WasCorrected,
    bool Success,
    string? ErrorMessage = null);

public sealed record LlmDiagnostics(int QueueLength, long Completed, long Failed, long MergedOrDropped);

public interface ILlmService : IAsyncDisposable
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<LlmTranslationResult> CorrectAndTranslateAsync(LlmTranslationRequest request, CancellationToken cancellationToken);
}
