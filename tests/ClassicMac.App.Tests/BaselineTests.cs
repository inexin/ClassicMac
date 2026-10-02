using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.Tests;

// The screenshot comparer itself (Baselines).
public class BaselineTests
{
    [Fact]
    public void Pixels_within_the_tolerance_match()
    {
        uint[] expected = [0xFF102030, 0xFF102030, 0xFF102030, 0xFF102030];
        uint[] actual = [0xFF102030, 0xFF122030, 0xFF10202E, 0xFF102033];
        var (count, mask) = Baselines.Differences(expected, actual, Baselines.Tolerance);
        Assert.Equal(1, count);
        Assert.Equal([false, false, false, true], mask);
        Assert.Equal(4, Baselines.Differences(expected, [0xFF000000, 0xFFFFFFFF, 0x00102030, 0xFF103030], 2).Count);
    }

    [Fact]
    public void Variants_name_their_files() =>
        Assert.Equal(["light", "dark", "150"], Baselines.All.Select(Baselines.Suffix));

    [Fact]
    public void Baselines_are_kept_in_tests_golden_app() =>
        Assert.Equal(Path.Combine("tests", "golden", "app"), Path.GetRelativePath(Path.GetFullPath(Path.Combine(Baselines.Folder, "..", "..", "..")), Baselines.Folder));

    // A missing baseline, a different size and different pixels each fail, naming the actual frame (and the diff).
    [Fact]
    public void A_mismatch_names_the_actual_frame_and_a_diff() => Headless.OnUiThread(() =>
    {
        if (Baselines.Updating)
        {
            return; // would write the baseline
        }

        var name = $"cm-test-{Guid.NewGuid():N}";
        var path = Path.Combine(Baselines.Folder, name + ".png");
        var red = Png(4, 3, 255, 0, 0);
        try
        {
            var missing = Baselines.Compare(name, red);
            Assert.Contains("no baseline", missing);
            Assert.True(File.Exists(Path.Combine(Baselines.FailureFolder, name + "-actual.png")));

            Directory.CreateDirectory(Baselines.Folder);
            File.WriteAllBytes(path, red);
            Assert.Null(Baselines.Compare(name, red));
            Assert.Null(Baselines.Compare(name, Png(4, 3, 253, 2, 0)));
            Assert.Contains("frame 5 × 3, baseline 4 × 3", Baselines.Compare(name, Png(5, 3, 255, 0, 0)));
            var changed = Baselines.Compare(name, Png(4, 3, 0, 0, 255));
            Assert.Contains("12 pixels differ", changed);
            var diff = Path.Combine(Baselines.FailureFolder, name + "-diff.png");
            Assert.Contains(diff, changed);
            Assert.All(Baselines.Decode(File.ReadAllBytes(diff)).Pixels, p => Assert.Equal(0xFFFF00FF, p));
        }
        finally
        {
            File.Delete(path);
            foreach (var file in Directory.EnumerateFiles(Baselines.FailureFolder, name + "*"))
            {
                File.Delete(file);
            }
        }
    });

    private static byte[] Png(int width, int height, byte r, byte g, byte b) =>
        PngEncoder.Instance.Encode(width, height, [.. Enumerable.Range(0, width * height).SelectMany(_ => new[] { r, g, b, (byte)255 })]);
}
