namespace ClassicMac.Resources.Tests;

// The real-file corpus lives outside the repo; CLASSICMAC_CORPUS points at it and the tests that use it skip otherwise.
internal static class Corpus
{
    // Raw forks: *.rsrc and *.rsf files, and the files in Basilisk II / SheepShaver shared-folder .rsrc directories
    // (whose .finf siblings hold Finder info, not forks).
    public static IReadOnlyList<string> ForkFiles()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus))
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of resource forks to run this.");

        return Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) switch
            {
                ".rsrc" => true,
                ".finf" => false,
                _ => f.EndsWith(".rsrc", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".rsf", StringComparison.OrdinalIgnoreCase),
            })
            .Where(f => new FileInfo(f).Length > 0)
            .ToList();
    }
}
