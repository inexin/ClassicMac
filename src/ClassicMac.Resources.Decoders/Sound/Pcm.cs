using System;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>
    /// Uncompressed sample formats (<i>Inside Macintosh: Sound</i> and the Sound Manager 3 format list), turned into
    /// WAV's: 8-bit unsigned, larger integers signed little-endian, floats IEEE little-endian.
    /// </summary>
    internal static class Pcm
    {
        private static readonly FourCC Raw = FourCC.FromString("raw "), Twos = FourCC.FromString("twos"), Sowt = FourCC.FromString("sowt"),
            In24 = FourCC.FromString("in24"), In32 = FourCC.FromString("in32"), Fl32 = FourCC.FromString("fl32"), Fl64 = FourCC.FromString("fl64");

        /// <summary>Bytes per sample of an uncompressed <paramref name="format"/>, or 0 for a codec.</summary>
        public static int BytesPerSample(FourCC format, int sampleSize)
        {
            // The Sound Manager reads 'raw ' and 'twos' as 8-bit when the header says 8 and as 16-bit otherwise, and 'sowt'
            // always as 16-bit (disassembly of Sound Manager 3.5.1); the headers refuse sizes other than 8 and 16.
            if (format == Sowt) return 2;
            if (format == Raw || format == Twos) return sampleSize == 8 ? 1 : 2;
            if (format == In24) return 3;
            if (format == In32 || format == Fl32) return 4;
            if (format == Fl64) return 8;
            return 0;
        }

        /// <summary>Whether the format holds floating-point samples.</summary>
        public static bool IsFloat(FourCC format) => format == Fl32 || format == Fl64;

        /// <summary>The samples in WAV's layout, or null when <paramref name="sound"/> is compressed.</summary>
        public static byte[]? ToWav(SampledSound sound)
        {
            var width = BytesPerSample(sound.Format, sound.SampleSize);
            if (width == 0) return null;
            var data = sound.Data.Span;
            var whole = data.Length / (width * sound.Channels) * width * sound.Channels;
            var output = data[..whole].ToArray();
            var f = sound.Format;
            if (width == 1)
            {
                // WAV's 8-bit samples are unsigned, as 'raw ' (offset binary) is; 'twos' and 'sowt' are signed.
                if (f != Raw) for (var i = 0; i < output.Length; i++) output[i] ^= 0x80;
            }
            else if (f != Sowt)
            {
                for (var i = 0; i < output.Length; i += width) output.AsSpan(i, width).Reverse();
            }
            return output;
        }
    }
}
