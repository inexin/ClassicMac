using System.Runtime.CompilerServices;

namespace ClassicMac.App.Tests;

// The view models are free of Avalonia: the views map keys and decode images, so the view models could move to a
// library of their own and be tested without a UI.
public class ViewModelFrameworkTests
{
    [Fact]
    public void No_view_model_uses_Avalonia()
    {
        var folder = Path.Combine(RepositoryRoot(), "src", "ClassicMac.App", "ViewModels");
        var users = Directory.EnumerateFiles(folder, "*.cs")
            .Where(f => File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && l.Contains("Avalonia", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Empty(users);
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
