using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// Which icon the Finder shows for an item (docs/formats/file-systems/finder-windows.md §2.3).
public class FinderIconResolverTests
{
    private static readonly FourCC Text = FourCC.FromString("TEXT"), Appl = FourCC.FromString("APPL"), Creator = FourCC.FromString("ABCD"),
        Other = FourCC.FromString("WXYZ");

    private static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];

    // An ICN# whose first byte tells the icons apart.
    private static byte[] Icon(byte marker)
    {
        var icon = FinderWindowTests.SolidIcon();
        icon[0] = marker;
        return icon;
    }

    private static ResourceFork Fork(params (string Type, short Id, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, data) in resources) fork.Add(new Resource(FourCC.FromString(type), id, data));
        return fork;
    }

    // A bundle with signature `signature`: FREF map 0 -> 128, 1 -> 129; ICN# map 0 -> 200, 1 -> 201.
    private static byte[] Bundle(string signature) =>
    [
        .. Encoding.ASCII.GetBytes(signature), .. BE16(0), .. BE16(1),
        .. "FREF"u8, .. BE16(1), .. BE16(0), .. BE16(128), .. BE16(1), .. BE16(129),
        .. "ICN#"u8, .. BE16(1), .. BE16(0), .. BE16(200), .. BE16(1), .. BE16(201),
    ];

    private static byte[] Fref(string type, short localIcon) => [.. Encoding.ASCII.GetBytes(type), .. BE16(localIcon), 0];

    private static ResourceFork Application(string signature = "ABCD") => Fork(
        ("BNDL", 128, Bundle(signature)),
        ("FREF", 128, Fref("APPL", 0)),
        ("FREF", 129, Fref("TEXT", 1)),
        ("ICN#", 200, Icon(0xA0)),
        ("ICN#", 201, Icon(0xA1)));

    private static ResourceFork System() => Fork(
        ("ICN#", FinderIconResolver.GenericDocumentId, Icon(0xD0)),
        ("ICN#", FinderIconResolver.GenericFolderId, Icon(0xF0)),
        ("ICN#", FinderIconResolver.GenericApplicationId, Icon(0xAA)));

    private static byte Marker(FinderIcon icon) => icon.Suite!.Members["ICN#"][0];

    private static FinderIconResolver Resolver(params FinderBundleSource[] bundles) => new(bundles, [System()]);

    [Fact]
    public void A_custom_icon_comes_first()
    {
        var own = Fork(("ICN#", FinderIconResolver.CustomIconId, Icon(0xC0)));

        var icon = Resolver(new FinderBundleSource(Creator, () => Application())).Find(FinderItemKind.Document, Text, Creator,
            FinderIconResolver.HasCustomIconFlag, () => own);

        Assert.Equal((FinderIconSource.Custom, (byte)0xC0), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void A_custom_icon_may_be_an_icon_family()
    {
        // 'icns': the header, then an ICN# element of 8 + 256 bytes.
        byte[] icns = [.. "icns"u8, 0, 0, 0x01, 0x10, .. "ICN#"u8, 0, 0, 0x01, 0x08, .. Icon(0xC1)];
        var own = Fork(("icns", FinderIconResolver.CustomIconId, icns));

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.HasCustomIconFlag, () => own);

        Assert.Equal((FinderIconSource.Custom, (byte)0xC1), (icon.Source, Marker(icon)));
        Assert.NotNull(FinderIconResolver.CustomIcon(own));
        Assert.Null(FinderIconResolver.CustomIcon(Fork()));
    }

    [Fact]
    public void Without_the_flag_a_custom_icon_is_not_used()
    {
        var own = Fork(("ICN#", FinderIconResolver.CustomIconId, Icon(0xC0)));

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => own);

        Assert.Equal(FinderIconSource.Generic, icon.Source);
    }

    [Fact]
    public void A_flagged_item_without_a_custom_icon_falls_back_to_its_application()
    {
        var icon = Resolver(new FinderBundleSource(Creator, () => Application())).Find(FinderItemKind.Document, Text, Creator,
            FinderIconResolver.HasCustomIconFlag, () => null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA1), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void Documents_and_applications_get_the_icons_their_application_s_bundle_maps()
    {
        var resolver = Resolver(new FinderBundleSource(Creator, () => Application()));

        var document = resolver.Find(FinderItemKind.Document, Text, Creator, 0, null);
        var application = resolver.Find(FinderItemKind.Application, Appl, Creator, 0, null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA1), (document.Source, Marker(document)));
        Assert.Equal((FinderIconSource.Application, (byte)0xA0), (application.Source, Marker(application)));
    }

    [Fact]
    public void A_type_the_bundle_does_not_map_gets_the_generic_icon()
    {
        var icon = Resolver(new FinderBundleSource(Creator, () => Application())).Find(FinderItemKind.Document, FourCC.FromString("PICT"), Creator, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (icon.Source, Marker(icon)));
    }

    [Theory]
    [InlineData(FinderItemKind.Document, 0xD0)]
    [InlineData(FinderItemKind.Folder, 0xF0)]
    [InlineData(FinderItemKind.Application, 0xAA)]
    public void Generic_icons_come_from_the_system_s_resources(FinderItemKind kind, int marker)
    {
        var icon = Resolver().Find(kind, Text, Other, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)marker), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void Without_any_source_there_is_no_icon()
    {
        var icon = new FinderIconResolver([], []).Find(FinderItemKind.Document, Text, Creator, 0, null);

        Assert.Equal(new FinderIcon(null, FinderIconSource.None), icon);
    }

    [Fact]
    public void Aliases_to_applications_and_folders_show_their_target_s_kind_of_icon()
    {
        var resolver = Resolver(new FinderBundleSource(Creator, () => Application()));

        var application = resolver.Find(FinderItemKind.Document, FourCC.FromString("adrp"), Creator, FinderIconResolver.IsAliasFlag, null);
        var folder = resolver.Find(FinderItemKind.Document, FourCC.FromString("fdrp"), FourCC.FromString("MACS"), FinderIconResolver.IsAliasFlag, null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA0), (application.Source, Marker(application)));
        Assert.Equal((FinderIconSource.Generic, (byte)0xF0), (folder.Source, Marker(folder)));
    }

    [Fact]
    public void A_bundle_is_read_once_and_only_when_its_creator_is_asked_for()
    {
        int reads = 0, otherReads = 0;
        var resolver = Resolver(
            new FinderBundleSource(Creator, () => { reads++; return Application(); }),
            new FinderBundleSource(Other, () => { otherReads++; return null; }));

        resolver.Find(FinderItemKind.Document, Text, Creator, 0, null);
        resolver.Find(FinderItemKind.Application, Appl, Creator, 0, null);

        Assert.Equal((1, 0), (reads, otherReads));
    }

    [Fact]
    public void A_bundle_with_another_signature_or_damaged_is_ignored()
    {
        var wrong = Resolver(new FinderBundleSource(Creator, () => Application("QQQQ")));
        var damaged = Resolver(new FinderBundleSource(Creator, () => Fork(("BNDL", 128, [1, 2, 3]))));
        var throwing = Resolver(new FinderBundleSource(Creator, () => throw new InvalidDataException("bad fork")));

        Assert.Equal(FinderIconSource.Generic, wrong.Find(FinderItemKind.Document, Text, Creator, 0, null).Source);
        Assert.Equal(FinderIconSource.Generic, damaged.Find(FinderItemKind.Document, Text, Creator, 0, null).Source);
        Assert.Equal(FinderIconSource.Generic, throwing.Find(FinderItemKind.Document, Text, Creator, 0, null).Source);
    }

    [Fact]
    public void Arguments_must_be_given()
    {
        Assert.Throws<ArgumentNullException>(() => new FinderIconResolver(null!, []));
        Assert.Throws<ArgumentNullException>(() => new FinderIconResolver([], null!));
        Assert.Throws<ArgumentNullException>(() => FinderIconResolver.CustomIcon(null!));
    }
}
