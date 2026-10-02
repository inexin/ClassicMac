using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Finder
{
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

        /// <summary>The icon its application's bundle maps to its type.</summary>
        Application,

        /// <summary>The system's generic icon for its kind.</summary>
        Generic,
    }

    /// <summary>An item's icon and where it came from.</summary>
    /// <param name="Suite">The icon suite, or null when none was found.</param>
    /// <param name="Source">Where it came from.</param>
    public sealed record FinderIcon(IconSuite? Suite, FinderIconSource Source);

    /// <summary>
    /// A file whose bundle may give the Finder its icons: a file with the hasBundle flag, by its creator (an application's
    /// creator is its signature), with its resource fork read on demand.
    /// </summary>
    /// <param name="Creator">The file's creator.</param>
    /// <param name="Fork">Reads its resource fork; null when it has none.</param>
    public sealed record FinderBundleSource(FourCC Creator, Func<ResourceFork?> Fork);

    /// <summary>
    /// Chooses the icon the Finder shows for an item, in order (docs/formats/file-systems/finder-windows.md §2.3): its
    /// custom icon (ID −16455, when the hasCustomIcon flag is set), the icon its application's bundle maps to its type
    /// (applications indexed by signature on first use, each bundle read once), the system's generic icon for its kind,
    /// or none. Safe to use from several threads.
    /// </summary>
    public sealed class FinderIconResolver
    {
        /// <summary>The ID of a custom icon's family (<c>kCustomIconResource</c>).</summary>
        public const short CustomIconId = -16455;

        /// <summary>The generic document icon (<c>kGenericDocumentIconResource</c>).</summary>
        public const short GenericDocumentId = -4000;

        /// <summary>The generic folder icon (<c>kGenericFolderIconResource</c>).</summary>
        public const short GenericFolderId = -3999;

        /// <summary>The generic application icon (<c>kGenericApplicationIconResource</c>).</summary>
        public const short GenericApplicationId = -3996;

        /// <summary>The Finder flag <c>kHasCustomIcon</c>.</summary>
        public const ushort HasCustomIconFlag = 0x0400;

        /// <summary>The Finder flag <c>kIsAlias</c>.</summary>
        public const ushort IsAliasFlag = 0x8000;

        private static readonly FourCC Bndl = FourCC.FromString("BNDL"), Fref = FourCC.FromString("FREF"), IconList = FourCC.FromString("ICN#"),
            Icns = FourCC.FromString("icns"), Appl = FourCC.FromString("APPL"), ApplicationAlias = FourCC.FromString("adrp"),
            FolderAlias = FourCC.FromString("fdrp");

        private readonly ILookup<FourCC, FinderBundleSource> bundles;
        private readonly IReadOnlyList<ResourceFork> systemForks;
        private readonly ReadOptions readOptions;
        private readonly Dictionary<FourCC, IReadOnlyList<(ResourceFork Fork, Bundle Bundle)>> applications = [];
        private readonly Dictionary<FinderItemKind, IconSuite?> generic = [];

        /// <summary>A resolver over the files with bundles and the forks holding the system's generic icons.</summary>
        public FinderIconResolver(IEnumerable<FinderBundleSource> bundles, IEnumerable<ResourceFork> systemForks, ReadOptions? readOptions = null)
        {
            ArgumentNullException.ThrowIfNull(bundles);
            ArgumentNullException.ThrowIfNull(systemForks);
            this.bundles = bundles.ToLookup(b => b.Creator);
            this.systemForks = systemForks.ToList();
            this.readOptions = readOptions ?? ReadOptions.Default;
        }

        /// <summary>
        /// The icon of an item of <paramref name="kind"/>, type and creator, with Finder <paramref name="flags"/>;
        /// <paramref name="customIconFork"/> reads the fork holding its custom icon (a file's own resource fork, a
        /// folder's <c>Icon\r</c> file's), only when the hasCustomIcon flag is set.
        /// </summary>
        public FinderIcon Find(FinderItemKind kind, FourCC type, FourCC creator, ushort flags, Func<ResourceFork?>? customIconFork)
        {
            if ((flags & HasCustomIconFlag) != 0 && customIconFork is not null && Read(customIconFork) is { } own && CustomIcon(own, readOptions) is { } custom)
                return new FinderIcon(custom, FinderIconSource.Custom);
            if ((flags & IsAliasFlag) != 0)
            {
                // An alias to an application or a folder has a type of its own (Finder.h: kApplicationAliasType,
                // kContainerFolderAliasType); other aliases take their original's type and creator.
                if (type == ApplicationAlias) (kind, type) = (FinderItemKind.Application, Appl);
                else if (type == FolderAlias) kind = FinderItemKind.Folder;
            }
            if (kind != FinderItemKind.Folder && ApplicationIcon(creator, type) is { } mapped)
                return new FinderIcon(mapped, FinderIconSource.Application);
            return Generic(kind) is { } suite ? new FinderIcon(suite, FinderIconSource.Generic) : new FinderIcon(null, FinderIconSource.None);
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
            if (suite.Members.ContainsKey("ICN#")) return suite;
            if (Lookup(Icns, CustomIconId) is not { } icns) return null;
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

        // The icon the bundle of an application with signature `creator` maps to `type` (finder.md §2.1).
        private IconSuite? ApplicationIcon(FourCC creator, FourCC type)
        {
            foreach (var (fork, bundle) in Applications(creator))
            {
                var map = (FourCC t) => bundle.Maps.Where(m => m.Type == t).SelectMany(m => m.Ids);
                foreach (var (_, frefId) in map(Fref))
                {
                    if (fork.Find(Fref, frefId) is not { } r) continue;
                    var reference = FinderResources.ReadFileReference(Data(r, fork), DecodeOptions.Default, out var complete);
                    if (!complete || reference.FileType != type) continue;
                    foreach (var (local, iconId) in map(IconList))
                    {
                        if (local != reference.LocalIconId) continue;
                        var suite = IconSuite.FromResources((t, id) => fork.Find(t, id) is { } m ? Data(m, fork) : null, iconId);
                        if (suite.Members.Count > 0) return suite;
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
                if (applications.TryGetValue(creator, out var found)) return found;
                var list = new List<(ResourceFork, Bundle)>();
                foreach (var source in bundles[creator])
                {
                    if (Read(source.Fork) is not { } fork) continue;
                    foreach (var r in fork.OfType(Bndl))
                    {
                        var bundle = FinderResources.ReadBundle(Data(r, fork), out var complete);
                        if (complete && bundle.Signature == creator) list.Add((fork, bundle));
                    }
                }
                return applications[creator] = list;
            }
        }

        // The system's generic icon for the kind (Icons.h; in the System file).
        private IconSuite? Generic(FinderItemKind kind)
        {
            lock (generic)
            {
                if (generic.TryGetValue(kind, out var cached)) return cached;
                short id = kind switch
                {
                    FinderItemKind.Folder => GenericFolderId,
                    FinderItemKind.Application => GenericApplicationId,
                    _ => GenericDocumentId,
                };
                IconSuite? suite = null;
                foreach (var fork in systemForks)
                {
                    var candidate = IconSuite.FromResources((t, i) => fork.Find(t, i) is { } r ? Data(r, fork) : null, id);
                    if (candidate.Members.ContainsKey("ICN#"))
                    {
                        suite = candidate;
                        break;
                    }
                }
                return generic[kind] = suite;
            }
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
}
