using System;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>A sound's samples in WAV's layout.</summary>
    /// <param name="Data">The samples, little-endian, channels interleaved.</param>
    /// <param name="Width">Bytes per sample.</param>
    /// <param name="IsFloat">Whether they are IEEE floats.</param>
    internal readonly record struct WavSamples(byte[] Data, int Width, bool IsFloat);

    /// <summary>
    /// Every sample format the decoder reads: uncompressed PCM, and the codecs of the Mac OS 9 Sound Manager (MACE 3:1
    /// and 6:1, IMA 4:1, µ-law).
    /// </summary>
    internal static class SoundCodecs
    {
        public static readonly FourCC Mace3 = FourCC.FromString("MAC3"), Mace6 = FourCC.FromString("MAC6"),
            Ima4 = FourCC.FromString("ima4"), MuLaw = FourCC.FromString("ulaw");

        /// <summary>
        /// Bytes per packet per channel, and samples a packet gives, for the codecs whose headers count packets; one
        /// byte and one sample for µ-law; null for anything else.
        /// </summary>
        public static (int Bytes, int Samples)? Packet(FourCC format) =>
            format == Mace3 ? (2, 6) : format == Mace6 ? (1, 6) : format == Ima4 ? (34, 64) : format == MuLaw ? (1, 1) : null;

        /// <summary>Bits per sample the codec gives (MACE gives 8, as the Sound Manager does).</summary>
        public static int OutputSize(FourCC format) => format == Mace3 || format == Mace6 ? 8 : 16;

        /// <summary>The samples of <paramref name="sound"/> for a WAV, or null for a format not read.</summary>
        public static WavSamples? ToWav(SampledSound sound)
        {
            var f = sound.Format;
            if (Pcm.ToWav(sound) is { } pcm)
            {
                return new WavSamples(pcm, Pcm.BytesPerSample(f, sound.SampleSize), Pcm.IsFloat(f));
            }

            if (f == Mace3 || f == Mace6)
            {
                return new WavSamples(Mace.Decode(sound.Data.Span, sound.Channels, f == Mace6), 1, false);
            }

            if (f == Ima4)
            {
                return new WavSamples(Little(Sound.Ima4.Decode(sound.Data, sound.Channels)), 2, false);
            }

            if (f == MuLaw)
            {
                var data = sound.Data.Span;
                var samples = new short[data.Length / sound.Channels * sound.Channels];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = MuLawSample(data[i]);
                }

                return new WavSamples(Little(samples), 2, false);
            }
            return null;
        }

        private static byte[] Little(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)samples[i];
                bytes[i * 2 + 1] = (byte)(samples[i] >> 8);
            }
            return bytes;
        }

        // ITU-T G.711 µ-law expansion.
        private static short MuLawSample(byte code)
        {
            var u = ~code & 0xFF;
            var magnitude = (((u & 0x0F) << 3) + 0x84) << ((u >> 4) & 7);
            return (short)((u & 0x80) != 0 ? 0x84 - magnitude : magnitude - 0x84);
        }
    }
}
