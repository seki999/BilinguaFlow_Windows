using BilinguaFlow.Core.Audio;

namespace BilinguaFlow.Core.Tests;

public sealed class RecordingPathFactoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"BilinguaFlow.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void Create_UsesSafeTimestampAndSourceName()
    {
        var sut = new RecordingPathFactory(_directory);
        var path = sut.Create(CaptureSource.System, new DateTimeOffset(2026, 9, 20, 18, 30, 0, TimeSpan.FromHours(9)));
        Assert.Equal(Path.Combine(_directory, "2026-09-20_183000_system.wav"), path);
    }

    [Fact]
    public void Create_DoesNotOverwriteAnExistingRecording()
    {
        Directory.CreateDirectory(_directory);
        var sut = new RecordingPathFactory(_directory);
        var timestamp = new DateTimeOffset(2026, 9, 20, 18, 30, 0, TimeSpan.Zero);
        File.WriteAllBytes(sut.Create(CaptureSource.Microphone, timestamp), []);
        Assert.EndsWith("2026-09-20_183000_microphone_1.wav", sut.Create(CaptureSource.Microphone, timestamp));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
