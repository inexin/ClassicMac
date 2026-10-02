using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// Archives, a self-extracting archive and a segment set made by Compact Pro 1.52 on Mac OS 9 from a synthetic file set
// (TestData/CompactPro152/README.md).
public sealed class CompactPro152OriginalTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "CompactPro152");
    private static readonly string FileSetForks = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffIt151", "Forks");
    private static readonly MacDate FixtureDate = new(0xE6E50754);

    [Fact]
    public void CompactProReaderExpandsEveryFileWithItsForksAndFinderInfo()
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = CompactProReader.Instance.Read(Fork("cp152"),
            new ContainerContext(diagnostics: diagnostics));

        AssertFixtureSet(files);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("cp152", "PACT", "CPCT")]
    [InlineData("cp152.sea", "APPL", "EXTR")]
    public void DefaultUnwrapperExpandsTheHostFile(string name, string type, string creator)
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, name),
            diagnostics: diagnostics);

        Assert.Equal(FourCC.FromString(type), result.File.FinderInfo.Type);
        Assert.Equal(FourCC.FromString(creator), result.File.FinderInfo.Creator);
        Assert.All(result.Children, child => Assert.Equal(CompactProReader.Instance.FormatName, child.Format));
        AssertFixtureSet([.. result.Leaves().Select(leaf => leaf.File)]);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    // The extractor (Compact Pro's own code) is not committed: the .sea is tested with a stand-in resource fork.
    public void SelfExtractingArchiveIsThePlainArchiveInTheDataForkWithItsOwnId()
    {
        byte[] plain = File.ReadAllBytes(Path.Combine(FixtureDirectory, "cp152"));
        byte[] sea = File.ReadAllBytes(Path.Combine(FixtureDirectory, "cp152.sea"));
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("cp152.sea"),
            FinderInfo = FinderInfo.Read(File.ReadAllBytes(Path.Combine(FixtureDirectory, ".finf", "cp152.sea"))),
            DataFork = ForkData.FromBytes(sea),
            ResourceFork = ForkData.FromBytes(new byte[16]),
        };

        Assert.Equal(plain.Length, sea.Length);
        Assert.Equal([2, 3], Enumerable.Range(0, plain.Length).Where(index => plain[index] != sea[index]));
        Assert.Equal(FourCC.FromString("EXTR"), file.FinderInfo.Creator);
        ContainerNode result = ContainerUnwrapper.Default.Unwrap(file, "Host file", new ContainerContext());
        AssertFixtureSet([.. result.Leaves().Select(leaf => leaf.File)]);
    }

    [Fact]
    // A 120001-byte fork from offset 8 runs across all three segments.
    public void SegmentSetIsReadFromItsLastSegmentWithTheOthersBesideIt()
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, "cpnoise.#3"),
            diagnostics: diagnostics);

        Assert.Equal(FourCC.FromString("PACT"), result.File.FinderInfo.Type);
        ContainerNode child = Assert.Single(result.Children);
        Assert.Equal(CompactProReader.Instance.FormatName, child.Format);
        AssertNoise(child.File);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void CompactProReaderJoinsTheSegmentsFromItsSiblings()
    {
        var diagnostics = new List<Diagnostic>();
        string last = Path.Combine(FixtureDirectory, "cpnoise.#3");

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromFile(last),
            new ContainerContext(diagnostics: diagnostics, siblings: HostFiles.Siblings(last))));

        AssertNoise(file);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("cpnoise.#1")]
    [InlineData("cpnoise.#2")]
    // Only the last segment has a directory offset.
    public void EarlierSegmentsAreNotArchivesOnTheirOwn(string name) =>
        Assert.False(CompactProReader.Instance.CanRead(Fork(name)));

    [Fact]
    public void MissingSegmentIsReportedAndItsEntriesSkipped()
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = CompactProReader.Instance.Read(Fork("cpnoise.#3"),
            new ContainerContext(diagnostics: diagnostics, siblings: () => [Segment("cpnoise.#1")]));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Message.Contains("segment(s) 2 of 3",
                StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Message.Contains("Noise.bin",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TwoSiblingsWithTheSameSegmentNumberAreRejected() =>
        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(Fork("cpnoise.#3"),
            new ContainerContext(siblings: () =>
                [Segment("cpnoise.#1"), Segment("cpnoise.#1"), Segment("cpnoise.#2")])));

    [Fact]
    // A first segment with another set id is not taken for cpnoise's.
    public void SiblingsFromAnotherSetAreIgnored()
    {
        byte[] wrongSet = File.ReadAllBytes(Path.Combine(FixtureDirectory, "cpnoise.#1"));
        wrongSet[3] ^= 1;
        var diagnostics = new List<Diagnostic>();

        Assert.Empty(CompactProReader.Instance.Read(Fork("cpnoise.#3"), new ContainerContext(diagnostics: diagnostics,
            siblings: () => [new MacFile { Name = MacString.FromMacRoman("other"), DataFork = ForkData.FromBytes(wrongSet) },
                Segment("cpnoise.#2")])));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Message.Contains("segment(s) 1 of 3", StringComparison.Ordinal));
    }

    [Fact]
    public void SegmentsShareTheConfiguredInputSizeLimit()
    {
        var context = new ContainerContext(
            options: new ContainerReadOptions { MaxExpandedBytesPerInput = 40_960 + 40_960 + 38_167 - 1 },
            siblings: () => [Segment("cpnoise.#1"), Segment("cpnoise.#2")]);

        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(Fork("cpnoise.#3"), context));
    }

    private static ForkData Fork(string name) => ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, name)));

    private static MacFile Segment(string name) => new() { Name = MacString.FromMacRoman(name), DataFork = Fork(name) };

    private static void AssertNoise(MacFile file)
    {
        Assert.Equal("Noise.bin", file.MacPath);
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureDirectory, "Forks", "Noise.bin.data")), file.DataFork.ToArray());
        Assert.Equal(0, file.ResourceFork.Length);
        Assert.Equal(FourCC.FromString("BINA"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("????"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(0xE6E50812), file.Created);
        Assert.Equal(new MacDate(0xE6E50812), file.Modified);
    }

    private static void AssertFixtureSet(IReadOnlyList<MacFile> files)
    {
        Assert.Equal(["Big.txt", "Empty", "Folder:Inner", "ReadMe"], files.Select(file => file.MacPath));
        AssertFile(files, "Big.txt", "Big.txt.data", null);
        AssertFile(files, "Empty", "Empty.data", null);
        AssertFile(files, "Folder:Inner", "Folder__Inner.data", null);
        AssertFile(files, "ReadMe", "ReadMe.data", "ReadMe.rsrc");
    }

    private static void AssertFile(IReadOnlyList<MacFile> files, string macPath, string dataName, string? resourceName)
    {
        MacFile file = Assert.Single(files, file => file.MacPath == macPath);
        Assert.Equal(File.ReadAllBytes(Path.Combine(FileSetForks, dataName)), file.DataFork.ToArray());
        Assert.Equal(resourceName is null ? [] : File.ReadAllBytes(Path.Combine(FileSetForks, resourceName)),
            file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0, file.FinderInfo.Flags);
        Assert.Equal(FixtureDate, file.Created);
        Assert.Equal(FixtureDate, file.Modified);
    }
}
