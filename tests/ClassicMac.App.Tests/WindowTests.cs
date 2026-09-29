using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

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
            Assert.Equal(["_New Resource…", "_Duplicate", "De_lete", "Get _Info…", "Edit _Hex…", "_Replace Data from File…", "_Save Resource As…"],
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
}
