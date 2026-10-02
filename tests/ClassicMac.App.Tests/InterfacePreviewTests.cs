using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

// Dialogs, alerts, item lists and menus preview as drawings: the items with their controls, icons and colours.
public class InterfacePreviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-ui-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] Pascal(string text) => [(byte)MacRoman.Encode(text).Length, .. MacRoman.Encode(text)];

    private static byte[] Item(int type, short[] rect, byte[] data) =>
        [0, 0, 0, 0, .. rect.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }), (byte)type, (byte)data.Length, .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    // A file with DLOG 128 (items: OK, a check box, a pop-up control, an icon, a missing control), its grey dctb, an
    // ALRT 129 on the same list, a lone DITL 130 and a MENU 128.
    internal static string Disk(string folder)
    {
        byte[] ditl = [0, 4, .. Item(4, [60, 200, 80, 260], Pascal("OK")[1..]), .. Item(5, [20, 10, 38, 150], Pascal("Sound")[1..]),
            .. Item(7, [60, 10, 80, 180], [0, 128]), .. Item(32, [10, 220, 42, 252], [0, 128]), .. Item(7, [90, 10, 110, 100], [0, 99])];
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Dialogs", [], PreviewTests.Fork(
            ("DLOG", 128, null, [0, 40, 0, 40, 0, 160, 1, 64, 0, 5, 1, 0, 0, 0, 0, 0, 0, 0, 0, 128, .. Pascal("Options")]),
            ("dctb", 128, null, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]),
            ("DITL", 128, null, ditl),
            ("CNTL", 128, null, [0, 60, 0, 10, 0, 80, 0, 180, 0, 0, 1, 0, 0, 60, 0, 1, 0x03, 0xF0, 0, 0, 0, 0, .. Pascal("Speed:")]),
            ("ICON", 128, null, [.. Enumerable.Repeat((byte)0xF0, 128)]),
            ("ALRT", 129, null, [0, 40, 0, 40, 0, 160, 1, 64, 0, 128, 0x55, 0x5D]),
            ("DITL", 130, null, [0, 0, .. Item(8, [10, 10, 30, 200], MacRoman.Encode("Hello ^0"))]),
            ("MENU", 128, null, [0, 128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x03, .. Pascal("File"), .. Pascal("Open…"), 0, (byte)'O', 0, 0,
                .. Pascal("-"), 0, 0, 0, 0, .. Pascal("Quit"), 0, (byte)'Q', 0, 0, 0])));
        var path = Path.Combine(folder, "ui.img");
        File.WriteAllBytes(path, disk.Build("UI"));
        return path;
    }

    private static async Task<(MainViewModel Model, FileNode File)> Open(string path)
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single();
        await file.EnsureLoadedAsync();
        return (model, file);
    }

    internal static ResourceNode Resource(FileNode file, string type, short id) =>
        file.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    [Fact]
    public async Task Dialogs_alerts_and_item_lists_preview_as_drawings()
    {
        var (model, file) = await Open(Disk(folder));

        model.Selected = Resource(file, "DLOG", 128);
        await model.PreviewTask;
        var preview = model.Preview.Dialog!;
        var dialog = preview.Drawing;
        Assert.True(model.Preview.IsDialog && model.Preview.IsZoomable);
        Assert.Equal(2, model.Zoom);
        Assert.Equal(("Options", 5, 280, 120), (dialog.Title, dialog.Definition, dialog.Width, dialog.Height));
        Assert.Equal(new(0xCCCC, 0xCCCC, 0xCCCC), dialog.Content);
        Assert.Equal([4, 5, 7, 32, 7], dialog.Items.Select(i => i.Item.Type));
        Assert.Equal((short)1008, dialog.Items[2].Control!.Definition);
        Assert.NotNull(dialog.Items[3].Image); // the icon
        Assert.Null(dialog.Items[4].Control); // CNTL 99 does not exist
        // Drawn as a movable modal dialog: a 27-row title bar and 6-pixel sides above and beside the content, 7 with the shadow.
        Assert.Equal((280 + 13, 120 + 34), (preview.PixelWidth, preview.PixelHeight));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], preview.Png[..4]);

        model.Selected = Resource(file, "ALRT", 129);
        await model.PreviewTask;
        var alert = model.Preview.Dialog!.Drawing;
        Assert.Equal((DialogKind.Alert, 2, 5), (alert.Kind, alert.DefaultItem, alert.Items.Count));

        model.Selected = Resource(file, "DITL", 130);
        await model.PreviewTask;
        var list = model.Preview.Dialog!.Drawing;
        Assert.Equal((2, 210, 40), (list.Definition, list.Width, list.Height)); // the items plus a margin
    }

    [Fact]
    public async Task Menus_preview_pulled_down()
    {
        var (model, file) = await Open(Disk(folder));

        model.Selected = Resource(file, "MENU", 128);
        await model.PreviewTask;

        Assert.True(model.Preview.IsMenu);
        var menu = model.Preview.Menu!;
        Assert.Equal(("File", 3), (menu.Title, menu.Items.Count));
        Assert.True(menu.Items[1].IsDivider);
        Assert.False(menu.Items[2].Enabled); // bit 3 clear
        Assert.False(menu.Items[1].Enabled); // a divider
    }
}
