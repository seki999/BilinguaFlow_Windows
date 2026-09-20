using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Core.Speech;

public sealed record SenseVoiceModelFiles(string Directory, string ModelPath, string TokensPath)
{
    public bool Exists => File.Exists(ModelPath) && File.Exists(TokensPath);
}

public sealed record RecognitionResult(DateTimeOffset Timestamp, CaptureSource Source, string Language, string Text,
    bool IsFinal, TimeSpan AudioDuration, TimeSpan ProcessingTime)
{
    public double RealTimeFactor => AudioDuration.TotalSeconds <= 0 ? 0 : ProcessingTime.TotalSeconds / AudioDuration.TotalSeconds;
}

public sealed record TranscriptionDiagnostics(int QueueLength, long DroppedAudioChunks, TimeSpan? InitializationTime = null);

public interface ISpeechRecognitionService : IAsyncDisposable
{
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
    ITranscriptionSession Create(CaptureSource source);
}

public sealed record LlmRequest(string AsrText, SourceLanguage Language, ContextProfile? Context = null);
public sealed record LlmResult(string CorrectedText, string TranslatedText);

public interface ILlmService
{
    Task<LlmResult> CorrectAndTranslateAsync(LlmRequest request, CancellationToken cancellationToken);
}
