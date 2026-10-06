using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.Resize (docs/formats/file-systems/hfs.md §3.2, §3.3): a volume grown or shrunk within its allocation block
// size, its files and folders untouched (a shrink moves what lies past the new end down first).
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

    // Found by fuzzing (hfs-edit): a volume grown with 4 KB blocks, then shrunk to its smallest, must still pass First
    // Aid at each step.
    [Fact]
    public void A_volume_resized_down_to_its_smallest_still_passes_First_Aid()
    {
        var date = new MacDate(3_000_000_000);
        var image = HfsWriter.Format(1_638_400, "Fuzz", date);
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "1d", date, date);
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "e2é 3", date, date);
        foreach (var (size, block) in new (long, uint?)[] { (1_157_120, 4096), (774_144, null), (833_024, null), (27_648, null) })
        {
            image = HfsWriter.Resize(ForkData.FromBytes(image), size, block);
            var report = HfsFirstAid.Verify(ForkData.FromBytes(image));
            Assert.True(report.Verdict == FirstAidVerdict.AppearsOk,
                $"after resizing to {size}: " + string.Join("; ", report.Problems.Select(p => $"{p.Number} {p.Message} {p.Arg2} {p.Arg3}")));
        }
    }

    // Found by fuzzing (hfs-edit): Disk First Aid sizes the bitmap from the whole volume, so a 400 KB volume grown to
    // 4,102 sectors needs 2 bitmap sectors and at most 4,095 blocks, though 4,096 would fit its one sector.
    [Theory]
    [InlineData(2_100_224)]
    [InlineData(2_099_712)]
    [InlineData(2_101_248)]
    [InlineData(4_198_400)]
    public void A_grown_volume_has_the_bitmap_and_block_count_First_Aid_expects(long size)
    {
        var image = HfsWriter.Resize(ForkData.FromBytes(Volume(409_600)), size);
        var report = HfsFirstAid.Verify(ForkData.FromBytes(image));
        Assert.True(report.Verdict == FirstAidVerdict.AppearsOk,
            string.Join("; ", report.Problems.Select(p => $"{p.Number} {p.Message} {p.Arg2} {p.Arg3}")));
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

    // A file past the new end: a filler deleted before it leaves the room below.
    private static byte[] WithLateFile()
    {
        var image = Volume(800 * 1024);
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Filler", new byte[500 * 1024], Array.Empty<byte>(), FinderInfo.Empty);
        var late = Enumerable.Range(0, 100 * 1024).Select(b => (byte)(b * 7 + 1)).ToArray();
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Docs:Late", late, new byte[] { 9, 8, 7 }, FinderInfo.Empty);
        return HfsWriter.DeleteFile(ForkData.FromBytes(image), "Filler");
    }

    private static int FirstBlockOf(byte[] image, string macPath) =>
        U16(image, Record(image, macPath) + 0x4A);                                    // filExtRec's first start

    // The offset of a file's catalog record data in the image (the catalog in one extent, as the writer formats it).
    private static int Record(byte[] image, string macPath)
    {
        int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 0x14));
        int catalog = (U16(image, Mdb + 0x1C) + U16(image, Mdb + 0x96) * blockSize / 512) * 512;
        var name = System.Text.Encoding.Latin1.GetBytes(macPath[(macPath.LastIndexOf(':') + 1)..]);
        for (var at = catalog; at < image.Length - name.Length; at++)
        {
            if (image[at] == name.Length && image.AsSpan(at + 1, name.Length).SequenceEqual(name) && image[at + 1 + name.Length + (name.Length % 2 == 0 ? 1 : 0)] == 2)
            {
                return at + 1 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
            }
        }

        throw new InvalidOperationException(macPath);
    }

    [Fact]
    public void A_volume_shrinks_with_what_lies_past_its_new_end_moved_down()
    {
        var source = WithLateFile();
        int start = U16(source, Mdb + 0x1C);
        Assert.True((FirstBlockOf(source, "Docs:Late") + start) * 512L > 400 * 1024);   // past the new end

        var shrunk = HfsWriter.Resize(ForkData.FromBytes(source), 400 * 1024);

        Assert.Equal(400 * 1024, shrunk.Length);
        int blocks = U16(shrunk, Mdb + 0x12);
        Assert.Equal((400 * 2 - start - 2), blocks);                                   // 512-byte blocks
        Assert.True(FirstBlockOf(shrunk, "Docs:Late") < blocks);
        AssertSameFiles(source, shrunk);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(shrunk)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(shrunk)).Verdict);
        Assert.Equal(shrunk.AsSpan(Mdb, 162).ToArray(), shrunk.AsSpan(shrunk.Length - 1024, 162).ToArray());
        var after = HfsWriter.CreateFile(ForkData.FromBytes(shrunk), "Docs:New", new byte[50 * 1024], Array.Empty<byte>(), FinderInfo.Empty);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(after)));                          // its free space is usable
    }

    [Fact]
    public void A_catalog_past_the_new_end_is_moved_down_with_the_MDB_naming_it()
    {
        var source = Volume(800 * 1024);
        // The catalog's blocks moved to the volume's last free blocks, as a volume's later growth may leave them.
        int blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(Mdb + 0x14));
        int start = U16(source, Mdb + 0x1C), blocks = U16(source, Mdb + 0x12), bitmap = U16(source, Mdb + 0x0E) * 512;
        int from = U16(source, Mdb + 0x96), count = U16(source, Mdb + 0x98);
        int to = blocks - count;
        source.AsSpan((start + from) * 512, count * blockSize).CopyTo(source.AsSpan((start + to) * 512));
        for (var b = 0; b < count; b++)
        {
            source[bitmap + (from + b) / 8] &= (byte)~(0x80 >> ((from + b) % 8));
            source[bitmap + (to + b) / 8] |= (byte)(0x80 >> ((to + b) % 8));
        }

        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(Mdb + 0x96), (ushort)to);
        source.AsSpan(Mdb, 512).CopyTo(source.AsSpan(source.Length - 1024));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(source)).Verdict);

        var shrunk = HfsWriter.Resize(ForkData.FromBytes(source), 600 * 1024);

        Assert.True(U16(shrunk, Mdb + 0x96) + count <= U16(shrunk, Mdb + 0x12));
        AssertSameFiles(source, shrunk);
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(shrunk)).Verdict);
    }

    [Fact]
    public void A_shrink_that_would_cut_off_blocks_in_use_is_refused()
    {
        var source = WithLateFile();
        source = HfsWriter.CreateFile(ForkData.FromBytes(source), "More", new byte[400 * 1024], Array.Empty<byte>(), FinderInfo.Empty);

        var refused = Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), 400 * 1024));
        Assert.Contains("in use", refused.Message);
    }

    private static void AssertSameFolders(byte[] before, byte[] after) =>
        Assert.Equal(HfsReader.Instance.ReadFolders(ForkData.FromBytes(before), new ContainerContext()).Select(f => (f.MacPath, f.CatalogId)),
            HfsReader.Instance.ReadFolders(ForkData.FromBytes(after), new ContainerContext()).Select(f => (f.MacPath, f.CatalogId)));

    // Past 65,535 blocks of its size the volume is laid out again with the block size Mac OS's initializer gives the new
    // size: every fork in one extent, the catalog's records otherwise kept.
    [Fact]
    public void Growing_past_65535_blocks_takes_a_larger_block_size()
    {
        var source = Volume(20 * 1024 * 1024);                                         // 512-byte blocks: 32 MB at most

        var grown = HfsWriter.Resize(ForkData.FromBytes(source), 100 * 1024 * 1024);

        Assert.Equal(100 * 1024 * 1024, grown.Length);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32BigEndian(grown.AsSpan(Mdb + 0x14)));   // ((204,800 >> 16) + 1) × 512
        Assert.Equal(source.AsSpan(Mdb + 0x24, 28).ToArray(), grown.AsSpan(Mdb + 0x24, 28).ToArray());   // the name
        Assert.Equal(source.AsSpan(Mdb + 0x02, 4).ToArray(), grown.AsSpan(Mdb + 0x02, 4).ToArray());     // the creation date
        AssertSameFiles(source, grown);
        AssertSameFolders(source, grown);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(grown)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(grown)).Verdict);
        var after = HfsWriter.CreateFile(ForkData.FromBytes(grown), "Docs:Big", new byte[60 * 1024 * 1024], Array.Empty<byte>(), FinderInfo.Empty);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(after)));                         // the new space is usable
    }

    [Fact]
    public void Fragmented_forks_and_their_overflow_records_are_laid_out_whole()
    {
        // Small files with every other one deleted, then a file spread over the gaps: overflow records.
        var source = HfsWriter.Format(2 * 1024 * 1024, "Frag", new MacDate(3_100_000_000));
        for (var i = 0; i < 40; i++)
        {
            source = HfsWriter.CreateFile(ForkData.FromBytes(source), $"Pad {i:D2}", new byte[1024], Array.Empty<byte>(), FinderInfo.Empty);
        }

        for (var i = 0; i < 40; i += 2)
        {
            source = HfsWriter.DeleteFile(ForkData.FromBytes(source), $"Pad {i:D2}");
        }

        var spread = Enumerable.Range(0, (U16(source, Mdb + 0x22) - 8) * 512).Select(b => (byte)(b * 13)).ToArray();   // more than the tail
        source = HfsWriter.CreateFile(ForkData.FromBytes(source), "Spread", spread, new byte[] { 1, 2, 3 }, FinderInfo.Empty);
        int extentsTree = (U16(source, Mdb + 0x1C) + U16(source, Mdb + 0x86)) * 512;     // 512-byte blocks
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(extentsTree + 14 + 6)) > 0);   // overflow records
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(source)).Verdict);

        var grown = HfsWriter.Resize(ForkData.FromBytes(source), 40 * 1024 * 1024);

        AssertSameFiles(source, grown);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(grown)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(grown)).Verdict);
    }

    // A chosen allocation block size (hfs.md §3.1, §3.2): the initializer's automatic one, or a larger multiple of 512.
    [Theory]
    [InlineData(800L * 1024, 512u)]
    [InlineData(20L * 1024 * 1024, 512u)]
    [InlineData(40L * 1024 * 1024, 1024u)]
    [InlineData(100L * 1024 * 1024, 2048u)]
    public void The_automatic_block_size_is_the_initializer_s(long size, uint expected)
    {
        Assert.Equal(expected, HfsWriter.AutomaticBlockSize(size));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32BigEndian(HfsWriter.Format(size, "Disk").AsSpan(Mdb + 0x14)));
    }

    [Fact]
    public void Format_takes_a_larger_block_size()
    {
        var image = HfsWriter.Format(20 * 1024 * 1024, "Disk", blockSize: 4096);

        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 0x14)));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(image)).Verdict);
        Assert.Throws<ArgumentOutOfRangeException>(() => HfsWriter.Format(20 * 1024 * 1024, "Disk", blockSize: 1000));    // not 512s
        Assert.Throws<ArgumentOutOfRangeException>(() => HfsWriter.Format(100L * 1024 * 1024, "Disk", blockSize: 512));  // past 65,535 blocks
    }

    [Theory]
    [InlineData(20L * 1024 * 1024, 2048u)]                                             // the same size, larger blocks
    [InlineData(40L * 1024 * 1024, 4096u)]                                             // larger, larger blocks
    [InlineData(10L * 1024 * 1024, 1024u)]                                             // smaller, larger blocks
    public void Resize_lays_the_volume_out_again_at_a_chosen_block_size(long size, uint blockSize)
    {
        var source = Volume(20 * 1024 * 1024);

        var resized = HfsWriter.Resize(ForkData.FromBytes(source), size, blockSize);

        Assert.Equal(size, resized.Length);
        Assert.Equal(blockSize, BinaryPrimitives.ReadUInt32BigEndian(resized.AsSpan(Mdb + 0x14)));
        AssertSameFiles(source, resized);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(resized)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(resized)).Verdict);
    }

    [Fact]
    public void The_same_size_and_odd_sizes_are_refused()
    {
        var source = Volume(20 * 1024 * 1024);

        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), source.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => HfsWriter.Resize(ForkData.FromBytes(source), 30L * 1024 * 1024 + 100));
    }
}
