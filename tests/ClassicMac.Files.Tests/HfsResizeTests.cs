using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.Resize (docs/formats/file-systems/hfs.md §3.2): a volume grown within its allocation block size, its files
// and folders untouched.
public sealed class HfsResizeTests
{
    private const int Mdb = 1024;

    private static int U16(byte[] image, int offset) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset));

    private static IReadOnlyList<MacFile> Files(byte[] image) => HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    // A new volume with a folder and files of a few sizes in it.
    private static byte[] Volume(long size)
    {
        var image = HfsWriter.Format(size, "Disk", new MacDate(3_100_000_000));
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "Docs");
        for (var i = 0; i < 5; i++)
        {
            var data = Enumerable.Range(0, 700 * (i + 1)).Select(b => (byte)(b * (i + 3))).ToArray();
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Docs:File {i}", data, new byte[] { (byte)i, 1, 2 }, FinderInfo.Empty);
        }

        return image;
    }

    private static void AssertSameFiles(byte[] before, byte[] after)
    {
        var old = Files(before);
        var now = Files(after);
        Assert.Equal(old.Select(f => f.MacPath), now.Select(f => f.MacPath));
        foreach (var (o, n) in old.Zip(now))
        {
            Assert.Equal(o.DataFork.ToArray(), n.DataFork.ToArray());
            Assert.Equal(o.ResourceFork.ToArray(), n.ResourceFork.ToArray());
            Assert.Equal((o.CatalogId, o.Created, o.Modified), (n.CatalogId, n.Created, n.Modified));
        }
    }

    [Fact]
    public void A_volume_grows_with_free_blocks_at_its_end()
    {
        var source = Volume(800 * 1024);
        var original = source.ToArray();
        int blocks = U16(source, Mdb + 0x12), free = U16(source, Mdb + 0x22), start = U16(source, Mdb + 0x1C);

        var grown = HfsWriter.Resize(ForkData.FromBytes(source), 1440 * 1024);

        Assert.Equal(original, source);
        Assert.Equal(1440 * 1024, grown.Length);
        int added = U16(grown, Mdb + 0x12) - blocks;
        Assert.Equal((1440 - 800) * 2, added);                                         // 512-byte blocks
        Assert.Equal(free + added, U16(grown, Mdb + 0x22));
        Assert.Equal(start, U16(grown, Mdb + 0x1C));                                    // the bitmap had room
        AssertSameFiles(source, grown);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(grown)));
        Assert.Equal(grown.AsSpan(Mdb, 162).ToArray(), grown.AsSpan(grown.Length - 1024, 162).ToArray());   // the alternate MDB
        Assert.NotEqual(0x4244, U16(grown, source.Length - 1024));                      // the old one is gone
    }

    [Fact]
    public void When_the_bitmap_is_full_the_allocation_area_moves_up()
    {
        var source = Volume(400 * 1024);                                               // a 1-sector bitmap: 4,096 blocks at most
        int start = U16(source, Mdb + 0x1C);

        var grown = HfsWriter.Resize(ForkData.FromBytes(source), 4 * 1024 * 1024);

        Assert.Equal(start + 1, U16(grown, Mdb + 0x1C));                                // drAlBlSt: one more bitmap sector
        Assert.True(U16(grown, Mdb + 0x12) > 4096);
        AssertSameFiles(source, grown);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(grown)));
        var after = HfsWriter.CreateFile(ForkData.FromBytes(grown), "Docs:Big", new byte[3 * 1024 * 1024], Array.Empty<byte>(), FinderInfo.Empty);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(after)));                         // the new space is usable
    }

    [Fact]
    public void Growing_past_65535_blocks_shrinking_and_odd_sizes_are_refused()
    {
        var source = Volume(20 * 1024 * 1024);                                         // 512-byte blocks: 32 MB at most

        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), 100 * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), 10 * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), source.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => HfsWriter.Resize(ForkData.FromBytes(source), 30L * 1024 * 1024 + 100));
    }
}
