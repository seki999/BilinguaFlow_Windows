using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Speech;

namespace BilinguaFlow.App.ViewModels;

public sealed record TranscriptItemViewModel(
    DateTimeOffset Timestamp,
    string Source,
    string Language,
    string Text,
    bool IsFinal,
    string Performance)
{
    public static TranscriptItemViewModel FromResult(RecognitionResult result) => new(
        result.Timestamp,
        result.Source == CaptureSource.System ? "REMOTE" : "MIC",
        result.Language,
        result.Text,
        result.IsFinal,
        $"{result.AudioDuration.TotalSeconds:F1}s audio · {result.Language} · {result.ProcessingTime.TotalSeconds:F1}s ASR · " +
        $"RTF {result.RealTimeFactor:F2} · " + (result.WasMerged ? $"merged: {result.OriginalSegmentCount}" : "merged: no (1)"));
}
