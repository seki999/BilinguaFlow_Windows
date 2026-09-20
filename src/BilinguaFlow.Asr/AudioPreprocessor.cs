using BilinguaFlow.Core.Audio;

namespace BilinguaFlow.Asr;

public sealed class AudioPreprocessor
{
    public const int TargetSampleRate = 16_000;

    public float[] ConvertToMono16Khz(AudioChunk chunk)
    {
        if (chunk.SampleRate <= 0 || chunk.Channels <= 0)
            throw new NotSupportedException("The audio format has an invalid sample rate or channel count.");

        var mono = DecodeMono(chunk);
        return chunk.SampleRate == TargetSampleRate ? mono : ResampleLinear(mono, chunk.SampleRate, TargetSampleRate);
    }

    private static float[] DecodeMono(AudioChunk chunk)
    {
        var bytesPerSample = chunk.BitsPerSample / 8;
        if (bytesPerSample <= 0 || chunk.Data.Length % (bytesPerSample * chunk.Channels) != 0)
            throw new NotSupportedException("The audio buffer does not match its declared format.");

        var frames = chunk.Data.Length / bytesPerSample / chunk.Channels;
        var output = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < chunk.Channels; channel++)
            {
                var offset = (frame * chunk.Channels + channel) * bytesPerSample;
                sum += DecodeSample(chunk.Data.AsSpan(offset, bytesPerSample), chunk.Encoding, chunk.BitsPerSample);
            }
            output[frame] = Math.Clamp((float)(sum / chunk.Channels), -1, 1);
        }
        return output;
    }

    private static float DecodeSample(ReadOnlySpan<byte> bytes, AudioSampleEncoding encoding, int bits)
    {
        if (encoding == AudioSampleEncoding.IeeeFloat && bits == 32) return BitConverter.ToSingle(bytes);
        if (encoding != AudioSampleEncoding.Pcm) throw new NotSupportedException($"Unsupported audio encoding: {encoding} {bits}-bit.");
        return bits switch
        {
            16 => BitConverter.ToInt16(bytes) / 32768f,
            24 => DecodePcm24(bytes) / 8388608f,
            32 => BitConverter.ToInt32(bytes) / 2147483648f,
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bits}.")
        };
    }

    private static int DecodePcm24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    public static float[] ResampleLinear(ReadOnlySpan<float> input, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (input.IsEmpty) return [];
        if (sourceRate == targetRate) return input.ToArray();
        var outputLength = Math.Max(1, (int)Math.Round(input.Length * (double)targetRate / sourceRate));
        var output = new float[outputLength];
        var ratio = (double)sourceRate / targetRate;
        for (var i = 0; i < output.Length; i++)
        {
            var position = i * ratio;
            var left = Math.Min((int)position, input.Length - 1);
            var right = Math.Min(left + 1, input.Length - 1);
            var fraction = (float)(position - left);
            output[i] = input[left] + (input[right] - input[left]) * fraction;
        }
        return output;
    }
}
