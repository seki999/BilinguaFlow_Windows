using System.Globalization;

namespace BilinguaFlow.Core.Audio;

public interface IRecordingPathFactory
{
    string Create(CaptureSource source, DateTimeOffset timestamp);
}

public sealed class RecordingPathFactory(string baseDirectory) : IRecordingPathFactory
{
    public string Create(CaptureSource source, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(baseDirectory);
        var label = source == CaptureSource.System ? "system" : "microphone";
        var stem = $"{timestamp.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}_{label}";
        var candidate = Path.Combine(baseDirectory, $"{stem}.wav");
        for (var suffix = 1; File.Exists(candidate); suffix++)
            candidate = Path.Combine(baseDirectory, $"{stem}_{suffix}.wav");
        return candidate;
    }
}
