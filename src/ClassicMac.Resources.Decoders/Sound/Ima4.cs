using System;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>
    /// IMA 4:1 (<c>ima4</c>) as the Mac OS 9 Sound Manager's decompressor expands it (disassembly of its <c>sdec</c>
    /// <c>ima4</c> component, 68k and PowerPC; checked against Sound Manager 3.5.1's output on harness samples). Packets
    /// of 34 bytes per channel, channels alternating by packet: a 2-byte preamble (predictor in the upper 9 bits, step
    /// index in the low 7) and 64 samples, low nibble first. The standard IMA tables, the difference built by shifts
    /// that truncate. The decompressor works in batches of 16 packets and looks at a preamble only at the start of a
    /// batch, and only when it differs from the running state. Output is 16-bit signed; a partial packet is dropped.
    /// </summary>
    internal static class Ima4
    {
        private const int PacketBytes = 34, PacketSamples = 64, Batch = 16;

        private static readonly short[] Steps =
        [
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
            22385, 24623, 27086, 29794, 32767,
        ];

        private static readonly sbyte[] IndexChange = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

        /// <summary>Expands <paramref name="data"/> to 16-bit samples, channels interleaved.</summary>
        public static short[] Decode(ReadOnlyMemory<byte> data, int channels)
        {
            var packets = data.Length / (PacketBytes * channels);
            var output = new short[packets * PacketSamples * channels];
            for (var c = 0; c < channels; c++)
            {
                int predictor = 0, index = 0;
                for (var p = 0; p < packets; p++)
                {
                    var packet = data.Slice((p * channels + c) * PacketBytes, PacketBytes);
                    if (p % Batch == 0)
                    {
                        var word = new BigEndianReader(packet).ReadUInt16At(0);
                        var start = (short)(word & 0xFF80);
                        var startIndex = word & 0x7F;
                        if (startIndex != index || Math.Abs(start - predictor) > 0x7F)
                        {
                            predictor = start;
                            index = startIndex;
                        }
                    }
                    var at = p * PacketSamples * channels + c;
                    var bytes = packet.Span;
                    for (var i = 2; i < PacketBytes; i++)
                    {
                        Nibble(bytes[i] & 0x0F, ref predictor, ref index, output, ref at, channels);
                        Nibble(bytes[i] >> 4, ref predictor, ref index, output, ref at, channels);
                    }
                }
            }
            return output;
        }

        // A preamble's index above 88 would read past Apple's table; it is held at 88 here.
        private static void Nibble(int n, ref int predictor, ref int index, short[] output, ref int at, int channels)
        {
            int step = Steps[Math.Min(index, 88)];
            var d = step >> 3;
            if ((n & 4) != 0)
            {
                d += step;
            }

            if ((n & 2) != 0)
            {
                d += step >> 1;
            }

            if ((n & 1) != 0)
            {
                d += step >> 2;
            }

            predictor = Math.Clamp(predictor + ((n & 8) != 0 ? -d : d), -32768, 32767);
            index = Math.Clamp(index + IndexChange[n], 0, 88);
            output[at] = (short)predictor;
            at += channels;
        }
    }
}
