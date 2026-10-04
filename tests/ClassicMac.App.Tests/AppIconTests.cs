using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

using static Headless;

// The app icon (design/icon/README.md): the Windows icon in the csproj and on the window, the title bar's hand-drawn
// pixel size for the display scaling drawn 1:1, and the About box's.
public class AppIconTests
{
    private static string Source(string relative, [CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", relative));

    [Theory]
    [InlineData(1.0, 16)]
    [InlineData(1.25, 20)]
    [InlineData(1.5, 24)]
    [InlineData(1.75, 24)]
    [InlineData(2.0, 32)]
    [InlineData(2.5, 40)]
    [InlineData(3.0, 48)]
    [InlineData(4.0, 64)]
    [InlineData(0.5, 16)]
    public void The_title_bar_icon_is_the_hand_drawn_size_for_the_scaling(double scaling, int size) =>
        Assert.Equal(size, AppIconView.PixelsFor(16, scaling));

    [Fact]
    public void The_csproj_sets_the_windows_icon_and_the_assets_are_there()
    {
        var csproj = File.ReadAllText(Source("src/ClassicMac.App/ClassicMac.App.csproj"));
        Assert.Contains(@"<ApplicationIcon>Assets\icon\classicmac.ico</ApplicationIcon>", csproj, StringComparison.Ordinal);
        foreach (var name in new[] { "classicmac.ico", "classicmac-16.png", "classicmac-20.png", "classicmac-24.png", "classicmac-32.png",
            "classicmac-40.png", "classicmac-48.png", "classicmac-64.png", "classicmac-128.png" })
        {
            Assert.True(File.Exists(Source("src/ClassicMac.App/Assets/icon/" + name)), name);
        }
    }

    [Fact]
    public void The_window_has_the_icon_and_the_title_bar_draws_its_pixel_size_one_to_one() => OnUiThread(() =>
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        Assert.NotNull(window.Icon);
        var icon = window.Named<Border>("TitleBar")!.GetVisualDescendants().OfType<AppIconView>().Single();
        foreach (var (scaling, pixels) in new[] { (1.0, 16), (1.25, 20), (1.5, 24), (2.0, 32) })
        {
            window.SetRenderScaling(scaling);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(pixels, icon.Bitmap!.PixelSize.Width);
            Assert.Equal(pixels / scaling, icon.DesiredSize.Width, 3);           // one device pixel per icon pixel
            Assert.Equal(pixels / scaling, icon.DesiredSize.Height, 3);
        }

        window.SetRenderScaling(1);
        window.Close();
    });

    [Fact]
    public void The_about_box_shows_the_icon() => OnUiThread(() =>
    {
        var box = AboutBox.Create(AboutInfo.Current, _ => { });
        box.Show();
        Dispatcher.UIThread.RunJobs();
        var icon = box.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "AppIcon");
        Assert.Equal(128, ((Avalonia.Media.Imaging.Bitmap)icon.Source!).PixelSize.Width);
        Assert.Equal(64, icon.Bounds.Width);
        box.Close();
    });
}
