using System.Text.RegularExpressions;

namespace ClassicMac.Graphics.Tests;

// ClassicMac.Graphics is one package in layers, a folder and namespace each: the base (the project's root, MacPaint/
// and Tiff/, namespace ClassicMac.Graphics), then Fonts, QuickTime and QuickDraw, then Pict. A layer may use only the
// ones below it; a sibling or higher layer can only be reached by naming its namespace, which this test looks for.
public class LayeringTests
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [""] = [],
        ["MacPaint"] = [],
        ["Tiff"] = [],
        ["Fonts"] = [],
        ["QuickTime"] = [],
        ["QuickDraw"] = ["Fonts"],
        ["Pict"] = ["QuickTime", "QuickDraw", "Fonts"],
    };

    [Fact]
    public void Every_layer_uses_only_the_layers_below_it()
    {
        var root = Path.Combine(FindRoot(), "src", "ClassicMac.Graphics");
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts[0] is "obj" or "bin")
            {
                continue;
            }

            var layer = parts.Length == 1 ? "" : parts[0];
            Assert.True(Allowed.ContainsKey(layer), $"{relative} is outside a layer folder.");
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bClassicMac\.Graphics\.(QuickTime|QuickDraw|Pict|Fonts)\b"))
            {
                var used = m.Groups[1].Value;
                if (used != layer && !Allowed[layer].Contains(used))
                {
                    violations.Add($"{relative} ({(layer == "" ? "base" : layer)}) uses {used}");
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations.Distinct()));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ClassicMac.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("ClassicMac.slnx not found above the test output.");
    }
}
