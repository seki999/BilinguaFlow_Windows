using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;

namespace BilinguaFlow.Core.Tests;

public sealed class AudioPreprocessorTests
{
    [Fact]
    public void ConvertToMono16Khz_AveragesStereoPcm16()
    {
        short[] samples = [short.MaxValue, short.MinValue, 16384, 16384];
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        var chunk = new AudioChunk(CaptureSource.Microphone, bytes, 16_000, 2, 16, AudioSampleEncoding.Pcm);

        var result = new AudioPreprocessor().ConvertToMono16Khz(chunk);

        Assert.Equal(2, result.Length);
        Assert.InRange(result[0], -0.001f, 0.001f);
        Assert.InRange(result[1], 0.499f, 0.501f);
    }

    [Fact]
    public void ResampleLinear_Converts48KhzLengthTo16Khz()
    {
        var input = Enumerable.Repeat(0.25f, 4_800).ToArray();
        var result = AudioPreprocessor.ResampleLinear(input, 48_000, 16_000);
        Assert.Equal(1_600, result.Length);
        Assert.All(result, sample => Assert.Equal(0.25f, sample));
    }
}
