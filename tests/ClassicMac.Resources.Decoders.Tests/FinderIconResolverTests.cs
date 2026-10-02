using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// Which icon the Finder shows for an item (docs/formats/file-systems/finder-windows.md §2.3).
public class FinderIconResolverTests
{
    private static readonly FourCC Text = FourCC.FromString("TEXT"), Appl = FourCC.FromString("APPL"), Creator = FourCC.FromString("ABCD"),
        Other = FourCC.FromString("WXYZ"), Macs = FourCC.FromString("MACS");

    private static FourCC F(string s) => FourCC.FromString(s);

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

    private static ResourceFork Application(string signature = "ABCD", string document = "TEXT", string documentIcon = "ICN#") => Fork(
        ("BNDL", 128, Bundle(signature)),
        ("FREF", 128, Fref("APPL", 0)),
        ("FREF", 129, Fref(document, 1)),
        ("ICN#", 200, Icon(0xA0)),
        (documentIcon, 201, documentIcon == "icl8" ? new byte[1024] : Icon(0xA1)));

    // The System's icons by the fallback table's IDs, each marked.
    private static ResourceFork System(params (string Type, short Id, byte[] Data)[] more) => Fork(
    [
        ("ICN#", -4000, Icon(0xD0)), ("ICN#", -3999, Icon(0xF0)), ("ICN#", -3996, Icon(0xAA)), ("ICN#", -3824, Icon(0xAC)),
        ("ICN#", -3991, Icon(0xAD)), ("ICN#", -3985, Icon(0x5D)), ("ICN#", -3983, Icon(0x5F)), ("ICN#", -3971, Icon(0x9F)),
        ("ICN#", -20789, Icon(0xAB)), ("ICN#", -20786, Icon(0x1B)), .. more,
    ]);

    private static byte Marker(IconSuite suite) => suite.Members["ICN#"][0];

    private static byte Marker(FinderIcon icon) => Marker(icon.Suite!);

    private static FinderIconResolver Resolver(params FinderBundleSource[] bundles) => new(bundles, [System()]);

    private static FinderIconResolver WithApplication(Func<ResourceFork?>? fork = null) =>
        Resolver(new FinderBundleSource(Creator, fork ?? (() => Application())));

