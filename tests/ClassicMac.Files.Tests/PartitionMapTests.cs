using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class PartitionMapTests
{
    [Fact]
    public void Mac_volumes_come_out_and_drivers_are_skipped()
    {
        var hfs = Enumerable.Repeat((byte)0x48, 1024).ToArray();
        var driver = new byte[512];
        var image = PartitionMap(("Driver", "Apple_Driver43", driver), ("Macintosh HD", "Apple_HFS", hfs));
        var input = ForkData.FromBytes(image);
        var diagnostics = new List<Diagnostic>();

        Assert.True(PartitionMapReader.Instance.CanRead(input));
        var volume = Assert.Single(PartitionMapReader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Macintosh HD", volume.Name.ToMacRoman());
        Assert.Equal(hfs, volume.DataFork.ToArray());
        Assert.Equal(2, diagnostics.Count(d => d.Code == "partition.skipped")); // the map itself and the driver
    }

    // As the Mac OS 9 CD driver reads them: block 0 may be zeros, entries every 2048 bytes when there is no 'PM' at 512,
    // and every block number in 2048-byte units.
    [Fact]
    public void CD_maps_use_a_2048_byte_stride()
    {
        var hfs = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        var image = PartitionMap(2048, driverDescriptor: false, ("CD", "Apple_HFS", hfs));
        var input = ForkData.FromBytes(image);

        Assert.True(PartitionMapReader.Instance.CanRead(input));
        var volume = Assert.Single(PartitionMapReader.Instance.Read(input, new ContainerContext()));
        Assert.Equal(hfs, volume.DataFork.ToArray());
    }

    [Fact]
    public void A_partition_past_the_end_is_cut()
    {
        var image = PartitionMap(("Disk", "Apple_HFS", new byte[2048]));
        var diagnostics = new List<Diagnostic>();

        var volume = Assert.Single(PartitionMapReader.Instance.Read(
            ForkData.FromBytes(image[..^1024]), new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(1024, volume.DataFork.Length);
        Assert.Contains(diagnostics, d => d.Code == "partition.truncated");
    }

    [Fact]
    public void Plain_volumes_are_not_partition_maps()
    {
        Assert.False(PartitionMapReader.Instance.CanRead(ForkData.FromBytes(new byte[2048])));
    }
}
