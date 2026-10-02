using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.M68k;

// MPW %A5Init: trailer (header offset, 'mpwd'); header (belowA5 size, version, reserved, data offset, reloc offset);
// packed data and relocations with MPW's varint.
public class MpwA5InitTests
{
    private const int Prefix = 6;

    private static byte[] Segment(uint below, byte[] data, byte[] relocs, ushort version = 1, uint? dataOffset = null,
        uint? relocOffset = null, uint? headerOffset = null)
    {
        var w = new BigEndianWriter();
        w.WriteBytes(new byte[] { 0x4E, 0x71, 0x4E, 0x71, 0x4E, 0x75 }); // some code before the data
        w.WriteUInt32(below);
        w.WriteUInt16(version);
        w.WriteUInt16(0);
        w.WriteUInt32(dataOffset ?? 16u);
        w.WriteUInt32(relocOffset ?? (uint)(16 + data.Length));
        w.WriteBytes(data);
        w.WriteBytes(relocs);
        if (w.Length % 2 != 0)
        {
            w.WriteByte(0);
        }

        w.WriteUInt32(headerOffset ?? Prefix);
        w.WriteFourCC(FourCC.FromString("mpwd"));
        return w.ToArray();
    }

    // Four runs: a nibble count and skip; a varint count; a varint skip; a repeated run (the $Fx pair), then the end.
    private static readonly byte[] Data =
    [
        0x21, 0xA1, 0xA2,                   // skip 4, copy 2
        0x10, 0x05, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, // count varint 5, skip 2
        0x01, 0x80, 0x03, 0xC1, 0xC2,       // copy 2, skip varint 3 (14-bit form)
        0x00, 0xF0, 0x01, 0x02, 0x02, 0xD1, 0xD2, // count varint 1 repeated 2, skip varint 2: twice skip 2, copy 1
        0x00, 0x00,                          // count varint 0: the end
    ];

    // Relocations from the base (A5 − $400): +4; +$200; delta 3 twice; a 4-byte delta (top bit lost in 32-bit maths); end.
    private static readonly byte[] Relocs =
    [
        0x02,
        0x81, 0x00,
        0x00, 0x03, 0x02,
        0x00, 0x80, 0x00, 0x00, 0x08,
        0x00, 0x00,
    ];

    [Fact]
    public void The_trailer_is_the_header_offset_then_mpwd()
    {
        Assert.True(MpwA5Init.HasTrailer(Segment(0x400, Data, Relocs)));
        Assert.False(MpwA5Init.HasTrailer(new byte[] { 0, 0, 0, 6, (byte)'m', (byte)'p', (byte)'w', (byte)'x' }));
        Assert.False(MpwA5Init.HasTrailer(new byte[] { (byte)'m', (byte)'p', (byte)'w', (byte)'d' }));
    }

