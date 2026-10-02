using ClassicMac.Code.M68k;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// The A9FF form: A9FF type id version flags first.b last.b offsets[...]; each routine at $0A + its offset (fitted to the
// Mac OS 9 System's resources).
public class PackageHeaderTests
{
    private static byte[] Package(string type, short id, ushort version, ushort flags, sbyte first, sbyte last, params ushort[] offsets)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(0xA9FF);
        w.WriteBytes(MacRoman.Encode(type));
        w.WriteInt16(id);
        w.WriteUInt16(version);
        w.WriteUInt16(flags);
        w.WriteByte(unchecked((byte)first));
        w.WriteByte(unchecked((byte)last));
        foreach (var o in offsets) w.WriteUInt16(o);
        w.WriteBytes(Words(0x4E56, 0, 0x4E5E, 0x4E75));
        return w.ToArray();
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Fact]
    public void Reads_one_offset_per_selector()
    {
        var diagnostics = new List<Diagnostic>();
        var header = PackageHeader.Read(Package("PACK", 15, 1, 0, 0, 2, 0x16, 0x18, 0x1A), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal((FourCC.FromString("PACK"), (short)15, (ushort)1, (ushort)0, (sbyte)0, (sbyte)2),
            (header.Type, header.Id, header.Version, header.Flags, header.FirstSelector, header.LastSelector));
        Assert.Equal([new PackageEntry(0, 0x16), new PackageEntry(1, 0x18), new PackageEntry(2, 0x1A)], header.Entries);
        Assert.Equal([0x20L, 0x22L, 0x24L], header.Entries.Select(e => e.TargetOffset));
    }

    [Fact]
    public void Offsets_count_from_the_flags_word_at_0A()
    {
        // A9FF 'PACK' 000F 0001 0000 00 00 | 0006 | 4E56 0000 4E5E 4E75: selector 0's routine is at $0A + 6 = $10, the LINK.
        var data = Bytes("A9FF 5041 434B 000F 0001 0000 0000 0006 4E56 0000 4E5E 4E75");
        var entry = Assert.Single(PackageHeader.Read(data, [])!.Entries);
        Assert.Equal((0, (ushort)6, (long?)0x10), (entry.Selector, entry.Offset, entry.TargetOffset));
        Assert.Equal(0x4E56, new BigEndianReader(data).ReadUInt16At((int)entry.TargetOffset!.Value));
        Assert.Equal(0x0A, PackageHeader.DispatchBase);
    }

    [Fact]
    public void A_zero_offset_has_no_routine()
    {
        var header = PackageHeader.Read(Package("PACK", 13, 4, 0, 0, 1, 0x10, 0), [])!;
        Assert.Equal([(long?)0x1A, null], header.Entries.Select(e => e.TargetOffset));
    }

    [Fact]
    public void Selectors_may_be_negative()
    {
        var header = PackageHeader.Read(Package("proc", -16516, 4, 0, -128, -127, 0x0E, 0x10), [])!;
        Assert.Equal([-128, -127], header.Entries.Select(e => e.Selector));
    }

    [Fact]
    public void Flags_bit_0_steps_the_selectors_by_2()
    {
        var header = PackageHeader.Read(Package("proc", -16498, 1, 1, 0, 4, 0x14, 0x16, 0x18), [])!;
        Assert.Equal([new PackageEntry(0, 0x14), new PackageEntry(2, 0x16), new PackageEntry(4, 0x18)], header.Entries);
    }

    [Fact]
    public void Stepping_by_2_stops_at_the_last_selector_even_when_it_is_odd()
    {
        // first 0, last 5: selectors 0, 2, 4.
        var header = PackageHeader.Read(Package("proc", -16498, 1, 1, 0, 5, 0x14, 0x16, 0x18), [])!;
        Assert.Equal([0, 2, 4], header.Entries.Select(e => e.Selector));
    }

    [Fact]
    public void Stepping_by_2_from_a_negative_to_a_positive_selector()
    {
        // PACK 11's range: -12 to 60 by 2, 37 entries.
        var header = PackageHeader.Read(Package("PACK", 11, 0x11, 1, -12, 60, [.. Enumerable.Repeat((ushort)0x60, 37)]), [])!;
        Assert.Equal(37, header.Entries.Count);
        Assert.Equal((-12, 0, 60), (header.Entries[0].Selector, header.Entries[6].Selector, header.Entries[^1].Selector));
    }

    [Fact]
    public void The_version_and_flags_are_kept_as_stored()
    {
        var header = PackageHeader.Read(Package("PACK", 8, 0x68, 0x8001, 0, 0, 0x10), [])!;
        Assert.Equal(((ushort)0x68, (ushort)0x8001), (header.Version, header.Flags));
    }

    [Fact]
    public void Other_resources_are_not_the_A9FF_form()
    {
        Assert.Null(PackageHeader.Read(Words(0x600A, 0, 0x5041, 0x434B, 2, 0x15, 0x4E75), []));
    }

    [Fact]
    public void A_table_past_the_resource_is_reported_and_the_rest_read()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Package("PACK", 9, 0, 0, 0, 9, 0x20, 0x22);
        var header = PackageHeader.Read(data[..(PackageHeader.TableOffset + 4)], diagnostics)!;
        Assert.Equal(2, header.Entries.Count);
        Assert.Equal("m68k.package-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_last_selector_before_the_first_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(PackageHeader.Read(Package("PACK", 9, 0, 0, 5, 1), diagnostics)!.Entries);
        Assert.Equal("m68k.package-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_resource_shorter_than_the_header_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(PackageHeader.Read(Package("PACK", 9, 0, 0, 0, 0)[..13], diagnostics));
        Assert.Equal("m68k.package-truncated", Assert.Single(diagnostics).Code);
    }
}
