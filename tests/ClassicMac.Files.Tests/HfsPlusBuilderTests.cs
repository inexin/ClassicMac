using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The test builder's HFS Plus volumes read back through the reader, which checks them as it reads.
public class HfsPlusBuilderTests
{
    [Fact]
    public void A_built_volume_reads_back_with_its_folders_files_and_overflow_extents()
    {
        var builder = new HfsPlusBuilder();
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Letter", "dear sir"u8.ToArray(), new byte[300]);
        var fragmented = Enumerable.Range(0, 12 * HfsPlusBuilder.Block).Select(i => (byte)(i * 3)).ToArray();
        builder.File(HfsPlusBuilder.Root, "Fragmented", fragmented, [], fragments: 12);
        var image = builder.Build("Plus");

        var context = new ContainerContext();
        var files = HfsPlusReader.Read(ForkData.FromBytes(image), context);

        Assert.Empty(context.Diagnostics);
        Assert.Equal(["Docs:Letter", "Fragmented"], files.Select(f => f.MacPath).Order(StringComparer.Ordinal));
        Assert.Equal(fragmented, files.Single(f => f.MacPath == "Fragmented").DataFork.ToArray());
        Assert.Equal("dear sir"u8.ToArray(), files.Single(f => f.MacPath == "Docs:Letter").DataFork.ToArray());
    }
}
