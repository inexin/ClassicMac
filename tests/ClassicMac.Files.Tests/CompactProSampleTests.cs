using System.Security.Cryptography;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// munbox's Compact Pro sample (MIT; said to be made by Compact Pro 1.52) and the RLE escape rules
// (TestData/CompactProMunbox/README.md).
public sealed class CompactProSampleTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "CompactProMunbox");

    [Fact]
    public void MunboxSampleExpandsEveryFileToItsListedMd5()
    {
        var expected = File.ReadAllLines(Path.Combine(FixtureDirectory, "md5sums.txt"))
            .Where(line => line.Length > 0)
            .Select(line => line.Split("  ./", 2))
            .ToDictionary(parts => parts[1].Replace('/', ':'), parts => parts[0]);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = CompactProReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, "testfile.compact_pro_152.cpt"))),
            new ContainerContext(diagnostics: diagnostics));

        // Nine files at the root, in Folder1 and in Folder1:Folder2.
        Assert.Equal(expected.Count, files.Count);
        foreach (MacFile file in files)
        {
            string path = string.Join(":", file.FolderPath.Append(file.Name).Select(name => name.ToString()));
            Assert.True(expected.TryGetValue(path, out string? md5), path);
            Assert.Equal(md5, Convert.ToHexStringLower(MD5.HashData(file.DataFork.ToArray())));
            Assert.Equal(0, file.ResourceFork.Length);
        }
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("81 81 81 82 05", "81 81 81 81 81 81")] // after 81 81 the second 81 starts a new escape
    [InlineData("81 81 82 05", "81 81 81 81 81")]
    [InlineData("81 81 41", "81 81 41")]
    [InlineData("81 81 81 41", "81 81 81 41")]
    [InlineData("81 82 00", "81 82")]
    [InlineData("41 81 82 01 42", "41 42")] // a run of one adds nothing
    [InlineData("41 81 82 03", "41 41 41")]
    [InlineData("81 41", "81 41")]
    public void RleEscapesFollowTheHalfEscapeRule(string input, string expected)
    {
        byte[] output = Convert.FromHexString(expected.Replace(" ", ""));

        Assert.Equal(output, CompactProReader.DecodeRle8182(Convert.FromHexString(input.Replace(" ", "")), output.Length));
    }
}
