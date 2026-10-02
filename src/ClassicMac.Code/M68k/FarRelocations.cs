using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>
    /// A far (<c>$FFFF</c>) segment's relocation lists, as MPW writes them: the A5 list (each long gets A5 added) and the
    /// PC list (each long gets the segment's address added). A list is a run of deltas from a running offset that starts
    /// at 0 (the resource start): a byte below <c>$80</c> is a delta of twice its value, a byte from <c>$80</c> with the
    /// next byte a 15-bit delta, again doubled; 0 ends the list [Doc: Mac OS Runtime Architectures, the 32-bit
    /// everything segment header; Verified: an MPW far application].
    /// </summary>
    public static class FarRelocations
    {
        /// <summary>
        /// Reads the list at <paramref name="listOffset"/> in the segment and returns the patched longs' offsets in the
        /// resource. Problems go to <paramref name="diagnostics"/> (<c>m68k.far-reloc-*</c>). The <c>$80 $00</c> form
        /// (a 4-byte delta, for segments past 64 KB) has not been seen and is reported, not guessed at.
        /// </summary>
        public static IReadOnlyList<long> Read(ReadOnlyMemory<byte> segment, long listOffset, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var offsets = new List<long>();
            var reader = new BigEndianReader(segment);
            if (listOffset < 0 || listOffset >= reader.Length)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.far-reloc-offset",
                    $"A relocation list at {listOffset:X} lies outside the {reader.Length}-byte segment."));
                return offsets;
            }
            reader.Position = (int)listOffset;
            long offset = 0;
            while (true)
            {
                int at = reader.Position;
                if (!reader.TryReadByte(out var b)) { Truncated(at); break; }
                if (b == 0) break;
                long delta;
                if (b < 0x80) delta = 2L * b;
                else
                {
                    if (!reader.TryReadByte(out var next)) { Truncated(at); break; }
                    if (b == 0x80 && next == 0)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.far-reloc-escape",
                            "A relocation list uses the $80 $00 long-delta form, which has not been verified; reading stopped.", at));
                        break;
                    }
                    delta = 2L * (((b & 0x7F) << 8) | next);
                }
                offset += delta;
                if (offset > reader.Length - 4L)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.far-reloc-range",
                        $"A relocation at {offset:X} lies outside the {reader.Length}-byte segment; left out.", at));
                    continue;
                }
                offsets.Add(offset);
            }
            return offsets;

            void Truncated(int at) => diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.far-reloc-truncated",
                $"A relocation list runs past the end of the segment; {offsets.Count} relocations read.", at));
        }

        /// <summary>
        /// The bytes the list at <paramref name="listOffset"/> takes, its 0 terminator included: as far as
        /// <see cref="Read"/> reads it (to the end of the segment when it is cut short; 0 when it starts outside).
        /// </summary>
        public static int ListLength(ReadOnlyMemory<byte> segment, long listOffset)
        {
            var reader = new BigEndianReader(segment);
            if (listOffset < 0 || listOffset >= reader.Length) return 0;
            reader.Position = (int)listOffset;
            while (reader.TryReadByte(out var b) && b != 0)
            {
                if (b < 0x80) continue;
                if (!reader.TryReadByte(out var next) || (b == 0x80 && next == 0)) break;
            }
            return reader.Position - (int)listOffset;
        }
    }
}
