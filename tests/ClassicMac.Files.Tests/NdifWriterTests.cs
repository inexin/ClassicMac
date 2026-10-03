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
        new Random(5).NextBytes(changed.AsSpan(100 * 512, 100 * 512));                       // the second ADC chunk: bytes ADC cannot shrink

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

    // A changed chunk's runs of zero sectors become zero chunks, which store nothing, so an edit does not store empty
    // space; the map gains their entries.
    [Fact]
    public void A_changed_chunk_s_zero_runs_become_zero_chunks()
    {
        var volume = Volume();
        var image = Compressed(volume);
        var changed = volume.ToArray();
        changed.AsSpan(100 * 512, 20 * 512).Fill(0x11);
        changed.AsSpan(120 * 512, 60 * 512).Clear();
        changed.AsSpan(180 * 512, 20 * 512).Fill(0x22);                                      // the second ADC chunk

        var written = NdifWriter.Rewrite(image, changed, new HashSet<long> { 100, 150, 199 });

        var diagnostics = new List<Diagnostic>();
        Assert.Equal(changed, Decoded(written, diagnostics));
        Assert.Empty(diagnostics);
        var map = Map(written);
        var after = Entries(map);
        Assert.Equal([(0L, 0x83), (100, 0x83), (120, 0x00), (180, 0x83), (200, 0x00), (796, 0x02), (800, 0xFF)],
            after.Select(e => (e.Start, (int)e.Type)));                                         // the runs between compressed
        Assert.Equal((0L, 0L), (after[2].Offset, after[2].Stored));
        Assert.InRange(after[1].Stored, 1, 20 * 512 / 10);
        Assert.Equal(0x80 + 12 * after.Length, map.Length);
    }

    // In an ADC image a changed chunk is compressed again, as Disk Copy stores it: ADC while it is no longer than the
    // sectors, and the buffer size (+$48) still covers it.
    [Fact]
    public void A_changed_chunk_in_an_ADC_image_is_compressed()
    {
        var volume = Volume();
        var image = Compressed(volume);
        var changed = volume.ToArray();
        for (var i = 100 * 512; i < 200 * 512; i++)
        {
            changed[i] = (byte)(i % 13 * 17);
        }

        var written = NdifWriter.Rewrite(image, changed, new HashSet<long> { 150 });

        var diagnostics = new List<Diagnostic>();
        Assert.Equal(changed, Decoded(written, diagnostics));
        Assert.Empty(diagnostics);
        var map = Map(written);
        var after = Entries(map);
        Assert.Equal([(0L, 0x83), (100, 0x83), (200, 0x00), (796, 0x02), (800, 0xFF)], after.Select(e => (e.Start, (int)e.Type)));
        Assert.InRange(after[1].Stored, 1, 100 * 512 / 10);
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x48)) >= 101);
    }

    // An image with no ADC chunk (Disk Copy's read-only and read/write images, or a version 10 map, which cannot hold
    // one) keeps changed chunks raw.
    [Fact]
    public void An_image_without_ADC_chunks_keeps_changed_ones_raw()
    {
        var volume = Volume();
        var (data, resource) = Build(volume, "Test Disk", (200, Kind.Raw), (596, Kind.Zero), (4, Kind.Raw));
        var changed = volume.ToArray();
        changed.AsSpan(100 * 512, 100 * 512).Fill(0x5A);

        var written = NdifWriter.Rewrite(Image(data, resource), changed, new HashSet<long> { 150 });

        Assert.DoesNotContain(Entries(Map(written)), e => e.Type == 0x83);
        Assert.Equal(changed, Decoded(written, []));
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
