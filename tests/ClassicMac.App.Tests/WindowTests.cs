using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The main window loads its XAML and draws, headless (Skia, no screen); the frame is saved for a look when asked.
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

    [Fact]
    public void The_main_window_draws_a_disk()
    {
        _ = Started.Value;
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var disk = new HfsBuilder();
            var games = disk.Folder(HfsBuilder.Root, "Games");
            disk.File(games, "Realmz", [1, 2, 3], [], type: "APPL", creator: "RLMZ");
            disk.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
            var path = Path.Combine(folder, "disk.img");
            File.WriteAllBytes(path, disk.Build("Disk"));

            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            // The view-model continues on Avalonia's dispatcher, which a test must pump itself.
            var opening = model.OpenAsync(path);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!opening.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.True(opening.IsCompleted, "opening timed out");
            model.Selected = model.Roots[0].Children.OfType<FileNode>().First(); // opened: the task has completed
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame!.PixelSize.Width > 600);
#pragma warning disable CS0618 // the simple overload is enough for a test snapshot
            if (Environment.GetEnvironmentVariable("CLASSICMAC_SCREENSHOT") is { Length: > 0 } shot) frame.Save(shot);
#pragma warning restore CS0618
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
