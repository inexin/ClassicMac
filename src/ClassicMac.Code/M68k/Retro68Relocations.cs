using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>
    /// The kind of a Retro68 relocation (the low 2 bits of each <c>'RELA'</c> value): kind 0 adds the segment's address,
    /// kinds 1 to 3 add the A5 displacement and say what the long, an A5 offset, points at
    /// [Code: Retro68's runtime (Retro68Relocate, the segment loader); Verified: two builds of a Retro68 application].
    /// </summary>
    public enum Retro68RelocationBase : byte
    {
        /// <summary>The segment's address (its resource start; nothing in <c>'DATA'</c> 0): the long is an offset in the <c>'CODE'</c> resource.</summary>
        Segment = 0,
        /// <summary>The A5 displacement: the long is an A5 offset into the initialized data.</summary>
        InitializedData = 1,
        /// <summary>The A5 displacement: the long is an A5 offset into the zero-filled data (BSS).</summary>
        UninitializedData = 2,
        /// <summary>The A5 displacement: the long is an A5 offset above A5 (a jump-table entry + 2, or the application parameters).</summary>
        A5 = 3,
    }

    /// <summary>
    /// One Retro68 relocation: the long at <paramref name="Offset"/> gets <paramref name="Base"/>'s displacement added; a
    /// relative one also gets its own address subtracted.
    /// </summary>
    /// <param name="Offset">The patched long's offset in the resource (the segment's code start included).</param>
    /// <param name="Base">The kind: what is added.</param>
    /// <param name="Relative">From the second, PC-relative list.</param>
    public readonly record struct Retro68Relocation(long Offset, Retro68RelocationBase Base, bool Relative = false);

    /// <summary>
    /// Retro68's relocations (<c>'RELA'</c> n for <c>'CODE'</c> n; <c>'RELA'</c> 0 for <c>'DATA'</c> 0): unsigned LEB128
    /// values, each list ended by a 0 byte. Each value's high bits are a step from the previous position (the first from
    /// −1) and its low 2 bits the kind. A nonzero byte after the first list's 0 starts a second, PC-relative list
    /// [Code: Retro68's runtime, Retro68ApplyRelocations; Verified: a Retro68 application]
    /// (docs/formats/code/code-data.md §1.3).
    /// </summary>
    public static class Retro68Relocations
    {
        /// <summary>The length of a near segment's header.</summary>
        public const int NearHeaderLength = 4;

        /// <summary>The length of a far segment's header.</summary>
        public const int FarHeaderLength = 0x28;

        /// <summary>Where a segment's code starts: after the 4-byte near header, or the <c>$28</c>-byte far one (<c>$FFFF</c>).</summary>
        public static int CodeStart(ReadOnlySpan<byte> segment) =>
            segment.Length >= 2 && segment[0] == 0xFF && segment[1] == 0xFF ? FarHeaderLength : NearHeaderLength;

        /// <summary>
        /// Reads a <c>'RELA'</c>. Problems go to <paramref name="diagnostics"/> (<c>m68k.rela-*</c>); a relocation outside the
        /// target is reported and left out.
        /// </summary>
        /// <param name="rela">The <c>'RELA'</c> data.</param>
        /// <param name="codeStart">Where positions count from in the target: 0 for <c>'DATA'</c>, <see cref="CodeStart"/> for a segment.</param>
        /// <param name="targetLength">The target's length, to check each patched long lies inside it.</param>
        /// <param name="diagnostics">Receives problems.</param>
        public static IReadOnlyList<Retro68Relocation> Read(ReadOnlyMemory<byte> rela, int codeStart, int targetLength,
            ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(rela);
            var relocations = new List<Retro68Relocation>();
            // The absolute list, then, when the byte after its 0 is not 0, the relative one (Retro68ApplyRelocations).
            if (ReadList(reader, codeStart, targetLength, relative: false, relocations, diagnostics)
                && reader.Position + 1 < reader.Length && reader.ReadByteAt(reader.Position + 1) != 0)
            {
                reader.Position++;
                ReadList(reader, codeStart, targetLength, relative: true, relocations, diagnostics);
            }
            return relocations;
        }

        // One list, to its 0 byte (left as the next byte to read). False when it ends otherwise.
        private static bool ReadList(BigEndianReader reader, int codeStart, int targetLength, bool relative,
            List<Retro68Relocation> relocations, ICollection<Diagnostic> diagnostics)
        {
            long position = -1;
            while (true)
            {
                int at = reader.Position;
                // The runtime ends a list on a 0 byte, not on a value of 0: 80 00 is a step of 0.
                if (at < reader.Length && reader.ReadByteAt(at) == 0) return true;
                if (!TryReadUleb(reader, out var value, out var overflow))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.rela-truncated",
                        $"The 'RELA' list ends without its 0 terminator; {relocations.Count} relocations read.", at));
                    return false;
                }
                if (overflow)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.rela-value",
                        "A 'RELA' value is wider than 32 bits; reading stopped.", at));
                    return false;
                }

                position += value >> 2;
                var kind = (Retro68RelocationBase)(value & 3);
                long offset = codeStart + position;
                if (position < 0 || offset > targetLength - 4L)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.rela-range",
                        $"A relocation at {offset:X} lies outside the {targetLength}-byte target; left out.", at));
                    continue;
                }
                if ((offset & 1) != 0)
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.rela-odd",
                        $"A relocation at odd offset {offset:X} would fault on a 68000.", at));
                relocations.Add(new Retro68Relocation(offset, kind, relative));
            }
        }

        // Little-endian 7-bit groups, the high bit set on all but the last.
        private static bool TryReadUleb(BigEndianReader reader, out uint value, out bool overflow)
        {
            value = 0;
            overflow = false;
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (!reader.TryReadByte(out var b)) return false;
                if (shift < 64) v |= (ulong)(b & 0x7F) << shift;
                if (shift >= 35 || v > uint.MaxValue) overflow = true;
                if ((b & 0x80) == 0) break;
            }
            value = (uint)v;
            return true;
        }
    }
}
