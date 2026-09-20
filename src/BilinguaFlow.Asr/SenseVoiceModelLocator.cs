using BilinguaFlow.Core.Speech;

namespace BilinguaFlow.Asr;

public static class SenseVoiceModelLocator
{
    public static SenseVoiceModelFiles Locate(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var directory = Path.Combine(current.FullName, "models", "sensevoice");
            var files = FromDirectory(directory);
            if (files.Exists || Directory.Exists(Path.Combine(current.FullName, "models"))) return files;
            current = current.Parent;
        }
        return FromDirectory(Path.Combine(Path.GetFullPath(startDirectory), "models", "sensevoice"));
    }

    public static SenseVoiceModelFiles FromDirectory(string directory) => new(
        Path.GetFullPath(directory),
        Path.Combine(Path.GetFullPath(directory), "model.int8.onnx"),
        Path.Combine(Path.GetFullPath(directory), "tokens.txt"));
}
