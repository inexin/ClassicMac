using ClassicMac.Code.M68k;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// Near: first entry offset (from the jump table), count. Far ($FFFF): two (offset, count) pairs, A5 and PC reloc lists.
public class SegmentHeaderTests
{
    [Fact]
    public void A_near_header_is_the_first_entry_offset_and_count()
    {
        var diagnostics = new List<Diagnostic>();
        var header = SegmentHeader.Read(Near(0x2E8, 0x0F, 0x4E, 0x75), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.False(header.IsFar);
        Assert.Equal(4, header.Length);
        Assert.Equal((0x2E8u, 0x0Fu), (header.FirstNearOffset, header.NearCount));
        Assert.Equal(Enumerable.Range(0x2E8 / 8, 15), header.EntryIndices);
    }

    [Fact]
    public void A_far_header_has_both_pairs_and_the_relocation_lists()
    {
        var header = SegmentHeader.Read(Far(0x10, 0x47, 0, 0, [0x4E, 0x75], a5Relocations: 0x2C78, pcRelocations: 0x2CD6), [])!;
        Assert.True(header.IsFar);
        Assert.Equal(0x28, header.Length);
        Assert.Equal((0x10u, 0x47u, 0u, 0u), (header.FirstNearOffset, header.NearCount, header.FirstFarOffset, header.FarCount));
        Assert.Equal((0x2C78u, 0x11111111u, 0x2CD6u, 0x22222222u),
            (header.A5RelocationOffset, header.A5AtLastRelocation, header.PcRelocationOffset, header.AddressAtLastRelocation));
        Assert.Equal(Enumerable.Range(2, 0x47), header.EntryIndices);
    }

    [Fact]
    public void Every_far_field_is_read_from_its_own_offset()
    {
        // FFFF 0000 | 00000010 00000047 | 00000018 00000002 | 00002C78 11111111 | 00002CD6 22222222 | 33333333
        var data = Convert.FromHexString("FFFF0000" + "0000001000000047" + "0000001800000002" + "00002C7811111111"
            + "00002CD622222222" + "33333333" + "4E75");
        var diagnostics = new List<Diagnostic>();
        Assert.Equal(new SegmentHeader(true, 0x10, 0x47, 0x18, 2, 0x2C78, 0x11111111, 0x2CD6, 0x22222222),
            SegmentHeader.Read(data, diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(Enumerable.Range(2, 0x47), SegmentHeader.Read(data, [])!.EntryIndices);
    }

    [Fact]
    public void A_far_header_using_the_second_pair_owns_those_entries()
    {
        var header = SegmentHeader.Read(Far(0, 0, 0xD8, 1, []), [])!;
        Assert.Equal([0xD8 / 8], header.EntryIndices);
    }

    [Fact]
    public void A_far_header_with_both_pairs_owns_their_union()
    {
        var header = SegmentHeader.Read(Far(0x10, 2, 0x18, 2, []), [])!;
        Assert.Equal([2, 3, 4], header.EntryIndices);
    }

    [Fact]
    public void A_far_header_with_both_counts_zero_owns_nothing()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(SegmentHeader.Read(Far(0, 0, 0xE8, 0, Zeros(0x28)), diagnostics)!.EntryIndices);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_first_entry_offset_off_the_8_byte_grid_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Equal([1], SegmentHeader.Read(Near(0x0C, 1), diagnostics)!.EntryIndices);
        Assert.Equal("m68k.segment-entry-offset", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 })]
    public void A_segment_shorter_than_its_header_is_reported(byte[] data)
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(SegmentHeader.Read(data, diagnostics));
        Assert.Equal("m68k.segment-header", Assert.Single(diagnostics).Code);
    }
}
