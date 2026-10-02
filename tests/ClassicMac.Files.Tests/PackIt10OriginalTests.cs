using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// An archive made by PackIt 1.0 on Mac OS 9 from a synthetic file set (TestData/PackIt10/README.md).
public sealed class PackIt10OriginalTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "PackIt10");
    private static readonly string Forks = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffIt151", "Forks");

    [Fact]
    public void PackIt10StoredEntriesExpandWithForksFinderInfoAndDates()
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(FixtureDirectory, "pk10_plain.pit"))),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(["ReadMe", "Big.txt", "Empty"], files.Select(file => file.MacPath));
        AssertFile(files[0], "ReadMe.data", "ReadMe.rsrc");
        AssertFile(files[1], "Big.txt.data", null);
        AssertFile(files[2], "Empty.data", null);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void PackIt10ArchiveUnwrapsByDefault()
    {
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(FixtureDirectory, "pk10_plain.pit"),
            diagnostics: diagnostics);

        Assert.Equal(3, result.Children.Count);
        Assert.All(result.Children, child => Assert.Equal(PackItReader.Instance.FormatName, child.Format));
        Assert.Empty(diagnostics);
    }

    private static void AssertFile(MacFile file, string dataName, string? resourceName)
    {
        Assert.Equal(File.ReadAllBytes(Path.Combine(Forks, dataName)), file.DataFork.ToArray());
        Assert.Equal(resourceName is null ? [] : File.ReadAllBytes(Path.Combine(Forks, resourceName)),
            file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(0xE6E4FFDC), file.Created);
        Assert.Equal(new MacDate(0xE6E4FFDC), file.Modified);
    }
}
