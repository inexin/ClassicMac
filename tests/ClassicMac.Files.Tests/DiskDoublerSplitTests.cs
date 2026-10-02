using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// DiskDoubler Pro 4.1.1's Split of its AD1 archive of the corpus's `sources` folder
// (TestData/DiskDoublerOriginal/README.md).
public sealed class DiskDoublerSplitTests
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");

    [Theory]
    [InlineData("sources.ddpro411.ad1.dd.1")]
    [InlineData("sources.ddpro411.ad1.dd.2")]
    public void SplitSetReassemblesTheArchiveFromAnyPart(string partName)
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, partName),
            diagnostics: diagnostics);

        ContainerNode split = Assert.Single(result.Children);
        Assert.Equal(DiskDoublerSplitReader.Instance.FormatName, split.Format);
        MacFile archive = split.File;
        Assert.Equal("sources.ddpro411.ad1.dd", archive.Name.ToMacRoman());
        Assert.Equal(15918, archive.DataFork.Length);
        Assert.Equal(342, archive.ResourceFork.Length);
        Assert.Equal(FourCC.FromString("DDA2"), archive.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("DDAP"), archive.FinderInfo.Creator);
        Assert.Equal("DDA2"u8.ToArray(), archive.DataFork.ToArray()[..4]);

        string crossVersion = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItOriginalCrossVersion");
        MacFile Leaf(string name) => Assert.Single(split.Leaves(), leaf => leaf.File.Name.ToMacRoman() == name).File;
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDirectory, "ExpectedDataFork.pict")),
            Leaf("testfile.PICT").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDirectory, "ExpectedResourceFork.bin")),
            Leaf("testfile.PICT").ResourceFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(crossVersion, "ExpectedTestFile.txt")),
            Leaf("testfile.txt").DataFork.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SplitSetWithAMissingPartIsReportedNotTruncated()
    {
        MacFile last = Part(2);
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(last, "Host file",
            new ContainerContext(diagnostics: diagnostics, siblings: () => []));

        Assert.Empty(result.Children);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Message.Contains("part 1 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public void PartOfAnotherSetIsNotTaken()
    {
        MacFile first = Part(1);
        byte[] other = Part(2).DataFork.ToArray();
        other[7] ^= 1; // the set identifier
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerSplitReader.Instance.Read(first, new ContainerContext(
            diagnostics: diagnostics,
            siblings: () => [new MacFile { Name = Part(2).Name, DataFork = ForkData.FromBytes(other) }]));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume");
    }

    [Fact]
    public void PayloadChecksumMismatchIsReportedAndTheDataKept()
    {
        byte[] damaged = Part(2).DataFork.ToArray();
        damaged[200] ^= 1;
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerSplitReader.Instance.Read(Part(1), new ContainerContext(
            diagnostics: diagnostics,
            siblings: () => [new MacFile { Name = Part(2).Name, DataFork = ForkData.FromBytes(damaged) }]));

        Assert.Equal(15918, Assert.Single(files).DataFork.Length);
        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("archive.fork-checksum", diagnostic.Code);
        Assert.Contains("part 2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderIsRecognisedOnlyWithBothMarkers()
    {
        byte[] part = Part(1).DataFork.ToArray();
        Assert.True(DiskDoublerSplitReader.Instance.CanRead(ForkData.FromBytes(part)));
        part[90] = (byte)'X';
        Assert.False(DiskDoublerSplitReader.Instance.CanRead(ForkData.FromBytes(part)));
    }

    private static MacFile Part(int number) => new()
    {
        Name = MacString.FromMacRoman($"sources.ddpro411.ad1.dd.{number}"),
        DataFork = ForkData.FromBytes(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, $"sources.ddpro411.ad1.dd.{number}"))),
    };
}
