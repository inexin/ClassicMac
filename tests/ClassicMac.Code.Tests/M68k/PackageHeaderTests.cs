using ClassicMac.Code.M68k;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// The A9FF form: A9FF type id version flags first.b last.b offsets[...] (fitted to the Mac OS 9 System's resources).
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

    [Fact]
    public void Reads_one_offset_per_selector()
    {
        var diagnostics = new List<Diagnostic>();
        var header = PackageHeader.Read(Package("PACK", 15, 1, 0, 0, 2, 0x16, 0x18, 0x1A), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal((FourCC.FromString("PACK"), (short)15, (ushort)1, (ushort)0, (sbyte)0, (sbyte)2),
            (header.Type, header.Id, header.Version, header.Flags, header.FirstSelector, header.LastSelector));
        Assert.Equal([new PackageEntry(0, 0x16), new PackageEntry(1, 0x18), new PackageEntry(2, 0x1A)], header.Entries);
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
