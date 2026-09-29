using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Interface;
using MenuItem = Avalonia.Controls.MenuItem;
using MacMenuItem = ClassicMac.Resources.Decoders.Interface.MenuItem;

namespace ClassicMac.App.Tests;

// The main window loads its XAML and draws, headless (Skia, no screen): the tree, an image preview, a styled-text
// preview, the hex view, the tree's context menu and a sound's waveform. With CLASSICMAC_SCREENSHOT set to a path,
// the frames are saved beside it for a look.
public class WindowTests
{
    // One headless Avalonia session, whose own thread is the UI thread; every test runs on it.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    private static void OnUiThread(Action test) => Session.Value.Dispatch(test, CancellationToken.None).GetAwaiter().GetResult();

    // The app, drawn with Skia (not the headless stub drawing), for the session.
    private static class HeadlessApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    // The view-model continues on Avalonia's dispatcher, which a test must pump itself.
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(task.IsCompleted, "timed out");
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(MainWindow window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 600);
        if (Environment.GetEnvironmentVariable("CLASSICMAC_SCREENSHOT") is { Length: > 0 } shot)
        {
#pragma warning disable CS0618 // the simple overload is enough for a test snapshot
            frame.Save(Path.Combine(Path.GetDirectoryName(shot)!, $"{Path.GetFileNameWithoutExtension(shot)}-{name}.png"));
#pragma warning restore CS0618
        }
    }

    [Fact]
    public void The_main_window_draws_the_tree_and_previews() => OnUiThread(The_main_window_draws_the_tree_and_previewsBody);

    private static void The_main_window_draws_the_tree_and_previewsBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var text = MacRoman.Encode("Divinity\rThis is NOT the full version of the manual.\rCafé ƒ™");
            var disk = new HfsBuilder();
            var games = disk.Folder(HfsBuilder.Root, "Games");
            disk.File(games, "Realmz", [1, 2, 3], [], type: "APPL", creator: "RLMZ");
            disk.File(HfsBuilder.Root, "Manual", [], PreviewTests.Fork(
                ("ICN#", 128, null, [.. Enumerable.Range(0, 128).Select(i => (byte)(i % 8 < 4 ? 0xF0 : 0x0F)), .. Enumerable.Repeat((byte)0xFF, 128)]),
                ("TEXT", 128, null, text),
                ("snd ", 128, "Sine", SoundPreviewTests.Sound(20000)),
                ("styl", 128, null, PreviewTests.Styl((0, 20, 1, 24, 0, 0, 0), (9, 3, 0, 12, 0, 0, 0), (17, 3, 1, 12, 0xFFFF, 0, 0), (20, 3, 0, 12, 0, 0, 0)))));
            var path = Path.Combine(folder, "disk.img");
            File.WriteAllBytes(path, disk.Build("Disk"));

            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(path));
            var manual = model.Roots[0].Children.OfType<FileNode>().Single(f => f.Title == "Manual");
            manual.IsExpanded = true;
            Pump(manual.EnsureLoadedAsync());
            Capture(window, "tree");

            var types = manual.Children.OfType<ResourceTypeNode>().ToList();
            model.Selected = types.Single(t => t.Type.ToString() == "ICN#").Children[0];
            Pump(model.PreviewTask);
            Capture(window, "icon");

            model.Selected = types.Single(t => t.Type.ToString() == "TEXT").Children[0];
            Pump(model.PreviewTask);
            Capture(window, "text");

            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            Capture(window, "hex");

            // The tree's context menu, on a resource: the Resource commands and Save Resource As apply, not the file's.
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            var menu = tree.ContextMenu!;
            menu.Open(tree);
            Dispatcher.UIThread.RunJobs();
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["_New Resource…", "_Duplicate", "De_lete", "Get _Info…", "Edit _Hex…", "_Replace Data from File…", "I_mport Image or Sound…",
                "_Save Resource As…"],
                items.Where(i => i.Command?.CanExecute(null) == true).Select(i => (string)i.Header!));
            Capture(window, "context-menu");
            menu.Close();

            model.Selected = types.Single(t => t.Type.ToString() == "snd ").Children[0];
            Pump(model.PreviewTask);
            Assert.True(model.Preview.IsSound);
            Capture(window, "sound");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_document_preview_draws_text_and_pictures() => OnUiThread(The_document_preview_draws_text_and_picturesBody);

    private static void The_document_preview_draws_text_and_picturesBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(DocumentTests.Disk(folder)));
            model.Selected = model.Roots[0].Children.Single(c => c.Title == "Manual");
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "document");
            var pictures = window.GetVisualDescendants().OfType<DocumentPictureView>().ToList();
            Assert.Equal(3, pictures.Count);
            Assert.Equal(new Size(260, 65), pictures[2].Bounds.Size); // scaled to the column
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Dialogs_and_menus_draw() => OnUiThread(Dialogs_and_menus_drawBody);

    private static void Dialogs_and_menus_drawBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(InterfacePreviewTests.Disk(folder)));
            var file = model.Roots[0].Children.OfType<FileNode>().Single();
            Pump(file.EnsureLoadedAsync());
            model.Selected = InterfacePreviewTests.Resource(file, "DLOG", 128);
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "dialog");
            var dialog = new DialogView { Dialog = model.Preview.Dialog, Scale = 2 };
            dialog.Measure(Size.Infinity);
            Assert.Equal((2 * (280 + 12 + 24), 2 * (120 + 12 + 19 + 24)), (dialog.DesiredSize.Width, dialog.DesiredSize.Height)); // borders, gutters; the title bar
            model.Selected = InterfacePreviewTests.Resource(file, "MENU", 128);
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "menu");
            var menu = new MenuView { Menu = model.Preview.Menu, Scale = 1 };
            menu.Measure(Size.Infinity);
            Assert.Equal(20 + 3 * 16 + 24 + 3, menu.DesiredSize.Height); // the bar, three rows, gutters and frame
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Imported_images_are_read_as_unpremultiplied_rgba() => OnUiThread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"cm-import-{Guid.NewGuid():N}.png");
        byte[] rgba = [255, 0, 0, 255, 0, 0, 255, 128, 9, 9, 9, 0, 10, 200, 30, 255];
        File.WriteAllBytes(path, ClassicMac.Resources.Decoders.Images.PngEncoder.Instance.Encode(2, 2, rgba));
        try
        {
            var image = new MainViewModel().LoadImage(path);
            Assert.Equal((2, 2), (image.Width, image.Height));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels[..4]);
            Assert.Equal(new byte[] { 10, 200, 30, 255 }, image.Pixels[12..]);
            Assert.Equal(128, image.Pixels[7]);
            Assert.InRange(image.Pixels[6], 253, 255);                                  // premultiplied and back
            Assert.Equal(0, image.Pixels[11]);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void The_edit_tab_shows_a_form_for_text_resources() => OnUiThread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"cm-forms-{Guid.NewGuid():N}.rsrc");
        byte[] vers = [0x01, 0x20, 0x60, 0x03, 0, 0, 5, .. "1.2b3"u8, 9, .. "1.2b3 (c)"u8];
        var items = InterfaceWriter.WriteDialogItems([
            new DialogItem(new MacRect(70, 150, 90, 220), 4, true, "OK", null, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(10, 10, 50, 220), 8, false, "Hello there", null, ReadOnlyMemory<byte>.Empty)]);
        var dialog = InterfaceWriter.WriteWindow(new WindowTemplate(new MacRect(40, 40, 140, 280), 1, true, false, 0, "", 128, null), dialog: true);
        var menu = InterfaceWriter.WriteMenu(new MenuResource(128, 0, 0, 0, 0xFFFFFFFF, "File",
            [new MacMenuItem("Open…", 0, (byte)'O', 0, 0, true), new MacMenuItem("-", 0, 0, 0, 0, false), new MacMenuItem("Quit", 0, (byte)'Q', 0, 0, true)]));
        File.WriteAllBytes(path, PreviewTests.Fork(("STR#", 128, null, [0, 2, 3, .. "one"u8, 3, .. "two"u8]), ("vers", 1, null, vers),
            ("TEXT", 128, null, "Some text"u8.ToArray()), ("DITL", 128, null, items), ("DLOG", 128, null, dialog), ("MENU", 128, null, menu)));
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            var input = open.Result!;
            Pump(input.EnsureLoadedAsync());
            foreach (var (type, form) in new[] { ("STR#", typeof(StringListForm)), ("vers", typeof(VersionForm)), ("TEXT", typeof(TextForm)),
                ("DLOG", typeof(WindowForm)), ("DITL", typeof(DialogItemsForm)), ("MENU", typeof(MenuForm)) })
            {
                model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
                Pump(model.PreviewTask);
                Assert.IsType(form, model.Form);
                model.SelectedTab = 3;
                Dispatcher.UIThread.RunJobs();
                Capture(window, "edit-" + type.TrimEnd('#'));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
            }

            // The item list's preview follows its form before Apply.
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "DITL").Children[0];
            Pump(model.PreviewTask);
            var ditl = Assert.IsType<DialogItemsForm>(model.Form);
            ditl.Items[1].Text = "Changed";
            Assert.Equal("Changed", model.FormDialog!.Items[1].Item.Text);
            ditl.Items[0].Text = "日本";
            Assert.Contains("Mac OS Roman", model.FormError);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    });
}
