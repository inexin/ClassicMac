namespace ClassicMac.Tests;

// The real-file corpus lives outside the repo: CLASSICMAC_CORPUS names one folder or several, separated by ';'. The
// tests that use it skip when none of them exists.
internal static class CorpusFolders
{
    public static IReadOnlyList<string> Roots { get; } =
        (Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists)
            .ToList();

    public static bool Any => Roots.Count > 0;

    public static IEnumerable<string> EnumerateFiles(string pattern, SearchOption option) =>
        Roots.SelectMany(r => Directory.EnumerateFiles(r, pattern, option));

    // Folders of deliberately damaged inputs (the harness's damage tests): a folder named ndiftest, or one holding a
    // .classicmac-damage-test file. Tests that expect clean reads skip them; the tests written for them read them.
    public static bool IsDamageTest(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Path.GetFileName(dir) == "ndiftest" || File.Exists(Path.Combine(dir, ".classicmac-damage-test"))) return true;
            if (Roots.Any(r => string.Equals(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase))) break;
        }
        return false;
    }

    // Formats ClassicMac does not read yet, which tests that expect clean reads skip: UDIF (.dmg), found by the 'koly'
    // trailer in its last 512 bytes.
    public static bool IsUnsupported(string path)
    {
        try
        {
            // Shared, as an emulator may hold the image open.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 512) return false;
            stream.Seek(-512, SeekOrigin.End);
            Span<byte> magic = stackalloc byte[4];
            stream.ReadExactly(magic);
            return magic.SequenceEqual("koly"u8);
        }
        catch (IOException)
        {
            return false; // in use; the caller's own read decides
        }
    }

    public static IEnumerable<string> EnumerateDirectories(string pattern, SearchOption option) =>
        Roots.SelectMany(r => Directory.EnumerateDirectories(r, pattern, option));
}
