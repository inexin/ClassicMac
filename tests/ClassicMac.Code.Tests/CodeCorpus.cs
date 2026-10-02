namespace ClassicMac.Code.Tests;

// The code corpus lives outside the repo: CLASSICMAC_CODE_CORPUS names folders (searched without recursion) or single
// files, separated by ';', the pattern of tests/Shared/CorpusFolders.cs. A sample is found by file name and, where two
// samples share a name, by length. The tests that use it skip when it is not set or the sample is not there.
internal static class CodeCorpus
{
    public static IReadOnlyList<string> Roots { get; } =
        (Environment.GetEnvironmentVariable("CLASSICMAC_CODE_CORPUS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => Directory.Exists(r) || File.Exists(r))
            .ToList();

    public static IEnumerable<string> Files(string pattern) =>
        Roots.SelectMany(r => File.Exists(r)
            ? (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(r)) ? [r] : [])
            : Directory.EnumerateFiles(r, pattern, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string name, long? length = null) =>
        Files(name).FirstOrDefault(f => length is null || new FileInfo(f).Length == length);

    // The sample's bytes, or a skip.
    public static byte[] Require(string name, long? length = null)
    {
        if (Roots.Count == 0)
        {
            Assert.Skip("Set CLASSICMAC_CODE_CORPUS to the folders holding the code samples to run this.");
        }

        var path = Find(name, length);
        if (path is null)
        {
            Assert.Skip($"{name} is not in the code corpus.");
        }

        return File.ReadAllBytes(path!);
    }
}
