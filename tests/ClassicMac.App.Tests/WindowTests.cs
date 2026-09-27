using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The main window loads its XAML and draws, headless (Skia, no screen): the tree, an image preview and a styled-text
// preview. With CLASSICMAC_SCREENSHOT set to a path, the frames are saved beside it for a look.
public class WindowTests
{
    private static readonly Lazy<bool> Started = new(() =>
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        return true;
    });

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
    public void The_main_window_draws_the_tree_and_previews()
    {
        _ = Started.Value;
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
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
