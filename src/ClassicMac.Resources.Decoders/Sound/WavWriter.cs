using System;
using System.Buffers.Binary;
using System.IO;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>A loop and the note samples sound at, for WAV's <c>smpl</c> chunk.</summary>
    /// <param name="MidiNote">The MIDI unity note (60 = middle C).</param>
    /// <param name="LoopStart">The first frame of the loop, or null for no loop.</param>
    /// <param name="LoopEnd">The last frame of the loop (inclusive).</param>
    internal readonly record struct SamplerInfo(int MidiNote, uint? LoopStart, uint LoopEnd);

    /// <summary>
    /// RIFF WAVE files (Microsoft's <i>Multimedia Programming Interface and Data Specifications 1.0</i> and the
    /// <c>WAVE_FORMAT_EXTENSIBLE</c> note): PCM or IEEE float, <c>WAVE_FORMAT_EXTENSIBLE</c> above 16 bits or 2 channels
    /// as the latter requires, a <c>fact</c> chunk for float, and a <c>smpl</c> chunk for a loop or base note.
    /// </summary>
    internal static class WavWriter
    {
        private static readonly byte[] SubFormatTail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

        public static byte[] Write(ReadOnlySpan<byte> samples, int channels, int bytesPerSample, bool isFloat, double sampleRate, SamplerInfo? sampler)
        {
            var rate = (uint)Math.Clamp(Math.Round(sampleRate), 1, uint.MaxValue);
            var blockAlign = channels * bytesPerSample;
            var frames = (uint)(samples.Length / blockAlign);
            var extensible = isFloat || bytesPerSample > 2 || channels > 2;

            using var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write("RIFF"u8);
            w.Write(0u); // patched below
            w.Write("WAVE"u8);

            w.Write("fmt "u8);
            w.Write(extensible ? 40u : 16u);
            w.Write((ushort)(extensible ? 0xFFFE : 1));
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write(rate * (uint)blockAlign);
            w.Write((ushort)blockAlign);
            w.Write((ushort)(bytesPerSample * 8));
            if (extensible)
            {
                w.Write((ushort)22);
                w.Write((ushort)(bytesPerSample * 8)); // valid bits
                w.Write(0u); // no speaker positions
                w.Write((ushort)(isFloat ? 3 : 1));
                w.Write(SubFormatTail);
            }
            if (isFloat)
            {
                w.Write("fact"u8);
                w.Write(4u);
                w.Write(frames);
            }

            if (sampler is { } s)
            {
                var loops = s.LoopStart is null ? 0u : 1u;
                w.Write("smpl"u8);
                w.Write(36 + loops * 24);
                w.Write(0u); // manufacturer
                w.Write(0u); // product
                w.Write((uint)Math.Round(1e9 / rate)); // sample period, ns
                w.Write((uint)s.MidiNote);
                w.Write(0u); // pitch fraction
                w.Write(0u); // SMPTE format
                w.Write(0u); // SMPTE offset
                w.Write(loops);
                w.Write(0u); // sampler data
                if (s.LoopStart is { } start)
                {
                    w.Write(0u); // cue point ID
                    w.Write(0u); // forward loop
                    w.Write(start);
                    w.Write(s.LoopEnd);
                    w.Write(0u); // fraction
                    w.Write(0u); // play forever
                }
            }

            w.Write("data"u8);
            w.Write((uint)samples.Length);
            w.Write(samples);
            if (samples.Length % 2 != 0)
            {
                w.Write((byte)0);
            }

            w.Flush();

            var bytes = stream.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
            return bytes;
        }
    }
}
