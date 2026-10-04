using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc;

/// <summary>
/// The pattern-initialized data (pidata) unpacker: expands a PEF <see cref="PefSectionKind.PatternInitData"/>
/// section's contents into its initial image [Doc: Mac OS Runtime Architectures, ch. 8, "Pattern-Initialized Data"].
/// </summary>
public static class PatternData
{
    /// <summary>The longest image <see cref="Unpack"/> builds unless told otherwise (256 MB).</summary>
    public const int DefaultMaxLength = 1 << 28;

    private enum Step { Done, Truncated, TooLong }

    /// <summary>
    /// Unpacks <paramref name="packed"/>. Each instruction is an opcode byte (the opcode in bits 5–7, a count in bits
    /// 0–4; a count of 0 means the count follows as an argument) and its arguments, each a big-endian run of 7-bit
    /// groups, bit 7 set on all but the last:
    /// <list type="bullet">
    /// <item>0 Zero: <c>count</c> zero bytes.</item>
    /// <item>1 BlockCopy: the next <c>count</c> bytes.</item>
    /// <item>2 RepeatedBlock: argument <c>repeat</c>; the next <c>count</c> bytes, <c>repeat</c> + 1 times.</item>
    /// <item>3 InterleaveRepeatBlockWithBlockCopy: arguments <c>customSize</c> and <c>repeat</c>; a common block of
    /// <c>count</c> bytes, then <c>repeat</c> custom blocks of <c>customSize</c> bytes; out goes common, custom 1,
    /// common, custom 2 … common.</item>
    /// <item>4 InterleaveRepeatBlockWithZero: as 3 with a common block of <c>count</c> zero bytes that is not stored.</item>
    /// </list>
    /// Opcodes 5–7 are undefined. Damaged input (an undefined opcode, a run past the end, an image longer than
    /// <paramref name="maxLength"/>) is reported as a <c>pef.pidata-*</c> error and the image unpacked so far returned.
    /// </summary>
    /// <param name="packed">The section's contents.</param>
    /// <param name="diagnostics">Receives problems.</param>
    /// <param name="maxLength">The longest image to build; unpacking stops with an error past it.</param>
    public static byte[] Unpack(ReadOnlySpan<byte> packed, ICollection<Diagnostic> diagnostics, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var output = new BigEndianWriter(Math.Clamp(packed.Length * 2, 16, Math.Max(maxLength, 16)));
        int p = 0;
        while (p < packed.Length)
        {
            int at = p;
            byte b = packed[p++];
            int op = b >> 5;
            long count = b & 0x1F;
            if (op > 4)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.pidata-bad-opcode",
                    $"Pattern data opcode {op} (byte 0x{b:X2}) is undefined; unpacking stopped.", at));
                break;
            }
            var step = count == 0 && !TryArgument(packed, ref p, out count) ? Step.Truncated : op switch
            {
                0 => Zero(output, count, maxLength),
                1 => Copy(output, packed, ref p, count, maxLength),
                2 => RepeatedBlock(output, packed, ref p, count, maxLength),
                3 => Interleave(output, packed, ref p, count, withCommonBlock: true, maxLength),
                _ => Interleave(output, packed, ref p, count, withCommonBlock: false, maxLength),
            };
            if (step == Step.Truncated)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.pidata-truncated",
                    "A pattern data instruction runs past the end of the section; unpacking stopped.", at));
                break;
            }
            if (step == Step.TooLong)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.pidata-too-long",
                    $"Pattern data unpacks to more than {maxLength} bytes; unpacking stopped.", at));
                break;
            }
        }
        return output.ToArray();
    }

    // An argument: big-endian 7-bit groups, bit 7 set on every byte but the last. More than 32 bits is damage.
    private static bool TryArgument(ReadOnlySpan<byte> packed, ref int p, out long value)
    {
        value = 0;
        for (int i = 0; i < 5; i++)
        {
            if (p >= packed.Length)
            {
                return false;
            }

            byte b = packed[p++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value <= uint.MaxValue;
            }
        }
        return false;
    }

    // a × b for counts of at most 2^32, saturating rather than wrapping (sums of two stay below long.MaxValue).
    private static long Times(long a, long b) => a != 0 && b > (long.MaxValue / 4) / a ? long.MaxValue / 4 : a * b;

    private static bool Fits(BigEndianWriter output, long more, int maxLength) => more <= maxLength - output.Length;

    private static Step Zero(BigEndianWriter output, long count, int maxLength)
    {
        if (!Fits(output, count, maxLength))
        {
            return Step.TooLong;
        }

        output.WriteZeros((int)count);
        return Step.Done;
    }

    private static Step Copy(BigEndianWriter output, ReadOnlySpan<byte> packed, ref int p, long count, int maxLength)
    {
        if (count > packed.Length - p)
        {
            return Step.Truncated;
        }

        if (!Fits(output, count, maxLength))
        {
            return Step.TooLong;
        }

        output.WriteBytes(packed.Slice(p, (int)count));
        p += (int)count;
        return Step.Done;
    }

    private static Step RepeatedBlock(BigEndianWriter output, ReadOnlySpan<byte> packed, ref int p, long blockSize, int maxLength)
    {
        if (!TryArgument(packed, ref p, out long repeat) || blockSize > packed.Length - p)
        {
            return Step.Truncated;
        }

        if (!Fits(output, Times(blockSize, repeat + 1), maxLength))
        {
            return Step.TooLong;
        }

        var block = packed.Slice(p, (int)blockSize);
        p += (int)blockSize;
        if (blockSize > 0)
        {
            for (long i = 0; i <= repeat; i++)
            {
                output.WriteBytes(block);
            }
        }

        return Step.Done;
    }

    // Opcodes 3 and 4: (common, custom i) × repeat, then common; the common block is stored once (3) or is zeros (4).
    private static Step Interleave(BigEndianWriter output, ReadOnlySpan<byte> packed, ref int p, long commonSize,
        bool withCommonBlock, int maxLength)
    {
        if (!TryArgument(packed, ref p, out long customSize) || !TryArgument(packed, ref p, out long repeat))
        {
            return Step.Truncated;
        }

        ReadOnlySpan<byte> common = default;
        if (withCommonBlock)
        {
            if (commonSize > packed.Length - p)
            {
                return Step.Truncated;
            }

            common = packed.Slice(p, (int)commonSize);
            p += (int)commonSize;
        }
        if (Times(customSize, repeat) > packed.Length - p)
        {
            return Step.Truncated;
        }

        if (!Fits(output, Times(commonSize, repeat + 1) + Times(customSize, repeat), maxLength))
        {
            return Step.TooLong;
        }

        for (long i = 0; i < repeat && commonSize + customSize > 0; i++)
        {
            if (withCommonBlock)
            {
                output.WriteBytes(common);
            }
            else
            {
                output.WriteZeros((int)commonSize);
            }

            output.WriteBytes(packed.Slice(p, (int)customSize));
            p += (int)customSize;
        }
        if (withCommonBlock)
        {
            output.WriteBytes(common);
        }
        else
        {
            output.WriteZeros((int)commonSize);
        }

        return Step.Done;
    }
}
