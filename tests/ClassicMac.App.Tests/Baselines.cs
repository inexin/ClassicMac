using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ClassicMac.App.Tests;

/// <summary>
/// Screenshot baselines for the app's own UI (design/BACKLOG-PLAN.md F6): a window is drawn in light, dark and at 150%
/// display scaling and each frame compared with <c>tests/golden/app/&lt;name&gt;-&lt;light|dark|150&gt;.png</c>.
/// Compared on Windows only (text is rasterised by the platform's font back end, so other systems differ); set
/// <c>CLASSICMAC_UPDATE_BASELINES=1</c> to rewrite them. A mismatch writes the actual frame and a diff image to a temp
/// folder and names them in the failure.
/// </summary>
internal static class Baselines
{
    /// <summary>
    /// The largest difference allowed in any channel of a pixel. Skia picks its blending code by CPU (SSE2, AVX2,
    /// AVX-512, NEON), and those round differently by one or two levels on blended pixels; a real change (a moved line,
    /// a different colour or glyph) differs far more.
    /// </summary>
    public const int Tolerance = 2;

    /// <summary>
    /// How many pixels may differ by more than <see cref="Tolerance"/>, each by at most <see cref="StrayLimit"/>. Skia
    /// caches stroked paths and glyph masks for the process, so the anti-aliased edge of an identical path (a toolbar
    /// icon's corner) can come out a few levels apart depending on what earlier tests drew; a real change moves more
    /// pixels, or moves them further.
    /// </summary>
    public const int StrayPixels = 8;

    /// <inheritdoc cref="StrayPixels"/>
    public const int StrayLimit = 32;

    public enum Variant { Light, Dark, Scaled150 }

    public static readonly Variant[] All = [Variant.Light, Variant.Dark, Variant.Scaled150];

    public static bool Compared => OperatingSystem.IsWindows();

    public static bool Updating => Environment.GetEnvironmentVariable("CLASSICMAC_UPDATE_BASELINES") == "1";

    public static string Folder { get; } = SourceFolder();

    // Where a mismatch's actual frame and diff go: CLASSICMAC_BASELINE_FAILURES (CI keeps it), else the temp folder.
    public static string FailureFolder { get; } =
        Environment.GetEnvironmentVariable("CLASSICMAC_BASELINE_FAILURES") ?? Path.Combine(Path.GetTempPath(), "classicmac-baselines");

    private static string SourceFolder([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "golden", "app"));

    public static string Suffix(Variant variant) => variant switch
    {
        Variant.Light => "light",
        Variant.Dark => "dark",
        _ => "150",
    };

    /// <summary>
    /// Draws the window in each variant and compares (or, updating, writes) the frames; mismatches are added to
    /// <paramref name="failures"/>. The window is left in the light theme at 100%.
    /// </summary>
    public static void Check(TopLevel window, string name, ICollection<string> failures, params Variant[] variants)
    {
        if (!Compared)
        {
            return;
        }

        var app = Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            foreach (var variant in variants.Length == 0 ? All : variants)
            {
                app.RequestedThemeVariant = variant == Variant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.SetRenderScaling(variant == Variant.Scaled150 ? 1.5 : 1);
                Dispatcher.UIThread.RunJobs();
                var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
                using var png = new MemoryStream();
#pragma warning disable CS0618 // the simple overload is enough for a test snapshot
                frame.Save(png);
#pragma warning restore CS0618
                if (Compare($"{name}-{Suffix(variant)}", png.ToArray()) is { } failure)
                {
                    failures.Add(failure);
                }
            }
        }
        finally
        {
            app.RequestedThemeVariant = theme;
            window.SetRenderScaling(1);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Fails with every mismatch, or skips on systems where frames are not compared.</summary>
    public static void Verify(ICollection<string> failures)
    {
        if (!Compared)
        {
            Assert.Skip("Screenshot baselines are compared on Windows only.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Compares a frame's PNG with its baseline: null when they match (or the baseline was just written).</summary>
    public static string? Compare(string name, byte[] actualPng)
    {
        var path = Path.Combine(Folder, name + ".png");
        if (Updating)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(path, actualPng);
            return null;
        }
        var actual = Decode(actualPng);
        Directory.CreateDirectory(FailureFolder);
        var actualPath = Path.Combine(FailureFolder, name + "-actual.png");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(actualPath, actualPng);
            return $"{name}: no baseline {path} (CLASSICMAC_UPDATE_BASELINES=1 writes it); actual frame: {actualPath}";
        }
        var expected = Decode(File.ReadAllBytes(path));
        if (expected.Size != actual.Size)
        {
            File.WriteAllBytes(actualPath, actualPng);
            return $"{name}: frame {actual.Size.Width} × {actual.Size.Height}, baseline {expected.Size.Width} × {expected.Size.Height} ({path}); actual frame: {actualPath}";
        }
        var diff = Differences(expected.Pixels, actual.Pixels, Tolerance);
        if (diff.Count <= StrayPixels && Differences(expected.Pixels, actual.Pixels, StrayLimit).Count == 0)
        {
            return null;
        }

        File.WriteAllBytes(actualPath, actualPng);
        var diffPath = Path.Combine(FailureFolder, name + "-diff.png");
        WriteDiff(diffPath, actual, diff.Mask);
        return $"{name}: {diff.Count} pixels differ from {path} (more than {Tolerance} in a channel); actual: {actualPath}, diff (magenta): {diffPath}";
    }

    public sealed record Frame(PixelSize Size, uint[] Pixels);

    /// <summary>The pixels that differ by more than <paramref name="tolerance"/> in any channel.</summary>
    public static (int Count, bool[] Mask) Differences(uint[] expected, uint[] actual, int tolerance)
    {
        var mask = new bool[actual.Length];
        var count = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            uint e = expected[i], a = actual[i];
            for (var shift = 0; shift < 32; shift += 8)
            {
                if (Math.Abs((int)(e >> shift & 0xFF) - (int)(a >> shift & 0xFF)) > tolerance)
                {
                    mask[i] = true;
                    count++;
                    break;
                }
            }
        }
        return (count, mask);
    }

    // A PNG as BGRA words (Avalonia's own decoder, so the baseline and the frame are read alike).
    public static Frame Decode(byte[] png)
    {
        using var bitmap = new Bitmap(new MemoryStream(png));
        var size = bitmap.PixelSize;
        var pixels = new uint[size.Width * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length * 4, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        // A bitmap copies out in its own format: RGBA on macOS, where BGRA is wanted.
        if (bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = (p & 0xFF00FF00) | (p >> 16 & 0xFF) | (p & 0xFF) << 16;
            }
        }

        return new Frame(size, pixels);
    }

    // The actual frame faded, differing pixels magenta.
    private static void WriteDiff(string path, Frame actual, bool[] mask)
    {
        using var bitmap = new WriteableBitmap(actual.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var pixels = new uint[actual.Pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = actual.Pixels[i];
            var grey = ((p >> 16 & 0xFF) + (p >> 8 & 0xFF) + (p & 0xFF)) / 3 / 4 + 192;
            pixels[i] = mask[i] ? 0xFFFF00FF : 0xFF000000 | grey << 16 | grey << 8 | grey;
        }
        using (var locked = bitmap.Lock())
        {
            for (var y = 0; y < actual.Size.Height; y++)
            {
                Marshal.Copy((int[])(object)pixels, y * actual.Size.Width, locked.Address + y * locked.RowBytes, actual.Size.Width);
            }
        }
#pragma warning disable CS0618
        bitmap.Save(path);
#pragma warning restore CS0618
    }
}
