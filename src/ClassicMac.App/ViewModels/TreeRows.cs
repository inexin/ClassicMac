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

namespace ClassicMac.App.ViewModels;

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

    // Files by what they are (FileTypeIcons): a page with an emblem, or a shape of their own.
    Picture,
    Sound,
    Movie,
    Pdf,
    WebPage,
    WordProcessor,
    Spreadsheet,
    Font,
    Extension,
    ControlPanel,
    Preferences,
    SystemFile,
}

// Text a tree row and the Details tab show for a node: its type and creator, a size, its host size.
public static class NodeFormat
{
    /// <summary>
    /// "type · creator", a zero code (a file never given one, as from another system) a dash; null when both are zero.
    /// </summary>
    public static string? FormatTypeCreator(FourCC type, FourCC creator) =>
        type.Value == 0 && creator.Value == 0
            ? null
            : $"{(type.Value == 0 ? "—" : type.ToString())} · {(creator.Value == 0 ? "—" : creator.ToString())}";

    /// <summary>A size for people: bytes below 1 KB, else KB, MB or GB to one decimal.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} {(bytes == 1 ? "byte" : "bytes")}");
        }

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

    internal static long HostSize(InputNode input)
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
    internal static bool IsArchive(string format) =>
        format.Contains("archive", StringComparison.OrdinalIgnoreCase) || format.Contains("split file", StringComparison.OrdinalIgnoreCase)
        || format is "gzip" or "LHA" or "tar" or "Zip";
}

// The pictures a tree row and the folder preview show for a node: an icon resource drawn small or large, a thumbnail.
public static class NodeImages
{
    internal static readonly HashSet<string> SuiteTypes = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8"];

    /// <summary>How many rows of <paramref name="node"/>'s input have asked for their icon (for tests).</summary>
    internal static int ResolvedIcons(NodeViewModel node) => node.Input.IconRequests;

    internal static bool IsIconResource(string type) => SuiteTypes.Contains(type) || type is "icns" or "cicn" or "CURS";

    // An icon resource's 16-pixel icon: the suite of its ID plotted at 16 × 16 (the small member, else the large one
    // shrunk); a cicn shrunk by nearest neighbour; a cursor as it is.
    internal static byte[]? ResourceIcon(ResourceNode node) => ResourceIcon(node, large: false);

    /// <summary>
    /// The node's large icon for the inspector's header, as PNG: an icon resource's family at 32 × 32 (at 16 when it
    /// has only small members), a cicn fitted into 32, a cursor as it is; a file's own Finder icon at 32. Null for the
    /// others (the header shows the kind icon, a file's by its type).
    /// </summary>
    internal static byte[]? LargeIcon(NodeViewModel node) => node switch
    {
        ResourceNode resource when IsIconResource(resource.Resource.Type.ToString()) => ResourceIcon(resource, large: true),
        ResourceNode { Resource.Type: var type } resource when type.ToString() == "PICT" => Thumbnail(resource),
        ResourceNode { Resource.Type: var type } resource when type.ToString() == "FOND" => FamilyTile(resource),
        FileNode file => FolderPreviews.OwnIcon(file, 32),
        _ => null,
    };

    // A picture fitted into 32 × 32 by nearest neighbour; none when it does not decode.
    internal static byte[]? Thumbnail(ResourceNode node)
    {
        var diagnostics = new List<Diagnostic>();
        try
        {
            var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, node.Input.Options, diagnostics);
            return Fit(ClassicMac.Graphics.Pict.PictReader.Decode(data), 32);
        }
        catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.EndOfStreamException or NotSupportedException or ArgumentException
            or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // A font family: "Aa" in its 24 pt (or largest) strike; none without a strike in its file.
    internal static byte[]? FamilyTile(ResourceNode node)
    {
        var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, node.Input.Options, []);
        return FontFamilyPreview.Create(node.Resource, data, node.Fork, node.Input.Options)?.Tile();
    }

    internal static readonly HashSet<string> LargeMembers = ["ICN#", "icl4", "icl8", "il32"];

    internal static byte[]? ResourceIcon(ResourceNode node, bool large)
    {
        var resource = node.Resource;
        var fork = node.Fork;
        var options = node.Input.Options;
        var diagnostics = new List<Diagnostic>();
        ReadOnlyMemory<byte>? Lookup(FourCC type, short id) =>
            fork.Find(type, id) is { } r ? ResourceDecompression.Default.GetData(r, fork, options, diagnostics) : null;
        byte[]? Suite(IconSuite suite) =>
            Plot(suite, large && suite.Members.Keys.Any(LargeMembers.Contains) ? 32 : 16);
        try
        {
            var type = resource.Type.ToString();
            var data = Lookup(resource.Type, resource.Id)?.ToArray() ?? [];
            return type switch
            {
                "cicn" => Fit(QuickDrawResources.DecodeCicn(data), large ? 32 : 16),
                "CURS" => Png(QuickDrawResources.DecodeCursor(data).Image),
                "icns" => Suite(IconSuite.FromFamily(IconFamily.ReadIcns(data, diagnostics))),
                _ => Suite(IconSuite.FromResources(Lookup, resource.Id)),
            };
        }
        catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.EndOfStreamException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A suite plotted at 16 × 16 on a transparent canvas, as PNG; null when it has no 1-bit member.</summary>
    internal static byte[]? Plot16(IconSuite suite) => Plot(suite, 16);

    /// <summary>A suite plotted at <paramref name="size"/> square on a transparent canvas, as PNG; null when it has no 1-bit member.</summary>
    internal static byte[]? Plot(IconSuite suite, int size)
    {
        var canvas = new RgbaBitmap(size, size);
        var port = new QuickDrawPort(canvas, QuickDrawOptions.Default);
        return suite.Plot(port, new MacRect(0, 0, (short)size, (short)size)) ? Png(canvas) : null;
    }

    /// <summary>A bitmap fitted into 16 × 16 by nearest neighbour (never smoothed), as PNG; one that fits as it is.</summary>
    internal static byte[] Shrink16(RgbaBitmap bitmap) => Fit(bitmap, 16);

    /// <summary>A bitmap fitted into <paramref name="size"/> square by nearest neighbour (never smoothed), as PNG; one that fits as it is.</summary>
    internal static byte[] Fit(RgbaBitmap bitmap, int size)
    {
        if (bitmap.Width <= size && bitmap.Height <= size)
        {
            return Png(bitmap);
        }

        var longest = Math.Max(bitmap.Width, bitmap.Height);
        int w = Math.Max(1, bitmap.Width * size / longest), h = Math.Max(1, bitmap.Height * size / longest);
        var small = new RgbaBitmap(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                small[x, y] = bitmap[x * bitmap.Width / w, y * bitmap.Height / h];
            }
        }

        return Png(small);
    }

    internal static byte[] Png(RgbaBitmap bitmap) => PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels);
}
