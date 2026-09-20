using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilinguaFlow.Core.Tests;

public sealed class SpeechPipelineTests
{
    [Fact]
    public void ModelLocator_ReportsMissingExpectedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var files = SenseVoiceModelLocator.FromDirectory(directory);
        Assert.False(files.Exists);
        Assert.Equal(Path.Combine(directory, "model.int8.onnx"), files.ModelPath);
        Assert.Equal(Path.Combine(directory, "tokens.txt"), files.TokensPath);
    }

    [Fact]
    public void RecognitionResult_ComputesRealTimeFactor()
    {
        var result = new RecognitionResult(DateTimeOffset.Now, CaptureSource.System, "Japanese", "text", true,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        Assert.Equal(0.5, result.RealTimeFactor);
    }

    [Fact]
    public async Task Session_StopCancelsWorkerWithoutHanging()
    {
        await using var session = new TranscriptionSession(CaptureSource.Microphone, new FakeRecognizer(), NullLogger<TranscriptionSession>.Instance);
        await session.StartAsync(SourceLanguage.English, CancellationToken.None);
        await session.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FakeRecognizer : ISpeechRecognitionService
    {
        public bool IsInitialized => true;
        public Task<TimeSpan> InitializeAsync(SenseVoiceModelFiles files, SourceLanguage language, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
        public Task<string> RecognizeAsync(float[] samples, SourceLanguage language, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
