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

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// A folder (or a volume's root, or a container's top level) drawn as the Finder's icon view of its window
    /// (docs/formats/file-systems/finder-windows.md): the items' icons where the Finder put them, from the volume's folder
    /// records, custom icons, applications' bundles and the System's generic icons on the same volume or in the open files.
    /// </summary>
    internal static class FolderPreviews
    {
        private static readonly FourCC Zsys = FourCC.FromString("zsys"), Ffil = FourCC.FromString("FFIL"), Macs = FourCC.FromString("MACS");
        private static readonly HashSet<string> ApplicationTypes = ["APPL", "APPC", "APPD", "appe"];
        private static readonly byte[] IconFileName = "Icon\r"u8.ToArray();

        // What is read once per container: its folders' records and its icon and font sources.
        private static readonly ConditionalWeakTable<ContainerNode, Volume> Volumes = [];

        /// <summary>
        /// The preview of a folder node, an input with contents or a container read open; null for other nodes.
        /// </summary>
        public static PreviewImage? Build(NodeViewModel node, DecodeOptions options, ReadOptions readOptions, DialogSources? sources)
        {
            if (Locate(node) is not var (holder, path)) return null;
            sources ??= DialogSources.None;
            var volume = Volumes.GetValue(holder, h => new Volume(h, readOptions));
            var window = volume.Window(path, sources);
            var fonts = volume.Fonts ?? sources.Fonts;
            var bitmap = FinderWindowRenderer.Render(window, new FinderWindowOptions
            {
                ScreenDepth = options.ScreenDepth,
                QuickDraw = options.QuickDraw == ResourceManagerModel.Rom68k ? QuickDrawVersion.MacRom : QuickDrawVersion.MacOS9,
                Fonts = fonts,
                TextFallback = SystemTextFallback.Instance,
            });
            int count = window.Items.Count(i => !i.IsInvisible);
            var caption = string.Create(CultureInfo.InvariantCulture, $"{count} item{(count == 1 ? "" : "s")}");
            return new PreviewImage(PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels), bitmap.Width, bitmap.Height, caption);
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

            public Volume(ContainerNode holder, ReadOptions readOptions)
            {
                this.readOptions = readOptions;
                files = holder.Children.Select(c => c.File).ToList();
                if (holder.Children.Any(c => c.Format == HfsReader.Instance.FormatName))
                {
                    try
                    {
                        foreach (var folder in HfsReader.Instance.ReadFolders(holder.File.DataFork, new ContainerContext()))
                            folders.TryAdd(Key(folder.Path), folder);
                    }
                    catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
                    {
                        // Unreadable here means read as files only: folders preview with grid placement.
                    }
                }
                // The System files on the volume: generic icons and the label font. [ClassicMac: Mac OS keeps them there]
                foreach (var system in files.Where(f => f.FinderInfo.Type == Zsys && f.FinderInfo.Creator == Macs))
                    if (Fork(system) is { } fork) systemForks.Add(fork);
                fonts = new(LoadFonts);
            }

            public FontLibrary? Fonts => fonts.Value;

            // The window of the folder at `path`: its record's rectangle and scroll, and its items.
            public FinderWindow Window(IReadOnlyList<string> path, DialogSources sources)
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
                    var icon = resolver.Find(FinderItemKind.Folder, default, default, (ushort)info.Flags, () => IconFile(folder.Path));
                    items.Add(new FinderWindowItem(folder.Name, info.Location, (ushort)info.Flags, icon.Suite, FinderItemKind.Folder));
                }
                foreach (var file in files.Where(f => f.FolderPath.Count > path.Count && Key(f.FolderPath.Take(path.Count)) == key))
                {
                    var name = file.FolderPath[path.Count];
                    if (seen.Add(name.ToMacRoman()))
                        items.Add(new FinderWindowItem(name, default, 0, resolver.Find(FinderItemKind.Folder, default, default, 0, null).Suite, FinderItemKind.Folder));
                }
                foreach (var file in files.Where(f => Key(f.FolderPath) == key))
                {
                    var finder = file.FinderInfo;
                    var kind = ApplicationTypes.Contains(finder.Type.ToString()) ? FinderItemKind.Application : FinderItemKind.Document;
                    var icon = resolver.Find(kind, finder.Type, finder.Creator, (ushort)finder.Flags, () => Fork(file));
                    items.Add(new FinderWindowItem(file.Name, finder.Location, (ushort)finder.Flags, icon.Suite, kind));
                }
                var record = folders.GetValueOrDefault(key)?.FinderInfo ?? FolderFinderInfo.Empty;
                return new FinderWindow { Bounds = record.WindowBounds, ScrollPosition = record.ScrollPosition, Items = items };
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
                if (file.ResourceFork.Length == 0) return null;
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
                        if (file.ResourceFork.Length > 0) added += library.AddResourceFork(file.ResourceFork.ToArray());
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or EndOfStreamException)
                    {
                    }
                }
                return added > 0 ? library : null;
            }
        }
    }
}
