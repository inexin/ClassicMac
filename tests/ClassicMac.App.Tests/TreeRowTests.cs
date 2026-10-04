using System.IO.Compression;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Graphics;

namespace ClassicMac.App.Tests;

// What a browse-tree row shows (design/boards/browse-tree.md): its icon (T4), its right-aligned meta, the unsaved mark,
// the "not read" chip and the loading placeholder (T5).
public class TreeRowTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-rows-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static T Child<T>(NodeViewModel node, string title) where T : NodeViewModel =>
        node.Children.OfType<T>().Single(c => c.Title == title);

    // An ICN# whose icon is all black (full mask); an ics# whose icon is black in its left half.
    private static byte[] Icn() => Enumerable.Repeat((byte)0xFF, 256).ToArray();

    private static byte[] IcsLeftHalf() => [.. Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0xFF, 0 }), .. Enumerable.Repeat((byte)0xFF, 32)];

    private static byte[] Zip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(zip.CreateEntry("a.txt").Open()))
        {
            entry.Write("a");
        }

        return stream.ToArray();
    }

    private static FinderInfo Info(string type, string creator, FinderFlags flags = FinderFlags.None) =>
        new() { Type = FourCC.FromString(type), Creator = FourCC.FromString(creator), Flags = flags };

    // A disk: an application, a document with icon resources (ICN# 128 alone, ICN# and ics# 129, a cicn and a CURS),
    // a document with a custom icon, a zip archive and a MacBinary-wrapped disk image in "Containers".
    internal static string Disk(string folder)
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "App", [], [], info: Info("APPL", "ABCD"));
        disk.File(HfsBuilder.Root, "Icons", [], PreviewTests.Fork(
            ("ICN#", 128, null, Icn()), ("ICN#", 129, null, Icn()), ("ics#", 129, null, IcsLeftHalf()),
            ("CURS", 128, null, [.. Enumerable.Repeat((byte)0xFF, 32), .. Enumerable.Repeat((byte)0xFF, 32), 0, 0, 0, 0]),
            ("TEXT", 128, null, "twelve bytes"u8.ToArray())));
        disk.File(HfsBuilder.Root, "Custom", [], PreviewTests.Fork(("ICN#", -16455, null, Icn())), info: Info("TEXT", "ttxt", FinderFlags.HasCustomIcon));
        var containers = disk.Folder(HfsBuilder.Root, "Containers");
        disk.File(containers, "Files.zip", Zip(), [], type: "ZIP ", creator: "ZIP ");
        var inner = new HfsBuilder();
        inner.File(HfsBuilder.Root, "Note", [1], []);
        disk.File(containers, "Inner.img", inner.Build("Inner"), [], type: "dImg", creator: "dCpy");
        var path = Path.Combine(folder, "rows.img");
        File.WriteAllBytes(path, disk.Build("Rows"));
        return path;
    }

    [Fact]
    public async Task Each_node_kind_has_its_icon()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var containers = Child<FolderNode>(input, "Containers");
        var icons = Child<FileNode>(input, "Icons");
        await icons.EnsureLoadedAsync();

        Assert.Equal(TreeIconKind.HardDisk, input.IconKind);
        Assert.Equal(TreeIconKind.Folder, containers.IconKind);
        Assert.Equal(TreeIconKind.Application, Child<FileNode>(input, "App").IconKind);
        Assert.Equal(TreeIconKind.Document, icons.IconKind);
        Assert.Equal(TreeIconKind.Parcel, Child<ContainerFileNode>(containers, "Files.zip (Zip)").IconKind);
        Assert.Equal(TreeIconKind.Floppy, Child<ContainerFileNode>(containers, "Inner.img (HFS volume)").IconKind);
        var type = Child<ResourceTypeNode>(icons, "'ICN#' (2)");
        Assert.Equal(TreeIconKind.ResourceType, type.IconKind);
        Assert.Equal(TreeIconKind.Resource, type.Children[0].IconKind);
        Assert.Equal(TreeIconKind.Loading, Child<FileNode>(input, "Custom").Children.Single().IconKind);
        Assert.True(Child<FileNode>(input, "Custom").Children.Single().IsLoading);
        Assert.False(icons.IsLoading);
        Assert.True(type.IsResourceType);
        Assert.False(icons.IsResourceType);
    }

    [Fact]
    public async Task The_meta_is_type_and_creator_for_files_size_for_resources_format_and_size_for_the_input()
    {
        var model = new MainViewModel();
        model.TreeDisplay.ShowDetails = true;                                // the details column (off by default)
        var path = Disk(folder);
        var input = (await model.OpenAsync(path))!;
        var icons = Child<FileNode>(input, "Icons");
        await icons.EnsureLoadedAsync();

        Assert.Equal("TEXT · ttxt", icons.Meta);
        Assert.Equal("APPL · ABCD", Child<FileNode>(input, "App").Meta);
        Assert.Equal("ZIP  · ZIP ", Child<ContainerFileNode>(Child<FolderNode>(input, "Containers"), "Files.zip (Zip)").Meta);
        Assert.Equal("12 bytes", Child<ResourceTypeNode>(icons, "'TEXT' (1)").Children[0].Meta);
        Assert.Equal("256 bytes", Child<ResourceTypeNode>(icons, "'ICN#' (2)").Children[0].Meta);
        Assert.Equal($"HFS volume · {new FileInfo(path).Length / 1024} KB", input.Meta);
        Assert.Null(Child<FolderNode>(input, "Containers").Meta);
        Assert.Null(Child<ResourceTypeNode>(icons, "'TEXT' (1)").Meta);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_576, "1 MB")]
    [InlineData(1_468_006, "1.4 MB")]
    [InlineData(3_221_225_472, "3 GB")]
    public void Sizes_read_as_bytes_KB_MB_or_GB(long bytes, string text) => Assert.Equal(text, NodeFormat.FormatSize(bytes));

    // A file never given a type or creator (copied from another system) has zeros there: no meta, or a dash for the one
    // that is zero, not "\x00\x00\x00\x00".
    [Theory]
    [InlineData("TEXT", "ttxt", "TEXT · ttxt")]
    [InlineData("\\x00\\x00\\x00\\x00", "\\x00\\x00\\x00\\x00", null)]
    [InlineData("TEXT", "\\x00\\x00\\x00\\x00", "TEXT · —")]
    [InlineData("\\x00\\x00\\x00\\x00", "ttxt", "— · ttxt")]
    public void Zero_types_and_creators_are_left_out(string type, string creator, string? meta) =>
        Assert.Equal(meta, NodeFormat.FormatTypeCreator(FourCC.FromString(type), FourCC.FromString(creator)));

    [Fact]
    public async Task The_unsaved_mark_is_apart_from_the_name()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var file = Child<FileNode>(input, "App");
        var changed = new List<string?>();
        file.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(("App", false), (file.Name, file.IsUnsaved));
        file.Title = file.BaseTitle + " •";
        Assert.Equal(("App", true), (file.Name, file.IsUnsaved));
        Assert.Contains(nameof(NodeViewModel.Name), changed);
        Assert.Contains(nameof(NodeViewModel.IsUnsaved), changed);
        file.Title = file.BaseTitle;
        Assert.False(file.IsUnsaved);
    }

    [Fact]
    public async Task An_unread_container_says_so_until_it_is_read()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var zip = Child<ContainerFileNode>(Child<FolderNode>(input, "Containers"), "Files.zip (Zip)");
        var changed = new List<string?>();
        zip.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(zip.IsUnread);
        Assert.False(input.IsUnread);
        await zip.EnsureLoadedAsync();
        Assert.False(zip.IsUnread);
        Assert.Contains(nameof(NodeViewModel.IsUnread), changed);
    }

    // The 16-pixel icon of a PNG.
    private static RgbaBitmap Decode(byte[] png)
    {
        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        var bitmap = new RgbaBitmap(decoded.Width, decoded.Height);
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                var c = decoded.GetPixel(x, y);
                bitmap[x, y] = new RgbaColor(c.Red, c.Green, c.Blue, c.Alpha);
            }
        }

        return bitmap;
    }

    private static readonly RgbaColor Black = new(0, 0, 0), White = new(255, 255, 255);

    [Fact]
    public async Task Icon_resources_show_their_own_small_icon()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var icons = Child<FileNode>(input, "Icons");
        await icons.EnsureLoadedAsync();
        var icn = Child<ResourceTypeNode>(icons, "'ICN#' (2)");

        // ICN# 129 has an ics# 129: drawn from it (left half black).
        var withSmall = icn.Children[1];
        await withSmall.RequestIconAsync();
        var small = Decode(withSmall.IconPng!);
        Assert.Equal((16, 16), (small.Width, small.Height));
        Assert.Equal((Black, White), (small[2, 8], small[12, 8]));

        // ICN# 128 alone: the 32-pixel icon shrunk to 16 (all black).
        var alone = icn.Children[0];
        await alone.RequestIconAsync();
        Assert.All(Enumerable.Range(0, 256), i => Assert.Equal(Black, Decode(alone.IconPng!)[i % 16, i / 16]));

        // The ics# itself, and a cursor.
        var ics = Child<ResourceTypeNode>(icons, "'ics#' (1)").Children[0];
        await ics.RequestIconAsync();
        Assert.Equal(Black, Decode(ics.IconPng!)[0, 0]);
        var cursor = Child<ResourceTypeNode>(icons, "'CURS' (1)").Children[0];
        await cursor.RequestIconAsync();
        Assert.Equal((16, 16), (Decode(cursor.IconPng!).Width, Decode(cursor.IconPng!).Height));

        // Other resources keep the kind icon.
        var text = Child<ResourceTypeNode>(icons, "'TEXT' (1)").Children[0];
        await text.RequestIconAsync();
        Assert.Null(text.IconPng);
    }

    [Fact]
    public async Task A_cicn_is_shrunk_to_16_pixels_by_nearest_neighbour()
    {
        var bitmap = new RgbaBitmap(32, 32);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bitmap[x, y] = (x / 2 + y / 2) % 2 == 0 ? Black : White;
            }
        }

        var small = Decode(NodeImages.Shrink16(bitmap));
        Assert.Equal((16, 16), (small.Width, small.Height));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal((x + y) % 2 == 0 ? Black : White, small[x, y]);
            }
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Files_show_their_Finder_icon_and_folders_never_do()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var custom = Child<FileNode>(input, "Custom");
        var containers = Child<FolderNode>(input, "Containers");

        await custom.RequestIconAsync();
        var icon = Decode(custom.IconPng!);
        Assert.Equal((16, 16), (icon.Width, icon.Height));
        Assert.Equal(Black, icon[8, 8]);

        // Without the System's generic icons, a plain document keeps the kind icon; a folder never asks.
        var app = Child<FileNode>(input, "App");
        await app.RequestIconAsync();
        Assert.Null(app.IconPng);
        await containers.RequestIconAsync();
        Assert.Null(containers.IconPng);
        Assert.Equal(2, NodeImages.ResolvedIcons(custom));
    }

    [Fact]
    public async Task An_icon_is_resolved_once()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(folder)))!;
        var custom = Child<FileNode>(input, "Custom");
        await Task.WhenAll(custom.RequestIconAsync(), custom.RequestIconAsync());
        await custom.RequestIconAsync();
        Assert.Equal(1, NodeImages.ResolvedIcons(custom));
        Assert.NotNull(custom.IconPng);
    }
}
