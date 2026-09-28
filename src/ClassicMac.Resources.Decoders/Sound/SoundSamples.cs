using System;
using System.Buffers.Binary;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>A sound's samples as floats in [-1, 1], channels interleaved, for playing or drawing.</summary>
    /// <param name="Samples">The samples, frame by frame.</param>
    /// <param name="Channels">Channels per frame.</param>
    /// <param name="SampleRate">Frames per second (exact, not rounded).</param>
    public sealed record DecodedSound(float[] Samples, int Channels, double SampleRate)
    {
        /// <summary>Frames (samples per channel).</summary>
        public int Frames => Samples.Length / Channels;

        /// <summary>The length in seconds.</summary>
        public double Duration => SampleRate > 0 ? Frames / SampleRate : 0;
    }

    /// <summary>Decodes a <see cref="SampledSound"/>'s samples.</summary>
    public static class SoundSamples
    {
        /// <summary>The samples of <paramref name="sound"/>, or null when its format is not read.</summary>
        public static DecodedSound? Decode(SampledSound sound)
        {
            ArgumentNullException.ThrowIfNull(sound);
            if (SoundCodecs.ToWav(sound) is not { } decoded) return null;
            var (wav, width, isFloat) = decoded;
            var samples = new float[wav.Length / width];
            var span = wav.AsSpan();
            for (var i = 0; i < samples.Length; i++)
            {
                var s = span.Slice(i * width, width);
                samples[i] = width switch
                {
                    1 => (s[0] - 128) / 128f,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    3 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
                    4 when isFloat => BinaryPrimitives.ReadSingleLittleEndian(s),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                    _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                };
            }
            return new DecodedSound(samples, sound.Channels, sound.SampleRate);
        }
    }
}
