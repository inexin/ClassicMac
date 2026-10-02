using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.M68k;

// CodeWarrior 68k 'DATA' 0: code-reloc offset, 3 packed blocks (A5 offset, ops to 00), 6 relocation lists (count, deltas).
public class CodeWarriorDataTests
{
    private static readonly byte[] Block1 =
    [
        0xFF, 0xFF, 0xFF, 0xF0,   // A5 − 16
        0x82, 0xAA, 0xBB, 0xCC,   // copy 3
        0x41,                     // skip 2
        0x21, 0x77,               // fill $77 × 3
        0x12,                     // fill $FF × 3
        0x00,
    ];

    private static readonly byte[] Block2 = [0x00, 0x00, 0x00, 0x28, 0x00];

    private static readonly byte[] Block3 =
    [
        0x00, 0x00, 0x00, 0x30,
        0x01, 0x11, 0x22,               // skip 4, FF FF, 2 literals
        0x02, 0x33, 0x44, 0x55,         // skip 4, FF, 3 literals
        0x03, 0x66, 0x77, 0x88,         // A9 F0, skip 2, 2 literals, skip 1, 1 literal
        0x04, 0x99, 0xAA, 0xBB, 0xCC,   // A9 F0, skip 1, 3 literals, skip 1, 1 literal
        0x00,
    ];

    private static readonly byte[][] GlobalLists =
    [
        [0, 0, 0, 4, 0x81, 0xFF, 0x40, 0x10, 0x7F, 0xFF],     // +2, −2, +32, −2: 2, 0, 32, 30
        [0, 0, 0, 2, 0x00, 0x00, 0x12, 0x34, 0x3F, 0xFF, 0xFF, 0xFE], // absolute $2468, absolute −4
        [0, 0, 0, 0],
    ];

    private static readonly byte[][] CodeLists =
    [
        [0, 0, 0, 1, 0x82],
        [0, 0, 0, 2, 0x3F, 0xFF, 0xFF, 0xFE, 0x7F, 0xFF],
        [0, 0, 0, 0],
    ];

    private static byte[] Data(uint? codeRelocOffset = null, byte[]? block3 = null, bool truncate = false)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(0);
        w.WriteBytes(Block1);
        w.WriteBytes(Block2);
        w.WriteBytes(block3 ?? Block3);
        foreach (var list in GlobalLists) w.WriteBytes(list);
        int codeAt = w.Length;
        foreach (var list in CodeLists) w.WriteBytes(list);
        w.WriteUInt32At(0, codeRelocOffset ?? (uint)codeAt);
        var bytes = w.ToArray();
        return truncate ? bytes[..^3] : bytes;
    }

    [Fact]
    public void Reads_the_code_relocation_offset_and_the_three_blocks()
    {
        var data = Data();
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(data, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal((uint)(4 + Block1.Length + Block2.Length + Block3.Length + GlobalLists.Sum(l => l.Length)), cw.CodeRelocationOffset);
        Assert.Equal([(-16, -5), (0x28, 0x28), (0x30, 0x50)], cw.Blocks.Select(b => (b.Start, b.End)));
        Assert.Equal(data.Length, cw.End);
    }

    [Fact]
    public void The_short_ops_copy_skip_and_fill()
    {
        var block = CodeWarriorData.Read(Data(), []).Blocks[0];
        Assert.Equal([-16, -11, -8], block.Runs.Select(r => r.A5Offset));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, block.Runs[0].Bytes.ToArray());
        Assert.Equal(new byte[] { 0x77, 0x77, 0x77 }, block.Runs[1].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, block.Runs[2].Bytes.ToArray());
        Assert.Empty(CodeWarriorData.Read(Data(), []).Blocks[1].Runs);
    }

    [Fact]
    public void Ops_1_to_4_write_their_fixed_shapes()
    {
        var runs = CodeWarriorData.Read(Data(), []).Blocks[2].Runs;
        Assert.Equal(
        [
            (0x34, "FFFF1122"),
            (0x3C, "FF334455"),
            (0x40, "A9F0"), (0x44, "6677"), (0x47, "88"),
            (0x48, "A9F0"), (0x4B, "99AABB"), (0x4F, "CC"),
        ], runs.Select(r => (r.A5Offset, Convert.ToHexString(r.Bytes.Span))));
    }

    [Fact]
    public void Reads_the_six_relocation_lists_with_the_three_delta_forms()
    {
        var cw = CodeWarriorData.Read(Data(), []);
        Assert.Equal(Enum.GetValues<CodeWarriorRelocationKind>(), cw.Relocations.Select(r => r.Kind));
        Assert.Equal([2, 0, 32, 30], cw.Relocations[0].Offsets);
        Assert.Equal([0x2468, -4], cw.Relocations[1].Offsets);
        Assert.Empty(cw.Relocations[2].Offsets);
        Assert.Equal([4], cw.Relocations[3].Offsets);
        Assert.Equal([-4, -6], cw.Relocations[4].Offsets);
        Assert.Empty(cw.Relocations[5].Offsets);
    }

    [Fact]
    public void A_code_relocation_offset_that_misses_the_fourth_list_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        CodeWarriorData.Read(Data(codeRelocOffset: 2), diagnostics);
        Assert.Equal("m68k.cw-code-reloc-offset", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x0A)]
    [InlineData(0x0F)]
    public void Ops_5_to_15_are_the_startup_SysError_and_stop_reading(byte op)
    {
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(Data(block3: [0, 0, 0, 0x30, 0x81, 0x01, 0x02, op, 0x00]), diagnostics);
        Assert.Equal("m68k.cw-data-op", Assert.Single(diagnostics).Code);
        Assert.Equal(3, cw.Blocks.Count);
        Assert.Single(cw.Blocks[2].Runs);
        Assert.Empty(cw.Relocations);
    }

    [Fact]
    public void Data_cut_off_in_a_relocation_list_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(Data(truncate: true), diagnostics);
        Assert.Equal("m68k.cw-data-truncated", Assert.Single(diagnostics).Code);
        Assert.Equal(3, cw.Blocks.Count);
        Assert.Equal(5, cw.Relocations.Count);
    }

    [Fact]
    public void Data_cut_off_in_a_block_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(Data()[..(4 + Block1.Length + 3)], diagnostics);
        Assert.Equal("m68k.cw-data-truncated", Assert.Single(diagnostics).Code);
        Assert.Single(cw.Blocks);
    }

    [Fact]
    public void Data_cut_off_in_a_literal_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(Data()[..(4 + 4 + 2)], diagnostics);
        Assert.Equal("m68k.cw-data-truncated", Assert.Single(diagnostics).Code);
        Assert.Empty(cw.Blocks);
    }

    [Fact]
    public void A_resource_shorter_than_its_first_long_is_not_read()
    {
        Assert.Throws<InvalidDataException>(() => CodeWarriorData.Read(new byte[3], []));
    }
}
