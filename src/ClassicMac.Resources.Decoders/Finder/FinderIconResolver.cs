using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Finder;

/// <summary>What an item in a Finder window is, for its generic icon and its placeholder.</summary>
public enum FinderItemKind
{
    /// <summary>A document (or any file that is not an application).</summary>
    Document,

    /// <summary>An application.</summary>
    Application,

    /// <summary>A folder.</summary>
    Folder,
}

/// <summary>Where an item's icon came from.</summary>
public enum FinderIconSource
{
    /// <summary>No icon was found; a placeholder is drawn.</summary>
    None,

    /// <summary>The item's own custom icon (ID −16455).</summary>
    Custom,

    /// <summary>The icon its application's bundle maps to its type (the Finder's desktop database).</summary>
    Application,

    /// <summary>The system's icon for its type, or the generic icon for its kind.</summary>
    Generic,
}

/// <summary>An item's icon, where it came from, and the badges drawn over it.</summary>
/// <param name="Suite">The icon suite, or null when none was found.</param>
/// <param name="Source">Where it came from.</param>
public sealed record FinderIcon(IconSuite? Suite, FinderIconSource Source)
{
    /// <summary>The badges, in drawing order: alias, locked, custom; those the system files lack are left out.</summary>
    public IReadOnlyList<IconSuite> Badges { get; init; } = [];
}

/// <summary>
/// A file whose bundle may give the Finder its icons: a file with the hasBundle flag, by its creator (an application's
/// creator is its signature), with its resource fork read on demand.
/// </summary>
/// <param name="Creator">The file's creator.</param>
/// <param name="Fork">Reads its resource fork; null when it has none.</param>
public sealed record FinderBundleSource(FourCC Creator, Func<ResourceFork?> Fork);

/// <summary>
/// Chooses the icon and badges the Finder shows for an item (docs/formats/file-systems/finder-windows.md §2.3): its custom
/// icon (ID −16455), then for a file its creator and type looked up as <c>GetIconRef</c> does, with applications' bundles
/// standing in for the desktop database, the System's icon mapping table (<c>'isrv'</c> 128) and the generic icons; for a
/// folder the folder icon. Safe to use from several threads.
/// </summary>
public sealed class FinderIconResolver
{
    /// <summary>The ID of a custom icon's family and of a custom badge (<c>kCustomIconResource</c>).</summary>
    public const short CustomIconId = -16455;

    /// <summary>The generic document icon (<c>kGenericDocumentIconResource</c>).</summary>
    public const short GenericDocumentId = -4000;

    /// <summary>The generic folder icon (<c>kGenericFolderIconResource</c>).</summary>
    public const short GenericFolderId = -3999;

    /// <summary>The generic application icon (<c>kGenericApplicationIconResource</c>).</summary>
    public const short GenericApplicationId = -3996;

    /// <summary>The Finder flag <c>kHasCustomIcon</c>.</summary>
    public const ushort HasCustomIconFlag = 0x0400;

    /// <summary>The Finder flag <c>kIsStationery</c>.</summary>
    public const ushort IsStationeryFlag = 0x0800;

    /// <summary>The Finder flag <c>kIsAlias</c>.</summary>
    public const ushort IsAliasFlag = 0x8000;

    /// <summary>The extended Finder flag <c>kExtendedFlagHasCustomBadge</c>.</summary>
    public const ushort HasCustomBadgeFlag = 0x0100;

    /// <summary>The extended Finder flag <c>kExtendedFlagsAreInvalid</c>.</summary>
    public const ushort ExtendedFlagsInvalidFlag = 0x8000;

    /// <summary>
    /// The Finder's own type-to-icon list, used when the System has no <c>'isrv'</c> 128 [Code: Icon Services 9.2.2].
    /// </summary>
    public static IReadOnlyDictionary<FourCC, short> DefaultIconIds { get; } = new Dictionary<FourCC, short>
    {
        [F("docu")] = -4000,
        [F("sdoc")] = -3985,
        [F("APPL")] = -3996,
        [F("APPC")] = -3824,
        [F("APPD")] = -3991,
        [F("fldr")] = -3999,
        [F("ofld")] = -3997,
        [F("shfl")] = -3978,
        [F("dbox")] = -3979,
        [F("mntd")] = -3977,
        [F("ownd")] = -3980,
        [F("prvf")] = -3994,
        [F("hdsk")] = -3995,
        [F("flpy")] = -3998,
        [F("cddr")] = -3987,
        [F("srvr")] = -3972,
        [F("desk")] = -3992,
        [F("trsh")] = -3993,
        [F("ftrh")] = -3984,
        [F("macs")] = -3983,
        [F("pref")] = -3971,
        [F("abdg")] = -20789,
        [F("lbdg")] = -20786,
        [F("mbdg")] = -20787,
        [F("sbdg")] = -20788,
    };

