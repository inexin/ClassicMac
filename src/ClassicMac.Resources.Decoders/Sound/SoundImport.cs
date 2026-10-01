using System;
using System.Buffers.Binary;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>
    /// Makes <c>snd </c> resources: a format 1 resource with the sampled-sound synthesizer and one <c>bufferCmd</c>
    /// pointing to the sound header (as <i>Inside Macintosh: Sound</i> lays out a sampled sound), from samples or from
    /// a WAV file.
    /// </summary>
    public static class SoundImport
    {
        private const int SampledSynth = 5;
        private const uint InitMono = 0x80, InitStereo = 0xC0;
        private const int HeaderAt = 20;

        /// <summary>
        /// A <c>snd </c> resource from samples in the Sound Manager's order: 8-bit offset binary or 16-bit big-endian
        /// two's complement, channels interleaved. Mono 8-bit sound gets a standard header, other sound an extended one.
        /// </summary>
        /// <param name="samples">The samples.</param>
        /// <param name="channels">Channels (1 to 64).</param>
        /// <param name="sampleSize">8 or 16.</param>
        /// <param name="sampleRate">Frames per second, below 65536 (a Fixed).</param>
        /// <param name="loopStart">The loop's first frame (0 for no loop).</param>
        /// <param name="loopEnd">The frame after the loop (0 for no loop).</param>
        /// <param name="baseNote">The MIDI note the samples sound at (60, middle C, by default).</param>
        public static byte[] Write(ReadOnlySpan<byte> samples, int channels, int sampleSize, double sampleRate,
            uint loopStart = 0, uint loopEnd = 0, byte baseNote = 60)
        {
            if (channels is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(channels), "A sound has 1 to 64 channels.");
            if (sampleSize is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(sampleSize), "The Sound Manager plays 8- and 16-bit samples.");
            if (!(sampleRate > 0 && sampleRate < 65536)) throw new ArgumentOutOfRangeException(nameof(sampleRate), "The sample rate must be below 65536 Hz.");
            int frameBytes = channels * sampleSize / 8;
            uint frames = (uint)(samples.Length / frameBytes);
            samples = samples[..(int)(frames * frameBytes)];
            bool standard = channels == 1 && sampleSize == 8;
            uint rate = (uint)Math.Round(sampleRate * 65536);

            var w = new BigEndianWriter();
            w.WriteUInt16(1);                                        // format 1
            w.WriteUInt16(1);                                        // one synthesizer
            w.WriteUInt16(SampledSynth);
            w.WriteUInt32(channels == 2 ? InitStereo : InitMono);
            w.WriteUInt16(1);                                        // one command
            w.WriteUInt16(0x8000 | SoundCommand.BufferCmd);
            w.WriteUInt16(0);
            w.WriteUInt32(HeaderAt);

            w.WriteUInt32(0);                                        // samplePtr: the samples follow
            w.WriteUInt32(standard ? frames : (uint)channels);       // length, or numChannels
            w.WriteUInt32(rate);
            w.WriteUInt32(loopStart);
            w.WriteUInt32(loopEnd);
            w.WriteByte(standard ? 0x00 : 0xFF);             // encode: stdSH or extSH
            w.WriteByte(baseNote);
            if (!standard)
            {
                w.WriteUInt32(frames);
                WriteExtended80(w, sampleRate);                      // AIFFSampleRate
                w.WriteUInt32(0);                                    // markerChunk
                w.WriteUInt32(0);                                    // instrumentChunks
                w.WriteUInt32(0);                                    // AESRecording
                w.WriteUInt16(sampleSize);
                w.WriteUInt16(0); w.WriteUInt32(0); w.WriteUInt32(0); w.WriteUInt32(0); // futureUse1-4
            }
            w.WriteBytes(samples);
            return w.ToArray();
        }

        /// <summary>
        /// A <c>snd </c> resource from a RIFF WAVE file: 8-bit PCM stays 8-bit, deeper PCM and float become 16-bit; the
        /// first loop and the unity note of a <c>smpl</c> chunk become the header's loop and base note.
        /// </summary>
        /// <exception cref="InvalidDataException">The file is not a WAV file of PCM or float samples.</exception>
        public static byte[] FromWav(ReadOnlySpan<byte> wav)
        {
            if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Not a RIFF WAVE file.");
            int format = 0, channels = 0, bits = 0;
            uint rate = 0;
            ReadOnlySpan<byte> data = default;
            bool hasData = false;
            uint loopStart = 0, loopEnd = 0;
            byte note = 60;
            for (int at = 12; at + 8 <= wav.Length;)
            {
                var id = wav.Slice(at, 4);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]);
                int body = at + 8;
                int length = (int)Math.Min(size, wav.Length - body);
                var chunk = wav.Slice(body, length);
                if (id.SequenceEqual("fmt "u8) && length >= 16)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                    rate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                    if (format == 0xFFFE && length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[24..]);
                }
                else if (id.SequenceEqual("data"u8))
                {
                    data = chunk;
                    hasData = true;
                }
                else if (id.SequenceEqual("smpl"u8) && length >= 36)
                {
                    note = (byte)Math.Min(127u, BinaryPrimitives.ReadUInt32LittleEndian(chunk[12..]));
                    if (BinaryPrimitives.ReadUInt32LittleEndian(chunk[28..]) > 0 && length >= 60)
                    {
                        loopStart = BinaryPrimitives.ReadUInt32LittleEndian(chunk[44..]);
                        loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(chunk[48..]) + 1;   // smpl's end is inclusive
                    }
                }
                at = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
            }
            if (!hasData || channels == 0) throw new InvalidDataException("The WAV file has no 'fmt ' or 'data' chunk.");
            bool isFloat = format == 3;
            if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(isFloat && bits is 32 or 64))
                throw new InvalidDataException($"WAV format {format} with {bits}-bit samples is not PCM or float.");

            int width = bits / 8, count = data.Length / width;
            byte[] samples;
            if (bits == 8)
            {
                samples = data[..count].ToArray();                   // WAV's 8-bit is offset binary too
            }
            else
            {
                var output = new BigEndianWriter(count * 2);
                for (int i = 0; i < count; i++)
                {
                    var s = data.Slice(i * width, width);
                    int value = bits switch
                    {
                        16 => BinaryPrimitives.ReadInt16LittleEndian(s),
                        24 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) >> 16,
                        32 when !isFloat => BinaryPrimitives.ReadInt32LittleEndian(s) >> 16,
                        32 => FromFloat(BinaryPrimitives.ReadSingleLittleEndian(s)),
                        _ => FromFloat(BinaryPrimitives.ReadDoubleLittleEndian(s)),
                    };
                    output.WriteInt16(value);
                }
                samples = output.ToArray();
            }
            if (loopEnd <= loopStart) (loopStart, loopEnd) = (0, 0);
            return Write(samples, channels, bits == 8 ? 8 : 16, rate, loopStart, loopEnd, note);
        }

        private static int FromFloat(double v) => (int)Math.Clamp(Math.Round(v * 32768), short.MinValue, short.MaxValue);

        // An IEEE 754 80-bit extended number (AIFF's sample rate), big-endian.
        private static void WriteExtended80(BigEndianWriter w, double value)
        {
            if (value <= 0)
            {
                w.WriteZeros(10);
                return;
            }
            int exponent = (int)Math.Floor(Math.Log2(value));
            ulong mantissa = (ulong)Math.Round(value / Math.Pow(2, exponent - 63));
            w.WriteUInt16(exponent + 16383);
            w.WriteUInt64(mantissa);
        }
    }
}
