using ClassicMac.Code.M68k;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.M68k;

// Real 68k applications (CLASSICMAC_CODE_CORPUS), checked against numbers counted by an independent reader
// [Verified: the samples named in each test].
public class M68kCorpusTests
{
    [Fact]
    public void Disk_Copy_6_1_2_carries_the_HDI_driver()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("DC612"));
        var diagnostics = new List<Diagnostic>();
        var header = DriverHeader.Read(fork.Find(FourCC.FromString("DRVR"), 0)!.GetData(), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.True(header.IsStandard);
        Assert.Equal((".HDI", (ushort)0x18, (ushort)0x30), (header.Name, header.Open, header.Close));
    }

    private static (CodeApplication App, List<Diagnostic> Diagnostics) Read(string name, long? length = null)
    {
        var fork = ResourceFork.Read(CodeCorpus.Require(name, length));
        var diagnostics = new List<Diagnostic>();
        return (CodeApplication.Read(fork, diagnostics), diagnostics);
    }

    private static void AssertClean(List<Diagnostic> diagnostics) =>
        Assert.True(!diagnostics.Any(d => d.Severity != DiagnosticSeverity.Info),
            string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message)));

    [Fact]
    public void ResEdit_is_MPW_near_with_a_bootstrap_entry_and_compressed_segments()
    {
        var (app, diagnostics) = Read("ResEdit.rsrc");
        AssertClean(diagnostics);
        Assert.Equal(CodeModel.MpwNear, app.Model);
        Assert.Equal((0xEC0u, 0xAA0u, 0xEA0u, 0x20u), (app.AboveA5, app.BelowA5, app.JumpTableSize, app.JumpTableOffset));
        Assert.Equal(468, app.JumpTable.Count);
        Assert.False(app.IsFarModel);
        Assert.Equal(25, app.Segments.Count);
        Assert.Equal(18, app.Segments.Count(s => s.IsCompressed));
        Assert.All(app.Segments, s => Assert.True(s.IsReadable && s.Header is { IsFar: false }));
        Assert.Equal(new CodeEntryPoint(66, 0x0C, 0x10), app.Entry);
        Assert.Equal(new CodeEntryPoint(1, 0x3634, 0x3638), app.OriginalEntry);
        var code2 = app.FindSegment(2)!.Header!;
        Assert.Equal((0x2E8u, 0x0Fu), (code2.FirstNearOffset, code2.NearCount));

        Assert.Equal((short)5, app.A5InitSegment);
        Assert.Equal("%A5Init", app.FindSegment(5)!.Name);
        var init = app.A5Init!;
        Assert.Equal((0x1B2, 0xA9Eu, 55, 58), (init.HeaderOffset, init.BelowA5Size, init.Runs.Count, init.Relocations.Count));
        Assert.Equal((915L, 940L), (init.DataEnd, init.RelocationEnd));
        Assert.Equal(-0xA92, init.Relocations[0]);
        Assert.Equal(app.FindSegment(5)!.Data.Length - 8, init.RelocationEnd);

        // Every JSR d16(A5) ($4EAD, found by a word scan) in CODE 1 calls a jump-table entry + 2 naming a segment.
        var code1 = new BigEndianReader(app.FindSegment(1)!.Data);
        var calls = Enumerable.Range(0, (code1.Length - 2) / 2).Select(i => 2 * i)
            .Where(at => code1.ReadUInt16At(at) == 0x4EAD).Select(at => (int)code1.ReadInt16At(at + 2)).ToList();
        Assert.Equal(145, calls.Count);
        Assert.All(calls, displacement => Assert.NotNull(app.FindSegment(app.ResolveA5(displacement)!.Segment)));
    }

    [Fact]
    public void Realmz_is_CodeWarrior_with_DATA_0()
    {
        var (app, diagnostics) = Read("Realmz 7.1.2", 576955);
        AssertClean(diagnostics);
        Assert.Equal(CodeModel.CodeWarrior, app.Model);
        Assert.Equal((0x3F578u, 0x1F48u, 8u, 0x20u), (app.AboveA5, app.BelowA5, app.JumpTableSize, app.JumpTableOffset));
        Assert.Equal(JumpTableEntryKind.NearUnloaded, Assert.Single(app.JumpTable).Kind);
        var code1 = Assert.Single(app.Segments);
        Assert.Equal(("ANSI Libraries", 485276), (code1.Name, code1.Data.Length));
        Assert.Equal((0u, 1u), (code1.Header!.FirstNearOffset, code1.Header.NearCount));
        Assert.Equal(new CodeEntryPoint(1, 0, 4), app.Entry);
        Assert.False(app.HasPowerPCFragment);

        var data = app.CodeWarriorData!;
        Assert.Equal(0x52D2u, data.CodeRelocationOffset);
        Assert.Equal(0xDA4CL, data.End);
        Assert.Equal([(-0xF10, 0), (0x28, 0x28), (0x28, 0x3BD54)], data.Blocks.Select(b => (b.Start, b.End)));
        Assert.Equal([25, 10, 0, 26862, 391, 0], data.Relocations.Select(r => r.Offsets.Count));
    }

    [Fact]
    public void QDHarness_is_Retro68()
    {
        var (app, diagnostics) = Read("QDHarness.APPL");
        AssertClean(diagnostics);
        Assert.Equal(CodeModel.Retro68, app.Model);
        Assert.True(app.IsFarModel);
        Assert.Equal(JumpTableEntryKind.FarMarker, app.JumpTable[1].Kind);
        Assert.Equal(0x0000FFFF00000000ul, app.JumpTable[1].Raw);
        Assert.Equal(new CodeEntryPoint(1, 0, 4), app.Entry);
        Assert.Equal("Runtime", app.FindSegment(1)!.Name);
        Assert.False(app.FindSegment(1)!.Header!.IsFar);
        Assert.All(app.Segments.Skip(1), s => Assert.True(s.Header!.IsFar));
        var main = app.FindSegment(2)!;
        Assert.Equal("Main", main.Name);
        Assert.Equal((0u, 0u, 1u), (main.Header!.NearCount, main.Header.FirstNearOffset, main.Header.FarCount));
        // Every relocation of every segment is even and inside its segment (checked by the reader, so no diagnostics).
        Assert.NotEmpty(app.DataRelocations);

        // The build the expected numbers were counted on; a rebuilt harness has other sizes.
        if (app.AboveA5 != 0x100) Assert.Skip($"QDHarness.APPL is another build (above A5 ${app.AboveA5:X}); the counted build has $100.");
        Assert.Equal((0x100u, 0x17A4u), (app.AboveA5, app.BelowA5));
        Assert.Equal(28, app.JumpTable.Count);
        Assert.Equal(8, app.Segments.Count);
        Assert.Equal(0xD8u, main.Header.FirstFarOffset);
        var relocations = app.FindSegment(1)!.Retro68Relocations;
        Assert.Equal(1291, relocations.Count);
        Assert.Equal([863, 256, 97, 75], Enum.GetValues<Retro68RelocationBase>().Select(b => relocations.Count(r => r.Base == b)));
    }

    [Fact]
    public void Disk_Copy_6_1_2_is_MPW_far_and_fat()
    {
        var (app, diagnostics) = Read("DC612");
        AssertClean(diagnostics);
        Assert.Equal(CodeModel.MpwFar, app.Model);
        Assert.True(app.HasPowerPCFragment);
        Assert.True(app.IsFarModel);
        Assert.Equal((0xEA0u, 0x158Cu, 0xE80u), (app.AboveA5, app.BelowA5, app.JumpTableSize));
        Assert.Equal(464, app.JumpTable.Count);
        Assert.Equal(30, app.Segments.Count);
        Assert.Equal(29, app.Segments.Count(s => s.Header!.IsFar));
        Assert.Equal(new CodeEntryPoint(30, 0, 4), app.Entry);
        Assert.False(app.FindSegment(30)!.Header!.IsFar);

        var code1 = app.FindSegment(1)!;
        Assert.Equal(new SegmentHeader(true, 0x10, 0x47, 0, 0, 0x2C78, 0, 0x2CD6, 0), code1.Header);
        Assert.Equal((88, 33), (code1.A5Relocations.Count, code1.PcRelocations.Count));
        // CODE 1 +$1B3A is JSR $0992.L; its operand is A5-relocated, and A5 + $992 is a jump-table entry + 2 into CODE 17.
        Assert.Contains(0x1B3CL, code1.A5Relocations);
        Assert.Equal(0x0992u, new BigEndianReader(code1.Data).ReadUInt32At(0x1B3C));
        Assert.Equal((short)17, app.ResolveA5(0x992)!.Segment);

        Assert.Equal((short)2, app.A5InitSegment);
        var init = app.A5Init!;
        Assert.Equal((0x1D6, 0x158Cu, 60, 36), (init.HeaderOffset, init.BelowA5Size, init.Runs.Count, init.Relocations.Count));
        Assert.Equal((861L, 886L, -2624), (init.DataEnd, init.RelocationEnd, init.Relocations[0]));
    }
}
