using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>The branch a standard code-resource header starts with.</summary>
    public enum CodeResourceBranch
    {
        /// <summary><c>BRA.S</c> (<c>$60xx</c>), usually <c>$600A</c>.</summary>
        BraShort,
        /// <summary><c>BRA.W</c> (<c>$6000 disp.w</c>).</summary>
        BraWord,
        /// <summary><c>JMP d16(PC)</c> (<c>$4EFA disp.w</c>).</summary>
        JmpPcRelative,
    }

    /// <summary>
    /// Apple's standard code-resource header: a branch over the header, a flags word, the resource type, ID and version,
    /// then the code. Execution starts at offset 0, on the branch. A resource without it is raw code, also entered at 0
    /// [Doc: Inside Macintosh, the definition procedures' resource format; Verified: the Mac OS 9 System's code resources].
    /// </summary>
    /// <param name="Branch">The branch form.</param>
    /// <param name="BranchTarget">Where the branch lands, from the resource start.</param>
    /// <param name="Flags">The flags word of the <c>BRA.S</c> form (0 in most of the System's, 1 in <c>'CDEF'</c> −1 and 1); 0 for the other forms, whose second word is the displacement.</param>
    /// <param name="Type">The resource type the header names.</param>
    /// <param name="Id">The ID the header names (usually, not always, the resource's own).</param>
    /// <param name="Version">The version.</param>
    public sealed record CodeResourceHeader(CodeResourceBranch Branch, int BranchTarget, ushort Flags, FourCC Type, short Id, ushort Version)
    {
        /// <summary>The header's length; the code after it starts here.</summary>
        public const int Length = 12;

        /// <summary>
        /// Reads the header, or returns null for raw code. With <paramref name="type"/> the header must name it; without,
        /// it must name four printable characters. A branch landing outside the resource, or inside the header's fields, is
        /// reported (<c>m68k.code-header-branch</c>).
        /// </summary>
        public static CodeResourceHeader? Read(ReadOnlyMemory<byte> data, FourCC? type, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < Length) return null;
            ushort w0 = reader.ReadUInt16At(0);
            CodeResourceBranch branch;
            int target;
            ushort flags = 0;
            if (w0 == 0x6000) { branch = CodeResourceBranch.BraWord; target = 2 + reader.ReadInt16At(2); }
            else if (w0 == 0x4EFA) { branch = CodeResourceBranch.JmpPcRelative; target = 2 + reader.ReadInt16At(2); }
            else if ((w0 & 0xFF00) == 0x6000 && (w0 & 0xFF) != 0xFF)
            {
                branch = CodeResourceBranch.BraShort;
                target = 2 + (sbyte)(w0 & 0xFF);
                flags = reader.ReadUInt16At(2);
            }
            else return null;

            var named = reader.ReadFourCCAt(4);
            if (type is { } expected ? named != expected : !IsPrintable(reader.ReadBytesAt(4, 4))) return null;
            if (target < 0 || target >= reader.Length)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.code-header-branch",
                    $"The standard header's branch lands at {target:X}, outside the {reader.Length}-byte resource.", 0));
            else if (target < Length)
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.code-header-branch",
                    $"The standard header's branch lands at {target:X}, inside the header.", 0));
            return new CodeResourceHeader(branch, target, flags, named, reader.ReadInt16At(8), reader.ReadUInt16At(10));
        }

        private static bool IsPrintable(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
                if (b is < 0x20 or > 0x7E) return false;
            return true;
        }
    }
}
