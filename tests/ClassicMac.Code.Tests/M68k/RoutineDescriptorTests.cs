using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// $AAFE RoutineDescriptor: AAFE version.b flags.b reserved.l reserved.b selectorInfo.b routineCount.w, then
// (count + 1) × RoutineRecord: procInfo.l reserved.b ISA.b flags.w procDescriptor.l reserved.l selector.l.
public class RoutineDescriptorTests
{
    private static readonly byte[] Pef = new PefBuilder { Main = (1, 0) }
        .AddSection(PefSectionKind.Code, PefBuilder.Words(0x4E800020))
        .AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(0, 4))
        .Build();

    private static void Descriptor(BigEndianWriter w, byte flags, byte version = 7,
        params (uint ProcInfo, byte Isa, ushort Flags, uint Proc)[] routines)
    {
        w.WriteUInt16(0xAAFE);
        w.WriteByte(version);
        w.WriteByte(flags);
        w.WriteUInt32(0);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteUInt16(routines.Length - 1);
        foreach (var (procInfo, isa, routineFlags, proc) in routines)
        {
            w.WriteUInt32(procInfo);
            w.WriteByte(0);
            w.WriteByte(isa);
            w.WriteUInt16(routineFlags);
            w.WriteUInt32(proc);
            w.WriteUInt32(0);
            w.WriteUInt32(0x10);
        }
    }

    // A fat definition procedure: the standard header, the descriptor at +$0C, the PEF at +$2C.
    private static byte[] BehindHeader()
    {
        var w = new BigEndianWriter();
        w.WriteBytes(Words(0x600A, 0));
        w.WriteFourCC(FourCC.FromString("CDEF"));
        w.WriteBytes(Words(0, 0x0B));
        Descriptor(w, 0x20, routines: (0x3BB0, 1, 7, 0x20));
        w.WriteBytes(Pef);
        return w.ToArray();
    }

    private static byte[] AtZero(uint proc = 0x20, int pefLength = -1, byte version = 7)
    {
        var w = new BigEndianWriter();
        Descriptor(w, 0, version, (0xEBD80, 1, 7, proc));
        w.WriteBytes(pefLength < 0 ? Pef : Pef[..pefLength]);
        return w.ToArray();
    }

    [Fact]
    public void Finds_the_descriptor_at_0_or_behind_a_standard_header()
    {
        Assert.Equal(0, RoutineDescriptor.Find(AtZero()));
        Assert.Equal(0x0C, RoutineDescriptor.Find(BehindHeader()));
        Assert.Null(RoutineDescriptor.Find(Words(0x4E56, 0, 0x4E5E, 0x4E75)));
        // A standard header whose branch does not land on $AAFE.
        Assert.Null(RoutineDescriptor.Find(Words(0x600A, 0, 0x4344, 0x4546, 0, 1, 0x4E75, 0x4E75)));
        Assert.Null(RoutineDescriptor.Read(Words(0x4E56, 0, 0x4E5E, 0x4E75), []));
    }

    [Fact]
    public void Reads_a_descriptor_at_0_and_its_PEF()
    {
        var diagnostics = new List<Diagnostic>();
        var descriptor = RoutineDescriptor.Read(AtZero(), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal((0, (byte)7, (byte)0, (byte)0), (descriptor.Offset, descriptor.Version, descriptor.Flags, descriptor.SelectorInfo));
        var routine = Assert.Single(descriptor.Routines);
        Assert.Equal((0xEBD80u, (byte)1, (ushort)7, 0x20u, 0x10u), (routine.ProcInfo, routine.Isa, routine.Flags, routine.ProcDescriptor, routine.Selector));
        Assert.True(routine.IsPowerPC);
        Assert.True(routine.IsRelative);
        Assert.Equal(0x20L, routine.TargetOffset);
        Assert.Equal(new PefEntryPoint(1, 0), routine.Pef!.Loader!.Main);
    }

    [Fact]
    public void Reads_a_descriptor_behind_a_standard_header()
    {
        var descriptor = RoutineDescriptor.Read(BehindHeader(), [])!;
        Assert.Equal((0x0C, (byte)0x20), (descriptor.Offset, descriptor.Flags));
        var routine = Assert.Single(descriptor.Routines);
        Assert.Equal(0x3BB0u, routine.ProcInfo);
        Assert.Equal(0x2CL, routine.TargetOffset);
        Assert.NotNull(routine.Pef);
    }

    [Fact]
    public void Absolute_and_68k_routines_have_no_target_in_the_resource()
    {
        var w = new BigEndianWriter();
        Descriptor(w, 0, routines: [(0x3BB0, 0, 0, 0x00400000), (0x3BB0, 1, 7, 0x34)]);
        w.WriteBytes(Pef);
        var descriptor = RoutineDescriptor.Read(w.ToArray(), [])!;
        Assert.Equal(2, descriptor.Routines.Count);
        var m68k = descriptor.Routines[0];
        Assert.False(m68k.IsPowerPC);
        Assert.False(m68k.IsRelative);
        Assert.Null(m68k.TargetOffset);
        Assert.Null(m68k.Pef);
        Assert.NotNull(descriptor.Routines[1].Pef);
    }

    [Fact]
    public void A_relative_target_that_is_not_a_PEF_is_68k_code()
    {
        var w = new BigEndianWriter();
        Descriptor(w, 0, routines: (0x3BB0, 0, 1, 0x20));
        w.WriteBytes(Words(0x4E56, 0, 0x4E5E, 0x4E75));
        var diagnostics = new List<Diagnostic>();
        var routine = Assert.Single(RoutineDescriptor.Read(w.ToArray(), diagnostics)!.Routines);
        Assert.Empty(diagnostics);
        Assert.Equal(0x20L, routine.TargetOffset);
        Assert.Null(routine.Pef);
    }

    [Fact]
    public void A_relative_target_outside_the_resource_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var routine = Assert.Single(RoutineDescriptor.Read(AtZero(proc: 0x10000), diagnostics)!.Routines);
        Assert.Null(routine.Pef);
        Assert.Equal("m68k.routine-target", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_PEF_too_short_to_read_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var routine = Assert.Single(RoutineDescriptor.Read(AtZero(pefLength: 20), diagnostics)!.Routines);
        Assert.Null(routine.Pef);
        Assert.Equal("m68k.routine-pef", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Records_past_the_resource_are_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var descriptor = RoutineDescriptor.Read(AtZero()[..30], diagnostics)!;
        Assert.Empty(descriptor.Routines);
        Assert.Equal("m68k.routine-descriptor-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_descriptor_header_past_the_resource_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var descriptor = RoutineDescriptor.Read(AtZero()[..8], diagnostics)!;
        Assert.Empty(descriptor.Routines);
        Assert.Equal("m68k.routine-descriptor-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_version_other_than_7_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var descriptor = RoutineDescriptor.Read(AtZero(version: 6), diagnostics)!;
        Assert.Equal((byte)6, descriptor.Version);
        Assert.Single(descriptor.Routines);
        Assert.Equal("m68k.routine-descriptor-version", Assert.Single(diagnostics).Code);
    }
}
