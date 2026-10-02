using System.Security.Cryptography;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// Archives made by MacLHA 2.24, from lhasa's test suite (TestData/MacLha224/README.md).
public sealed class MacLha224Tests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "MacLha224");
    private const string Gpl2Md5 = "b234ee4d69f5fce4486a80fdaf4a4263"; // the GPL-2 licence text, 18 092 bytes

    [Fact]
    public void Level0EntryHoldsAMacBinaryFileThatUnwrapsToTheMacFile()
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, "l0_lh5.lzh"),
            diagnostics: diagnostics);

        ContainerNode entry = Assert.Single(result.Children);
        Assert.Equal(LhaReader.Instance.FormatName, entry.Format);
        Assert.Equal(18304, entry.File.DataFork.Length); // a 128-byte MacBinary header and the padded data fork
        MacFile file = Assert.Single(entry.Children).File;
        Assert.Equal("gpl-2", file.MacPath);
        Assert.Equal(Gpl2Md5, Md5(file.DataFork));
        Assert.Equal(0, file.ResourceFork.Length);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("l0_lh1.lzh")]
    [InlineData("l1_lh1.lzh")]
    [InlineData("l2_lh1.lzh")]
    public void Lh1EntryDecodesToTheMacBinaryFile(string archiveName)
    {
        // -lh1-: LZHUF's adaptive Huffman tree and its fixed position table; this decoded 7 bytes, then failed at
        // the first match (a 3-bit position code read as 1 bit) and then at the second code (the leaf group's
        // leader was the right-most leaf, not the left-most).
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, archiveName),
            diagnostics: diagnostics);

        ContainerNode entry = Assert.Single(result.Children);
        Assert.Equal(18304, entry.File.DataFork.Length);
        MacFile file = Assert.Single(entry.Children).File;
        Assert.Equal("gpl-2", file.MacPath);
        Assert.Equal(Gpl2Md5, Md5(file.DataFork));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NonMacEntryIsThePlainFile()
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = LhaReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, "l1_nm_lh5.lzh"))),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal("gpl-2", file.MacPath);
        Assert.Equal(Gpl2Md5, Md5(file.DataFork));
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("l1_subdir.lzh", new[] { "subdir", "subdir2" })]
    [InlineData("l2_full_subdir.lzh", new[] { "Untitled", "subdir", "subdir2" })] // a full path starts at the volume
    public void DirectoryNamesSplitAtFFAndTheMacBinaryFileKeepsItsFinderInfo(string archiveName, string[] folders)
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, archiveName),
            diagnostics: diagnostics);

        ContainerNode entry = Assert.Single(result.Children);
        Assert.Equal(folders, entry.File.FolderPath.Select(folder => folder.ToMacRoman()));
        MacFile file = Assert.Single(entry.Children).File;
        Assert.Equal("hello.txt", file.Name.ToMacRoman());
        Assert.Equal("hello world"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.NotNull(file.Created);
        Assert.NotEqual(file.Created, file.Modified);
        Assert.Empty(diagnostics);
    }

    private static string Md5(ForkData fork) => Convert.ToHexStringLower(MD5.HashData(fork.ToArray()));
}
