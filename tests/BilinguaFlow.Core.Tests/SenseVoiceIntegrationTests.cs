using BilinguaFlow.Asr;
using BilinguaFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BilinguaFlow.Core.Tests;

public sealed class SenseVoiceIntegrationTests
{
    [Fact]
    [Trait("Category", "ModelIntegration")]
    public async Task LocalModel_InitializesAndDecodesSilence_WhenInstalled()
    {
        var files = SenseVoiceModelLocator.Locate(AppContext.BaseDirectory);
        if (!files.Exists) return;

        await using var service = new SenseVoiceSpeechRecognitionService(NullLogger<SenseVoiceSpeechRecognitionService>.Instance);
        var initializationTime = await service.InitializeAsync(files, SourceLanguage.English, CancellationToken.None);
        var text = await service.RecognizeAsync(new float[16_000], SourceLanguage.English, CancellationToken.None);

        Assert.True(service.IsInitialized);
        Assert.True(initializationTime > TimeSpan.Zero);
        Assert.NotNull(text); // Some SenseVoice builds return a non-speech event token for silence.
    }
}