    private static FourCC F(string s) => FourCC.FromString(s);

    private static readonly FourCC Bndl = F("BNDL"), Fref = F("FREF"), IconList = F("ICN#"), Icns = F("icns"), Isrv = F("isrv"),
        Badg = F("badg"), Rgb = F("rgb "), Appl = F("APPL"), Appc = F("APPC"), Appd = F("APPD"), Docu = F("docu"),
        Fldr = F("fldr"), Macs = F("macs"), Pref = F("pref"), Unknown = F("????"), AliasBadge = F("abdg"), LockedBadge = F("lbdg");

    // GetIconRef's creators whose icons come from the system's table [Code: Icon Services 9.2.2].
    private static readonly HashSet<FourCC> SystemCreators = [F("movr"), F("drag"), F("MACS"), Macs, F("DMOV"), F("chrp")];

    // Types the system's table is not asked for [Code: Icon Services 9.2.2].
    private static readonly HashSet<FourCC> UnmappedTypes =
        [F("dict"), F("dspl"), F("mbug"), F("ppdf"), F("prof"), F("sdev"), F("thme"), F("uams"), F("utbl")];

    // Aliases to containers show a folder; to the System Folder, its icon [Code: Finder 9.2.2].
    private static readonly HashSet<FourCC> FolderAliasTypes = [F("fdrp"), F("fadr"), F("famn"), F("fash"), F("drop")];

    // An icon suite member's exact size, by type.
    private static readonly Dictionary<string, int> MemberSizes = new(StringComparer.Ordinal)
    {
        ["ICN#"] = 256,
        ["icl4"] = 512,
        ["icl8"] = 1024,
        ["ics#"] = 64,
        ["ics4"] = 128,
        ["ics8"] = 256,
        ["icm#"] = 48,
        ["icm4"] = 96,
        ["icm8"] = 192,
    };

    private readonly ILookup<FourCC, FinderBundleSource> bundles;
    private readonly IReadOnlyList<ResourceFork> systemForks;
    private readonly ReadOptions readOptions;
    private readonly Dictionary<FourCC, IReadOnlyList<(ResourceFork Fork, Bundle Bundle)>> applications = [];
    private readonly Dictionary<short, IconSuite?> systemIcons = [];
    private readonly Lazy<IReadOnlyDictionary<FourCC, short>> iconIds;
    private readonly Lazy<IReadOnlyList<RgbColor>> labelColors;

    /// <summary>A resolver over the files with bundles and the System's forks (System, System Resources).</summary>
    public FinderIconResolver(IEnumerable<FinderBundleSource> bundles, IEnumerable<ResourceFork> systemForks, ReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        ArgumentNullException.ThrowIfNull(systemForks);
        this.bundles = bundles.ToLookup(b => b.Creator);
        this.systemForks = systemForks.ToList();
        this.readOptions = readOptions ?? ReadOptions.Default;
        iconIds = new(ReadIconIds);
        labelColors = new(ReadLabelColors);
    }

    /// <summary>
    /// The label colours 0–7: the System's <c>'rgb '</c> −16392 + n for labels 1–7 [Code: Finder 9.2.2], else
    /// <see cref="IconSuite.DefaultLabelColors"/>.
    /// </summary>
    public IReadOnlyList<RgbColor> LabelColors => labelColors.Value;

    /// <summary>
    /// The resource ID of the system's icon for <paramref name="type"/>: the System's <c>'isrv'</c> 128 when a system fork
    /// has it, else <see cref="DefaultIconIds"/>; null when the type has none.
    /// </summary>
    public short? SystemIconId(FourCC type) => iconIds.Value.TryGetValue(type, out var id) ? id : null;

