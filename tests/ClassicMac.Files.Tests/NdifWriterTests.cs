using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using static ClassicMac.Files.Tests.NdifBuilder;

namespace ClassicMac.Files.Tests;

// NdifWriter.Rewrite (docs/formats/disk-images/ndif.md §3): an NDIF image made again around a changed disk. Chunk
// boundaries stay; a chunk whose sectors are unchanged keeps its stored bytes, a changed one is stored raw.
public sealed class NdifWriterTests
{
    private static readonly FourCC Bcem = FourCC.FromString("bcem");

    private static byte[] Volume()
    {
        var image = HfsWriter.Format(400 * 1024, "Test Disk");
        return HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", Enumerable.Repeat((byte)'a', 3000).ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
    }

    private static MacFile Image(byte[] data, byte[] resource) => new()
    {
        Name = MacString.FromMacRoman("Test.img"),
        FinderInfo = new FinderInfo { Type = FourCC.FromString("rohd"), Creator = FourCC.FromString("ddsk") },
        DataFork = ForkData.FromBytes(data),
        ResourceFork = ForkData.FromBytes(resource),
    };

    private static byte[] Map(MacFile image) => ResourceFork.Read(image.ResourceFork.ToArray()).Find(Bcem, 128)!.GetData().ToArray();

    private static (long Start, byte Type, long Offset, long Stored)[] Entries(byte[] map) =>
        [.. Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x7C))).Select(k =>
        {
            var e = map.AsSpan(0x80 + k * 12);
            var word = BinaryPrimitives.ReadUInt32BigEndian(e);
            return ((long)(word >> 8), (byte)word, (long)BinaryPrimitives.ReadUInt32BigEndian(e[4..]), (long)BinaryPrimitives.ReadUInt32BigEndian(e[8..]));
        })];

    private static byte[] Decoded(MacFile image, List<Diagnostic> diagnostics) =>
        NdifReader.Instance.Read(image, new ContainerContext(diagnostics: diagnostics)).Single().DataFork.ToArray();

    // An image with a CRC, as read-only and compressed images carry: ADC, zero and raw chunks.
    private static MacFile Compressed(byte[] volume)
    {
        var (data, resource) = Build(volume, "Test Disk", (100, Kind.Adc), (100, Kind.Adc), (596, Kind.Zero), (4, Kind.Raw));
        var map = ResourceFork.Read(resource).Find(Bcem, 128)!.GetData().ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x50), NdifReader.Crc(ForkData.FromBytes(volume)));
        return Image(data, Fork(("bcem", 128, map)));
    }

    [Fact]
    public void A_changed_chunk_is_stored_raw_and_the_others_keep_their_bytes()
    {
        var volume = Volume();
        var image = Compressed(volume);
        var changed = volume.ToArray();
        changed[150 * 512 + 7] ^= 0xFF;                                                      // in the second ADC chunk

        var written = NdifWriter.Rewrite(image, changed);

        var diagnostics = new List<Diagnostic>();
        Assert.Equal(changed, Decoded(written, diagnostics));
        Assert.Empty(diagnostics);                                                           // the CRC matches the new disk
        var before = Entries(Map(image));
        var after = Entries(Map(written));
        Assert.Equal(before.Select(e => e.Start), after.Select(e => e.Start));                // the same boundaries
        Assert.Equal([0x83, 0x02, 0x00, 0x02, 0xFF], after.Select(e => (int)e.Type));
        Assert.Equal(100 * 512L, after[1].Stored);
        var data = image.DataFork.ToArray();
        var newData = written.DataFork.ToArray();
        Assert.Equal(data.AsSpan((int)before[0].Offset, (int)before[0].Stored).ToArray(), newData.AsSpan((int)after[0].Offset, (int)after[0].Stored).ToArray());
        Assert.Equal(NdifReader.Crc(ForkData.FromBytes(changed)), BinaryPrimitives.ReadUInt32BigEndian(Map(written).AsSpan(0x50)));
        Assert.Equal(image.FinderInfo, written.FinderInfo);
    }

    // Given the sectors that changed (an edit session knows them), the old disk is not decoded to compare chunks: a chunk
    // with no changed sector keeps its stored bytes, and the result is the one a full comparison gives.
    [Fact]
    public void The_changed_sectors_name_the_chunks_to_store_again()
    {
        var volume = Volume();
        var image = Compressed(volume);
        var changed = volume.ToArray();
        changed[150 * 512 + 7] ^= 0xFF;

        var compared = NdifWriter.Rewrite(image, changed);
        var named = NdifWriter.Rewrite(image, changed, changedSectors: new HashSet<long> { 150 });

        Assert.Equal(compared.DataFork.ToArray(), named.DataFork.ToArray());
        Assert.Equal(Map(compared), Map(named));
    }

    [Fact]
    public void An_unchanged_disk_gives_the_same_data_and_map()
    {
        var volume = Volume();
        var image = Compressed(volume);

        var written = NdifWriter.Rewrite(image, volume);

        Assert.Equal(image.DataFork.ToArray(), written.DataFork.ToArray());
        Assert.Equal(Map(image), Map(written));
    }

    [Fact]
    public void A_read_write_image_without_a_CRC_keeps_none_and_stays_raw()
    {
        var volume = Volume();
        var (data, resource) = Build(volume, "Test Disk", (800, Kind.Raw));
        var changed = volume.ToArray();
        changed[10] = 1;

        var written = NdifWriter.Rewrite(Image(data, resource), changed);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(Map(written).AsSpan(0x50)));
        Assert.Equal([0x02, 0xFF], Entries(Map(written)).Select(e => (int)e.Type));
        Assert.Equal(changed, written.DataFork.ToArray());
    }

    [Fact]
    public void A_disk_of_another_size_or_a_segmented_image_is_refused()
    {
        var volume = Volume();
        var image = Compressed(volume);
        Assert.Throws<InvalidDataException>(() => NdifWriter.Rewrite(image, volume.AsSpan(0, 512 * 400).ToArray()));

        var map = Map(image);
        BinaryPrimitives.WriteUInt16BigEndian(map, 12);
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x54), 1);
        Assert.Throws<InvalidDataException>(() => NdifWriter.Rewrite(Image(image.DataFork.ToArray(), Fork(("bcem", 128, map))), volume));
    }
}
