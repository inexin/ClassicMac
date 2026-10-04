using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassicMac.App.Controls;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Interface;
using MenuItem = ClassicMac.Resources.Decoders.Interface.MenuItem;

namespace ClassicMac.App.Tests;

// Pixel content at any display scaling (design/TOKENS.md): a whole number of device pixels per Mac pixel,
// k = max(1, floor(zoom × scaling)), the control sized width × k / scaling DIPs, its origin on a whole device pixel.
public class PixelScalingTests
{
    [Theory]
    [InlineData(1, 1.0, 1)]
    [InlineData(1, 1.25, 1)]
    [InlineData(1, 1.5, 1)]
    [InlineData(1, 1.75, 1)]
    [InlineData(1, 2.0, 2)]
    [InlineData(2, 1.0, 2)]
    [InlineData(2, 1.25, 2)]
    [InlineData(2, 1.5, 3)]
    [InlineData(4, 1.25, 5)]
    [InlineData(8, 1.75, 14)]
    [InlineData(1, 0.75, 1)]
    [InlineData(3, 1.1, 3)] // 3.3000000000000003
    public void Device_pixels_per_Mac_pixel_are_a_whole_number(double zoom, double scaling, int expected) =>
        Assert.Equal(expected, PixelScaling.DevicePixels(zoom, scaling));

    [Fact]
    public void Floating_point_error_does_not_lose_a_device_pixel()
    {
        Assert.Equal(7, PixelScaling.DevicePixels(7, 0.1 * 10)); // 0.1 × 10 is 1 exactly; 0.7 × 10 is 7.000000000000001
        Assert.Equal(3, PixelScaling.DevicePixels(2.0, 1.4999999999999998));
    }

    [Theory]
    [InlineData(1, 1.0, 1.0)]
    [InlineData(1, 1.5, 1 / 1.5)]
    [InlineData(2, 1.5, 2.0)]
    [InlineData(1, 2.0, 1.0)]
    [InlineData(2, 1.25, 2 / 1.25)]
    public void A_Mac_pixel_is_k_device_pixels_in_DIPs(double zoom, double scaling, double dips) =>
        Assert.Equal(dips, PixelScaling.Scale(zoom, scaling), 12);

    [Fact]
    public void The_control_is_sized_to_whole_device_pixels()
    {
        var size = PixelScaling.Size(new Size(32, 20), 1, 1.5);
        Assert.Equal(32.0, size.Width * 1.5, 9);
        Assert.Equal(20.0, size.Height * 1.5, 9);
        var zoomed = PixelScaling.Size(new Size(32, 20), 2, 1.5);
        Assert.Equal(96.0, zoomed.Width * 1.5, 9);
        Assert.Equal(60.0, zoomed.Height * 1.5, 9);
    }

    // A picture shrunk below a device pixel per Mac pixel (a document picture wider than its column) keeps its zoom:
    // there is no whole number of device pixels to draw it with.
    [Theory]
    [InlineData(0.5, 1.0, 0.5)]
    [InlineData(0.5, 1.5, 0.5)]
    [InlineData(0.8, 1.5, 1 / 1.5)] // 1.2 device pixels: one
    [InlineData(0.6, 2.0, 0.5)]     // 1.2 device pixels: one
    public void Below_one_device_pixel_a_shrunk_picture_keeps_its_zoom(double zoom, double scaling, double dips) =>
        Assert.Equal(dips, PixelScaling.Scale(zoom, scaling), 12);

    [Theory]
    [InlineData(10.3, 1.5, 10.0)]   // 15.45 device pixels: 15
    [InlineData(10.4, 1.5, 32 / 3.0)] // 15.6: 16
    [InlineData(7.0, 1.25, 7.2)]     // 8.75: 9
    [InlineData(5.25, 2.0, 5.0)]     // 10.5: 10 (to even)
    [InlineData(4.0, 1.0, 4.0)]
    public void Positions_snap_to_whole_device_pixels(double dip, double scaling, double snapped) =>
        Assert.Equal(snapped, PixelScaling.Snap(dip, scaling), 12);

