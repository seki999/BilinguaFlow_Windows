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

    [Fact]
    public async Task Session_PropagatesSelectedJapaneseLanguageToRecognizerAndResult()
    {
        var recognizer = new FakeRecognizer { ResultText = "テスト" };
        await using var session = new TranscriptionSession(CaptureSource.System, recognizer,
            NullLogger<TranscriptionSession>.Instance, new SpeechSegmentationOptions(MinimumZeroCrossingRate: 0));
        RecognitionResult? result = null;
        session.ResultAvailable += (_, value) => result = value;
        await session.StartAsync(SourceLanguage.Japanese, CancellationToken.None);
        Assert.True(session.TryEnqueue(CreateFloatChunk(
            Enumerable.Repeat(0.2f, 16_000 * 2).Concat(new float[16_000]).ToArray())));

        await session.StopAsync(true).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SourceLanguage.Japanese, recognizer.LastLanguage);
        Assert.NotNull(result);
        Assert.Equal("Japanese", result.Language);
        Assert.Equal(AsrEngine.SenseVoice, result.AsrEngine);
    }

    [Fact]
    public async Task CompareMode_AssociatesPrimaryAndComparisonResultsWithoutReplacingPrimary()
    {
        var primary = new FakeRecognizer { ResultText = "primary", Engine = AsrEngine.SenseVoice };
        var comparison = new FakeRecognizer { ResultText = "comparison", Engine = AsrEngine.Whisper };
        await using var session = new TranscriptionSession(CaptureSource.System, primary,
            NullLogger<TranscriptionSession>.Instance, new SpeechSegmentationOptions(MinimumZeroCrossingRate: 0), comparison);
        var results = new List<RecognitionResult>();
        session.ResultAvailable += (_, value) => results.Add(value);
        await session.StartAsync(SourceLanguage.Japanese, CancellationToken.None);
        session.TryEnqueue(CreateFloatChunk(Enumerable.Repeat(0.2f, 16_000 * 2).Concat(new float[16_000]).ToArray()));
        await session.StopAsync(true).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.AsrEngine == AsrEngine.SenseVoice && !x.IsComparison && x.Text == "primary");
        Assert.Contains(results, x => x.AsrEngine == AsrEngine.Whisper && x.IsComparison && x.Text == "comparison");
    }

    [Fact]
    public async Task Session_PublishesMoviePipelineDiagnostics()
    {
        var recognizer = new FakeRecognizer();
        var options = SegmentationProfiles.For(CaptureMode.Movie, CaptureSource.System, movieDebugMode: true);
        await using var session = new TranscriptionSession(CaptureSource.System, recognizer,
            NullLogger<TranscriptionSession>.Instance, options);
        TranscriptionDiagnostics? latest = null;
        session.DiagnosticsChanged += (_, value) => latest = value;
        await session.StartAsync(SourceLanguage.Japanese, CancellationToken.None);
        Assert.True(session.TryEnqueue(CreateFloatChunk(Enumerable.Repeat(0.1f, 16_000 * 3).ToArray())));

        await session.StopAsync(true).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(latest);
        Assert.Equal(1, latest.CapturedAudioChunks);
        Assert.True(latest.SpeechSegmentsDetected >= 1);
        Assert.True(latest.SubmittedSegments >= 1);
        Assert.Equal(latest.SubmittedSegments, latest.CompletedRecognitions);
        Assert.Equal(SourceLanguage.Japanese, recognizer.LastLanguage);
    }

    private static AudioChunk CreateFloatChunk(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new AudioChunk(CaptureSource.System, bytes, 16_000, 1, 32, AudioSampleEncoding.IeeeFloat);
    }

    private sealed class FakeRecognizer : ISpeechRecognitionService
    {
        public AsrEngine Engine { get; init; } = AsrEngine.SenseVoice;
        public bool IsInitialized => true;
        public string ResultText { get; init; } = string.Empty;
        public SourceLanguage? LastLanguage { get; private set; }
        public Task<TimeSpan> InitializeAsync(SenseVoiceModelFiles files, SourceLanguage language, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
        public Task<string> RecognizeAsync(float[] samples, SourceLanguage language, CancellationToken cancellationToken)
        {
            LastLanguage = language;
            return Task.FromResult(ResultText);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
