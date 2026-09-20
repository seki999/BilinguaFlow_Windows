namespace BilinguaFlow.Core.Models;

public enum CaptureMode { Movie, Meeting, Microphone }

public enum SourceLanguage { English, Japanese }
public enum AsrEngine { SenseVoice, Whisper }

public sealed record ContextProfile(
    string Name,
    SourceLanguage Language,
    string Description,
    IReadOnlyList<string> Keywords);
