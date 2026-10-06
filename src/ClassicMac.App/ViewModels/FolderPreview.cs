using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// A folder (or a volume's root, or a container's top level) drawn as the Finder's icon view of its window
/// (docs/formats/file-systems/finder-windows.md): the items' icons where the Finder put them, from the volume's folder
/// records, custom icons, applications' bundles and the System's generic icons on the same volume or in the open files.
/// </summary>
internal static class FolderPreviews
{
    private static readonly FourCC Zsys = FourCC.FromString("zsys"), Zsyr = FourCC.FromString("zsyr"), Ffil = FourCC.FromString("FFIL"),
        Macs = FourCC.FromString("MACS"), Pref = FourCC.FromString("pref");
    private static readonly HashSet<string> ApplicationTypes = ["APPL", "APPC", "APPD", "appe"];
    private static readonly byte[] IconFileName = "Icon\r"u8.ToArray();

    // What is read once per container: its folders' records and its icon and font sources.
    private static readonly ConditionalWeakTable<ContainerNode, Volume> Volumes = [];

    /// <summary>
    /// The preview of a folder node, an input with contents or a container read open; null for other nodes.
    /// </summary>
    public static PreviewImage? Build(NodeViewModel node, DecodeOptions options, ReadOptions readOptions, DialogSources? sources)
    {
        if (Locate(node) is not var (holder, path))
        {
            return null;
        }

        sources ??= DialogSources.None;
        var volume = Volumes.GetValue(holder, h => new Volume(h, readOptions));
        var (window, resolver) = volume.Window(path, sources);
        var fonts = volume.Fonts ?? sources.Fonts;
        var preferences = volume.Preferences ?? FinderPreferences.Default;
        var finderOptions = new FinderWindowOptions
        {
            ScreenDepth = options.ScreenDepth,
            QuickDraw = options.QuickDraw == ResourceManagerModel.Rom68k ? QuickDrawVersion.MacRom : QuickDrawVersion.MacOS9,
            LabelFontId = preferences.ViewsFontId,
            LabelFontSize = preferences.ViewsFontSize,
            LabelColors = resolver.LabelColors,
            Fonts = fonts,
            TextFallback = SystemTextFallback.Instance,
        };
        var bitmap = FinderWindowRenderer.RenderWindow(window, finderOptions);
        int count = FinderWindowRenderer.Place(window, finderOptions).Count;
        // A list view is drawn as large icons; the caption says so.
        var view = window.View.Kind == FinderViewKind.List ? $"; {window.View.Name}, shown as icons" : "";
        var caption = string.Create(CultureInfo.InvariantCulture, $"{count} item{(count == 1 ? "" : "s")}{view}");
        return new PreviewImage(PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels), bitmap.Width, bitmap.Height, caption);
    }

    /// <summary>
    /// A file's own icon at <paramref name="size"/> square, as PNG, for the inspector's header: its custom icon or the
    /// one its application's bundle gives its type, found as the folder preview finds them, cached per volume; null when
    /// it has neither (the header shows its type's icon, as the tree does: design/boards/browse-tree.md, T4).
    /// </summary>
    public static byte[]? OwnIcon(FileNode node, int size) =>
        Holder(node) is { } holder ? Volumes.GetValue(holder, h => new Volume(h, node.Input.Options)).OwnIcon(node.File, size) : null;

    // The container whose contents hold a file node: its input's or its container file's.
    private static ContainerNode? Holder(NodeViewModel node)
    {
        var at = node.Parent;
        while (at is not null and not InputNode and not ContainerFileNode)
        {
            at = at.Parent;
        }

        return at switch
        {
            InputNode input => input.Root,
            ContainerFileNode container => container.Node,
            _ => null,
        };
    }

    // The container holding the node's files, and the node's folder path in it.
    private static (ContainerNode Holder, IReadOnlyList<string> Path)? Locate(NodeViewModel node)
    {
        var path = new List<string>();
        var at = node;
        while (at is FolderNode)
        {
            path.Insert(0, at.Title);
            at = at.Parent!;
        }
        ContainerNode? holder = at switch
        {
            InputNode input => input.Root,
            ContainerFileNode container => container.Node,
            _ => null,
        };
        // A container holding one file that holds others (MacBinary or a disk image around a disk) shows that file's
        // contents: the window the Finder would open for it [ClassicMac].
        while (path.Count == 0 && holder is { Children: [{ Children.Count: > 0 } only] })
        {
            holder = only;
        }

        return holder is { Children.Count: > 0 } ? (holder, path) : null;
    }

    private static string Key(IEnumerable<string> path) => string.Join(":", path);

    private static string Key(IEnumerable<MacString> path) => Key(path.Select(p => p.ToMacRoman()));

    private sealed class Volume
    {
        private readonly ReadOptions readOptions;
        private readonly Dictionary<string, MacFolder> folders = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<MacFile> files;
        private readonly Lazy<FontLibrary?> fonts;
        private readonly List<ResourceFork> systemForks = [];
        private FinderIconResolver? resolver;
        private IReadOnlyList<ResourceFork> openForks = [];

        private readonly MacString holderName;

        public Volume(ContainerNode holder, ReadOptions readOptions)
        {
            this.readOptions = readOptions;
            holderName = holder.File.Name;
            files = holder.Children.Select(c => c.File).ToList();
            if (holder.Children.Any(c => c.Format == HfsReader.Instance.FormatName))
            {
                try
                {
                    foreach (var folder in HfsReader.Instance.ReadFolders(holder.File.DataFork, new ContainerContext()))
                    {
                        folders.TryAdd(Key(folder.Path), folder);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
                {
                    // Unreadable here means read as files only: folders preview with grid placement.
                }
            }
            // The System and System Resources files on the volume: system icons, badges, label colours and the views
            // font; the Finder Preferences file: the views font's family and size. [ClassicMac: Mac OS keeps them there]
            foreach (var system in files.Where(f => (f.FinderInfo.Type == Zsys || f.FinderInfo.Type == Zsyr) && f.FinderInfo.Creator == Macs))
            {
                if (Fork(system) is { } fork)
                {
                    systemForks.Add(fork);
                }
            }

            foreach (var file in files.Where(f => f.FinderInfo.Type == Pref && f.FinderInfo.Creator == Macs && f.Name.ToMacRoman() == "Finder Preferences"))
            {
                if (Preferences is null && Fork(file) is { } fork)
                {
                    Preferences = FinderPreferences.FromFork(fork, readOptions);
                }
            }

            fonts = new(LoadFonts);
        }

        public FontLibrary? Fonts => fonts.Value;

        // The views font from the volume's Finder Preferences, if it has one.
        public FinderPreferences? Preferences { get; }

        // The window of the folder at `path`: its record's rectangle, scroll and view, and its items.
        public (FinderWindow Window, FinderIconResolver Resolver) Window(IReadOnlyList<string> path, DialogSources sources)
        {
            lock (systemForks)
            {
                return WindowLocked(path, sources);
            }
        }

        private readonly Dictionary<(MacFile File, int Size), byte[]?> treeIcons = new(new IconKeyComparer());

        // A file by reference, with the icon's size.
        private sealed class IconKeyComparer : IEqualityComparer<(MacFile File, int Size)>
        {
            public bool Equals((MacFile File, int Size) x, (MacFile File, int Size) y) => ReferenceEquals(x.File, y.File) && x.Size == y.Size;

            public int GetHashCode((MacFile File, int Size) key) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.File), key.Size);
        }

        // A file's own icon (custom, or its application's), once per file; with the resolver the folder preview last
        // made, or one with the volume's own System files. A generic icon is not the file's own.
        public byte[]? OwnIcon(MacFile file, int size)
        {
            lock (systemForks)
            {
                if (treeIcons.TryGetValue((file, size), out var cached))
                {
                    return cached;
                }

                var icon = FileIcon(resolver ?? Resolver(DialogSources.None), file, () => Fork(file));
                var own = icon.Source is FinderIconSource.Custom or FinderIconSource.Application ? icon.Suite : null;
                return treeIcons[(file, size)] = own is null ? null : NodeImages.Plot(own, size);
            }
        }

        // A file's icon as the Finder finds it.
        private static FinderIcon FileIcon(FinderIconResolver resolver, MacFile file, Func<ResourceFork?> fork)
        {
            var finder = file.FinderInfo;
            var kind = ApplicationTypes.Contains(finder.Type.ToString()) ? FinderItemKind.Application : FinderItemKind.Document;
            // The extended Finder flags: FXInfo +8, a word over fdScript and fdXFlags (Finder.h, ExtendedFileInfo).
            var extended = finder.Extended.Length >= 10 ? new BigEndianReader(finder.Extended).ReadUInt16At(8) : (ushort)0;
            return resolver.Find(kind, finder.Type, finder.Creator, (ushort)finder.Flags, fork, extended, file.IsLocked);
        }

        private (FinderWindow Window, FinderIconResolver Resolver) WindowLocked(IReadOnlyList<string> path, DialogSources sources)
        {
            var key = Key(path);
            var resolver = Resolver(sources);
            var items = new List<FinderWindowItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Subfolders: those the volume records, then those only the files' paths name (archives).
            foreach (var folder in folders.Values.Where(f => !f.IsRoot && Key(f.FolderPath) == key).OrderBy(f => f.Name.ToMacRoman(), StringComparer.Ordinal))
            {
                seen.Add(folder.Name.ToMacRoman());
                var info = folder.FinderInfo;
                var icon = resolver.Find(FinderItemKind.Folder, default, default, (ushort)info.Flags, () => IconFile(folder.Path),
                    (ushort)(((byte)info.Script << 8) | (byte)info.ExtendedFlags));
                items.Add(new FinderWindowItem(folder.Name, info.Location, (ushort)info.Flags, icon.Suite, FinderItemKind.Folder) { Badges = icon.Badges });
            }
            foreach (var file in files.Where(f => f.FolderPath.Count > path.Count && Key(f.FolderPath.Take(path.Count)) == key))
            {
                var name = file.FolderPath[path.Count];
                if (seen.Add(name.ToMacRoman()))
                {
                    items.Add(new FinderWindowItem(name, default, 0, resolver.Find(FinderItemKind.Folder, default, default, 0, null).Suite, FinderItemKind.Folder));
                }
            }
            foreach (var file in files.Where(f => Key(f.FolderPath) == key))
            {
                var finder = file.FinderInfo;
                var kind = ApplicationTypes.Contains(finder.Type.ToString()) ? FinderItemKind.Application : FinderItemKind.Document;
                var icon = FileIcon(resolver, file, () => Fork(file));
                items.Add(new FinderWindowItem(file.Name, finder.Location, (ushort)finder.Flags, icon.Suite, kind) { Badges = icon.Badges });
            }
            var self = folders.GetValueOrDefault(key);
            var record = self?.FinderInfo ?? FolderFinderInfo.Empty;
            var root = folders.Values.FirstOrDefault(f => f.IsRoot);
            // The title: the folder's name with its icon, or at the top the volume's (a disk's icon) or the
            // container's name [ClassicMac: an archive's top level has no Finder window; its own name and a folder].
            var title = path.Count > 0 ? MacString.FromMacRoman(path[^1]) : root?.Name ?? holderName;
            var titleIcon = path.Count == 0 && root is not null
                ? resolver.SystemTypeIcon(FourCC.FromString("hdsk")).Suite
                : resolver.Find(FinderItemKind.Folder, default, default, (ushort)record.Flags, path.Count > 0 ? () => IconFile(self?.Path ?? []) : null).Suite;
            var window = new FinderWindow
            {
                Title = title,
                TitleIcon = titleIcon,
                FreeBytes = root?.FreeBytes,
                Bounds = record.WindowBounds,
                ScrollPosition = record.ScrollPosition,
                Flags = (ushort)record.Flags,
                IsVolumeRoot = self?.IsRoot == true,
                View = FinderView.Read(record.View, record.Script, record.OpenChain),
                Items = items,
            };
            return (window, resolver);
        }

        // One resolver per volume, made again when the open files with generic icons change.
        private FinderIconResolver Resolver(DialogSources sources)
        {
            lock (systemForks)
            {
                if (resolver is null || !openForks.SequenceEqual(sources.GenericIconForks))
                {
                    openForks = sources.GenericIconForks;
                    resolver = new FinderIconResolver(
                        files.Where(f => (f.FinderInfo.Flags & FinderFlags.HasBundle) != 0).Select(f => new FinderBundleSource(f.FinderInfo.Creator, () => Fork(f))),
                        [.. systemForks, .. openForks], readOptions);
                }
                return resolver;
            }
        }

        // The fork of a folder's Icon\r file, which holds its custom icon.
        private ResourceFork? IconFile(IReadOnlyList<MacString> path)
        {
            var key = Key(path);
            return files.FirstOrDefault(f => f.Name.Bytes.SequenceEqual(IconFileName) && Key(f.FolderPath) == key) is { } file ? Fork(file) : null;
        }

        private ResourceFork? Fork(MacFile file)
        {
            if (file.ResourceFork.Length == 0)
            {
                return null;
            }

            try
            {
                return MacFileResources.Read(file, readOptions).Fork;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
            {
                return null;
            }
        }

        // The label font: the families of the volume's System files and of the font suitcases beside them.
        private FontLibrary? LoadFonts()
        {
            var library = new FontLibrary();
            var added = 0;
            var suitcases = files.Where(f => f.FinderInfo.Type == Ffil && files.Any(s => s.FinderInfo.Type == Zsys && s.FinderInfo.Creator == Macs
                && Key(f.FolderPath) == Key([.. s.FolderPath, MacString.FromMacRoman("Fonts")])));
            foreach (var file in files.Where(f => f.FinderInfo.Type == Zsys && f.FinderInfo.Creator == Macs).Concat(suitcases))
            {
                try
                {
                    if (file.ResourceFork.Length > 0)
                    {
                        added += library.AddResourceFork(file.ResourceFork.ToArray());
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or EndOfStreamException)
                {
                }
            }
            return added > 0 ? library : null;
        }
    }
}
