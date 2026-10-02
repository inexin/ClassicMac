using ClassicMac.Tests;
namespace ClassicMac.Resources.Tests;

// The real-file corpus lives outside the repo; CLASSICMAC_CORPUS points at it and the tests that use it skip otherwise.
internal static class Corpus
{
    // Raw forks: *.rsrc and *.rsf files, and the files in Basilisk II / SheepShaver shared-folder .rsrc directories
    // (whose .finf siblings hold Finder info, not forks).
    public static IReadOnlyList<string> ForkFiles()
    {
        if (!CorpusFolders.Any)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of resource forks to run this.");
        }

        return CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
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

    // The corpus forks Mac OS 9 would open, read; those it would refuse (the harness's damage tests among them) are
    // counted in Refused, not failed.
    public static IReadOnlyList<(string Path, byte[] Bytes, ResourceFork Fork)> ReadableForks(out int refused)
    {
        var forks = new List<(string, byte[], ResourceFork)>();
        refused = 0;
        foreach (var file in ForkFiles())
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                forks.Add((file, bytes, ResourceFork.Read(bytes)));
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }
        return forks;
    }
}
