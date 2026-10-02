using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.M68k;

// MPW far relocation lists: deltas from a running offset starting at 0; b < $80 → 2b; b ≥ $80 → 2·((b & $7F) << 8 | next); 0 ends.
public class FarRelocationsTests
{
    private static byte[] Segment(int length, int listAt, params byte[] list)
    {
        var bytes = new byte[Math.Max(length, listAt + list.Length)];
        list.CopyTo(bytes, listAt);
        return bytes;
    }

    [Fact]
    public void Deltas_are_one_or_two_bytes_doubled()
    {
        var diagnostics = new List<Diagnostic>();
        var offsets = FarRelocations.Read(Segment(0x300, 0x2F0, 0x05, 0x81, 0x00, 0x02, 0x00), 0x2F0, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal([0xAL, 0x20AL, 0x20EL], offsets);
    }

    [Fact]
    public void An_empty_list_is_its_terminator()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(FarRelocations.Read(Segment(0x40, 0x30, 0x00), 0x30, diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_list_without_its_terminator_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Segment(0x10, 0x0E, 0x02, 0x02);
        Assert.Equal([4L, 8L], FarRelocations.Read(data, 0x0E, diagnostics));
        Assert.Equal("m68k.far-reloc-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_two_byte_delta_cut_off_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Segment(0x10, 0x0F, 0x81);
        Assert.Empty(FarRelocations.Read(data, 0x0F, diagnostics));
        Assert.Equal("m68k.far-reloc-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void The_80_00_escape_is_unverified_and_stops_reading()
    {
        var diagnostics = new List<Diagnostic>();
        var offsets = FarRelocations.Read(Segment(0x40, 0x30, 0x02, 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00), 0x30, diagnostics);
        Assert.Equal([4L], offsets);
        Assert.Equal("m68k.far-reloc-escape", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_long_outside_the_segment_is_reported_and_left_out()
    {
        var diagnostics = new List<Diagnostic>();
        // The segment is $40 long: 2·$1E = $3C fits ($3C + 4 = $40); the next, $3E, does not.
        var offsets = FarRelocations.Read(Segment(0x40, 0x30, 0x1E, 0x01, 0x00), 0x30, diagnostics);
        Assert.Equal([0x3CL], offsets);
        Assert.Equal("m68k.far-reloc-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_list_offset_outside_the_segment_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(FarRelocations.Read(new byte[0x20], 0x20, diagnostics));
        Assert.Equal("m68k.far-reloc-offset", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void List_length_includes_the_terminator()
    {
        Assert.Equal(5, FarRelocations.ListLength(Segment(0x300, 0x2F0, 0x05, 0x81, 0x00, 0x02, 0x00), 0x2F0));
        Assert.Equal(1, FarRelocations.ListLength(Segment(0x40, 0x30, 0x00), 0x30));
        Assert.Equal(2, FarRelocations.ListLength(Segment(0x10, 0x0E, 0x02, 0x02), 0x0E));     // to the end: no terminator
        Assert.Equal(2, FarRelocations.ListLength(Segment(0x10, 0x0E, 0x02, 0x81), 0x0E));     // a cut two-byte delta
        Assert.Equal(2, FarRelocations.ListLength(Segment(0x40, 0x30, 0x80, 0x00, 0x00), 0x30)); // the escape stops it
        Assert.Equal(0, FarRelocations.ListLength(Segment(0x10, 0), 0x20));                      // outside
    }
}
