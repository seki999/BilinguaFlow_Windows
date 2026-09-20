using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Core.Models;

namespace BilinguaFlow.Core.Tests;

public sealed class EnergyVadSegmenterTests
{
    private static readonly SpeechSegmentationOptions TestOptions = new(
        SampleRate: 1_000, FrameMilliseconds: 20, MinimumSpeechMilliseconds: 500,
        MinimumRecognitionSegmentMilliseconds: 1_200, EndSilenceMilliseconds: 900,
        MergeGapMilliseconds: 1_200, PreRollMilliseconds: 250, PostRollMilliseconds: 250,
        MaximumSegmentMilliseconds: 12_000, SpeechThreshold: 0.01f,
        MinimumZeroCrossingRate: 0, MaximumZeroCrossingRate: 1);

    [Fact]
    public void MovieProfile_HasStabilizedThresholds()
    {
        var profile = SegmentationProfiles.For(CaptureMode.Movie, CaptureSource.System);
        Assert.Equal(500, profile.MinimumSpeechMilliseconds);
        Assert.Equal(1_200, profile.MinimumRecognitionSegmentMilliseconds);
        Assert.Equal(900, profile.EndSilenceMilliseconds);
        Assert.Equal(1_200, profile.MergeGapMilliseconds);
        Assert.Equal(250, profile.PreRollMilliseconds);
        Assert.Equal(250, profile.PostRollMilliseconds);
        Assert.Equal(12_000, profile.MaximumSegmentMilliseconds);
    }

    [Fact]
    public void ShortSegment_IsBufferedInsteadOfRecognizedImmediately()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        var results = sut.Process(Join(Silence(250), Speech(600), Silence(1_000)));
        Assert.Empty(results);
    }

    [Fact]
    public void TwoNearbyShortSegments_AreMerged()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        Assert.Empty(sut.Process(Join(Silence(250), Speech(600), Silence(1_000))));

        var results = sut.Process(Join(Speech(600), Silence(1_000)));

        var segment = Assert.Single(results);
        Assert.Equal(2, segment.OriginalSegmentCount);
        Assert.True(segment.WasMerged);
        Assert.True(segment.Duration.TotalSeconds > 1.2);
    }

    [Fact]
    public void BufferedShortSegment_IsFlushedAfterMergeTimeout()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        Assert.Empty(sut.Process(Join(Silence(250), Speech(600), Silence(1_000))));

        var result = Assert.Single(sut.Process(Silence(300)));

        Assert.True(result.FlushedByTimeout);
        Assert.False(result.WasMerged);
    }

    [Fact]
    public void BufferedShortSegment_IsFlushedByWallClockWhenAudioCallbacksStop()
    {
        var clock = new ManualTimeProvider();
        var sut = new EnergyVadSegmenter(TestOptions, clock);
        Assert.Empty(sut.Process(Join(Silence(250), Speech(600), Silence(1_000))));

        clock.Advance(TimeSpan.FromMilliseconds(301));
        var result = Assert.Single(sut.FlushExpired());

        Assert.True(result.FlushedByTimeout);
    }

    [Fact]
    public void Segment_IncludesConfiguredPreRoll()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        var result = Assert.Single(sut.Process(Join(Silence(300), Speech(1_300), Silence(900))));
        Assert.All(result.Samples.Take(250), value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Segment_IncludesConfiguredPostRoll()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        var result = Assert.Single(sut.Process(Join(Silence(300), Speech(1_300), Silence(900))));
        Assert.All(result.Samples.TakeLast(250), value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Flush_EmitsUsefulBufferedSpeechOnStop()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        Assert.Empty(sut.Process(Join(Silence(250), Speech(600))));

        var result = Assert.Single(sut.Flush());

        Assert.True(result.FlushedByStop);
        Assert.Equal(1, result.OriginalSegmentCount);
    }

    [Fact]
    public void SpeechShorterThanMinimum_IsRejected()
    {
        var sut = new EnergyVadSegmenter(TestOptions);
        Assert.Empty(sut.Process(Join(Silence(250), Speech(400), Silence(1_300))));
        Assert.Empty(sut.Flush());
    }

    [Fact]
    public void LoudNonCrossingEffect_IsRejectedBySpeechHeuristic()
    {
        var sut = new EnergyVadSegmenter(new SpeechSegmentationOptions());
        var constantEffect = Enumerable.Repeat(0.5f, 16_000 * 2).ToArray();
        Assert.Empty(sut.Process(constantEffect));
        Assert.Empty(sut.Flush());
    }

    private static float[] Speech(int milliseconds) => Enumerable.Repeat(0.2f, milliseconds).ToArray();
    private static float[] Silence(int milliseconds) => new float[milliseconds];
    private static float[] Join(params float[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
