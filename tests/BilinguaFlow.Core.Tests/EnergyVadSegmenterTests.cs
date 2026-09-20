using BilinguaFlow.Asr;

namespace BilinguaFlow.Core.Tests;

public sealed class EnergyVadSegmenterTests
{
    [Fact]
    public void Process_EmitsSpeechAfterConfiguredSilence()
    {
        var sut = new EnergyVadSegmenter();
        var speech = Enumerable.Repeat(0.2f, 16_000 * 400 / 1000);
        var silence = Enumerable.Repeat(0f, 16_000 * 700 / 1000);

        var segments = sut.Process(speech.Concat(silence).ToArray());

        var segment = Assert.Single(segments);
        Assert.Equal(6_400, segment.Length);
    }

    [Fact]
    public void Flush_DiscardsSpeechShorterThanMinimum()
    {
        var sut = new EnergyVadSegmenter();
        sut.Process(Enumerable.Repeat(0.2f, 16_000 * 200 / 1000).ToArray());
        Assert.Null(sut.Flush());
    }
}
