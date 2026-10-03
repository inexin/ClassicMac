using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsVolume (docs/PLAN.md, editing through a block overlay): a read-only base and the 512-byte sectors written over it.
public sealed class HfsVolumeTests
{
    private static byte[] Base(int sectors = 8) => [.. Enumerable.Range(0, sectors * 512).Select(i => (byte)(i * 7))];

    private static byte[] Read(HfsVolume volume, long offset, int length)
    {
        var buffer = new byte[length];
        volume.Read(offset, buffer);
        return buffer;
    }

    [Fact]
    public void Reads_come_from_the_base_until_written_and_the_base_never_changes()
    {
        var bytes = Base();
        var original = bytes.ToArray();
        var volume = new HfsVolume(ForkData.FromBytes(bytes));

        Assert.Equal(bytes.Length, volume.Length);
        Assert.Equal(bytes.AsSpan(700, 900).ToArray(), Read(volume, 700, 900));
        Assert.Empty(volume.ChangedSectors);

        volume.Write(1000, [1, 2, 3, 4, 5]);                                          // inside sector 1
        volume.Write(1534, [9, 9, 9, 9, 9, 9]);                                        // across sectors 2 and 3

        Assert.Equal([1, 2, 3, 4, 5], Read(volume, 1000, 5));
        Assert.Equal([9, 9, 9, 9, 9, 9], Read(volume, 1534, 6));
        Assert.Equal(bytes.AsSpan(999, 1).ToArray(), Read(volume, 999, 1));            // the rest of the sector stays
        Assert.Equal([1L, 2, 3], volume.ChangedSectors.Order());
        Assert.Equal(original, bytes);
        var expected = original.ToArray();
        new byte[] { 1, 2, 3, 4, 5 }.CopyTo(expected, 1000);
        new byte[] { 9, 9, 9, 9, 9, 9 }.CopyTo(expected, 1534);
        Assert.Equal(expected, volume.ToArray());
    }

    [Fact]
    public void A_fork_of_the_volume_changes_on_its_own()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Base()));
        volume.Write(10, [1]);

        var fork = volume.Fork();
        fork.Write(10, [2]);
        volume.Write(600, [3]);

        Assert.Equal([1], Read(volume, 10, 1));
        Assert.Equal([2], Read(fork, 10, 1));
        Assert.Equal([0L, 1], volume.ChangedSectors.Order());
        Assert.Equal([0L], fork.ChangedSectors);
    }

    [Fact]
    public void As_a_fork_data_it_reads_through_the_overlay()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Base()));
        volume.Write(510, [7, 7, 7, 7]);
        var expected = volume.ToArray();

        var data = volume.AsForkData();

        Assert.Equal(expected.Length, data.Length);
        Assert.Equal(expected, data.ToArray());
        using var stream = data.Open();
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal(expected.AsSpan(500, 20).ToArray(), data.Slice(500, 20).ToArray());
    }

    [Fact]
    public void Reads_and_writes_outside_the_volume_are_refused()
    {
        var volume = new HfsVolume(ForkData.FromBytes(Base(2)));

        Assert.Throws<ArgumentOutOfRangeException>(() => volume.Write(1020, [1, 2, 3, 4, 5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => volume.Read(-1, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => volume.Read(1023, new byte[2]));
        Assert.Empty(volume.ChangedSectors);
    }
}
