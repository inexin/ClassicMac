using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Files.Tests;

// A self-extracting archive (.sea) is an application whose resource fork holds the extractor and whose data fork is
// the archive from offset 0 to the end; the unwrapper reads that data fork like any archive.
public sealed class SelfExtractingArchiveTests
{
    private static readonly ForkData Stub = ForkData.FromBytes(new byte[] { 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });

    [Fact]
    public void StuffItSeaUnwrapsTheArchiveInItsDataFork()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItLegacy45");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "StuffItDeluxe45.sit"));
        var sea = new MacFile
        {
            Name = MacString.FromMacRoman("Archive.sea"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("APPL"), Creator = FourCC.FromString("aust") },
            DataFork = ForkData.FromBytes(archive),
            ResourceFork = Stub,
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(sea, "host", new ContainerContext());

        Assert.NotEmpty(result.Children);
        Assert.All(result.Children, child => Assert.StartsWith("StuffIt", child.Format));
    }
}