    /// <summary>
    /// The icon and badges of an item of <paramref name="kind"/>, type, creator and Finder <paramref name="flags"/>.
    /// <paramref name="ownFork"/> reads the fork holding its custom icon and badge (a file's own resource fork, a
    /// folder's <c>Icon\r</c> file's); <paramref name="extendedFlags"/> are the extended Finder flags (<c>FXInfo</c>,
    /// <c>DXInfo</c> +8); <paramref name="isLocked"/> adds the lock badge.
    /// </summary>
    public FinderIcon Find(FinderItemKind kind, FourCC type, FourCC creator, ushort flags, Func<ResourceFork?>? ownFork,
        ushort extendedFlags = 0, bool isLocked = false)
    {
        ResourceFork? own = null;
        bool read = false;
        ResourceFork? Own()
        {
            if (!read && ownFork is not null)
            {
                own = Read(ownFork);
            }

            read = true;
            return own;
        }
        var badge = (extendedFlags & (ExtendedFlagsInvalidFlag | HasCustomBadgeFlag)) == HasCustomBadgeFlag && Own() is { } badged
            ? CustomBadge(badged)
            : null;
        var badges = new List<IconSuite>();
        if ((flags & IsAliasFlag) != 0 && SystemIcon(AliasBadge) is { } alias)
        {
            badges.Add(alias);
        }

        if (isLocked && SystemIcon(LockedBadge) is { } locked)
        {
            badges.Add(locked);
        }

        if (badge?.Icon is { } custom)
        {
            badges.Add(custom);
        }

        return Icon(kind, type, creator, flags, Own, badge) with { Badges = badges };
    }

    private FinderIcon Icon(FinderItemKind kind, FourCC type, FourCC creator, ushort flags, Func<ResourceFork?> own, (IconSuite? Icon, FourCC Type, FourCC Creator)? badge)
    {
        if ((flags & HasCustomIconFlag) != 0 && own() is { } fork && CustomIcon(fork, readOptions) is { } customIcon)
        {
            return new FinderIcon(customIcon, FinderIconSource.Custom);
        }
        // A folder: its package icon with kHasBundle (not drawn here), else the folder icon [Code: Finder 9.2.2].
        if (kind == FinderItemKind.Folder)
        {
            return System(Fldr);
        }
        // A custom badge's override type and creator replace the item's when it has no custom icon.
        if (badge is { Type: var overrideType, Creator: var overrideCreator } && overrideType != default)
        {
            (type, creator) = (overrideType, overrideCreator);
        }

        bool system = false;
        if ((flags & IsStationeryFlag) != 0 && type != default)
        {
            type = new FourCC((type.Value & 0x00FFFFFF) | ((uint)'s' << 24));
        }

        if ((flags & IsAliasFlag) != 0)
        {
            if (FolderAliasTypes.Contains(type))
            {
                (type, system) = (Fldr, true);
            }
            else if (type == F("fasy"))
            {
                (type, system) = (Macs, true);
            }
        }
        return IconRef(creator, type, system);
    }

    // GetIconRef: the system's mapping for its own creators, preferences and aliases to containers; else the desktop
    // database (here the applications' bundles), the system's table by type, the generic icons [Code: Icon Services 9.2.2].
    private FinderIcon IconRef(FourCC creator, FourCC type, bool system)
    {
        if (creator == default || creator == Unknown || type == default || type == Unknown)
        {
            (creator, type) = (Macs, Docu);
        }

        var mapped = Mapped(type);
        if (system || SystemCreators.Contains(creator) || type == Pref)
        {
            var icon = System(mapped);
            return icon.Suite is not null ? icon : Generic(mapped);
        }
        if (ApplicationIcon(creator, type) is { } suite)
        {
            return new FinderIcon(suite, FinderIconSource.Application);
        }

        if (!UnmappedTypes.Contains(type) && System(type) is { Suite: not null } byType)
        {
            return byType;
        }

        return Generic(mapped);
    }

    // Types the system draws as an application, control panel or desk accessory.
    private static FourCC Mapped(FourCC type) => type.ToString() switch
    {
        "adrp" => Appl,
        "acdp" or "cdev" or "cpnl" => Appc,
        "addp" or "dfil" or "deka" => Appd,
        _ => type,
    };

