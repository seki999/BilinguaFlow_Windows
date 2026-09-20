using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Speech;

namespace BilinguaFlow.App.ViewModels;

public sealed class TranscriptItemViewModel : ObservableObject
{
    private string _correctedText;
    private string _translatedText = "";
    private string _llmStatus = "Waiting for Qwen...";
    private string _performance;

    private TranscriptItemViewModel(long sequenceId, RecognitionResult result)
    {
        SequenceId = sequenceId; Timestamp = result.Timestamp;
        Source = result.Source == CaptureSource.System ? "REMOTE" : "MIC";
        Language = result.Language; RawText = result.Text; _correctedText = result.Text;
        _performance = $"{result.AudioDuration.TotalSeconds:F1}s audio · {result.ProcessingTime.TotalSeconds:F1}s ASR · RTF {result.RealTimeFactor:F2}";
    }

    public long SequenceId { get; }
    public DateTimeOffset Timestamp { get; }
    public string Source { get; }
    public string Language { get; }
    public string RawText { get; }
    public string CorrectedText { get => _correctedText; private set => SetProperty(ref _correctedText, value); }
    public string TranslatedText { get => _translatedText; private set => SetProperty(ref _translatedText, value); }
    public string LlmStatus { get => _llmStatus; private set => SetProperty(ref _llmStatus, value); }
    public string Performance { get => _performance; private set => SetProperty(ref _performance, value); }

    public static TranscriptItemViewModel FromResult(long sequenceId, RecognitionResult result) => new(sequenceId, result);

    public void Apply(LlmTranslationResult result)
    {
        CorrectedText = result.CorrectedText; TranslatedText = result.TranslatedText;
        LlmStatus = result.Success ? $"Qwen: {result.ProcessingTime.TotalSeconds:F2}s" : result.ErrorMessage ?? "Translation error";
        Performance += $" · LLM {result.ProcessingTime.TotalSeconds:F2}s · E2E {result.EndToEndLatency.TotalSeconds:F2}s";
    }
}