    [Fact]
    public void Reads_the_header()
    {
        var segment = Segment(0x400, Data, Relocs);
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(segment, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(Prefix, init.HeaderOffset);
        Assert.Equal(0x400u, init.BelowA5Size);
        Assert.Equal((ushort)1, init.Version);
        Assert.Equal(Prefix + 16, init.DataOffset);
        Assert.Equal(Prefix + 16 + Data.Length, init.RelocationOffset);
        Assert.Equal(Prefix + 16 + Data.Length, init.DataEnd);
        Assert.Equal(Prefix + 16 + Data.Length + Relocs.Length, init.RelocationEnd);
    }

    [Fact]
    public void Unpacks_the_runs_with_every_count_and_skip_form()
    {
        var init = MpwA5Init.Read(Segment(0x400, Data, Relocs), []);
        Assert.Equal([4 - 0x400, 8 - 0x400, 16 - 0x400, 20 - 0x400, 23 - 0x400], init.Runs.Select(r => r.A5Offset));
        Assert.Equal(new byte[] { 0xA1, 0xA2 }, init.Runs[0].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xB1, 0xB2, 0xB3, 0xB4, 0xB5 }, init.Runs[1].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xC1, 0xC2 }, init.Runs[2].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xD1 }, init.Runs[3].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xD2 }, init.Runs[4].Bytes.ToArray());
    }

    [Fact]
    public void Reads_the_relocations_with_every_form()
    {
        var init = MpwA5Init.Read(Segment(0x400, Data, Relocs), []);
        Assert.Equal([4 - 0x400, 0x204 - 0x400, 0x20A - 0x400, 0x210 - 0x400, 0x220 - 0x400], init.Relocations);
    }

    [Theory]
    [InlineData(new byte[] { 0x7F }, 0x7Fu, null)]
    [InlineData(new byte[] { 0x92, 0x34 }, 0x1234u, null)]
    [InlineData(new byte[] { 0xC1, 0x23, 0x45 }, 0x12345u, null)]
    [InlineData(new byte[] { 0xBF, 0xFF }, 0x3FFFu, null)]
    [InlineData(new byte[] { 0xDF, 0xFF, 0xFF }, 0x1FFFFFu, null)]
    [InlineData(new byte[] { 0x80, 0x00 }, 0u, null)]
    [InlineData(new byte[] { 0xE0, 0x12, 0x34, 0x56, 0x78 }, 0x12345678u, null)]
    [InlineData(new byte[] { 0xE5, 0x12, 0x34, 0x56, 0x78 }, 0x12345678u, null)]  // the 1110 form's low nibble is not used
    [InlineData(new byte[] { 0xEF, 0xFF, 0xFF, 0xFF, 0xFF }, 0xFFFFFFFFu, null)]
    [InlineData(new byte[] { 0xF0, 0x05, 0x83, 0x00 }, 5u, 0x300u)]
    public void The_varint_forms(byte[] bytes, uint value, uint? repeat)
    {
        var reader = new BigEndianReader(bytes);
        Assert.Equal(value, MpwA5Init.ReadVarint(reader, out var count));
        Assert.Equal(repeat, count);
        Assert.Equal(bytes.Length, reader.Position);
    }

    [Fact]
    public void A_segment_without_the_trailer_is_not_read()
    {
        Assert.Throws<InvalidDataException>(() => MpwA5Init.Read(new byte[16], []));
    }

    [Fact]
    public void A_version_other_than_1_fails_as_the_initializer_does()
    {
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x400, Data, Relocs, version: 2), diagnostics);
        Assert.Equal((ushort)2, init.Version);
        Assert.Empty(init.Runs);
        Assert.Empty(init.Relocations);
        Assert.Equal("m68k.a5init-version", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_header_outside_the_segment_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x400, Data, Relocs, headerOffset: 0x1000), diagnostics);
        Assert.Empty(init.Runs);
        Assert.Equal("m68k.a5init-header", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Packed_data_running_past_the_segment_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        // Data offset points at the last byte before the trailer: a copy of 15 bytes cannot be read.
        var segment = Segment(0x400, [0x0F], [0x00, 0x00]);
        MpwA5Init.Read(segment, diagnostics);
        Assert.Contains(diagnostics, d => d.Code == "m68k.a5init-truncated");
    }

    [Fact]
    public void Data_and_relocation_offsets_outside_the_segment_are_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        MpwA5Init.Read(Segment(0x400, Data, Relocs, dataOffset: 0x9000, relocOffset: 0x9000), diagnostics);
        Assert.Equal(["m68k.a5init-truncated", "m68k.a5init-truncated"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void A_run_past_the_globals_is_reported_and_stops_the_data()
    {
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(4, [0x21, 0xA1, 0xA2, 0x00, 0x00], [0x00, 0x00]), diagnostics);
        Assert.Empty(init.Runs);
        Assert.Equal("m68k.a5init-data-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_relocation_past_the_globals_is_reported_and_left_out()
    {
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(8, [0x00, 0x00], [0x02, 0x01, 0x00, 0x00]), diagnostics);
        Assert.Equal([4 - 8], init.Relocations);
        Assert.Equal("m68k.a5init-reloc-range", Assert.Single(diagnostics).Code);
    }

    private static IEnumerable<(int, string)> Runs(MpwA5Init init) =>
        init.Runs.Select(r => (r.A5Offset, Convert.ToHexString(r.Bytes.Span)));

    [Fact]
    public void A_repeat_on_the_skip_varint_repeats_the_skip_and_copy()
    {
        // 00 03: count varint 3; skip varint F0 02 03: skip 2, repeat 3. Runs at 2, 7, 12.
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x100, [0x00, 0x03, 0xF0, 0x02, 0x03, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x00, 0x00],
            [0x00, 0x00]), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal([(2 - 0x100, "AABBCC"), (7 - 0x100, "DDEEFF"), (12 - 0x100, "112233")], Runs(init));
    }

    [Fact]
    public void With_a_repeat_on_both_varints_the_second_wins()
    {
        // Count F0 01 02 (1, repeat 2), skip F0 04 03 (4, repeat 3): three runs, at 4, 9 and 14.
        var init = MpwA5Init.Read(Segment(0x100, [0x00, 0xF0, 0x01, 0x02, 0xF0, 0x04, 0x03, 0xA1, 0xA2, 0xA3, 0x00, 0x00], [0x00, 0x00]), []);
        Assert.Equal([(4 - 0x100, "A1"), (9 - 0x100, "A2"), (14 - 0x100, "A3")], Runs(init));
    }

    [Fact]
    public void A_relocation_count_varint_with_a_repeat_uses_its_value()
    {
        // 00 05 F0 03 07: delta 5, count 3 (the repeat 7 is not used): 10, 20, 30.
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x100, [0x00, 0x00], [0x00, 0x05, 0xF0, 0x03, 0x07, 0x00, 0x00]), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal([10 - 0x100, 20 - 0x100, 30 - 0x100], init.Relocations);
    }

    [Fact]
    public void A_repeat_of_0_does_the_run_once_then_is_reported()
    {
        // F0 01 00: count 1, repeat 0. The routine copies once, then its count wraps (SUBQ.L #1; BNE); reading stops.
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x100, [0x10, 0xF0, 0x01, 0x00, 0xA1, 0x11, 0xB1, 0xB2, 0x00, 0x00], [0x00, 0x00]), diagnostics);
        Assert.Equal([(2 - 0x100, "A1")], Runs(init));
        Assert.Equal("m68k.a5init-count-zero", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_relocation_count_of_0_patches_once_then_is_reported()
    {
        // 00 05 00: delta 5, count 0: the routine patches +10, then its count wraps; reading stops.
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(0x100, [0x00, 0x00], [0x00, 0x05, 0x00, 0x02, 0x00, 0x00]), diagnostics);
        Assert.Equal([10 - 0x100], init.Relocations);
        Assert.Equal("m68k.a5init-count-zero", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_run_ending_at_the_end_of_the_globals_fits_and_one_byte_more_does_not()
    {
        // below 4: skip 2, copy 2 ends at 4.
        var diagnostics = new List<Diagnostic>();
        Assert.Equal([(2 - 4, "A1A2")], Runs(MpwA5Init.Read(Segment(4, [0x11, 0xA1, 0xA2, 0x00, 0x00], [0x00, 0x00]), diagnostics)));
        Assert.Empty(diagnostics);
        // below 3: the same run ends one byte past.
        Assert.Empty(MpwA5Init.Read(Segment(3, [0x11, 0xA1, 0xA2, 0x00, 0x00], [0x00, 0x00]), diagnostics).Runs);
        Assert.Equal("m68k.a5init-data-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_relocation_at_the_last_long_of_the_globals_fits()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Equal([-4], MpwA5Init.Read(Segment(8, [0x00, 0x00], [0x02, 0x00, 0x00]), diagnostics).Relocations);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_count_varint_80_00_ends_the_data_and_a_skip_varint_80_00_skips_nothing()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(MpwA5Init.Read(Segment(0x100, [0x00, 0x80, 0x00, 0x01, 0xA1, 0x00, 0x00], [0x00, 0x00]), diagnostics).Runs);
        Assert.Equal([(0 - 0x100, "A1A2")], Runs(MpwA5Init.Read(Segment(0x100, [0x01, 0x80, 0x00, 0xA1, 0xA2, 0x00, 0x00], [0x00, 0x00]), diagnostics)));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Relocations_past_the_globals_are_reported_once_per_list()
    {
        // below 8: +4 fits; +8, +12, +16 do not.
        var diagnostics = new List<Diagnostic>();
        var init = MpwA5Init.Read(Segment(8, [0x00, 0x00], [0x02, 0x02, 0x02, 0x02, 0x00, 0x00]), diagnostics);
        Assert.Equal([4 - 8], init.Relocations);
        Assert.Equal("m68k.a5init-reloc-range", Assert.Single(diagnostics).Code);
    }
}
