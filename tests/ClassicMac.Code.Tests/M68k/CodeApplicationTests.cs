using ClassicMac.Code.M68k;
using ClassicMac.Core;
using ClassicMac.Resources;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// CODE 0, the jump table, segments, the entry point and model detection, on hand-built forks.
public class CodeApplicationTests
{
    private static CodeApplication Read(ResourceFork fork, List<Diagnostic>? diagnostics = null,
        ResourceDecompression? decompression = null) => CodeApplication.Read(fork, diagnostics ?? [], decompression);

    // A near application: entry 0 → CODE 1 +0, entry 1 → CODE 1 +6, entry 2 → CODE 2 +0.
    private static ResourceFork NearApp(byte[]? code2 = null)
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), NearEntry(6, 1), NearEntry(0, 2)], below: 0x200));
        fork.Add("CODE", 1, Near(0, 2, Words(0x4E56, 0, 0x4E5E, 0x4E75, 0x4E71, 0x4E71, 0x4E75, 0x4E75)), "Main");
        fork.Add("CODE", 2, code2 ?? Near(0x10, 1, Words(0x4E75, 0x4E71)), "Other");
        return fork;
    }

    // The data an MPW %A5Init segment ends with: header (below, version 1, data at +16, relocs at +18), 00 00, 00 00, trailer.
    private static byte[] WithA5Init(byte[] segment, uint below)
    {
        var w = new BigEndianWriter();
        w.WriteBytes(segment);
        if (w.Length % 2 != 0) w.WriteByte(0);
        int header = w.Length;
        w.WriteUInt32(below);
        w.WriteUInt16(1);
        w.WriteUInt16(0);
        w.WriteUInt32(16);
        w.WriteUInt32(18);
        w.WriteBytes(Words(0, 0));
        w.WriteUInt32(header);
        w.WriteFourCC(FourCC.FromString("mpwd"));
        return w.ToArray();
    }

    [Fact]
    public void Reads_CODE_0()
    {
        var diagnostics = new List<Diagnostic>();
        var app = Read(NearApp(), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal((0x38u, 0x200u, 24u, 0x20u), (app.AboveA5, app.BelowA5, app.JumpTableSize, app.JumpTableOffset));
        Assert.Equal(3, app.JumpTable.Count);
        Assert.False(app.IsFarModel);
    }

    [Fact]
    public void Near_unloaded_entries_point_past_the_4_byte_header()
    {
        var app = Read(NearApp());
        var entry = app.JumpTable[1];
        Assert.Equal((1, 0x28, JumpTableEntryKind.NearUnloaded, (short)1, 6u), (entry.Index, entry.A5Offset, entry.Kind, entry.Segment, entry.Offset));
        Assert.Equal(10L, entry.ResourceOffset);
        Assert.Equal(0x00063F3C0001A9F0ul, entry.Raw);
    }

    [Fact]
    public void Loaded_entries_are_a_segment_and_a_JMP()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), LoadedEntry(1, 0x123456)]));
        fork.Add("CODE", 1, Near(0, 2, Words(0x4E75)));
        var entry = Read(fork).JumpTable[1];
        Assert.Equal((JumpTableEntryKind.NearLoaded, (short)1, 0x123456u), (entry.Kind, entry.Segment, entry.Address));
        Assert.Null(entry.ResourceOffset);
    }

    // A far application: entry 0 near (→ CODE 3, a near segment), the marker, far entries into CODE 1 and 2.
    private static ResourceFork FarApp()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 3), FarMarker, FarEntry(1, 0x28), FarEntry(1, 0x2C), FarEntry(2, 0x28),
            LoadedEntry(2, 0x40000)], below: 0x100));
        var code1 = Far(0x10, 2, 0, 0, Words(0x4E75, 0x4E75, 0x4E71, 0x4E71, 0, 0, 0, 0, 0x0400, 0x0204, 0x0000),
            a5Relocations: 0x28 + 16, pcRelocations: 0x28 + 18);
        fork.Add("CODE", 1, code1);
        fork.Add("CODE", 2, WithA5Init(Far(0x20, 2, 0, 0, Words(0x4E75, 0x4E75)), 0x100), "%A5Init");
        fork.Add("CODE", 3, Near(0, 1, Words(0x4E75)));
        return fork;
    }

    [Fact]
    public void The_far_marker_makes_the_rest_of_the_table_far()
    {
        var diagnostics = new List<Diagnostic>();
        var app = Read(FarApp(), diagnostics);
        Assert.Empty(diagnostics);
        Assert.True(app.IsFarModel);
        Assert.Equal([JumpTableEntryKind.NearUnloaded, JumpTableEntryKind.FarMarker, JumpTableEntryKind.FarUnloaded,
            JumpTableEntryKind.FarUnloaded, JumpTableEntryKind.FarUnloaded, JumpTableEntryKind.FarLoaded], app.JumpTable.Select(e => e.Kind));
        var far = app.JumpTable[3];
        Assert.Equal(((short)1, 0x2Cu, 0x2CL), (far.Segment, far.Offset, far.ResourceOffset));
        Assert.Equal((short)0, app.JumpTable[1].Segment);
        Assert.Null(app.JumpTable[1].ResourceOffset);
        Assert.Equal(0x40000u, app.JumpTable[5].Address);
    }

    [Fact]
    public void Far_segments_have_their_A5_and_PC_relocations()
    {
        var segment = Read(FarApp()).FindSegment(1)!;
        Assert.True(segment.Header!.IsFar);
        // The A5 list at $38 is 04 00 (delta 8, end); the PC list at $3A is 02 04 00 (deltas 4 and 8, end).
        Assert.Equal([8L], segment.A5Relocations);
        Assert.Equal([4L, 12L], segment.PcRelocations);
    }

    [Fact]
    public void The_entry_point_is_entry_0()
    {
        Assert.Equal(new CodeEntryPoint(1, 0, 4), Read(NearApp()).Entry);
        // In a far application entry 0 stays near.
        Assert.Equal(new CodeEntryPoint(3, 0, 4), Read(FarApp()).Entry);
        Assert.Null(Read(NearApp()).OriginalEntry);
    }

    [Fact]
    public void A_bootstrap_segment_in_entry_0_also_gives_the_saved_original_entry()
    {
        var fork = NearApp();
        fork.Remove(fork.Find(CodeBuilder.Code, 0)!);
        fork.Add("CODE", 0, Code0([NearEntry(0x0C, 66), NearEntry(6, 1), NearEntry(0, 2)]));
        // CODE 66: header 0000 0001, the table's A5 offset, the saved entry 0, then the code at +$10.
        var shim = new BigEndianWriter();
        shim.WriteBytes(Near(0, 1));
        shim.WriteUInt32(0x20);
        shim.WriteBytes(NearEntry(0x0A, 1));
        shim.WriteBytes(Words(0x4E75));
        fork.Add("CODE", 66, shim.ToArray());
        var app = Read(fork);
        Assert.Equal(new CodeEntryPoint(66, 0x0C, 0x10), app.Entry);
        Assert.Equal(new CodeEntryPoint(1, 0x0A, 0x0E), app.OriginalEntry);
    }

    [Fact]
    public void A5_displacements_two_past_an_entry_resolve_to_it()
    {
        var app = Read(NearApp());
        Assert.Same(app.JumpTable[0], app.ResolveA5(0x22));
        Assert.Same(app.JumpTable[2], app.ResolveA5(0x32));
        Assert.Equal(4L, app.ResolveA5(0x32)!.ResourceOffset);
    }

    [Theory]
    [InlineData(0x28)]   // the entry itself, not entry + 2
    [InlineData(0x26)]   // inside an entry
    [InlineData(0x3A)]   // past the table
    [InlineData(0x00)]   // the QuickDraw globals pointer
    [InlineData(0x02)]   // an application parameter
    [InlineData(-0x1E)]  // a global below A5
    public void Other_A5_displacements_are_globals(int displacement)
    {
        Assert.Null(Read(NearApp()).ResolveA5(displacement));
    }

    [Fact]
    public void Segments_keep_their_names_attributes_and_headers()
    {
        var app = Read(NearApp());
        Assert.Equal([(short)1, (short)2], app.Segments.Select(s => s.Id));
        var segment = app.FindSegment(2)!;
        Assert.Equal("Other", segment.Name);
        Assert.True(segment.IsReadable);
        Assert.False(segment.IsCompressed);
        Assert.Equal([2], segment.Header!.EntryIndices);
        Assert.Null(app.FindSegment(9));
    }

    [Fact]
    public void A_jump_table_size_not_a_multiple_of_8_is_reported()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), Words(0, 0)], jtSize: 12));
        fork.Add("CODE", 1, Near(0, 1, Words(0x4E75)));
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Single(app.JumpTable);
        Assert.Equal("m68k.jt-size", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_jump_table_past_CODE_0_is_truncated()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1)], jtSize: 16, above: 0x30));
        fork.Add("CODE", 1, Near(0, 1, Words(0x4E75)));
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Single(app.JumpTable);
        Assert.Equal("m68k.jt-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Above_A5_smaller_than_the_jump_table_is_reported()
    {
        var fork = NearApp();
        fork.Remove(fork.Find(CodeBuilder.Code, 0)!);
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), NearEntry(6, 1), NearEntry(0, 2)], above: 0x20));
        var diagnostics = new List<Diagnostic>();
        Read(fork, diagnostics);
        Assert.Equal("m68k.above-a5", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_unrecognized_entry_is_reported()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), Words(0x1234, 0x5678, 0x9ABC, 0xDEF0)]));
        fork.Add("CODE", 1, Near(0, 1, Words(0x4E75)));
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Equal(JumpTableEntryKind.Unrecognized, app.JumpTable[1].Kind);
        Assert.Equal("m68k.jt-entry", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_entry_into_a_missing_segment_is_reported()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), NearEntry(0, 7)]));
        fork.Add("CODE", 1, Near(0, 1, Words(0x4E75)));
        var diagnostics = new List<Diagnostic>();
        Read(fork, diagnostics);
        Assert.Equal("m68k.segment-missing", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_entry_past_its_segment_is_reported()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0x100, 1)]));
        fork.Add("CODE", 1, Near(0, 1, Words(0x4E75)));
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Equal("m68k.entry-range", Assert.Single(diagnostics).Code);
        Assert.Equal(new CodeEntryPoint(1, 0x100, 0x104), app.Entry);
    }

    [Fact]
    public void An_empty_jump_table_has_no_entry_point()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([]));
        var diagnostics = new List<Diagnostic>();
        Assert.Null(Read(fork, diagnostics).Entry);
        Assert.Equal("m68k.entry-missing", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_short_segment_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var app = Read(NearApp(code2: [0x00, 0x10]), diagnostics);
        Assert.Null(app.FindSegment(2)!.Header);
        Assert.Contains(diagnostics, d => d.Code == "m68k.segment-header");
    }

    [Fact]
    public void Without_CODE_0_there_is_no_application()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 1, Near(0, 1));
        Assert.Throws<InvalidDataException>(() => Read(fork));
        var shortFork = new ResourceFork();
        shortFork.Add("CODE", 0, new byte[15]);
        Assert.Throws<InvalidDataException>(() => Read(shortFork));
    }

    // A compressed resource: the version-9 header, then the payload, for the decompressor with ID 200 below.
    private static byte[] Compressed(byte[] payload, short dcmp = 200)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(CompressedResourceHeader.Signature);
        w.WriteUInt16(CompressedResourceHeader.Length);
        w.WriteByte(9);
        w.WriteByte(1);
        w.WriteUInt32(payload.Length);
        w.WriteInt16(dcmp);
        w.WriteUInt16(0);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteBytes(payload);
        return w.ToArray();
    }

    private sealed class Identity : IResourceDecompressor
    {
        public short Id => 200;

        public int Decompress(DecompressionContext context)
        {
            var input = context.Block[context.SourceOffset..context.BlockLength];
            input.CopyTo(context.Block, 0);
            return input.Length;
        }
    }

    [Fact]
    public void Compressed_segments_are_decompressed()
    {
        var fork = NearApp();
        var code2 = fork.Find(CodeBuilder.Code, 2)!;
        code2.SetData(Compressed(code2.GetData().ToArray()));
        code2.Attributes = ResourceAttributes.Compressed;
        var diagnostics = new List<Diagnostic>();
        var segment = Read(fork, diagnostics, new ResourceDecompression([new Identity()])).FindSegment(2)!;
        Assert.Empty(diagnostics);
        Assert.True(segment.IsCompressed);
        Assert.True(segment.IsReadable);
        Assert.Equal([2], segment.Header!.EntryIndices);
    }

    [Fact]
    public void A_compressed_segment_that_cannot_be_decompressed_is_reported_not_thrown()
    {
        var fork = NearApp();
        var code2 = fork.Find(CodeBuilder.Code, 2)!;
        code2.SetData(Compressed(code2.GetData().ToArray(), dcmp: 99));
        code2.Attributes = ResourceAttributes.Compressed;
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        var segment = app.FindSegment(2)!;
        Assert.False(segment.IsReadable);
        Assert.Null(segment.Header);
        Assert.Contains(diagnostics, d => d.Code == "code.compressed");
        Assert.Contains(diagnostics, d => d.Code == "resource.dcmp-unknown");
        // Entries into it are not checked against its bytes.
        Assert.DoesNotContain(diagnostics, d => d.Code.StartsWith("m68k.", StringComparison.Ordinal));
    }

    [Fact]
    public void Detects_the_MPW_near_model_by_its_A5Init_segment()
    {
        var fork = NearApp(code2: WithA5Init(Near(0x10, 1, Words(0x4E75)), 0x200));
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(CodeModel.MpwNear, app.Model);
        Assert.Equal((short)2, app.A5InitSegment);
        Assert.Equal(0x200u, app.A5Init!.BelowA5Size);
        Assert.False(app.HasPowerPCFragment);
    }

    [Fact]
    public void Detects_the_MPW_far_model()
    {
        var app = Read(FarApp());
        Assert.Equal(CodeModel.MpwFar, app.Model);
        Assert.Equal((short)2, app.A5InitSegment);
    }

    [Fact]
    public void Detects_Retro68_by_its_RELA_resources()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1), FarMarker, FarEntry(2, 0x28)]));
        fork.Add("CODE", 1, Near(0, 1, Zeros(16)), "Runtime");
        fork.Add("CODE", 2, Far(0, 0, 0x10, 1, Zeros(16)), "Main");
        fork.Add("DATA", 0, Zeros(8));
        fork.Add("RELA", 0, [(1 << 2) | 1, 0]);           // DATA +0, initialized data
        fork.Add("RELA", 1, [(3 << 2) | 0, 0]);           // CODE 1 +4 +2, code
        fork.Add("RELA", 2, [(5 << 2) | 3, 0]);           // CODE 2 +$28 +4, A5
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(CodeModel.Retro68, app.Model);
        Assert.Equal([new Retro68Relocation(0, Retro68RelocationBase.InitializedData)], app.DataRelocations);
        Assert.Equal([new Retro68Relocation(6, Retro68RelocationBase.Code)], app.FindSegment(1)!.Retro68Relocations);
        Assert.Equal([new Retro68Relocation(0x2C, Retro68RelocationBase.A5)], app.FindSegment(2)!.Retro68Relocations);
        Assert.Equal([2], app.FindSegment(2)!.Header!.EntryIndices);
    }

    [Fact]
    public void Detects_CodeWarrior_by_DATA_0_and_its_startup()
    {
        var fork = new ResourceFork();
        fork.Add("CODE", 0, Code0([NearEntry(0, 1)], above: 0x1000));
        fork.Add("CODE", 1, Near(0, 1, [0x9D, 0xCE, 0x59, 0x8F, 0x2F, 0x3C, (byte)'C', (byte)'O', (byte)'D', (byte)'E', 0x42, 0x67]),
            "ANSI Libraries");
        var data = new BigEndianWriter();
        data.WriteUInt32(4 + 15 + 12);
        for (int i = 0; i < 3; i++) data.WriteBytes(new byte[] { 0, 0, 0, 0, 0 });
        for (int i = 0; i < 6; i++) data.WriteUInt32(0);
        fork.Add("DATA", 0, data.ToArray());
        var diagnostics = new List<Diagnostic>();
        var app = Read(fork, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(CodeModel.CodeWarrior, app.Model);
        Assert.Equal(6, app.CodeWarriorData!.Relocations.Count);
    }

    [Fact]
    public void DATA_0_without_CodeWarrior_startup_is_not_CodeWarrior()
    {
        var fork = NearApp();
        fork.Add("DATA", 0, Zeros(8));
        var app = Read(fork);
        Assert.Equal(CodeModel.Unknown, app.Model);
        Assert.Null(app.CodeWarriorData);
    }

    [Fact]
    public void A_cfrg_0_makes_a_fat_application()
    {
        var fork = FarApp();
        fork.Add("cfrg", 0, Zeros(32));
        Assert.True(Read(fork).HasPowerPCFragment);
    }

    [Fact]
    public void Near_segments_without_an_initializer_are_an_unknown_model()
    {
        Assert.Equal(CodeModel.Unknown, Read(NearApp()).Model);
    }
}
