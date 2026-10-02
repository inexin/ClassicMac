using System.Formats.Tar;
using System.Text;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Graphics;

namespace ClassicMac.App.Tests;

// A folder, a volume's root or a container selected in the tree previews as the Finder's icon view of its window.
public class FolderPreviewTests : IDisposable
{
    private static readonly RgbaColor White = new(255, 255, 255), Black = new(0, 0, 0);

    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-folder-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];

    // ICN#s: solid black; black left half; black top half — each with a full mask.
    private static byte[] Solid() => Enumerable.Repeat((byte)0xFF, 256).ToArray();

    private static byte[] LeftHalf()
    {
        var icon = Solid();
        for (int row = 0; row < 32; row++) icon[row * 4 + 2] = icon[row * 4 + 3] = 0;
        return icon;
    }

    private static byte[] TopHalf()
    {
        var icon = Solid();
        Array.Clear(icon, 64, 64);
        return icon;
    }

    private static FinderInfo Info(string type, string creator, int v, int h, FinderFlags flags = FinderFlags.HasBeenInited) =>
        new() { Type = FourCC.FromString(type), Creator = FourCC.FromString(creator), Flags = flags, Location = new MacPoint((short)v, (short)h) };

    private static readonly FolderFinderInfo Window = new()
    {
        WindowBounds = new MacRect(50, 20, 250, 340), ScrollPosition = new MacPoint(10, 5),
        Location = new MacPoint(40, 100),
    };

    // An application ABCD whose bundle maps APPL to ICN# 200 and TEXT to ICN# 201 (top half).
    private static byte[] Application() => PreviewTests.Fork(
        ("BNDL", 128, null, [.. "ABCD"u8, .. BE16(0), .. BE16(1),
            .. "FREF"u8, .. BE16(1), .. BE16(0), .. BE16(128), .. BE16(1), .. BE16(129),
            .. "ICN#"u8, .. BE16(1), .. BE16(0), .. BE16(200), .. BE16(1), .. BE16(201)]),
        ("FREF", 128, null, [.. "APPL"u8, .. BE16(0), 0]),
        ("FREF", 129, null, [.. "TEXT"u8, .. BE16(1), 0]),
        ("ICN#", 200, null, Solid()),
        ("ICN#", 201, null, TopHalf()));

    // A disk with the root window and "Art": a custom-icon file, an invisible one, a document of ABCD, a document no
    // application claims, a subfolder with a custom icon (in its Icon\r file), and one item without a location.
    private string Disk(bool withSystem = false)
    {
        var disk = new HfsBuilder { CatalogLeaves = 5, RootInfo = Window with { WindowBounds = new MacRect(40, 0, 140, 200), ScrollPosition = default } };
        var art = disk.Folder(HfsBuilder.Root, "Art", Window);
        var sub = disk.Folder(art, "Sub", new FolderFinderInfo { Location = new MacPoint(110, 205), Flags = FinderFlags.HasCustomIcon });
        disk.File(sub, "Icon\r", [], PreviewTests.Fork(("ICN#", -16455, null, LeftHalf())), info: Info("TEXT", "ttxt", 0, 0, FinderFlags.IsInvisible));
        disk.File(art, "Custom", [], PreviewTests.Fork(("ICN#", -16455, null, Solid())), info: Info("TEXT", "ttxt", 20, 30, FinderFlags.HasCustomIcon));
        disk.File(art, "Hidden", [], PreviewTests.Fork(("ICN#", -16455, null, Solid())), info: Info("TEXT", "ttxt", 20, 130, FinderFlags.IsInvisible | FinderFlags.HasCustomIcon));
        disk.File(art, "Note", [], [], info: Info("TEXT", "ABCD", 110, 30));
        disk.File(art, "Orphan", [], [], info: Info("TEXT", "QQQQ", 110, 130));
        disk.File(art, "Unplaced", [], [], info: Info("TEXT", "QQQQ", 0, 0));
        disk.File(HfsBuilder.Root, "Writer", [], Application(), info: Info("APPL", "ABCD", 20, 30, FinderFlags.HasBundle));
        if (withSystem)
            disk.File(HfsBuilder.Root, "System", [], PreviewTests.Fork(("ICN#", -4000, null, LeftHalf())), info: Info("zsys", "MACS", 20, 130));
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static async Task<PreviewViewModel> Select(MainViewModel model, NodeViewModel node)
    {
        model.Selected = node;
        await model.PreviewTask;
        return model.Preview;
    }

    // The preview's PNG as RGBA pixels.
    private static RgbaBitmap Decode(PreviewImage image)
    {
        using var decoded = SkiaSharp.SKBitmap.Decode(image.Png);
        var bitmap = new RgbaBitmap(decoded.Width, decoded.Height);
        for (int y = 0; y < decoded.Height; y++)
            for (int x = 0; x < decoded.Width; x++)
            {
                var c = decoded.GetPixel(x, y);
                bitmap[x, y] = new RgbaColor(c.Red, c.Green, c.Blue, c.Alpha);
            }
        return bitmap;
    }

    [Fact]
    public async Task A_folder_shows_its_window_with_each_icon_where_the_Finder_put_it()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var art = input.Children.OfType<FolderNode>().Single(f => f.Title == "Art");

        var preview = await Select(model, art);

        Assert.Equal(PreviewKind.Folder, preview.Kind);
        Assert.True(preview.IsImage);
        Assert.True(preview.IsZoomable);
        Assert.True(preview.HasPreview);
        Assert.Equal(1, model.Zoom);
        Assert.Equal(1, model.SelectedTab);
        var image = Assert.Single(preview.Images);
        Assert.Equal((320, 200), (image.Width, image.Height));
        var bitmap = Decode(image);
        // Custom: its own icon, solid, at (v 20, h 30) less the scroll (10, 5).
        Assert.Equal(Black, bitmap[25, 10]);
        Assert.Equal(Black, bitmap[56, 41]);
        Assert.Equal(White, bitmap[24, 10]);
        // Hidden is invisible: nothing at (v 10, h 125).
        Assert.Equal(White, bitmap[140, 20]);
        // Note: its application's icon for TEXT (top half black).
        Assert.Equal(Black, bitmap[40, 101]);
        Assert.Equal(White, bitmap[40, 125]);
        // Sub: its custom icon from its Icon\r file (left half black).
        Assert.Equal(Black, bitmap[201, 110]);
        Assert.Equal(White, bitmap[225, 110]);
        // Orphan has no icon anywhere: a placeholder, drawn but not filled.
        var inked = Enumerable.Range(0, 32).Sum(y => Enumerable.Range(125, 32).Count(x => bitmap[x, 100 + y] != White));
        Assert.InRange(inked, 1, 32 * 32 / 2);
        Assert.Equal(PreviewKind.Folder, preview.Kind);
        Assert.Equal("5 items", image.Caption);
    }

    [Fact]
    public async Task Generic_icons_come_from_a_System_file_on_the_volume()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk(withSystem: true)))!;

        var bitmap = Decode((await Select(model, input.Children.OfType<FolderNode>().Single(f => f.Title == "Art"))).Images[0]);

        // Orphan: the System's generic document icon (left half black).
        Assert.Equal(Black, bitmap[126, 101]);
        Assert.Equal(White, bitmap[150, 101]);
    }

    [Fact]
    public async Task A_volume_s_root_shows_the_root_window()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;

        var preview = await Select(model, input);

        Assert.Equal(PreviewKind.Folder, preview.Kind);
        var image = Assert.Single(preview.Images);
        Assert.Equal((200, 100), (image.Width, image.Height));
        var bitmap = Decode(image);
        // Writer: an application, its own bundle's icon (solid) at (20, 30).
        Assert.Equal(Black, bitmap[30, 20]);
        Assert.Equal(Black, bitmap[61, 51]);
        // Art: the folder's icon (a placeholder) at its frLocation (40, 100).
        Assert.Contains(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(100, 32).Select(x => bitmap[x, 40 + y])), c => c != White);
    }

    [Fact]
    public async Task A_folder_without_a_window_record_arranges_its_items_in_a_grid()
    {
        var path = Path.Combine(folder, "files.tar");
        using (var stream = File.Create(path))
        using (var tar = new TarWriter(stream, TarEntryFormat.Pax))
        {
            foreach (var name in new[] { "Folder/a.txt", "Folder/b.txt", "Folder/Inner/c.txt" })
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.ASCII.GetBytes(name)) });
        }
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        var node = input.Children.OfType<FolderNode>().Single(f => f.Title == "Folder");

        var preview = await Select(model, node);

        Assert.Equal(PreviewKind.Folder, preview.Kind);
        var image = Assert.Single(preview.Images);
        Assert.Equal("3 items", image.Caption);
        Assert.Equal((ClassicMac.Resources.Decoders.Finder.FinderWindowRenderer.DefaultWidth,
            ClassicMac.Resources.Decoders.Finder.FinderWindowRenderer.DefaultHeight), (image.Width, image.Height));
        var bitmap = Decode(image);
        // Three placeholders in the first three grid cells.
        foreach (var cell in Enumerable.Range(0, 3))
        {
            int left = cell * 80 + 24, top = 8;
            Assert.Contains(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(left, 32).Select(x => bitmap[x, top + y])), c => c != White);
        }
    }

    [Fact]
    public async Task Generic_icons_come_from_the_open_files_too()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var other = Path.Combine(folder, "icons.rsrc");
        File.WriteAllBytes(other, PreviewTests.Fork(("ICN#", -4000, null, LeftHalf())));
        var icons = (await model.OpenAsync(other))!;
        var art = input.Children.OfType<FolderNode>().Single(f => f.Title == "Art");

        var before = Decode((await Select(model, art)).Images[0]);
        await icons.EnsureLoadedAsync();
        model.Selected = null;
        var after = Decode((await Select(model, art)).Images[0]);

        // Orphan: a placeholder until the fork with the generic document icon is loaded in the tree.
        Assert.Equal(White, before[126, 101]);
        Assert.Equal(Black, after[126, 101]);
        Assert.Equal(White, after[150, 101]);
    }

    [Fact]
    public async Task A_container_shows_its_contents_once_read()
    {
        var path = Path.Combine(folder, "disks.tar");
        var disk = File.ReadAllBytes(Disk());
        using (var stream = File.Create(path))
        using (var tar = new TarWriter(stream, TarEntryFormat.Pax))
        {
            // Two files, so the disks are a level down and read only when expanded.
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "disk.img") { DataStream = new MemoryStream(disk) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "copy.img") { DataStream = new MemoryStream(disk) });
        }
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        var container = input.Children.OfType<ContainerFileNode>().Single(c => c.File.Name.ToMacRoman() == "disk.img");

        var unread = await Select(model, container);
        await container.EnsureLoadedAsync();
        model.Selected = null;
        var read = await Select(model, container);

        Assert.Equal(PreviewKind.None, unread.Kind);
        Assert.Equal(PreviewKind.Folder, read.Kind);
        Assert.Equal((200, 100), (read.Images[0].Width, read.Images[0].Height));
    }

    /// <summary>
    /// Renders a folder of a real volume (CLASSICMAC_FOLDER_VOLUME, the folder's Mac path in CLASSICMAC_FOLDER_PATH) and
    /// writes the PNG to CLASSICMAC_FOLDER_OUT when set; skipped without a volume.
    /// </summary>
    [Fact]
    public async Task A_real_volume_s_folder_renders()
    {
        var volume = Environment.GetEnvironmentVariable("CLASSICMAC_FOLDER_VOLUME");
        Assert.SkipWhen(string.IsNullOrEmpty(volume) || !File.Exists(volume), "CLASSICMAC_FOLDER_VOLUME is not set.");
        var model = new MainViewModel();
        NodeViewModel node = (await model.OpenAsync(volume!))!;
        foreach (var part in (Environment.GetEnvironmentVariable("CLASSICMAC_FOLDER_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            node = node.Children.OfType<FolderNode>().Single(f => f.Title == part);

        var preview = await Select(model, node);

        Assert.True(preview.Kind == PreviewKind.Folder, string.Join(" | ", model.Diagnostics.Select(d => d.Diagnostic.Message)));
        if (Environment.GetEnvironmentVariable("CLASSICMAC_FOLDER_OUT") is { Length: > 0 } output)
            File.WriteAllBytes(output, preview.Images[0].Png);
    }

    [Fact]
    public async Task Files_still_preview_as_before()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var art = input.Children.OfType<FolderNode>().Single(f => f.Title == "Art");

        var note = await Select(model, art.Children.OfType<FileNode>().Single(f => f.Title == "Note"));

        Assert.Equal(PreviewKind.None, note.Kind);
    }
}
