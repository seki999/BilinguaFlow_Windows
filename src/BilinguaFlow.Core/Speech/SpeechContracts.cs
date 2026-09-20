using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Core.Speech;

public sealed record SpeechRecognitionRequest(
    Stream Audio,
    SourceLanguage Language,
    ContextProfile? Context = null);

public sealed record SpeechRecognitionResult(string Text, bool IsFinal, TimeSpan Offset);

public interface ISpeechRecognitionService
{
    IAsyncEnumerable<SpeechRecognitionResult> RecognizeAsync(
        SpeechRecognitionRequest request,
        CancellationToken cancellationToken);
}

public sealed record LlmRequest(string AsrText, SourceLanguage Language, ContextProfile? Context = null);

public sealed record LlmResult(string CorrectedText, string TranslatedText);

public interface ILlmService
{
    Task<LlmResult> CorrectAndTranslateAsync(LlmRequest request, CancellationToken cancellationToken);
}