    // The generic icon: an application's, control panel's or desk accessory's, else a document's, stationery's too
    // [Code: Icon Services 9.2.2; Verified: Mac OS 9.0 Finder, stationery].
    private FinderIcon Generic(FourCC type) => System(type == Appl || type == Appc || type == Appd ? type : Docu);

    /// <summary>
    /// The System's icon of one of its own types (<c>hdsk</c> a hard disk, <c>fldr</c> a folder, …: the table above),
    /// for a window's title; none when the open System files lack it.
    /// </summary>
    public FinderIcon SystemTypeIcon(FourCC type) => System(type);

    private FinderIcon System(FourCC type) =>
        SystemIcon(type) is { } suite ? new FinderIcon(suite, FinderIconSource.Generic) : new FinderIcon(null, FinderIconSource.None);

    private IconSuite? SystemIcon(FourCC type) => SystemIconId(type) is { } id ? SystemIconById(id) : null;

    // The system's icon of an ID: an 'icns' (System Resources) first, else the separate members (the System file),
    // each only at its exact size [Code: Icon Services 9.2.2].
    private IconSuite? SystemIconById(short id)
    {
        lock (systemIcons)
        {
            if (systemIcons.TryGetValue(id, out var cached))
            {
                return cached;
            }

            IconSuite? suite = null;
            foreach (var fork in systemForks)
            {
                if (FamilyAt(fork, id) is { } family)
                {
                    suite = family;
                    break;
                }
            }

            if (suite is null)
            {
                foreach (var fork in systemForks)
                {
                    var members = IconSuite.FromResources((t, i) => fork.Find(t, i) is { } r && Data(r, fork) is var d
                        && MemberSizes.TryGetValue(t.ToString(), out var size) && d.Length == size ? d : null, id);
                    if (members.Members.Count > 0)
                    {
                        suite = members;
                        break;
                    }
                }
            }

            return systemIcons[id] = suite;
        }
    }

    // The 'icns' of an ID as a suite; null when absent, empty or damaged.
    private IconSuite? FamilyAt(ResourceFork fork, short id)
    {
        if (fork.Find(Icns, id) is not { } resource)
        {
            return null;
        }

        try
        {
            var family = IconSuite.FromFamily(IconFamily.ReadIcns(Data(resource, fork)));
            return family.Members.Count > 0 ? family : null;
        }
        catch (Exception e) when (ExceptionFilters.IsMalformed(e))
        {
            return null;
        }
    }

    // 'badg' -16455 (Icons.h CustomBadgeResource): version (≤ 0), the badge's icon ID, its type and creator, the
    // window badge's type and creator, the override type and creator; at least $1C bytes [Code: Finder 9.2.2]. The
    // badge is the icon family of its ID in the same fork, else the system's icon for its type.
    private (IconSuite? Icon, FourCC Type, FourCC Creator)? CustomBadge(ResourceFork fork)
    {
        if (fork.Find(Badg, CustomIconId) is not { } resource)
        {
            return null;
        }

        var data = Data(resource, fork);
        if (data.Length < 0x1C)
        {
            return null;
        }

        var reader = new BigEndianReader(data);
        if (reader.ReadInt16At(0) > 0)
        {
            return null;
        }

        short id = reader.ReadInt16At(2);
        var type = reader.ReadFourCCAt(4);
        IconSuite? icon = null;
        if (id != 0)
        {
            icon = FamilyAt(fork, id) ?? (IconSuite.FromResources((t, i) => fork.Find(t, i) is { } r ? Data(r, fork) : null, id) is { Members.Count: > 0 } s ? s : null);
        }
        else if (type != default)
        {
            icon = SystemIcon(type);
        }

        return (icon, reader.ReadFourCCAt(0x14), reader.ReadFourCCAt(0x18));
    }

    /// <summary>
    /// The custom icon in <paramref name="fork"/>: the suite of ID −16455 when it has an <c>ICN#</c>, else the
    /// <c>'icns'</c> −16455 family; null when it has neither.
    /// </summary>
    public static IconSuite? CustomIcon(ResourceFork fork, ReadOptions? readOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fork);
        ReadOnlyMemory<byte>? Lookup(FourCC type, short id) =>
            fork.Find(type, id) is { } r ? ResourceDecompression.Default.GetData(r, fork, readOptions) : null;
        var suite = IconSuite.FromResources(Lookup, CustomIconId);
        if (suite.Members.ContainsKey("ICN#"))
        {
            return suite;
        }

