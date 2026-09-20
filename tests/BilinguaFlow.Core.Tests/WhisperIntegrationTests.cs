using BilinguaFlow.Asr;
using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Core.Tests;

public sealed class WhisperIntegrationTests
{
    [Theory]
    [InlineData(SourceLanguage.Japanese, "ja")]
    [InlineData(SourceLanguage.English, "en")]
    public void LanguageMapping_IsExplicit(SourceLanguage language, string expected) =>
        Assert.Equal(expected, WhisperSpeechRecognitionService.LanguageCode(language));

    [Fact]
    public void ModelLocator_ReturnsExpectedPathWhenModelIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = WhisperModelLocator.Locate(root);
        Assert.EndsWith(Path.Combine("models", "whisper", "ggml-small.bin"), path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Settings_DefaultToResponsiveCpuAndRequestedSegmentLengths()
    {
        var settings = new WhisperSettings("model.bin");
        Assert.InRange(settings.EffectiveThreads, 2, Math.Max(2, Environment.ProcessorCount));
        Assert.Equal(6_000, settings.PreferredSegmentMilliseconds);
        Assert.Equal(12_000, settings.MaximumSegmentMilliseconds);
    }
}
