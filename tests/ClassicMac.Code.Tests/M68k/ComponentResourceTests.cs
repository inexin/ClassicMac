using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.M68k;

// 'thng': ComponentDescription (20) + 4 ResourceSpecs (24) = 44; extended: version, registerFlags, iconFamily,
// platform count, then 12-byte platform entries.
public class ComponentResourceTests
{
    private static readonly FourCC Str = FourCC.FromString("STR ");

    private static byte[] Thng(bool extended = false, (string Type, short Id, short Platform)[]? platforms = null, int? count = null)
    {
        var w = new BigEndianWriter();
        w.WriteFourCC(FourCC.FromString("imdc"));
        w.WriteFourCC(FourCC.FromString("yuvs"));
        w.WriteFourCC(FourCC.FromString("appl"));
        w.WriteUInt32(0x80000000);
        w.WriteUInt32(0);
        Spec(w, "cdek", -21003);
        Spec(w, "STR ", -21003);
        Spec(w, "STR ", -21004);
        Spec(w, "ICON", 0);
        if (extended || platforms is not null)
        {
            w.WriteUInt32(0x00010001);
            w.WriteUInt32(0x0B);
            w.WriteInt16(-21005);
            w.WriteUInt32(count ?? platforms?.Length ?? 0);
            foreach (var (type, id, platform) in platforms ?? [])
            {
                w.WriteUInt32(0x80000000);
                Spec(w, type, id);
                w.WriteInt16(platform);
            }
        }
        return w.ToArray();
    }

    private static void Spec(BigEndianWriter w, string type, short id)
    {
        w.WriteFourCC(FourCC.FromString(type));
        w.WriteInt16(id);
    }

    [Fact]
    public void Reads_the_basic_form()
    {
        var diagnostics = new List<Diagnostic>();
        var thng = ComponentResource.Read(Thng(), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(("imdc", "yuvs", "appl"), (thng.Type.ToString(), thng.SubType.ToString(), thng.Manufacturer.ToString()));
        Assert.Equal((0x80000000u, 0u), (thng.Flags, thng.FlagsMask));
        Assert.Equal(new ResourceSpec(FourCC.FromString("cdek"), -21003), thng.Code);
        Assert.Equal(new ResourceSpec(Str, -21003), thng.Name);
        Assert.Equal(new ResourceSpec(Str, -21004), thng.Info);
        Assert.Equal(new ResourceSpec(FourCC.FromString("ICON"), 0), thng.Icon);
        Assert.False(thng.IsExtended);
        Assert.Empty(thng.Platforms);
    }

    [Fact]
    public void Reads_the_extended_form_with_its_platforms()
    {
        var diagnostics = new List<Diagnostic>();
        var thng = ComponentResource.Read(Thng(platforms: [("sift", 128, 1), ("nift", 128, 2)]), diagnostics);
        Assert.Empty(diagnostics);
        Assert.True(thng.IsExtended);
        Assert.Equal((0x00010001u, 0x0Bu, (short)-21005), (thng.Version, thng.RegisterFlags, thng.IconFamily));
        Assert.Equal(
        [
            new ComponentPlatform(0x80000000, new ResourceSpec(FourCC.FromString("sift"), 128), ComponentPlatform.M68k),
            new ComponentPlatform(0x80000000, new ResourceSpec(FourCC.FromString("nift"), 128), ComponentPlatform.PowerPC),
        ], thng.Platforms);
    }

    [Fact]
    public void An_extended_form_may_list_no_platforms()
    {
        var thng = ComponentResource.Read(Thng(extended: true), []);
        Assert.True(thng.IsExtended);
        Assert.Empty(thng.Platforms);
    }

    [Fact]
    public void Extended_fields_cut_short_are_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var thng = ComponentResource.Read(Thng(extended: true)[..50], diagnostics);
        Assert.False(thng.IsExtended);
        Assert.Equal("m68k.thng-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Platforms_past_the_resource_are_reported_and_the_rest_read()
    {
        var diagnostics = new List<Diagnostic>();
        var thng = ComponentResource.Read(Thng(platforms: [("sift", 128, 1)], count: 3), diagnostics);
        Assert.Single(thng.Platforms);
        Assert.Equal("m68k.thng-platform-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_resource_shorter_than_the_basic_form_is_not_read()
    {
        Assert.Throws<InvalidDataException>(() => ComponentResource.Read(Thng()[..43], []));
    }
}
