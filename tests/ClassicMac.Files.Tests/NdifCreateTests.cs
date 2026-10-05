using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

// New NDIF images (docs/formats/disk-images/ndif.md §3.2, §3.3): a disk laid out as Disk Copy 6.3.3 lays it out, and an
// image cut into the parts of a segmented image.
public sealed class NdifCreateTests
{
    private static readonly FourCC Bcem = FourCC.FromString("bcem"), BcmCount = FourCC.FromString("bcm#"), Vers = FourCC.FromString("vers");

    // A 400 KB volume with a folder and two files, its used area followed by free space.
    private static byte[] Volume()
    {
        var date = new MacDate(3_000_000_000);
        var image = HfsWriter.Format(400 * 1024, "Test Disk", date);
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "Docs", date, date);
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Docs:Letter", Enumerable.Repeat((byte)'a', 30_000).ToArray(), new byte[] { 1, 2, 3 }, FinderInfo.Empty, date, date);
        var noise = new byte[20_000];
        new Random(3).NextBytes(noise);
        return HfsWriter.CreateFile(ForkData.FromBytes(image), "Noise", noise, ReadOnlyMemory<byte>.Empty, FinderInfo.Empty, date, date);
    }

    private static byte[] Map(MacFile image) => ResourceFork.Read(image.ResourceFork.ToArray()).Find(Bcem, 128)!.GetData().ToArray();

    private static (long Start, byte Type, long Offset, long Stored)[] Entries(byte[] map) =>
        [.. Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x7C))).Select(k =>
        {
            var e = map.AsSpan(0x80 + k * 12);
            var word = BinaryPrimitives.ReadUInt32BigEndian(e);
            return ((long)(word >> 8), (byte)word, (long)BinaryPrimitives.ReadUInt32BigEndian(e[4..]), (long)BinaryPrimitives.ReadUInt32BigEndian(e[8..]));
        })];

    private static string Pascal(ReadOnlySpan<byte> bytes) => MacRoman.Decode(bytes.Slice(1, bytes[0]));

    private static uint U32(byte[] map, int at) => BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(at));

    private static byte[] Decoded(MacFile image, IEnumerable<MacFile>? siblings = null)
    {
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = true }, diagnostics,
            siblings: siblings is null ? null : () => siblings);
        var disk = Assert.Single(NdifReader.Instance.Read(image, context)).DataFork.ToArray();
        Assert.Empty(diagnostics);
        return disk;
    }

    // The sectors before the first allocation block, and where the used blocks end (the last one in use, plus one).
    private static (long First, long UsedEnd) Layout(byte[] volume)
    {
        var mdb = new BigEndianReader(volume.AsMemory(1024, 512));
        int vbmStart = mdb.ReadUInt16At(0x0E), blocks = mdb.ReadUInt16At(0x12), first = mdb.ReadUInt16At(0x1C);
        long sectorsPerBlock = mdb.ReadUInt32At(0x14) / 512;
        var last = Enumerable.Range(0, blocks).Last(b => (volume[vbmStart * 512 + b / 8] & (0x80 >> (b % 8))) != 0);
        return (first, first + (last + 1) * sectorsPerBlock);
    }

    [Fact]
    public void A_read_only_image_stores_the_used_area_raw_and_leaves_out_free_space()
    {
        var volume = Volume();
        var (first, usedEnd) = Layout(volume);
        long sectors = volume.Length / 512;

        var image = NdifWriter.Create(volume, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });

        Assert.Equal("Test.img", image.Name.ToMacRoman());
        Assert.Equal(("rohd", "ddsk"), (image.FinderInfo.Type.ToString(), image.FinderInfo.Creator.ToString()));
        var map = Map(image);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(map));
        Assert.Equal("Test Disk", Pascal(map.AsSpan(4)));
        Assert.Equal("Test Disk", ResourceFork.Read(image.ResourceFork.ToArray()).Find(Bcem, 128)!.Name!.Value.ToMacRoman());
        Assert.Equal((uint)sectors, U32(map, 0x44));
        Assert.Equal(0u, U32(map, 0x48));                                                       // nothing compressed
        Assert.Equal(NdifReader.Crc(ForkData.FromBytes(volume)), U32(map, 0x50));
        Assert.Equal(0u, U32(map, 0x54));
        Assert.Equal(
            [(0, 0x02, 0, first * 512), (first, 0x02, first * 512, (usedEnd - first) * 512), (usedEnd, 0x00, 0, 0),
             (sectors - 2, 0x02, usedEnd * 512, 512), (sectors - 1, 0x00, 0, 0), (sectors, 0xFF, usedEnd * 512 + 512, 0)],
            Entries(map));
        Assert.Equal(usedEnd * 512 + 512, image.DataFork.Length);
        Assert.Equal(volume, Decoded(image));
    }

    // What lies past the last used block (deleted files, the last sector) is not stored: it reads back as zeros, and
    // the checksum is the decoded disk's. Free blocks inside the used area keep their bytes, as Disk Copy keeps them.
    [Fact]
    public void Free_space_past_the_used_area_reads_as_zeros()
    {
        var volume = Volume();
        var (_, usedEnd) = Layout(volume);
        var junk = volume.ToArray();
        junk.AsSpan((int)usedEnd * 512, 4096).Fill(0xEE);
        junk.AsSpan(junk.Length - 512).Fill(0xEE);

        var image = NdifWriter.Create(junk, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });

        var decoded = Decoded(image);
        Assert.Equal(volume, decoded);
        Assert.Equal(NdifReader.Crc(ForkData.FromBytes(volume)), U32(Map(image), 0x50));
    }

    // Read-only compressed: the used area in chunks of the chunk size from the first allocation block, each ADC (map
    // version 11) or KenCode (version 10), or raw where it would not shrink; +$48 the largest compressed chunk and its
    // decoder's overrun.
    [Theory]
    [InlineData(NdifFormat.Adc, 11, 0x83)]
    [InlineData(NdifFormat.KenCode, 10, 0x80)]
    public void A_compressed_image_has_chunks_of_the_chunk_size(NdifFormat format, int version, byte codec)
    {
        var volume = Volume();
        var (first, usedEnd) = Layout(volume);
        long sectors = volume.Length / 512;

        var image = NdifWriter.Create(volume, "Test.img", new NdifCreateOptions { Format = format, ChunkSectors = 20 });

        var map = Map(image);
        Assert.Equal(version, BinaryPrimitives.ReadUInt16BigEndian(map));
        var entries = Entries(map);
        Assert.Equal((0L, (byte)0x02), (entries[0].Start, entries[0].Type));
        var used = entries.Skip(1).TakeWhile(e => e.Start < usedEnd).ToList();
        Assert.Equal(Enumerable.Range(0, (int)((usedEnd - first + 19) / 20)).Select(i => first + i * 20L), used.Select(e => e.Start));
        Assert.Contains(used, e => e.Type == codec);
        Assert.Contains(used, e => e.Type == 0x02);                                             // the noise does not shrink
        Assert.All(used.Where(e => e.Type == 0x02), e => Assert.Equal(20 * 512, e.Stored));
        Assert.Equal([(usedEnd, (byte)0x00), (sectors - 2, (byte)0x02), (sectors - 1, (byte)0x00), (sectors, (byte)0xFF)],
            entries.Skip(1 + used.Count).Select(e => (e.Start, e.Type)));
        Assert.InRange(U32(map, 0x48), 20u, 21u);                                                // 20 sectors and the overrun
        Assert.Equal(("rohd", "ddsk"), (image.FinderInfo.Type.ToString(), image.FinderInfo.Creator.ToString()));
        Assert.Equal(volume, Decoded(image));
    }

    // Read/write: the whole disk as one raw chunk, no checksum (Disk Copy keeps it 0 on a writable image).
    [Fact]
    public void A_read_write_image_is_one_raw_chunk()
    {
        var volume = Volume();

        var image = NdifWriter.Create(volume, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadWrite });

        var map = Map(image);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16BigEndian(map));
        Assert.Equal(0u, U32(map, 0x50));
        Assert.Equal([(0, 0x02, 0, volume.Length), (volume.Length / 512, 0xFF, volume.Length, 0)], Entries(map));
        Assert.Equal(("dimg", "ddsk"), (image.FinderInfo.Type.ToString(), image.FinderInfo.Creator.ToString()));
        Assert.Equal(volume, image.DataFork.ToArray());
        Assert.Equal(volume, Decoded(image));
    }

    // A disk that is no plain HFS volume has no used area to find: all of it is stored, in chunks from sector 0 when
    // compressed; named by the file.
    [Fact]
    public void A_disk_that_is_not_HFS_is_stored_whole()
    {
        var disk = new byte[100 * 512];
        new Random(1).NextBytes(disk.AsSpan(0, 30 * 512));

        var readOnly = NdifWriter.Create(disk, "Other.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });
        var compressed = NdifWriter.Create(disk, "Other.img", new NdifCreateOptions { Format = NdifFormat.Adc, ChunkSectors = 40 });

        Assert.Equal([(0, 0x02, 0, disk.Length), (100, 0xFF, disk.Length, 0)], Entries(Map(readOnly)));
        Assert.Equal("Other", Pascal(Map(readOnly).AsSpan(4)));
        Assert.Equal([0L, 40, 80, 100], Entries(Map(compressed)).Select(e => e.Start));
        Assert.Equal(disk, Decoded(readOnly));
        Assert.Equal(disk, Decoded(compressed));
    }

    // 'vers' 1 says what the image holds and its checksum, as Disk Copy's does, so the edit writer can renew the CRC.
    [Fact]
    public void The_version_resource_names_the_disk_and_its_checksum()
    {
        var volume = Volume();

        var image = NdifWriter.Create(volume, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });
        var readWrite = NdifWriter.Create(volume, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadWrite });

        var crc = NdifReader.Crc(ForkData.FromBytes(volume));
        Assert.EndsWith($"Mac™ OS HFS 400K image\rCRC: ${crc:X8}", LongVersion(image), StringComparison.Ordinal);
        Assert.EndsWith("Mac™ OS HFS 400K image", LongVersion(readWrite), StringComparison.Ordinal);
        Assert.Null(ResourceFork.Read(image.ResourceFork.ToArray()).Find(FourCC.FromString("STR "), -16396));   // not Disk Copy
    }

    private static string LongVersion(MacFile image)
    {
        var vers = ResourceFork.Read(image.ResourceFork.ToArray()).Find(Vers, 1)!.GetData().ToArray();
        var shortLength = vers[6];
        return Pascal(vers.AsSpan(7 + shortLength));
    }

    [Fact]
    public void Disks_and_options_an_image_cannot_hold_are_refused()
    {
        Assert.Throws<ArgumentException>(() => NdifWriter.Create(new byte[1000], "x.img"));
        Assert.Throws<ArgumentException>(() => NdifWriter.Create(ReadOnlyMemory<byte>.Empty, "x.img"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NdifWriter.Create(new byte[1024], "x.img", NdifCreateOptions.Default with { ChunkSectors = 0 }));
    }

    // Segmented (§1.6): the data fork cut raw into parts of ceil(sectors / parts) sectors; part 1 keeps the map (version
    // 12, flagged, its resource unnamed) and the other resources, the others are 'dseg' with "Part n/M" in 'vers'; every
    // part has a 'bcm#' with its number, the count, one ID and its own CRC.
    [Fact]
    public void An_image_is_split_into_the_parts_of_a_segmented_image()
    {
        var volume = Volume();
        var image = NdifWriter.Create(volume, "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });
        var data = image.DataFork.ToArray();

        var parts = NdifWriter.Split(image, 3, "Test seg");

        Assert.Equal(["Test seg 1of3", "Test seg 2of3", "Test seg 3of3"], parts.Select(p => p.Name.ToMacRoman()));
        Assert.Equal(["rohd", "dseg", "dseg"], parts.Select(p => p.FinderInfo.Type.ToString()));
        Assert.All(parts, p => Assert.Equal("ddsk", p.FinderInfo.Creator.ToString()));
        var size = (data.Length / 512 + 2) / 3 * 512;
        Assert.Equal(data, parts.SelectMany(p => p.DataFork.ToArray()));
        Assert.Equal([size, size, data.Length - 2 * size], parts.Select(p => (int)p.DataFork.Length));

        var first = ResourceFork.Read(parts[0].ResourceFork.ToArray());
        var map = first.Find(Bcem, 128)!;
        Assert.Null(map.Name);
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(map.GetData().Span));
        Assert.Equal(1u, U32(map.GetData().ToArray(), 0x54));
        Assert.Equal(Map(image).AsSpan(2, 0x52).ToArray(), map.GetData().Span.Slice(2, 0x52).ToArray());
        Assert.NotNull(first.Find(Vers, 1));
        byte[]? id = null;
        for (var n = 1; n <= 3; n++)
        {
            var fork = ResourceFork.Read(parts[n - 1].ResourceFork.ToArray());
            var record = fork.Find(BcmCount, 128)!.GetData().ToArray();
            Assert.Equal(24, record.Length);
            Assert.Equal((n, 3), (BinaryPrimitives.ReadUInt16BigEndian(record), BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(2))));
            id ??= record[4..20];
            Assert.Equal(id, record[4..20]);
            Assert.Equal(NdifReader.Crc(parts[n - 1].DataFork), U32(record, 20));
            if (n > 1)
            {
                Assert.Null(fork.Find(Bcem, 128));
                Assert.EndsWith($"Part {n}/3 of a disk image", LongVersion(parts[n - 1]), StringComparison.Ordinal);
            }
        }

        Assert.Equal(volume, Decoded(parts[0], parts.Skip(1)));
    }

    // Part numbers are as wide as the count; the name is cut so the part's stays within 31 characters.
    [Fact]
    public void Part_names_are_padded_and_cut_to_fit()
    {
        var image = NdifWriter.Create(Volume(), "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });

        var parts = NdifWriter.Split(image, 12, new string('n', 40));

        Assert.Equal(new string('n', 24) + " 01of12", parts[0].Name.ToMacRoman());
        Assert.Equal(new string('n', 24) + " 12of12", parts[11].Name.ToMacRoman());
    }

    [Fact]
    public void Splits_an_image_cannot_take_are_refused()
    {
        var image = NdifWriter.Create(Volume(), "Test.img", NdifCreateOptions.Default with { Format = NdifFormat.ReadOnly });

        Assert.Throws<ArgumentOutOfRangeException>(() => NdifWriter.Split(image, 1, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NdifWriter.Split(image, 129, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NdifWriter.Split(image, (int)(image.DataFork.Length / 512) + 1, "x"));
        var parts = NdifWriter.Split(image, 2, "x");
        Assert.Throws<InvalidDataException>(() => NdifWriter.Split(parts[0], 2, "y"));               // already segmented
    }

    // Disk Copy 6.3.3's own images of the same volumes (the harness's run14/out): made again from the read/write image,
    // each has the same data fork and the same map; split into four, the same parts as its segmented image.
    [Fact]
    public void New_images_match_Disk_Copy_s_own()
    {
        var folder = !CorpusFolders.Any ? null
            : CorpusFolders.EnumerateFiles("S800 RW.img", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(d => !Path.GetFileName(d!).StartsWith('.'));
        if (folder is null)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's run14/out images to run this.");
        }

        foreach (var (source, made, format) in new[]
        {
            ("S800 RW.img", "S800 RO.img", NdifFormat.ReadOnly), ("S800 RW.img", "S800 ADC.img", NdifFormat.Adc),
            ("S5M RW.img", "S5M RO.img", NdifFormat.ReadOnly), ("S5M RW.img", "S5M ADC.img", NdifFormat.Adc),
        })
        {
            var diskCopy = HostFiles.Read(Path.Combine(folder, made)).File;
            var ours = NdifWriter.Create(File.ReadAllBytes(Path.Combine(folder, source)), made, NdifCreateOptions.Default with { Format = format });
            Assert.True(diskCopy.DataFork.ToArray().AsSpan().SequenceEqual(ours.DataFork.ToArray()), made);
            Assert.Equal(Map(diskCopy), Map(ours));
            Assert.Equal(diskCopy.FinderInfo.Type, ours.FinderInfo.Type);
        }

        var whole = HostFiles.Read(Path.Combine(folder, "seg", "S5M RO.img")).File;
        var parts = NdifWriter.Split(whole, 4, "S5M RO seg");
        for (var n = 1; n <= 4; n++)
        {
            var theirs = HostFiles.Read(Path.Combine(folder, "seg", $"S5M RO seg {n}of4")).File;
            Assert.Equal(theirs.Name, parts[n - 1].Name);
            Assert.Equal(theirs.FinderInfo.Type, parts[n - 1].FinderInfo.Type);
            Assert.True(theirs.DataFork.ToArray().AsSpan().SequenceEqual(parts[n - 1].DataFork.ToArray()));
            var record = ResourceFork.Read(theirs.ResourceFork.ToArray()).Find(BcmCount, 128)!.GetData().ToArray();
            var ourRecord = ResourceFork.Read(parts[n - 1].ResourceFork.ToArray()).Find(BcmCount, 128)!.GetData().ToArray();
            Assert.Equal(record[..4], ourRecord[..4]);                                           // number and count
            Assert.Equal(record[20..], ourRecord[20..]);                                         // the part's CRC (the ID differs)
        }

        Assert.Equal(ResourceFork.Read(HostFiles.Read(Path.Combine(folder, "seg", "S5M RO seg 1of4")).File.ResourceFork.ToArray()).Find(Bcem, 128)!.GetData().ToArray(),
            ResourceFork.Read(parts[0].ResourceFork.ToArray()).Find(Bcem, 128)!.GetData().ToArray());
    }
}
