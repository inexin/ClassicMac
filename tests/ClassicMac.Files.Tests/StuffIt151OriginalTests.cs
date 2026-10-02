using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// Archives and segments made by StuffIt 1.5.1 on Mac OS 9 from a synthetic file set (TestData/StuffIt151/README.md).
public sealed class StuffIt151OriginalTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffIt151");
    private static readonly MacDate FixtureDate = new(0xE6E4F395);

    public static TheoryData<string> Archives => new()
    {
        "fx151_lzw_huff.sit", // methods 2 and 3
        "fx151_lzw.sit",      // method 2
        "fx151_huf.sit",      // methods 3 and 1
        "fx151_non.sit",      // method 0
    };

    [Theory]
    [MemberData(nameof(Archives))]
    public void StuffItReaderExpandsEveryFileWithItsForksAndFinderInfo(string archiveName)
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, archiveName))),
            new ContainerContext(diagnostics: diagnostics));

        AssertFixtureSet(files);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [MemberData(nameof(Archives))]
    public void DefaultUnwrapperExpandsTheArchive(string archiveName)
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, archiveName),
            diagnostics: diagnostics);

        Assert.All(result.Children, child => Assert.Equal(StuffItReader.Instance.FormatName, child.Format));
        AssertFixtureSet([.. result.Leaves().Select(leaf => leaf.File)]);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("fx151_non.seg1")]
    [InlineData("fx151_non.seg3")]
    [InlineData("fx151_non.seg5")]
    public void SegmentSetReassemblesTheArchiveFromAnySegment(string segmentName)
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, segmentName),
            diagnostics: diagnostics);

        ContainerNode archive = Assert.Single(result.Children);
        Assert.Equal(StuffItSplitReader.Instance.FormatName, archive.Format);
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDirectory, "fx151_non.sit")),
            archive.File.DataFork.ToArray());
        Assert.Equal(0, archive.File.ResourceFork.Length);
        Assert.Equal(FourCC.FromString("SIT!"), archive.File.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("SIT!"), archive.File.FinderInfo.Creator);
        AssertFixtureSet([.. archive.Leaves().Select(leaf => leaf.File)]);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void SegmentSetWithAMissingSegmentIsReportedNotTruncated()
    {
        MacFile Segment(int number) => new()
        {
            Name = MacString.FromMacRoman($"fx151_non.seg{number}"),
            DataFork = ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, $"fx151_non.seg{number}"))),
        };
        MacFile last = Segment(5);
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(last, "Host file",
            new ContainerContext(diagnostics: diagnostics, siblings: () => [Segment(1), Segment(2), Segment(4)]));

        Assert.Empty(result.Children);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Message.Contains("volume 3", StringComparison.Ordinal));
    }

    [Fact]
    public void SegmentHeaderIsRecognisedOnlyWithItsMagic()
    {
        byte[] segment = File.ReadAllBytes(Path.Combine(FixtureDirectory, "fx151_non.seg2"));
        Assert.True(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(segment)));
        segment[1] = 0xA8;
        Assert.False(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(segment)));
    }

    private static void AssertFixtureSet(IReadOnlyList<MacFile> files)
    {
        Assert.Equal(["Big.txt", "Empty", "Folder:Inner", "ReadMe"],
            files.Select(file => file.MacPath).Order(StringComparer.Ordinal));
        AssertFile(files, "Big.txt", "Big.txt.data", null);
        AssertFile(files, "Empty", "Empty.data", null);
        AssertFile(files, "Folder:Inner", "Folder__Inner.data", null);
        AssertFile(files, "ReadMe", "ReadMe.data", "ReadMe.rsrc");
    }

    private static void AssertFile(IReadOnlyList<MacFile> files, string macPath, string dataName, string? resourceName)
    {
        MacFile file = Assert.Single(files, file => file.MacPath == macPath);
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDirectory, "Forks", dataName)), file.DataFork.ToArray());
        Assert.Equal(resourceName is null ? [] : File.ReadAllBytes(Path.Combine(FixtureDirectory, "Forks", resourceName)),
            file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0, file.FinderInfo.Flags);
        Assert.Equal(FixtureDate, file.Created);
        Assert.Equal(FixtureDate, file.Modified);
    }
}