        if (Lookup(Icns, CustomIconId) is not { } icns)
        {
            return null;
        }

        try
        {
            var family = IconSuite.FromFamily(IconFamily.ReadIcns(icns));
            return family.Members.Count > 0 ? family : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // The icon the bundle of an application with signature `creator` maps to `type` (finder.md §2.1); like the desktop
    // database, only an icon with a mask member (ICN#, else ics#) [Code: Icon Services 9.2.2].
    private IconSuite? ApplicationIcon(FourCC creator, FourCC type)
    {
        foreach (var (fork, bundle) in Applications(creator))
        {
            var map = (FourCC t) => bundle.Maps.Where(m => m.Type == t).SelectMany(m => m.Ids);
            foreach (var (_, frefId) in map(Fref))
            {
                if (fork.Find(Fref, frefId) is not { } r)
                {
                    continue;
                }

                var reference = FinderResources.ReadFileReference(Data(r, fork), DecodeOptions.Default, out var complete);
                if (!complete || reference.FileType != type)
                {
                    continue;
                }

                foreach (var (local, iconId) in map(IconList))
                {
                    if (local != reference.LocalIconId)
                    {
                        continue;
                    }

                    var suite = IconSuite.FromResources((t, id) => fork.Find(t, id) is { } m ? Data(m, fork) : null, iconId);
                    if (suite.Members.ContainsKey("ICN#") || suite.Members.ContainsKey("ics#"))
                    {
                        return suite;
                    }
                }
            }
        }
        return null;
    }

    // The forks and bundles of the files with this creator whose bundle has it as signature; read once.
    private IReadOnlyList<(ResourceFork, Bundle)> Applications(FourCC creator)
    {
        lock (applications)
        {
            if (applications.TryGetValue(creator, out var found))
            {
                return found;
            }

            var list = new List<(ResourceFork, Bundle)>();
            foreach (var source in bundles[creator])
            {
                if (Read(source.Fork) is not { } fork)
                {
                    continue;
                }

                foreach (var r in fork.OfType(Bndl))
                {
                    var bundle = FinderResources.ReadBundle(Data(r, fork), out var complete);
                    if (complete && bundle.Signature == creator)
                    {
                        list.Add((fork, bundle));
                    }
                }
            }
            return applications[creator] = list;
        }
    }

    // 'isrv' 128 "Icon Mapping Table": 6-byte entries { type, resource ID }, the first of a type counting
    // [Code: Icon Services 9.2.2].
    private IReadOnlyDictionary<FourCC, short> ReadIconIds()
    {
        foreach (var fork in systemForks)
        {
            if (fork.Find(Isrv, 128) is not { } resource)
            {
                continue;
            }

            var data = Data(resource, fork);
            var reader = new BigEndianReader(data);
            var table = new Dictionary<FourCC, short>();
            for (int offset = 0; offset <= data.Length - 6; offset += 6)
            {
                table.TryAdd(reader.ReadFourCCAt(offset), reader.ReadInt16At(offset + 4));
            }

            return table;
        }
        return DefaultIconIds;
    }

    private IReadOnlyList<RgbColor> ReadLabelColors()
    {
        var colours = IconSuite.DefaultLabelColors.ToArray();
        for (int label = 1; label < colours.Length; label++)
        {
            foreach (var fork in systemForks)
            {
                if (fork.Find(Rgb, (short)(-16392 + label)) is not { } resource)
                {
                    continue;
                }

                var data = Data(resource, fork);
                if (data.Length >= 6)
                {
                    var reader = new BigEndianReader(data);
                    colours[label] = new RgbColor(reader.ReadUInt16At(0), reader.ReadUInt16At(2), reader.ReadUInt16At(4));
                }
                break;
            }
        }

        return colours;
    }

    private ReadOnlyMemory<byte> Data(Resource resource, ResourceFork fork) =>
        ResourceDecompression.Default.GetData(resource, fork, readOptions);

    // A fork, or null when it cannot be read.
    private static ResourceFork? Read(Func<ResourceFork?> fork)
    {
        try
        {
            return fork();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
