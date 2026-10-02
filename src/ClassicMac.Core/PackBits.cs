using System;
using System.Collections.Generic;

namespace ClassicMac.Core
{
    /// <summary>How <see cref="PackBits.Unpack"/> reads its input.</summary>
    public readonly record struct PackBitsOptions
    {
        /// <summary>A PackBits options value: byte units, the flag −128 a no-op.</summary>
        public PackBitsOptions()
        {
        }

        /// <summary>
        /// The unit runs count: 1 byte, or 2 for word packing (a picture's 16-bit pixels, packType 3).
        /// </summary>
        public int UnitSize { get; init; } = 1;

        /// <summary>
        /// Whether the flag −128 ($80) is a run of 129 copies of the next unit, as Mac OS 9's QuickDraw reads picture scan
        /// lines; otherwise it is a no-op, as Technical Note 1023 and the 68k ROM have it.
        /// </summary>
        public bool Flag80IsRun { get; init; }
    }

    /// <summary>Why <see cref="PackBits.Unpack"/> stopped.</summary>
    public enum PackBitsEnd
    {
        /// <summary>Every input byte was decoded.</summary>
        InputUsed,

        /// <summary>The output filled at a run's end; the input may go on.</summary>
        OutputFull,

        /// <summary>A literal block needs more input than is left; what there was is copied.</summary>
        LiteralPastInput,

        /// <summary>A repeat run has no unit to repeat.</summary>
        RepeatPastInput,

        /// <summary>A literal block reaches past the output; it is cut there.</summary>
        LiteralPastOutput,

        /// <summary>A repeat run reaches past the output; it is cut there, at a whole unit.</summary>
        RepeatPastOutput,
    }

    /// <summary>What <see cref="PackBits.Unpack"/> did.</summary>
    /// <param name="Read">The input bytes used.</param>
    /// <param name="Written">The output bytes written.</param>
    /// <param name="End">Why decoding stopped.</param>
    public readonly record struct PackBitsResult(int Read, int Written, PackBitsEnd End);

    /// <summary>
    /// Apple's PackBits run-length code (Technical Note 1023; docs/formats/codecs/packbits.md): a flag byte n, then n + 1
    /// literal units when n ≥ 0, or one unit written 1 − n times when n &lt; 0. Shared by pictures, MacPaint documents,
    /// QuickTime's planar codec and StuffIt's method 6; each caller decides what a stop short of its data means.
    /// </summary>
    public static class PackBits
    {
        /// <summary>
        /// Decodes <paramref name="source"/> into <paramref name="destination"/> until the input is used up, the output is
        /// full, or a run cannot be completed (<see cref="PackBitsResult.End"/>). Bytes not written are left as they are.
        /// </summary>
        public static PackBitsResult Unpack(ReadOnlySpan<byte> source, Span<byte> destination, PackBitsOptions? options = null)
        {
            var o = options ?? new PackBitsOptions();
            int unit = Unit(o.UnitSize);
            int read = 0, written = 0;
            while (read < source.Length)
            {
                if (written == destination.Length) return new(read, written, PackBitsEnd.OutputFull);
                int flag = unchecked((sbyte)source[read++]);
                if (flag == -128 && !o.Flag80IsRun) continue;
                if (flag >= 0)
                {
                    int length = (flag + 1) * unit;
                    int available = Math.Min(length, source.Length - read);
                    int room = destination.Length - written;
                    int copy = Math.Min(available, room);
                    source.Slice(read, copy).CopyTo(destination[written..]);
                    read += copy;
                    written += copy;
                    if (available < length && copy == available) return new(source.Length, written, PackBitsEnd.LiteralPastInput);
                    if (copy < length) return new(read, written, PackBitsEnd.LiteralPastOutput);
                    continue;
                }
                if (source.Length - read < unit) return new(source.Length, written, PackBitsEnd.RepeatPastInput);
                var value = source.Slice(read, unit);
                read += unit;
                int count = 1 - flag;
                for (int i = 0; i < count; i++)
                {
                    // Only whole units are repeated: a word that does not fit is left out, as QuickDraw's scan-line reader does.
                    if (destination.Length - written < unit) return new(read, written, PackBitsEnd.RepeatPastOutput);
                    value.CopyTo(destination[written..]);
                    written += unit;
                }
            }
            return new(read, written, PackBitsEnd.InputUsed);
        }

        /// <summary>
        /// Packs <paramref name="data"/> as ClassicMac's picture writer does (packbits.md §3): runs of 3 or more equal
        /// units (2 or more when packing words) as (1 − count, unit), everything else in literal blocks (count − 1, units),
        /// a block ending where a run of 3 begins; at most 128 units in a run or block; the flag −128 is never written.
        /// A trailing part of a word is not packed.
        /// </summary>
        public static byte[] Pack(ReadOnlySpan<byte> data, int unitSize = 1)
        {
            int unit = Unit(unitSize);
            var output = new List<byte>(data.Length + data.Length / 64 + 2);
            int n = data.Length / unit;
            int i = 0;
            while (i < n)
            {
                int run = 1;
                while (i + run < n && run < 128 && Same(data, unit, i, i + run)) run++;
                if (run >= 3 || (run == 2 && unit == 2))
                {
                    output.Add((byte)(1 - run));
                    output.AddRange(data.Slice(i * unit, unit));
                    i += run;
                    continue;
                }
                int start = i++;
                while (i < n && i - start < 128 && !(i + 2 < n && Same(data, unit, i, i + 1) && Same(data, unit, i, i + 2))) i++;
                output.Add((byte)(i - start - 1));
                output.AddRange(data.Slice(start * unit, (i - start) * unit));
            }
            return output.ToArray();
        }

        private static bool Same(ReadOnlySpan<byte> data, int unit, int a, int b) =>
            data.Slice(a * unit, unit).SequenceEqual(data.Slice(b * unit, unit));

        private static int Unit(int unitSize) =>
            unitSize is 1 or 2 ? unitSize : throw new ArgumentOutOfRangeException(nameof(unitSize), unitSize, "A PackBits unit is 1 or 2 bytes.");
    }
}