    [Fact]
    public void A_custom_icon_comes_first()
    {
        var own = Fork(("ICN#", FinderIconResolver.CustomIconId, Icon(0xC0)));

        var icon = WithApplication().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.HasCustomIconFlag, () => own);

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
        var icon = WithApplication().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.HasCustomIconFlag, () => null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA1), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void Documents_and_applications_get_the_icons_their_application_s_bundle_maps()
    {
        var resolver = WithApplication();

        var document = resolver.Find(FinderItemKind.Document, Text, Creator, 0, null);
        var application = resolver.Find(FinderItemKind.Application, Appl, Creator, 0, null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA1), (document.Source, Marker(document)));
        Assert.Equal((FinderIconSource.Application, (byte)0xA0), (application.Source, Marker(application)));
    }

    [Fact]
    public void A_bundle_icon_needs_a_mask_member()
    {
        // The TEXT icon has only an icl8: the Desktop DB would not take it, so the generic document icon is shown.
        var icon = WithApplication(() => Application(documentIcon: "icl8")).Find(FinderItemKind.Document, Text, Creator, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void A_type_the_bundle_does_not_map_gets_the_generic_icon()
    {
        var icon = WithApplication().Find(FinderItemKind.Document, F("PICT"), Creator, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (icon.Source, Marker(icon)));
    }

    [Theory]
    [InlineData(FinderItemKind.Document, "TEXT", 0xD0)]
    [InlineData(FinderItemKind.Application, "APPL", 0xAA)]
    [InlineData(FinderItemKind.Application, "APPC", 0xAC)]
    [InlineData(FinderItemKind.Application, "APPD", 0xAD)]
    [InlineData(FinderItemKind.Application, "TEXT", 0xD0)]
    [InlineData(FinderItemKind.Document, "pref", 0x9F)]
    [InlineData(FinderItemKind.Folder, "TEXT", 0xF0)]
    public void Generic_icons_come_from_the_system_s_resources_by_type(FinderItemKind kind, string type, int marker)
    {
        var icon = Resolver().Find(kind, F(type), Other, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)marker), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void Without_any_source_there_is_no_icon()
    {
        var icon = new FinderIconResolver([], []).Find(FinderItemKind.Document, Text, Creator, 0, null);

        Assert.Null(icon.Suite);
        Assert.Equal(FinderIconSource.None, icon.Source);
        Assert.Empty(icon.Badges);
    }

    [Theory]
    [InlineData("TEXT", "????")]
    [InlineData("????", "ABCD")]
    [InlineData("APPL", "\0\0\0\0")]
    [InlineData("\0\0\0\0", "ABCD")]
    public void An_unknown_type_or_creator_is_a_plain_document(string type, string creator)
    {
        var icon = WithApplication().Find(FinderItemKind.Document, F(type), F(creator), 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (icon.Source, Marker(icon)));
    }

    [Theory]
    [InlineData("MACS")]
    [InlineData("macs")]
    [InlineData("movr")]
    [InlineData("drag")]
    [InlineData("DMOV")]
    [InlineData("chrp")]
    public void The_system_s_creators_skip_the_bundles(string creator)
    {
        var resolver = Resolver(new FinderBundleSource(F(creator), () => Application(creator)));

        var icon = resolver.Find(FinderItemKind.Document, Text, F(creator), 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void Preferences_skip_the_bundles()
    {
        var icon = Resolver(new FinderBundleSource(Creator, () => Application(document: "pref"))).Find(FinderItemKind.Document, F("pref"), Creator, 0, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0x9F), (icon.Source, Marker(icon)));
    }

    [Theory]
    [InlineData("adrp", 0xAA)]
    [InlineData("acdp", 0xAC)]
    [InlineData("cdev", 0xAC)]
    [InlineData("cpnl", 0xAC)]
    [InlineData("addp", 0xAD)]
    [InlineData("dfil", 0xAD)]
    [InlineData("deka", 0xAD)]
    public void The_system_maps_control_panel_desk_accessory_and_application_alias_types(string type, int marker)
    {
        var system = Resolver().Find(FinderItemKind.Document, F(type), Macs, 0, null);
        var other = Resolver().Find(FinderItemKind.Document, F(type), Creator, 0, null);

        Assert.Equal((byte)marker, Marker(system));
        Assert.Equal((byte)marker, Marker(other));
    }

    [Theory]
    [InlineData("fdrp", 0xF0)]
    [InlineData("fadr", 0xF0)]
    [InlineData("famn", 0xF0)]
    [InlineData("fash", 0xF0)]
    [InlineData("drop", 0xF0)]
    [InlineData("fasy", 0x5F)]
    [InlineData("TEXT", 0xA1)]
    public void An_alias_shows_its_original_s_icon_with_the_alias_badge(string type, int marker)
    {
        var icon = WithApplication().Find(FinderItemKind.Document, F(type), Creator, FinderIconResolver.IsAliasFlag, null);

        Assert.Equal((byte)marker, Marker(icon));
        Assert.Equal([(byte)0xAB], icon.Badges.Select(Marker));
    }

    [Fact]
    public void A_folder_alias_s_kind_is_used_only_for_aliases()
    {
        var icon = Resolver().Find(FinderItemKind.Document, F("fdrp"), Creator, 0, null);

        Assert.Equal((byte)0xD0, Marker(icon));
    }

    [Fact]
    public void Stationery_looks_up_its_type_with_an_s()
    {
        var mapped = Resolver(new FinderBundleSource(Creator, () => Application(document: "sEXT")))
            .Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.IsStationeryFlag, null);
        var unmapped = WithApplication().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.IsStationeryFlag, null);

        Assert.Equal((FinderIconSource.Application, (byte)0xA1), (mapped.Source, Marker(mapped)));
        // The generic document icon, not the stationery pad's: as the Mac OS 9.0 Finder draws it.
        Assert.Equal((FinderIconSource.Generic, (byte)0xD0), (unmapped.Source, Marker(unmapped)));
    }

    [Fact]
    public void The_system_s_icon_mapping_table_maps_types_to_icons()
    {
        // 'isrv' 128: { type, ID } pairs. Profiles are among the types the Finder does not look up there.
        byte[] table = [.. "PICT"u8, .. BE16(-3000), .. "prof"u8, .. BE16(-3001), .. "docu"u8, .. BE16(-3002), 0, 0];
        var resolver = new FinderIconResolver([], [System(("isrv", 128, table), ("ICN#", -3000, Icon(0x31)), ("ICN#", -3001, Icon(0x32)), ("ICN#", -3002, Icon(0x33)))]);

        Assert.Equal((byte)0x31, Marker(resolver.Find(FinderItemKind.Document, F("PICT"), Creator, 0, null)));
        Assert.Equal((byte)0x33, Marker(resolver.Find(FinderItemKind.Document, F("prof"), Creator, 0, null)));
        Assert.Equal((byte)0x33, Marker(resolver.Find(FinderItemKind.Document, Text, Creator, 0, null)));
        // Without the table's entry, a type has no icon of its own: 'fldr' is not in this table.
        Assert.Null(resolver.Find(FinderItemKind.Folder, default, default, 0, null).Suite);
        Assert.Equal((short)-3000, resolver.SystemIconId(F("PICT")));
        Assert.Null(resolver.SystemIconId(F("fldr")));
    }

    [Theory]
    [InlineData("docu", -4000)]
    [InlineData("sdoc", -3985)]
    [InlineData("APPL", -3996)]
    [InlineData("APPC", -3824)]
    [InlineData("APPD", -3991)]
    [InlineData("fldr", -3999)]
    [InlineData("ofld", -3997)]
    [InlineData("shfl", -3978)]
    [InlineData("dbox", -3979)]
    [InlineData("mntd", -3977)]
    [InlineData("ownd", -3980)]
    [InlineData("prvf", -3994)]
    [InlineData("hdsk", -3995)]
    [InlineData("flpy", -3998)]
    [InlineData("cddr", -3987)]
    [InlineData("srvr", -3972)]
    [InlineData("desk", -3992)]
    [InlineData("trsh", -3993)]
    [InlineData("ftrh", -3984)]
    [InlineData("macs", -3983)]
    [InlineData("pref", -3971)]
    [InlineData("abdg", -20789)]
    [InlineData("lbdg", -20786)]
    [InlineData("mbdg", -20787)]
    [InlineData("sbdg", -20788)]
    public void Without_the_table_the_Finder_s_own_list_maps_types(string type, int id)
    {
        Assert.Equal((short)id, new FinderIconResolver([], []).SystemIconId(F(type)));
    }

    // A window's title icon: the System's icon of a type, such as a hard disk's for a volume's root.
    [Fact]
    public void The_System_s_icon_of_one_of_its_types_is_there_to_ask_for()
    {
        var resolver = new FinderIconResolver([], [System(("ICN#", -3995, Icon(0x4D)))]);

        var disk = resolver.SystemTypeIcon(F("hdsk"));

        Assert.Equal(FinderIconSource.Generic, disk.Source);
        Assert.Equal(0x4D, Marker(disk));
        Assert.Null(resolver.SystemTypeIcon(F("cddr")).Suite);
        Assert.Equal(FinderIconSource.None, resolver.SystemTypeIcon(F("zzzz")).Source);
    }

    [Fact]
    public void A_system_icon_family_comes_before_separate_icons()
    {
        byte[] icns = [.. "icns"u8, 0, 0, 0x01, 0x10, .. "ICN#"u8, 0, 0, 0x01, 0x08, .. Icon(0xE0)];
        var resolver = new FinderIconResolver([], [System(), Fork(("icns", -4000, icns))]);

        Assert.Equal((byte)0xE0, Marker(resolver.Find(FinderItemKind.Document, Text, Creator, 0, null)));
    }

    [Fact]
    public void Separate_system_icons_are_used_only_at_their_exact_sizes()
    {
        var resolver = new FinderIconResolver([], [Fork(("ICN#", -3999, new byte[255]), ("ICN#", -4000, Icon(0xD0)), ("icl8", -4000, new byte[1023]),
            ("icl4", -4000, new byte[512]))]);

        Assert.Null(resolver.Find(FinderItemKind.Folder, default, default, 0, null).Suite);
        var document = resolver.Find(FinderItemKind.Document, Text, Creator, 0, null).Suite!;
        Assert.Equal(["ICN#", "icl4"], document.Members.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_folder_with_a_bundle_shows_the_folder_icon()
    {
        var icon = Resolver().Find(FinderItemKind.Folder, default, default, 0x2000, null);

        Assert.Equal((FinderIconSource.Generic, (byte)0xF0), (icon.Source, Marker(icon)));
    }

    [Fact]
    public void A_locked_file_has_the_lock_badge()
    {
        var locked = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, null, isLocked: true);
        var both = Resolver().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.IsAliasFlag, null, isLocked: true);

        Assert.Equal([(byte)0x1B], locked.Badges.Select(Marker));
        Assert.Equal([(byte)0xAB, (byte)0x1B], both.Badges.Select(Marker));
        Assert.Empty(Resolver().Find(FinderItemKind.Document, Text, Creator, 0, null).Badges);
    }

    [Fact]
    public void Badges_the_system_lacks_are_left_out()
    {
        var icon = new FinderIconResolver([], []).Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.IsAliasFlag, null, isLocked: true);

        Assert.Empty(icon.Badges);
    }

    // 'badg' -16455: version, badge ID, badge type and creator, window badge type and creator, override type and creator.
    private static byte[] Badge(short id = 300, string type = "\0\0\0\0", string overrideType = "\0\0\0\0", string overrideCreator = "\0\0\0\0",
        short version = 0, int length = 0x1C)
    {
        byte[] data = [.. BE16(version), .. BE16(id), .. Encoding.Latin1.GetBytes(type), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            .. Encoding.Latin1.GetBytes(overrideType), .. Encoding.Latin1.GetBytes(overrideCreator)];
        return data[..length];
    }

    private static ResourceFork Badged(byte[] badge) => Fork(("badg", FinderIconResolver.CustomIconId, badge), ("ICN#", 300, Icon(0x30)));

    [Fact]
    public void A_custom_badge_comes_from_the_item_s_own_badg_resource()
    {
        var own = Badged(Badge());

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => own, extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Equal([(byte)0x30], icon.Badges.Select(Marker));
        Assert.Equal((byte)0xD0, Marker(icon));
    }

    [Theory]
    [InlineData(0x8100, 0, 0x1C)]   // the extended flags are not valid
    [InlineData(0x0000, 0, 0x1C)]   // no custom badge
    [InlineData(0x0100, 1, 0x1C)]   // a later version
    [InlineData(0x0100, 0, 0x1B)]   // too short
    public void A_custom_badge_needs_its_flag_and_a_valid_resource(int flags, int version, int length)
    {
        var own = Badged(Badge(version: (short)version, length: length));

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => own, extendedFlags: (ushort)flags);

        Assert.Empty(icon.Badges);
    }

    [Fact]
    public void A_custom_badge_may_name_a_system_icon_by_type()
    {
        var own = Badged(Badge(id: 0, type: "lbdg"));

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => own, extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Equal([(byte)0x1B], icon.Badges.Select(Marker));
    }

    [Fact]
    public void A_custom_badge_s_override_type_replaces_the_item_s_unless_it_has_a_custom_icon()
    {
        var own = Badged(Badge(overrideType: "APPL", overrideCreator: "ABCD"));
        own.Add(new Resource(FourCC.FromString("ICN#"), FinderIconResolver.CustomIconId, Icon(0xC0)));

        var plain = WithApplication().Find(FinderItemKind.Document, Text, Creator, 0, () => own, extendedFlags: FinderIconResolver.HasCustomBadgeFlag);
        var custom = WithApplication().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.HasCustomIconFlag, () => own,
            extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Equal((byte)0xA0, Marker(plain));
        Assert.Equal((byte)0xC0, Marker(custom));
    }

    [Fact]
    public void A_custom_badge_may_be_an_icon_family()
    {
        byte[] icns = [.. "icns"u8, 0, 0, 0x01, 0x10, .. "ICN#"u8, 0, 0, 0x01, 0x08, .. Icon(0x3F)];
        var own = Fork(("badg", FinderIconResolver.CustomIconId, Badge()), ("icns", 300, icns));

        var icon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => own, extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Equal([(byte)0x3F], icon.Badges.Select(Marker));
    }

    [Fact]
    public void A_custom_badge_flag_without_a_usable_badge_adds_none()
    {
        var missing = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => Fork(), extendedFlags: FinderIconResolver.HasCustomBadgeFlag);
        var noIcon = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => Badged(Badge(id: 0)), extendedFlags: FinderIconResolver.HasCustomBadgeFlag);
        var absent = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, () => Badged(Badge(id: 301)), extendedFlags: FinderIconResolver.HasCustomBadgeFlag);
        var noFork = Resolver().Find(FinderItemKind.Document, Text, Creator, 0, null, extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Empty(missing.Badges);
        Assert.Empty(noIcon.Badges);
        Assert.Empty(absent.Badges);
        Assert.Empty(noFork.Badges);
    }

    [Fact]
    public void The_own_fork_is_read_once()
    {
        int reads = 0;
        var own = Badged(Badge());
        own.Add(new Resource(FourCC.FromString("ICN#"), FinderIconResolver.CustomIconId, Icon(0xC0)));

        Resolver().Find(FinderItemKind.Document, Text, Creator, FinderIconResolver.HasCustomIconFlag, () => { reads++; return own; },
            extendedFlags: FinderIconResolver.HasCustomBadgeFlag);

        Assert.Equal(1, reads);
    }

    [Fact]
    public void A_damaged_system_icon_family_is_passed_over()
    {
        var resolver = new FinderIconResolver([], [Fork(("icns", -4000, [.. "icns"u8, 0, 0, 0x10, 0x00])), System()]);

        Assert.Equal((byte)0xD0, Marker(resolver.Find(FinderItemKind.Document, Text, Creator, 0, null)));
    }

    [Fact]
    public void Stationery_without_a_type_is_a_plain_document()
    {
        var icon = Resolver().Find(FinderItemKind.Document, default, Creator, FinderIconResolver.IsStationeryFlag, null);

        Assert.Equal((byte)0xD0, Marker(icon));
    }

    [Fact]
    public void Label_colours_come_from_the_system_s_rgb_resources()
    {
        var resolver = new FinderIconResolver([], [Fork(("rgb ", -16390, [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC]), ("rgb ", -16385, [1, 2]))]);

        var colours = resolver.LabelColors;

        Assert.Equal(8, colours.Count);
        Assert.Equal(new RgbColor(0x1234, 0x5678, 0x9ABC), colours[2]);
        Assert.Equal(IconSuite.DefaultLabelColors[1], colours[1]);
        Assert.Equal(IconSuite.DefaultLabelColors[7], colours[7]);   // too short
        Assert.Equal(IconSuite.DefaultLabelColors, new FinderIconResolver([], []).LabelColors);
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
