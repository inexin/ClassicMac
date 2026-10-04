using System;
using System.Collections.Generic;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k;

/// <summary>One routine of a routine descriptor (MixedMode.h RoutineRecord).</summary>
/// <param name="ProcInfo">The calling convention and parameter sizes.</param>
/// <param name="Isa">The instruction set and run-time architecture (low nibble: 0 68k, 1 PowerPC).</param>
/// <param name="Flags">The routine flags (bit 0: the procedure is relative to the descriptor; <c>$20</c>: it is an index).</param>
/// <param name="ProcDescriptor">The procedure: an offset from the descriptor when relative, else an address.</param>
/// <param name="Selector">The selector, for a dispatched routine.</param>
/// <param name="TargetOffset">For a relative procedure that is not an index, where it is in the resource; otherwise null.</param>
/// <param name="Pef">The PEF container there, when the target starts with one.</param>
public sealed record RoutineRecord(uint ProcInfo, byte Isa, ushort Flags, uint ProcDescriptor, uint Selector, long? TargetOffset,
    PefContainer? Pef)
{
    /// <summary>The flag for a procedure relative to the descriptor (kProcDescriptorIsRelative).</summary>
    public const ushort RelativeFlag = 0x0001;

    /// <summary>Whether the procedure is PowerPC code (kPowerPCISA).</summary>
    public bool IsPowerPC => (Isa & 0x0F) == 1;

    /// <summary>Whether the procedure is relative to the descriptor.</summary>
    public bool IsRelative => (Flags & RelativeFlag) != 0;

    /// <summary>The flag for a procedure that is an index, not an address or offset (kProcDescriptorIsIndex).</summary>
    public const ushort IndexFlag = 0x0020;

    /// <summary>Whether the procedure is an index (then it has no target in the resource).</summary>
    public bool IsIndex => (Flags & IndexFlag) != 0;
}

/// <summary>
/// A routine descriptor (<c>$AAFE</c>, _MixedModeMagic) in a fat or PowerPC code resource: at offset 0, or right after a
/// standard header whose branch lands on it. A relative PowerPC routine points at a PEF container in the resource
/// [Doc: Inside Macintosh: PowerPC System Software, the Mixed Mode Manager; Verified: the Mac OS 9 System's fat
/// definition procedures].
/// </summary>
public sealed class RoutineDescriptor
{
    private RoutineDescriptor() { }

    /// <summary>Where the descriptor is in the resource.</summary>
    public int Offset { get; private init; }

    /// <summary>The descriptor version (7, kRoutineDescriptorVersion).</summary>
    public byte Version { get; private init; }

    /// <summary>The descriptor flags (<c>$20</c> behind a standard header, 0 at offset 0 in the samples).</summary>
    public byte Flags { get; private init; }

    /// <summary>The selector info.</summary>
    public byte SelectorInfo { get; private init; }

    /// <summary>The routines (routineCount + 1 of them).</summary>
    public IReadOnlyList<RoutineRecord> Routines { get; private init; } = [];

    /// <summary>Where the descriptor is: 0, or a standard header's branch target; null when there is none.</summary>
    public static int? Find(ReadOnlyMemory<byte> data)
    {
        if (IsMagic(data.Span, 0))
        {
            return 0;
        }

        var header = CodeResourceHeader.Read(data, null, []);
        return header is not null && IsMagic(data.Span, header.BranchTarget) ? header.BranchTarget : null;
    }

    private static bool IsMagic(ReadOnlySpan<byte> data, int at) =>
        at >= 0 && at <= data.Length - 2 && data[at] == 0xAA && data[at + 1] == 0xFE;

    /// <summary>
    /// Reads the routine descriptor of a code resource, or returns null when it has none. Damage is reported
    /// (<c>m68k.routine-*</c>, and the PEF reader's <c>pef.*</c>).
    /// </summary>
    public static RoutineDescriptor? Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (Find(data) is not { } at)
        {
            return null;
        }

        var reader = new BigEndianReader(data);
        var routines = new List<RoutineRecord>();
        if (at > reader.Length - HeaderLength)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.routine-descriptor-truncated",
                "The routine descriptor's header runs past the resource.", at));
            return new RoutineDescriptor { Offset = at };
        }
        reader.Position = at + 2;
        var version = reader.ReadByte();
        var flags = reader.ReadByte();
        reader.Skip(5); // reserved1 (long), reserved2 (byte)
        var selectorInfo = reader.ReadByte();
        int count = reader.ReadUInt16() + 1;
        if (version != CurrentVersion)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.routine-descriptor-version",
                $"The routine descriptor's version is {version}, not {CurrentVersion}.", at));
        }

        for (int i = 0; i < count; i++)
        {
            if (reader.Remaining < RecordLength)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.routine-descriptor-truncated",
                    $"The routine descriptor has {count} routines but room for {i}.", reader.Position));
                break;
            }
            var procInfo = reader.ReadUInt32();
            reader.Skip(1);
            var isa = reader.ReadByte();
            var routineFlags = reader.ReadUInt16();
            var proc = reader.ReadUInt32();
            reader.Skip(4);
            var selector = reader.ReadUInt32();
            long? target = null;
            PefContainer? pef = null;
            if ((routineFlags & (RoutineRecord.RelativeFlag | RoutineRecord.IndexFlag)) == RoutineRecord.RelativeFlag)
            {
                target = at + (long)proc;
                if (target >= reader.Length)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.routine-target",
                        $"Routine {i}'s procedure at {target:X} lies outside the {reader.Length}-byte resource.", at));
                }
                else if (PefContainer.IsPef(data.Span[(int)target.Value..]))
                {
                    try
                    {
                        pef = PefContainer.Read(data[(int)target.Value..], diagnostics);
                    }
                    catch (System.IO.InvalidDataException e)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.routine-pef",
                            $"Routine {i}'s PEF container at {target:X} cannot be read: {e.Message}", target));
                    }
                }
            }
            routines.Add(new RoutineRecord(procInfo, isa, routineFlags, proc, selector, target, pef));
        }
        return new RoutineDescriptor { Offset = at, Version = version, Flags = flags, SelectorInfo = selectorInfo, Routines = routines };
    }

    private const int HeaderLength = 12;
    private const int RecordLength = 20;
    private const byte CurrentVersion = 7;
}
