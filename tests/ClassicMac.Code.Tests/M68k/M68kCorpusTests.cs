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

    // The MPW initializer's data in the Mac OS 9 System's resources: (type, id, header, belowA5Size, runs, relocations,
    // data end, relocation end, first relocation).
    private static readonly (string Type, short Id, int Header, uint Below, int Runs, int Relocations, long DataEnd, long RelocationEnd,
        int FirstRelocation)[] Initializers =
    [
        ("AINI", 2017, 0xAD6, 0x154, 6, 3, 2833, 2838, -0x11C),
        ("DRVR", -20268, 0x3C90, 0x690, 38, 9, 15975, 15981, -0x5E0),
        ("DRVR", -20267, 0x35F0, 0x660, 38, 9, 14279, 14285, -0x5B0),
        ("DRVR", 22, 0xAE2, 0x212, 21, 9, 2945, 2957, -0xCE),
        ("DRVR", 53, 0x3550, 0x660, 38, 9, 14119, 14125, -0x5B0),
        ("enet", 1648, 0x2A3E, 0x330, 63, 31, 11182, 11191, -0x29A),
        ("otdr", 9, 0x2358, 0x1BE, 32, 13, 9316, 9325, -0xE6),
        ("otlm", 9, 0x2192, 0x26C, 33, 14, 8810, 8820, -0x10C),
        ("ptch", -20917, 0x1028, 0xFA, 17, 7, 4288, 4294, -0x92),
        ("scod", -16476, 0x1AE, 0x294, 5, 3, 567, 573, -0x11C),
        ("scod", -16472, 0x1AE, 0x28A, 5, 3, 567, 573, -0x11C),
        ("scod", -16467, 0x1AE, 0xEFA, 235, 172, 1614, 1625, -0xEFA),
        ("wart", 1, 0xACA, 0xAE, 10, 4, 2863, 2869, -0x68),
    ];

    public static TheoryData<string, short, int, uint, int, int, long, long, int> SystemInitializers
    {
        get
        {
            var data = new TheoryData<string, short, int, uint, int, int, long, long, int>();
            foreach (var i in Initializers)
            {
                data.Add(i.Type, i.Id, i.Header, i.Below, i.Runs, i.Relocations, i.DataEnd, i.RelocationEnd, i.FirstRelocation);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SystemInitializers))]
    public void System_resources_carry_the_MPW_initializer(string type, short id, int header, uint below, int runs, int relocations,
        long dataEnd, long relocationEnd, int firstRelocation)
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var data = ResourceDecompression.Default.GetData(fork.Find(FourCC.FromString(type), id)!, fork);
        Assert.True(MpwA5Init.HasTrailer(data.Span));
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(data, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal((header, below, runs, relocations), (init.HeaderOffset, init.BelowA5Size, init.Runs.Count, init.Relocations.Count));
        Assert.Equal((dataEnd, relocationEnd, firstRelocation), (init.DataEnd, init.RelocationEnd, init.Relocations[0]));
        // The relocations end at the trailer or one pad byte before it.
        Assert.InRange(data.Length - 8 - init.RelocationEnd, 0, 1);
    }

    [Fact]
    public void Thirteen_System_resources_carry_the_MPW_initializer()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var found = fork.Resources.Where(r => MpwA5Init.HasTrailer(ResourceDecompression.Default.GetData(r, fork).Span))
            .Select(r => $"{r.Type} {r.Id}").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(Initializers.Select(i => $"{i.Type} {i.Id}").Order(StringComparer.Ordinal), found);
    }

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

    // Checks every Retro68 relocation against the stored long it patches: kind 0 is an offset in the segment's resource;
    // kinds 1 to 3 are A5 offsets (initialized data, zero-filled data, the jump table).
    private static void AssertRetro68Kinds(CodeApplication app, ResourceFork fork)
    {
        int dataLength = fork.Find(FourCC.FromString("DATA"), 0)!.GetData().Length;
        long below = app.BelowA5, bss = below - dataLength;
        foreach (var segment in app.Segments)
        {
            var reader = new BigEndianReader(segment.Data);
            foreach (var r in segment.Retro68Relocations)
            {
                Assert.True(r.Offset % 2 == 0 && r.Offset >= segment.Header!.Length && r.Offset <= segment.Data.Length - 4);
                Assert.False(r.Relative);
                int value = reader.ReadInt32At((int)r.Offset);
                switch (r.Base)
                {
                    case Retro68RelocationBase.Segment:
                        Assert.InRange(value, 0, segment.Data.Length);
                        break;
                    case Retro68RelocationBase.InitializedData:
                        Assert.InRange(value, -below, -bss);
                        break;
                    case Retro68RelocationBase.UninitializedData:
                        Assert.InRange(value, -bss, 0);
                        break;
                    default:
                        Assert.Equal(2, (value - (int)app.JumpTableOffset) % 8);
                        Assert.InRange(value, (int)app.JumpTableOffset, (int)app.AboveA5);
                        break;
                }
            }
        }
    }

    [Fact]
    public void QDHarness_is_Retro68()
    {
        // The build the numbers were counted on: 117,059 bytes (a rebuilt harness has other sizes and is skipped).
        var fork = ResourceFork.Read(CodeCorpus.Require("QDHarness.APPL", 117059));
        var diagnostics = new List<Diagnostic>();
        var app = CodeApplication.Read(fork, diagnostics);
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
        Assert.Equal((0u, 0u, 1u, 0xD8u), (main.Header!.NearCount, main.Header.FirstNearOffset, main.Header.FarCount, main.Header.FirstFarOffset));
        Assert.Equal((0x100u, 0x17A4u), (app.AboveA5, app.BelowA5));
        Assert.Equal(28, app.JumpTable.Count);
        Assert.Equal(8, app.Segments.Count);

        // 'RELA' 1 (Runtime): 1,291 relocations, 863, 256, 97 and 75 of kinds 0 to 3; 'RELA' 2 (Main): 497, no kind 0;
        // 'RELA' 0: 18 in 'DATA' 0. Every stream ends 00 00: no relative list.
        int[] Kinds(IReadOnlyList<Retro68Relocation> relocations) =>
            [.. Enum.GetValues<Retro68RelocationBase>().Select(b => relocations.Count(r => r.Base == b))];
        Assert.Equal([863, 256, 97, 75], Kinds(app.FindSegment(1)!.Retro68Relocations));
        Assert.Equal([0, 189, 38, 270], Kinds(main.Retro68Relocations));
        Assert.Equal([0, 12, 4, 2], Kinds(app.DataRelocations));
        Assert.All(app.Segments.Skip(2), s => Assert.Empty(s.Retro68Relocations));
        AssertRetro68Kinds(app, fork);
        // The largest kind-0 value in Runtime is its own length: offsets count from the resource start.
        var runtime = app.FindSegment(1)!;
        var r1 = new BigEndianReader(runtime.Data);
        Assert.Equal(0x13F70, runtime.Retro68Relocations.Where(r => r.Base == Retro68RelocationBase.Segment).Max(r => r1.ReadInt32At((int)r.Offset)));
        Assert.Equal(0x13F70, runtime.Data.Length);
    }

    [Fact]
    public void A_QDHarness_build_with_a_segment_relocation_in_a_far_segment()
    {
        // Another build (121,214 bytes): 'RELA' 2 holds one kind-0 relocation, so kind 0 is not only the Runtime's.
        var fork = ResourceFork.Read(CodeCorpus.Require("QDHarness", 121214));
        var diagnostics = new List<Diagnostic>();
        var app = CodeApplication.Read(fork, diagnostics);
        AssertClean(diagnostics);
        var main = app.FindSegment(2)!;
        Assert.Equal(576, main.Retro68Relocations.Count);
        var segment = Assert.Single(main.Retro68Relocations, r => r.Base == Retro68RelocationBase.Segment);
        Assert.Equal(0x3D2E, new BigEndianReader(main.Data).ReadInt32At((int)segment.Offset));
        AssertRetro68Kinds(app, fork);
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