    // A 1-pixel checkerboard drawn at 100%, 125%, 150% and 200% display scaling, at a fractional position: every Mac
    // pixel is k × k device pixels of its own colour, k = 1, 1, 1, 2; no blending, no doubled rows.
    [Theory]
    [InlineData(1.0, 1, 1)]
    [InlineData(1.25, 1, 1)]
    [InlineData(1.5, 1, 1)]
    [InlineData(2.0, 1, 2)]
    [InlineData(1.5, 2, 3)]
    [InlineData(1.25, 2, 2)]
    public void A_one_pixel_checkerboard_stays_one_to_one(double scaling, int zoom, int k) => Headless.OnUiThread(() =>
    {
        const int n = 9;
        var image = new PixelImage { Source = Checkerboard(n), Zoom = zoom, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window
        {
            Width = 200,
            Height = 120,
            Background = Brushes.Red,
            Content = new Border { Padding = new Thickness(10.3, 7.7, 0, 0), Child = image },
        };
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(n * k / scaling, image.Bounds.Width, 9);

        var (pixels, width) = Pixels(window.CaptureRenderedFrame()!);
        var origin = Array.FindIndex(pixels, p => p != Red);
        var (x0, y0) = (origin % width, origin / width);
        for (var y = 0; y < n * k + 1; y++)
        {
            for (var x = 0; x < n * k + 1; x++)
            {
                var expected = x == n * k || y == n * k ? Red : ((x / k + y / k) % 2 == 0 ? Black : White);
                Assert.True(pixels[(y0 + y) * width + x0 + x] == expected,
                    $"device pixel ({x}, {y}) of the checkerboard at {scaling:P0}: {pixels[(y0 + y) * width + x0 + x]:X8}, expected {expected:X8}");
            }
        }

        window.Close();
    });

    private const uint Red = 0xFFFF0000, Black = 0xFF000000, White = 0xFFFFFFFF;

    // An n × n checkerboard of single black and white pixels, black at the top left.
    private static Bitmap Checkerboard(int n)
    {
        var rgba = new byte[n * n * 4];
        for (var i = 0; i < n * n; i++)
        {
            var v = (byte)((i % n + i / n) % 2 == 0 ? 0 : 255);
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (v, v, v, 255);
        }
        return new Bitmap(new MemoryStream(PngEncoder.Instance.Encode(n, n, rgba)));
    }

    // A frame as ARGB words, row by row.
    private static (uint[] Pixels, int Width) Pixels(WriteableBitmap frame)
    {
        using var locked = frame.Lock();
        var pixels = new uint[frame.PixelSize.Width * frame.PixelSize.Height];
        var row = new byte[frame.PixelSize.Width * 4];
        for (var y = 0; y < frame.PixelSize.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(locked.Address + y * locked.RowBytes, row, 0, row.Length);
            for (var x = 0; x < frame.PixelSize.Width; x++)
            {
                var p = row.AsSpan(4 * x, 4);
                var (r, g, b, a) = locked.Format == PixelFormat.Rgba8888 ? (p[0], p[1], p[2], p[3]) : (p[2], p[1], p[0], p[3]);
                pixels[y * frame.PixelSize.Width + x] = (uint)(a << 24 | r << 16 | g << 8 | b);
            }
        }
        return (pixels, frame.PixelSize.Width);
    }

    // The interface previews size themselves by the same rule: at 150%, zoom 2 is 3 device pixels per Mac pixel.
    [Fact]
    public void Dialogs_and_menus_are_sized_in_device_pixels() => Headless.OnUiThread(() =>
    {
        var dialog = new DialogView { Dialog = new DialogPreview(null!, Png(40, 30), 40, 30), Scale = 2 };
        var menu = new MenuView { Menu = new MenuResource(128, 0, 0, 0, 0xFFFFFFFF, "File", [new MenuItem("Open", 0, 0, 0, 0, true)]), Scale = 2 };
        var panel = new StackPanel { Children = { dialog, menu } };
        var window = new Window { Width = 800, Height = 600, Content = new ScrollViewer { Content = panel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var menuAt100 = menu.DesiredSize;
        Assert.Equal(new Size(2 * (40 + 24), 2 * (30 + 24)), dialog.DesiredSize);

        window.SetRenderScaling(1.5);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(3 * (40 + 24) / 1.5, 3 * (30 + 24) / 1.5), dialog.DesiredSize);
        Assert.Equal(menuAt100.Height * 3 / 2 / 1.5, menu.DesiredSize.Height, 9);
        window.Close();
    });

    private static byte[] Png(int width, int height) => PngEncoder.Instance.Encode(width, height, new byte[width * height * 4]);
}
