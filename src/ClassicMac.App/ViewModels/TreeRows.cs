using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels
{
    /// <summary>The 16-pixel icon a tree row shows for its kind (design/boards/browse-tree.md, T4).</summary>
    public enum TreeIconKind
    {
        HardDisk,
        Floppy,
        Parcel,
        Folder,
        Application,
        Document,
        ResourceType,
        Resource,
        Loading,

        /// <summary>A "No name" group: two stacked documents (design/boards/tree-no-name.md).</summary>
        NoNameGroup,
    }

    // What a browse-tree row shows besides its title (design/boards/browse-tree.md): its icon, its own icon once
    // resolved, the right-aligned meta, the unsaved mark, the "not read" chip and the drag-source outline.
    public abstract partial class NodeViewModel
    {
        private static readonly HashSet<string> ApplicationTypes = ["APPL", "APPC", "APPD", "appe"];
        private static readonly HashSet<string> SuiteTypes = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8"];
        private const string UnsavedMark = " •";

        private Task? iconLoading;

        /// <summary>The node's own 16 × 16 icon as PNG (a file's Finder icon, an icon resource's), once resolved; else null.</summary>
        [ObservableProperty]
        private byte[]? iconPng;

        /// <summary>Whether the node is being dragged out of the tree (its row is outlined).</summary>
        [ObservableProperty]
        private bool isDragSource;

        /// <summary>The icon for the node's kind, shown until (or instead of) its own.</summary>
        public TreeIconKind IconKind => this switch
        {
            InputNode => TreeIconKind.HardDisk,
            ContainerFileNode container => IsArchive(container.ContentFormat) ? TreeIconKind.Parcel : TreeIconKind.Floppy,
            FileNode file => ApplicationTypes.Contains(file.File.FinderInfo.Type.ToString()) ? TreeIconKind.Application : TreeIconKind.Document,
            ResourceTypeNode => TreeIconKind.ResourceType,
            NoNameGroupNode => TreeIconKind.NoNameGroup,
            ResourceNode => TreeIconKind.Resource,
            LoadingNode => TreeIconKind.Loading,
            _ => TreeIconKind.Folder,
        };

        /// <summary>The "Loading…" placeholder: a spinner, no icon.</summary>
        public bool IsLoading => this is LoadingNode;

        /// <summary>A resource type's title (<c>'ICN#' (12)</c>) is set in mono.</summary>
        public bool IsResourceType => this is ResourceTypeNode;

        /// <summary>The name shown: the title without the unsaved mark, or the alias for a file with no name.</summary>
        public string Name => Alias ?? (IsUnsaved ? Title[..^UnsavedMark.Length] : Title);

        /// <summary>Whether the node has unsaved edits (its title carries " •").</summary>
        public bool IsUnsaved => Title != BaseTitle && Title.EndsWith(UnsavedMark, StringComparison.Ordinal);

        /// <summary>A container file not read yet: it is read when expanded.</summary>
        public bool IsUnread => this is ContainerFileNode { Node.UnreadFormat: not null };

        /// <summary>
        /// The row's right-aligned meta: type · creator for a file, the size for a resource, format and size for the
        /// input; null for the others.
        /// </summary>
        public string? Meta => this switch
        {
            FileNode file => $"{file.File.FinderInfo.Type} · {file.File.FinderInfo.Creator}",
            ContainerFileNode container => $"{container.File.FinderInfo.Type} · {container.File.FinderInfo.Creator}",
            ResourceNode resource => FormatSize(resource.Resource.Length),
            NoNameGroupNode group => string.Create(CultureInfo.InvariantCulture, $"{group.Children.Count} files"),
            InputNode input => $"{(input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "resource fork")} · {FormatSize(HostSize(input))}",
            _ => null,
        };

        partial void OnTitleChanged(string value)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(IsUnsaved));
        }

        /// <summary>Tells the row the container was read, so its "not read" chip goes.</summary>
        internal void OnRead() => OnPropertyChanged(nameof(IsUnread));

        /// <summary>Tells the row its meta changed (a "No name" group's count).</summary>
        internal void OnMetaChanged() => OnPropertyChanged(nameof(Meta));

        /// <summary>A size for people: bytes below 1 KB, else KB, MB or GB to one decimal.</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} {(bytes == 1 ? "byte" : "bytes")}");
            string[] units = ["KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value, 1):0.#} {units[unit]}");
        }

        private static long HostSize(InputNode input)
        {
            try
            {
                return new System.IO.FileInfo(input.Path).Length;
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return input.Root.File.DataFork.Length;
            }
        }

        // Archives show a parcel, disk images and the other containers a floppy.
        private static bool IsArchive(string format) =>
            format.Contains("archive", StringComparison.OrdinalIgnoreCase) || format.Contains("split file", StringComparison.OrdinalIgnoreCase)
            || format is "gzip" or "LHA" or "tar" or "Zip";

        /// <summary>
        /// Resolves the node's own icon (once), off the UI thread: a file's Finder icon, an icon resource's small icon.
        /// The tree asks only for rows on screen; nodes without one of their own complete at once.
        /// </summary>
        public Task RequestIconAsync() => iconLoading ??= LoadIconAsync();

        private async Task LoadIconAsync()
        {
            Func<byte[]?>? load = this switch
            {
                FileNode file => () => FolderPreviews.TreeIcon(file),
                ResourceNode resource when IsIconResource(resource.Resource.Type.ToString()) => () => ResourceIcon(resource),
                _ => null,
            };
            if (load is null) return;
            if (await Task.Run(load) is { } png) IconPng = png;
        }

        /// <summary>How many file icons the volume holding <paramref name="node"/> has resolved (for tests).</summary>
        internal static int ResolvedIcons(NodeViewModel node) => FolderPreviews.ResolvedTreeIcons(node);

        private static bool IsIconResource(string type) => SuiteTypes.Contains(type) || type is "icns" or "cicn" or "CURS";

        // An icon resource's 16-pixel icon: the suite of its ID plotted at 16 × 16 (the small member, else the large one
        // shrunk); a cicn shrunk by nearest neighbour; a cursor as it is.
        private static byte[]? ResourceIcon(ResourceNode node)
        {
            var resource = node.Resource;
            var fork = node.Fork;
            var options = node.Input.Options;
            var diagnostics = new List<Diagnostic>();
            ReadOnlyMemory<byte>? Lookup(FourCC type, short id) =>
                fork.Find(type, id) is { } r ? ResourceDecompression.Default.GetData(r, fork, options, diagnostics) : null;
            try
            {
                var type = resource.Type.ToString();
                var data = Lookup(resource.Type, resource.Id)?.ToArray() ?? [];
                return type switch
                {
                    "cicn" => Shrink16(QuickDrawResources.DecodeCicn(data)),
                    "CURS" => Png(QuickDrawResources.DecodeCursor(data).Image),
                    "icns" => Plot16(IconSuite.FromFamily(IconFamily.ReadIcns(data, diagnostics))),
                    _ => Plot16(IconSuite.FromResources(Lookup, resource.Id)),
                };
            }
            catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.EndOfStreamException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>A suite plotted at 16 × 16 on a transparent canvas, as PNG; null when it has no 1-bit member.</summary>
        internal static byte[]? Plot16(IconSuite suite)
        {
            var canvas = new RgbaBitmap(16, 16);
            var port = new QuickDrawPort(canvas, QuickDrawOptions.Default);
            return suite.Plot(port, new MacRect(0, 0, 16, 16)) ? Png(canvas) : null;
        }

        /// <summary>A bitmap fitted into 16 × 16 by nearest neighbour (never smoothed), as PNG; one that fits as it is.</summary>
        internal static byte[] Shrink16(RgbaBitmap bitmap)
        {
            if (bitmap.Width <= 16 && bitmap.Height <= 16) return Png(bitmap);
            var size = Math.Max(bitmap.Width, bitmap.Height);
            int w = Math.Max(1, bitmap.Width * 16 / size), h = Math.Max(1, bitmap.Height * 16 / size);
            var small = new RgbaBitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    small[x, y] = bitmap[x * bitmap.Width / w, y * bitmap.Height / h];
            return Png(small);
        }

        private static byte[] Png(RgbaBitmap bitmap) => PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels);
    }
}
