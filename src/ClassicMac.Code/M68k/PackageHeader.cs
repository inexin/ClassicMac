using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k;

/// <summary>One entry of a package's dispatch table.</summary>
/// <param name="Selector">The selector.</param>
/// <param name="Offset">The routine's offset as stored: from the flags word at <see cref="PackageHeader.DispatchBase"/>; 0 for none.</param>
public readonly record struct PackageEntry(int Selector, ushort Offset)
{
    /// <summary>Where the routine is in the resource (<see cref="PackageHeader.DispatchBase"/> + the offset), or null for none.</summary>
    public long? TargetOffset => Offset == 0 ? null : PackageHeader.DispatchBase + (long)Offset;
}

/// <summary>
/// The <c>$A9FF</c> form of a package or procedure resource (<c>'PACK'</c>, <c>'proc'</c>, <c>'dimg'</c>): <c>_Debugger</c>
/// guarding the header, the type and ID, then a version, a flags word, the first and last selector bytes and a table of
/// word offsets, one per selector (every second selector when flags bit 0 is set), each from the flags word at
/// <c>$0A</c>. The layout after the ID is fitted to the Mac OS 9 System's resources, not read from documentation or
/// code (docs/formats/code/code-resources.md §1.3).
/// </summary>
/// <param name="Type">The type in the header.</param>
/// <param name="Id">The ID in the header.</param>
/// <param name="Version">The version word.</param>
/// <param name="Flags">The flags word.</param>
/// <param name="FirstSelector">The first selector.</param>
/// <param name="LastSelector">The last selector.</param>
/// <param name="Entries">The table.</param>
public sealed record PackageHeader(FourCC Type, short Id, ushort Version, ushort Flags, sbyte FirstSelector, sbyte LastSelector,
    IReadOnlyList<PackageEntry> Entries)
{
    /// <summary>The <c>_Debugger</c> trap word the form starts with.</summary>
    public const ushort Signature = 0xA9FF;

    /// <summary>Where the table starts.</summary>
    public const int TableOffset = 14;

    /// <summary>
    /// What the table's offsets count from: the flags word. Counted from there, the Mac OS 9 System's routines start
    /// on a <c>LINK</c> or right after the previous routine's return [Fitted: the Mac OS 9 System's 14 <c>$A9FF</c>
    /// resources].
    /// </summary>
    public const int DispatchBase = 0x0A;

    /// <summary>
    /// Reads the <c>$A9FF</c> form, or returns null when the resource does not start with it. A table running past the
    /// resource, or a last selector before the first, is reported (<c>m68k.package-*</c>).
    /// </summary>
    public static PackageHeader? Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var reader = new BigEndianReader(data);
        if (reader.Length < 2 || reader.ReadUInt16At(0) != Signature)
        {
            return null;
        }

        if (reader.Length < TableOffset)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.package-truncated",
                $"The $A9FF header needs {TableOffset} bytes; the resource has {reader.Length}."));
            return null;
        }
        reader.Position = 2;
        var type = reader.ReadFourCC();
        var id = reader.ReadInt16();
        var version = reader.ReadUInt16();
        var flags = reader.ReadUInt16();
        var first = (sbyte)reader.ReadByte();
        var last = (sbyte)reader.ReadByte();
        var entries = new List<PackageEntry>();
        int step = (flags & 1) != 0 ? 2 : 1;
        if (last < first)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.package-range",
                $"The $A9FF header's last selector {last} is before its first {first}.", 12));
        }

        for (int selector = first; selector <= last; selector += step)
        {
            if (!reader.TryReadUInt16(out var offset))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.package-truncated",
                    $"The $A9FF dispatch table for selectors {first} to {last} runs past the resource; {entries.Count} entries read.", reader.Position));
                break;
            }
            entries.Add(new PackageEntry(selector, offset));
        }
        return new PackageHeader(type, id, version, flags, first, last, entries);
    }
}
