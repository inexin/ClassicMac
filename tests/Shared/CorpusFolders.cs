using System.IO.Enumeration;

namespace ClassicMac.Tests;

// The real-file corpus lives outside the repo: CLASSICMAC_CORPUS names folders or single files, separated by ';'. The
// tests that use it skip when none of them exists.
internal static class CorpusFolders
{
    public static IReadOnlyList<string> Roots { get; } =
        (Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => Directory.Exists(r) || File.Exists(r))
            .ToList();

    public static bool Any => Roots.Count > 0;

    public static IEnumerable<string> EnumerateFiles(string pattern, SearchOption option) =>
        Roots.SelectMany(r => File.Exists(r)
            ? (FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(r)) ? [r] : [])
            : Directory.EnumerateFiles(r, pattern, option));

    // Folders of deliberately damaged inputs (the harness's damage tests): a folder named ndiftest, or one holding a
    // .classicmac-damage-test file. Tests that expect clean reads skip them; the tests written for them read them.
    public static bool IsDamageTest(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir) == "ndiftest" || File.Exists(Path.Combine(dir, ".classicmac-damage-test")))
            {
                return true;
            }

            if (Roots.Any(r => string.Equals(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }
        return false;
    }

    // Files damaged on purpose inside a corpus file (malformed images kept in a disk image): a .classicmac-damage-paths
    // file beside it lists them, one per line, "<file name> | <Mac path>" ('#' starts a comment); a folder's path ends
    // in ':' and covers what it holds, and the volume's name may be left out. A diagnostic whose location (the nested
    // file's Mac path, then " > " and the files inside it) is in one of them is expected.
    public static bool IsDamageTest(string path, string? location)
    {
        if (IsDamageTest(path))
        {
            return true;
        }

        var list = Path.Combine(Path.GetDirectoryName(path) ?? "", ".classicmac-damage-paths");
        if (location is null || !File.Exists(list))
        {
            return false;
        }

        var macPath = location.Split(" > ")[0];
        var withoutVolume = macPath.Contains(':', StringComparison.Ordinal) ? macPath[(macPath.IndexOf(':', StringComparison.Ordinal) + 1)..] : macPath;
        foreach (var line in File.ReadLines(list))
        {
            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].StartsWith('#')
                || !string.Equals(parts[0], Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var listed = parts[1];
            if (new[] { macPath, withoutVolume }.Any(p => listed.EndsWith(':') ? p.StartsWith(listed, StringComparison.Ordinal) : p == listed))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> EnumerateDirectories(string pattern, SearchOption option) =>
        Roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateDirectories(r, pattern, option));
}
