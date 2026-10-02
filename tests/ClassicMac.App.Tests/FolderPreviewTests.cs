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

    private const int Top = 21;   // the window's header

    private static readonly FolderFinderInfo Window = new()
    {
        WindowBounds = new MacRect(50, 20, 250, 340), ScrollPosition = new MacPoint(10, 5),
        Location = new MacPoint(40, 100), Flags = FinderFlags.HasBeenInited,
    };

    // A white ICN# with a full mask, so badges and tints show on it.
    private static byte[] Hollow()
    {
        var icon = Solid();
        Array.Clear(icon, 0, 128);
        return icon;
    }

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
    private string Disk(bool withSystem = false, Action<HfsBuilder, uint>? more = null)
    {
        var disk = new HfsBuilder { CatalogLeaves = 8, RootInfo = Window with { WindowBounds = new MacRect(40, 0, 140, 200), ScrollPosition = default } };
        var art = disk.Folder(HfsBuilder.Root, "Art", Window);
        var sub = disk.Folder(art, "Sub", new FolderFinderInfo { Location = new MacPoint(110, 205), Flags = FinderFlags.HasCustomIcon | FinderFlags.HasBeenInited });
        disk.File(sub, "Icon\r", [], PreviewTests.Fork(("ICN#", -16455, null, LeftHalf())), info: Info("TEXT", "ttxt", 0, 0, FinderFlags.IsInvisible));
        disk.File(art, "Custom", [], PreviewTests.Fork(("ICN#", -16455, null, Solid())), info: Info("TEXT", "ttxt", 20, 30, FinderFlags.HasCustomIcon | FinderFlags.HasBeenInited));
        disk.File(art, "Hidden", [], PreviewTests.Fork(("ICN#", -16455, null, Solid())), info: Info("TEXT", "ttxt", 20, 130, FinderFlags.IsInvisible | FinderFlags.HasCustomIcon));
        disk.File(art, "Note", [], [], info: Info("TEXT", "ABCD", 110, 30));
        disk.File(art, "Orphan", [], [], info: Info("TEXT", "QQQQ", 110, 130));
        disk.File(art, "Unplaced", [], [], info: Info("TEXT", "QQQQ", 0, 0));
        disk.File(HfsBuilder.Root, "Writer", [], Application(), info: Info("APPL", "ABCD", 20, 30, FinderFlags.HasBundle | FinderFlags.HasBeenInited));
        if (withSystem)
            disk.File(HfsBuilder.Root, "System", [], PreviewTests.Fork(("ICN#", -4000, null, LeftHalf())), info: Info("zsys", "MACS", 20, 130));
        more?.Invoke(disk, art);
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
        // Custom: its own icon, solid, at (v 20, h 30) less the scroll (10, 5), below the header.
        Assert.Equal(Black, bitmap[25, Top + 10]);
        Assert.Equal(Black, bitmap[56, Top + 41]);
        Assert.Equal(White, bitmap[24, Top + 10]);
        // Hidden is invisible: nothing at (v 10, h 125).
        Assert.Equal(White, bitmap[140, Top + 20]);
        // Note: its application's icon for TEXT (top half black).
        Assert.Equal(Black, bitmap[40, Top + 101]);
        Assert.Equal(White, bitmap[40, Top + 125]);
        // Sub: its custom icon from its Icon\r file (left half black).
        Assert.Equal(Black, bitmap[201, Top + 110]);
        Assert.Equal(White, bitmap[225, Top + 110]);
        // Orphan has no icon anywhere: a placeholder, drawn but not filled.
        var inked = Enumerable.Range(0, 32).Sum(y => Enumerable.Range(125, 32).Count(x => bitmap[x, Top + 100 + y] != White));
        Assert.InRange(inked, 1, 32 * 32 / 2);
        // Unplaced: arranged in the first free cell from the scroll position, (64, 129), so drawn at (54, 124).
        Assert.Contains(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(124, 32).Select(x => bitmap[x, Top + 54 + y])), c => c != White);
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
        Assert.Equal(Black, bitmap[126, Top + 101]);
        Assert.Equal(White, bitmap[150, Top + 101]);
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
        Assert.Equal(Black, bitmap[30, Top + 20]);
        Assert.Equal(Black, bitmap[61, Top + 51]);
        // Art: the folder's icon (a placeholder) at its frLocation (40, 100).
        Assert.Contains(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(100, 32).Select(x => bitmap[x, Top + 40 + y])), c => c != White);
        Assert.Equal("2 items", image.Caption);
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
        // Three placeholders arranged from (0, 1): (0, 1), (0, 129), then (64, 1), drawn less the default scroll (-8, -16).
        foreach (var (top, left) in new[] { (0, 1), (0, 129), (64, 1) })
            Assert.Contains(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(left + 16, 32).Select(x => bitmap[x, Top + top + 8 + y])), c => c != White);
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
        Assert.Equal(White, before[126, Top + 101]);
        Assert.Equal(Black, after[126, Top + 101]);
        Assert.Equal(White, after[150, Top + 101]);
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

    private async Task<RgbaBitmap> Render(string disk, string folderTitle = "Art", MainViewModel? model = null)
    {
        model ??= new MainViewModel();
        var input = (await model.OpenAsync(disk))!;
        NodeViewModel node = folderTitle.Length == 0 ? input : input.Children.OfType<FolderNode>().Single(f => f.Title == folderTitle);
        return Decode((await Select(model, node)).Images[0]);
    }

    private async Task<PreviewImage> Image(string disk, string folderTitle)
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(disk))!;
        NodeViewModel node = folderTitle.Length == 0 ? input : input.Children.OfType<FolderNode>().Single(f => f.Title == folderTitle);
        return (await Select(model, node)).Images[0];
    }

    [Fact]
    public async Task A_list_view_is_named_in_the_caption()
    {
        var disk = Disk(more: (d, art) => d.File(d.Folder(HfsBuilder.Root, "Listed", Window with { View = 0x0300 }), "Item", [], []));

        var image = await Image(disk, "Listed");

        Assert.Equal("1 item; list view, shown as icons", image.Caption);
    }

    [Fact]
    public async Task Button_and_small_icon_views_are_drawn_as_they_are()
    {
        var disk = Disk(more: (d, art) =>
        {
            d.File(d.Folder(HfsBuilder.Root, "Buttons", Window with { Script = 0x20 }), "Item", [], [], info: Info("TEXT", "QQQQ", 20, 30));
            d.File(d.Folder(HfsBuilder.Root, "Small", Window with { View = 0x0147, Script = 0x50 }), "Item", [], [], info: Info("TEXT", "QQQQ", 20, 30));
        });

        var buttons = await Image(disk, "Buttons");
        var small = await Image(disk, "Small");

        Assert.Equal("1 item", buttons.Caption);
        Assert.Equal("1 item", small.Caption);
        // The button's bevel from (v 20, h 22) less the scroll (10, 5): its top-left corner.
        Assert.Equal(new RgbaColor(0x66, 0x66, 0x66), Decode(buttons)[17, Top + 10]);
        Assert.Equal(White, Decode(small)[17, Top + 10]);
    }

    [Fact]
    public async Task The_volume_s_own_files_are_left_out_of_its_root_window()
    {
        var disk = Disk(more: (d, _) => d.File(HfsBuilder.Root, "Desktop DB", [], [], info: Info("BTFL", "DMGR", 60, 30)));

        Assert.Equal("2 items", (await Image(disk, "")).Caption);
    }

    [Fact]
    public async Task A_folder_that_has_not_been_inited_is_arranged_and_opens_at_the_default_size()
    {
        var disk = Disk(more: (d, art) => d.File(d.Folder(art, "Loose", Window with { Flags = 0, Location = new MacPoint(20, 230) }), "Item", [], []));

        var model = new MainViewModel();
        var input = (await model.OpenAsync(disk))!;
        var art = input.Children.OfType<FolderNode>().Single(f => f.Title == "Art");
        var inner = Decode((await Select(model, art.Children.OfType<FolderNode>().Single(f => f.Title == "Loose"))).Images[0]);
        var bitmap = Decode((await Select(model, art)).Images[0]);

        Assert.Equal((404, 218), (inner.Width, inner.Height));
        // Not at (20, 230) less the scroll (10, 5), but arranged.
        Assert.DoesNotContain(Enumerable.Range(0, 32).SelectMany(y => Enumerable.Range(225, 32).Select(x => bitmap[x, Top + 10 + y])), c => c != White);
    }

    // A System Resources file with the lock and alias badges (top half black), and a System with 'rgb ' label 6 red.
    private static void Systems(HfsBuilder disk)
    {
        disk.File(HfsBuilder.Root, "System Resources", [], PreviewTests.Fork(("ICN#", -20786, null, TopHalf()), ("ICN#", -20789, null, LeftHalf())),
            info: Info("zsyr", "MACS", 0, 0, FinderFlags.HasBeenInited | FinderFlags.IsInvisible));
        disk.File(HfsBuilder.Root, "System", [], PreviewTests.Fork(("rgb ", -16386, null, [0xFF, 0xFF, 0, 0, 0, 0])),
            info: Info("zsys", "MACS", 0, 0, FinderFlags.HasBeenInited | FinderFlags.IsInvisible));
    }

    [Fact]
    public async Task Locked_files_and_aliases_get_the_System_s_badges()
    {
        var disk = Disk(more: (d, art) =>
        {
            Systems(d);
            d.File(art, "Locked", [], PreviewTests.Fork(("ICN#", -16455, null, Hollow())), info: Info("TEXT", "ttxt", 20, 130,
                FinderFlags.HasBeenInited | FinderFlags.HasCustomIcon), locked: true);
            d.File(art, "Alias", [], PreviewTests.Fork(("ICN#", -16455, null, Hollow())), info: Info("TEXT", "ttxt", 20, 230,
                FinderFlags.HasBeenInited | FinderFlags.HasCustomIcon | FinderFlags.IsAlias));
        });

        var bitmap = await Render(disk);

        // Locked at (20, 130) less (10, 5): the lock badge's top half.
        Assert.Equal(Black, bitmap[140, Top + 12]);
        Assert.Equal(White, bitmap[140, Top + 35]);
        // Alias at (20, 230): the alias badge's left half.
        Assert.Equal(Black, bitmap[230, Top + 20]);
        Assert.Equal(White, bitmap[250, Top + 20]);
    }

    [Fact]
    public async Task Label_colours_come_from_the_System_file()
    {
        var disk = Disk(more: (d, art) =>
        {
            Systems(d);
            d.File(art, "Hot", [], PreviewTests.Fork(("ICN#", -16455, null, Solid())), info: Info("TEXT", "ttxt", 20, 130,
                FinderFlags.HasBeenInited | FinderFlags.HasCustomIcon | (FinderFlags)(6 << 1)));
        });

        var bitmap = await Render(disk);

        Assert.Equal(new RgbaColor(255, 0, 0), bitmap[140, Top + 20]);
    }

    [Fact]
    public async Task Custom_badges_follow_the_extended_Finder_flags_of_files_and_folders()
    {
        byte[] badge = [0, 0, .. BE16(300), .. new byte[24]];
        var extended = new byte[16];
        extended[8] = 0x01;   // kExtendedFlagHasCustomBadge, in the word at FXInfo +8
        var disk = Disk(more: (d, art) =>
        {
            d.File(art, "Badged", [], PreviewTests.Fork(("ICN#", -16455, null, Hollow()), ("badg", -16455, null, badge), ("ICN#", 300, null, TopHalf())),
                info: Info("TEXT", "ttxt", 20, 130, FinderFlags.HasBeenInited | FinderFlags.HasCustomIcon) with { Extended = extended });
            var folder = d.Folder(art, "Badged folder", new FolderFinderInfo { Location = new MacPoint(20, 230), Flags = FinderFlags.HasBeenInited | FinderFlags.HasCustomIcon,
                Script = 0x01 });
            d.File(folder, "Icon\r", [], PreviewTests.Fork(("ICN#", -16455, null, Hollow()), ("badg", -16455, null, badge), ("ICN#", 300, null, TopHalf())),
                info: Info("TEXT", "ttxt", 0, 0, FinderFlags.IsInvisible));
        });

        var bitmap = await Render(disk);

        Assert.Equal(Black, bitmap[140, Top + 12]);
        Assert.Equal(White, bitmap[140, Top + 35]);
        Assert.Equal(Black, bitmap[240, Top + 12]);
        Assert.Equal(White, bitmap[240, Top + 35]);
    }

    [Fact]
    public async Task The_views_font_comes_from_the_volume_s_Finder_Preferences()
    {
        var fvl8 = new byte[0x2C];
        fvl8[0x1B] = 3;
        fvl8[0x1F] = 24;
        var plain = await Render(Disk());
        var large = await Render(Disk(more: (d, _) => d.File(HfsBuilder.Root, "Finder Preferences", [], PreviewTests.Fork(("fvl8", 128, null, fvl8)),
            info: Info("pref", "MACS", 0, 0, FinderFlags.HasBeenInited | FinderFlags.IsInvisible))));

        // The icons are where they were; the names, in Geneva 24, are not.
        Assert.Equal(plain[25, Top + 10], large[25, Top + 10]);
        Assert.NotEqual(plain.Pixels, large.Pixels);
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

    /// <summary>
    /// Compares a folder drawn from a disk image with a screenshot of the Finder showing it (the folder named by
    /// CLASSICMAC_FINDER_GOLDEN_FOLDER, default "Art"). CLASSICMAC_FINDER_GOLDEN names a folder holding the disk image
    /// (*.dsk), the full-screen screenshot at 1:1 (*.png) and, optionally, the System files to draw with (any other
    /// files, opened beside it). The window's content is cut from the screenshot at the folder's frRect; the icon area
    /// (below the header, inside the scroll bars) is compared pixel by pixel. Reports the match and writes the drawing
    /// and a difference image (mismatches in magenta) to CLASSICMAC_FINDER_GOLDEN_OUT when set. Skipped without a folder;
    /// nothing in it is committed.
    /// </summary>
    [Fact]
    public async Task A_folder_matches_the_Finder_s_screenshot()
    {
        var golden = Environment.GetEnvironmentVariable("CLASSICMAC_FINDER_GOLDEN");
        Assert.SkipWhen(string.IsNullOrEmpty(golden) || !Directory.Exists(golden), "CLASSICMAC_FINDER_GOLDEN is not set.");
        var disk = Directory.GetFiles(golden!, "*.dsk").Single();
        var screenshot = Directory.GetFiles(golden!, "*.png").Single();
        var folderName = Environment.GetEnvironmentVariable("CLASSICMAC_FINDER_GOLDEN_FOLDER") is { Length: > 0 } f ? f : "Art";
        var model = new MainViewModel();
        // The System files first, so a font suitcase's family (Geneva with its 10-point strike) replaces the System's.
        foreach (var other in Directory.GetFiles(golden!).Where(p => p != disk && p != screenshot && !Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
            .OrderBy(p => Path.GetFileName(p).StartsWith("System", StringComparison.Ordinal) ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
            if (await model.OpenAsync(other) is { } input) await input.EnsureLoadedAsync();
        var volume = (await model.OpenAsync(disk))!;
        var node = volume.Children.OfType<FolderNode>().Single(n => n.Title == folderName);
        var folderInfo = ClassicMac.Files.Hfs.HfsReader.Instance.ReadFolders(ForkData.FromBytes(File.ReadAllBytes(disk)), new ContainerContext())
            .Single(r => r.Name.ToMacRoman() == folderName).FinderInfo;

        var drawn = Decode((await Select(model, node)).Images[0]);

        using var screen = SkiaSharp.SKBitmap.Decode(screenshot);
        var bounds = folderInfo.WindowBounds;
        // A window opened at the default size records no rectangle: CLASSICMAC_FINDER_GOLDEN_ORIGIN gives its content's
        // top-left corner on the screen, as "left,top".
        if (Environment.GetEnvironmentVariable("CLASSICMAC_FINDER_GOLDEN_ORIGIN") is { Length: > 0 } origin)
        {
            var corner = origin.Split(',').Select(n => short.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            bounds = new MacRect(corner[1], corner[0], (short)(corner[1] + drawn.Height), (short)(corner[0] + drawn.Width));
        }
        Assert.Equal((bounds.Width, bounds.Height), (drawn.Width, drawn.Height));
        var difference = new RgbaBitmap(drawn.Width, drawn.Height);
        int compared = 0, matched = 0;
        for (int y = 0; y < drawn.Height; y++)
            for (int x = 0; x < drawn.Width; x++)
            {
                var c = screen.GetPixel(bounds.Left + x, bounds.Top + y);
                var expected = new RgbaColor(c.Red, c.Green, c.Blue);
                bool iconArea = y >= Top && y < drawn.Height - 15 && x < drawn.Width - 15;
                bool same = drawn[x, y] == expected;
                if (iconArea)
                {
                    compared++;
                    if (same) matched++;
                }
                difference[x, y] = same ? new RgbaColor((byte)(expected.R / 2 + 127), (byte)(expected.G / 2 + 127), (byte)(expected.B / 2 + 127))
                    : iconArea ? new RgbaColor(255, 0, 255) : new RgbaColor(255, 200, 255);
            }
        var report = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{folderName}: {matched} of {compared} icon-area pixels match ({100.0 * matched / compared:F2}%).");
        TestContext.Current.SendDiagnosticMessage(report);
        if (Environment.GetEnvironmentVariable("CLASSICMAC_FINDER_GOLDEN_OUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "report.txt"), report);
            File.WriteAllBytes(Path.Combine(output, "drawn.png"), ClassicMac.Resources.Decoders.Images.PngEncoder.Instance.Encode(drawn.Width, drawn.Height, drawn.Pixels));
            File.WriteAllBytes(Path.Combine(output, "difference.png"), ClassicMac.Resources.Decoders.Images.PngEncoder.Instance.Encode(difference.Width, difference.Height, difference.Pixels));
        }
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
