namespace BilinguaFlow.Asr;

public sealed record WhisperSettings(
    string ModelPath,
    int Threads = 0,
    int PreferredSegmentMilliseconds = 6_000,
    int MaximumSegmentMilliseconds = 12_000,
    int BeamSize = 1)
{
    public int EffectiveThreads => Threads > 0 ? Threads : Math.Max(2, Environment.ProcessorCount / 2);
}

public static class WhisperModelLocator
{
    public const string FileName = "ggml-small.bin";
    public static string Locate(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "models", "whisper", FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine(Path.GetFullPath(startDirectory), "models", "whisper", FileName);
    }
}
