using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;
using BilinguaFlow.Core.Speech;
using BilinguaFlow.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilinguaFlow.Core.Tests;

public sealed class LlmPipelineTests
{
    private static readonly QwenSettings Settings = new("missing.gguf", RecentContextCount: 3, LlmQueueCapacity: 2);

    [Fact]
    public void Profiles_ContainRequiredMovieAndLanguageSpecificMeetingOptions()
    {
        Assert.Equal("General Movie", ContextProfiles.GeneralMovie.Name);
        Assert.Equal(SourceLanguage.Japanese, ContextProfiles.JapaneseItMeeting.Language);
        Assert.Contains("Terraform", ContextProfiles.JapaneseItMeeting.Keywords);
        Assert.Equal(SourceLanguage.English, ContextProfiles.EnglishItMeeting.Language);
    }

    [Fact]
    public void Prompt_ContainsProfileLanguageContextAndJsonContract()
    {
        var prompt = new QwenPromptBuilder(Settings).Build(Request("現在の発話", SourceLanguage.Japanese,
            ["one", "two", "three", "four"]));

        Assert.Contains("source language is Japanese", prompt);
        Assert.Contains("Terraform", prompt);
        Assert.DoesNotContain("1. one", prompt);
        Assert.Contains("1. two", prompt);
        Assert.Contains("Current ASR: 現在の発話", prompt);
        Assert.Contains("/no_think", prompt);
        Assert.Contains("correctedText", prompt);
    }

    [Theory]
    [InlineData("{\"correctedText\":\"Terraform\",\"translatedText\":\"Terraform 配置\"}")]
    [InlineData("text```json\n{\"correctedText\":\"A\",\"translatedText\":\"甲\"}\n```tail")]
    public void Parser_AcceptsJsonAndFencedJson(string output)
    {
        Assert.True(LlmJsonParser.TryParse(output, out var result));
        Assert.False(string.IsNullOrWhiteSpace(result!.CorrectedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"correctedText\":\"x\"}")]
    public void Parser_RejectsMalformedOutput(string output) => Assert.False(LlmJsonParser.TryParse(output, out _));

    [Fact]
    public void ModelLocator_ReturnsExpectedMissingPathWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = QwenModelLocator.Locate(root);
        Assert.EndsWith(Path.Combine("models", "qwen", QwenModelLocator.FileName), path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task QueueOverflow_DropsOldestAndKeepsAsrFallback()
    {
        var service = new BlockingLlmService();
        await using var worker = new LlmTranslationWorker(service, Settings, NullLogger<LlmTranslationWorker>.Instance);
        var results = new List<LlmTranslationResult>();
        worker.ResultAvailable += (_, result) => { lock (results) results.Add(result); };
        worker.Start(CancellationToken.None);
        worker.TryEnqueue(Request("first", sequence: 1));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        worker.TryEnqueue(Request("second", sequence: 2));
        worker.TryEnqueue(Request("third", sequence: 3));
        worker.TryEnqueue(Request("fourth", sequence: 4));

        await Task.Delay(50);
        lock (results)
        {
            var dropped = Assert.Single(results, x => !x.Success);
            Assert.Equal(2, dropped.SequenceId);
            Assert.Equal("second", dropped.CorrectedText);
        }
        service.Release.TrySetResult();
        await worker.StopAsync();
    }

    [Fact]
    public async Task ClearContext_ClearsPendingQueueWithoutUnloadingService()
    {
        var service = new BlockingLlmService();
        await using var worker = new LlmTranslationWorker(service, Settings, NullLogger<LlmTranslationWorker>.Instance);
        worker.Start(CancellationToken.None);
        worker.TryEnqueue(Request("first", sequence: 1));
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        worker.TryEnqueue(Request("second", sequence: 2));
        worker.ClearContextAndQueue();
        service.Release.TrySetResult();
        await worker.StopAsync();
        Assert.True(service.IsReady);
        Assert.DoesNotContain(2, service.Processed);
    }

    [Fact]
    public async Task ServiceFailure_ReturnsRawAsrFallbackAndWorkerContinues()
    {
        await using var worker = new LlmTranslationWorker(new ThrowingLlmService(), Settings,
            NullLogger<LlmTranslationWorker>.Instance);
        var completion = new TaskCompletionSource<LlmTranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.ResultAvailable += (_, result) => completion.TrySetResult(result);
        worker.Start(CancellationToken.None);
        worker.TryEnqueue(Request("raw text"));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Success);
        Assert.Equal("raw text", result.CorrectedText);
        Assert.Empty(result.TranslatedText);
        await worker.StopAsync();
    }

    [Fact]
    public async Task RealQwen_TranslatesJapaneseAndEnglish_WhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("BILINGUAFLOW_RUN_QWEN_TEST") != "1") return;
        var modelPath = QwenModelLocator.Locate(AppContext.BaseDirectory);
        await using var service = new QwenLlamaService(new QwenSettings(modelPath, MaxOutputTokens: 128),
            NullLogger<QwenLlamaService>.Instance);
        await service.InitializeAsync(CancellationToken.None);

        var japanese = await service.CorrectAndTranslateAsync(Request("来週のリリースを確認します。", sequence: 10), CancellationToken.None);
        var english = await service.CorrectAndTranslateAsync(Request("We will review the release next week.",
            SourceLanguage.English, sequence: 11), CancellationToken.None);

        Assert.True(japanese.Success, japanese.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(japanese.TranslatedText));
        Assert.True(english.Success, english.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(english.TranslatedText));
    }

    private static LlmTranslationRequest Request(string text, SourceLanguage language = SourceLanguage.Japanese,
        IReadOnlyList<string>? recent = null, long sequence = 1) => new(sequence, text, language, CaptureMode.Meeting,
        CaptureSource.System, ContextProfiles.JapaneseItMeeting, "migration", recent ?? [], DateTimeOffset.Now,
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

    private sealed class BlockingLlmService : ILlmService
    {
        public bool IsReady => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<long> Processed { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<LlmTranslationResult> CorrectAndTranslateAsync(LlmTranslationRequest request, CancellationToken cancellationToken)
        {
            Processed.Add(request.SequenceId); Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(request.SequenceId, request.OriginalText, request.OriginalText, "译文", TimeSpan.Zero,
                TimeSpan.Zero, TimeSpan.Zero, false, true);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingLlmService : ILlmService
    {
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<LlmTranslationResult> CorrectAndTranslateAsync(LlmTranslationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("test failure");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
