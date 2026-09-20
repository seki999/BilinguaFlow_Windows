namespace BilinguaFlow.Llm;

public sealed record QwenSettings(
    string ModelPath,
    uint ContextSize = 4096,
    int Threads = 0,
    float Temperature = 0.1f,
    float TopP = 0.8f,
    int MaxOutputTokens = 192,
    int RecentContextCount = 5,
    int LlmQueueCapacity = 10)
{
    public int EffectiveThreads => Threads > 0 ? Threads : Math.Max(2, Environment.ProcessorCount / 2);
}

public static class QwenModelLocator
{
    public const string FileName = "Qwen_Qwen3-1.7B-Q4_K_M.gguf";

    public static string Locate(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "models", "qwen", FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine(Path.GetFullPath(startDirectory), "models", "qwen", FileName);
    }
}
