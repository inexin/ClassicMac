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
        foreach (var list in GlobalLists)
        {
            w.WriteBytes(list);
        }

        int codeAt = w.Length;
        foreach (var list in CodeLists)
        {
            w.WriteBytes(list);
        }

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

    // codeRelocOffset, three blocks, six lists, with the fourth list at codeRelocOffset.
    private static byte[] Build(byte[][] blocks, byte[][] lists)
    {
        var w = new BigEndianWriter();
        w.WriteUInt32(0);
        foreach (var block in blocks)
        {
            w.WriteBytes(block);
        }

        for (int i = 0; i < lists.Length; i++)
        {
            if (i == 3)
            {
                w.WriteUInt32At(0, w.Length);
            }

            w.WriteBytes(lists[i]);
        }
        return w.ToArray();
    }

    private static readonly byte[] EmptyList = [0, 0, 0, 0];

    [Fact]
    public void Each_short_op_at_its_limits()
    {
        // Block 1 at A5 + 0: FF copies 128; 7F skips 64; 3F 55 fills $55 × 33; 1F fills $FF × 16.
        var literal = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        byte[] block1 = [0, 0, 0, 0, 0xFF, .. literal, 0x7F, 0x3F, 0x55, 0x1F, 0x00];
        // Block 2 at A5 + $200: 80 copies 1; 40 skips 1; 20 55 fills $55 × 2; 10 fills $FF × 1.
        byte[] block2 = [0, 0, 2, 0, 0x80, 0xEE, 0x40, 0x20, 0x55, 0x10, 0x00];
        var diagnostics = new List<Diagnostic>();
        var cw = CodeWarriorData.Read(Build([block1, block2, [0, 0, 0, 0, 0]], [.. Enumerable.Repeat(EmptyList, 6)]), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal((0, 0xF1), (cw.Blocks[0].Start, cw.Blocks[0].End));
        Assert.Equal(
        [
            (0, Convert.ToHexString(literal)),
            (0xC0, new string('5', 66)),
            (0xE1, new string('F', 32)),
        ], cw.Blocks[0].Runs.Select(r => (r.A5Offset, Convert.ToHexString(r.Bytes.Span))));
        Assert.Equal((0x200, 0x205), (cw.Blocks[1].Start, cw.Blocks[1].End));
        Assert.Equal([(0x200, "EE"), (0x202, "5555"), (0x204, "FF")],
            cw.Blocks[1].Runs.Select(r => (r.A5Offset, Convert.ToHexString(r.Bytes.Span))));
    }

    [Fact]
    public void The_delta_forms_at_their_limits()
    {
        // 40 00 +0; 5F FF +$3FFE; 60 00 -$4000; 20 00 00 00 = -$40000000; 1F FF FF FF = $3FFFFFFE; BF +$7E; C0 -$80.
        byte[] list = [0, 0, 0, 7, 0x40, 0x00, 0x5F, 0xFF, 0x60, 0x00, 0x20, 0x00, 0x00, 0x00, 0x1F, 0xFF, 0xFF, 0xFF, 0xBF, 0xC0];
        var empty = new byte[] { 0, 0, 0, 0, 0 };
        var cw = CodeWarriorData.Read(Build([empty, empty, empty], [list, .. Enumerable.Repeat(EmptyList, 5)]), []);
        Assert.Equal([0, 0x3FFE, -2, -0x40000000, 0x3FFFFFFE, 0x4000007C, 0x3FFFFFFC], cw.Relocations[0].Offsets);
    }

    [Fact]
    public void A_count_with_its_top_bit_set_reads_no_entries_and_goes_on()
    {
        // The startup loops while the count is above 0, signed: $80000000 reads nothing; the five lists after it are read.
        var empty = new byte[] { 0, 0, 0, 0, 0 };
        var diagnostics = new List<Diagnostic>();
        var data = Build([empty, empty, empty], [[0x80, 0, 0, 0], .. Enumerable.Repeat(EmptyList, 5)]);
        var cw = CodeWarriorData.Read(data, diagnostics);
        Assert.Equal(6, cw.Relocations.Count);
        Assert.All(cw.Relocations, r => Assert.Empty(r.Offsets));
        Assert.Equal(data.Length, cw.End);
        Assert.Equal("m68k.cw-reloc-count", Assert.Single(diagnostics).Code);
    }
}
