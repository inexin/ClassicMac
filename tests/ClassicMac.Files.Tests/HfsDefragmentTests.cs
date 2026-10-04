using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

// HfsWriter.Defragment (docs/formats/file-systems/hfs.md §3.4): the volume laid out again in its own size and geometry,
// every fork in one extent and the free space in one run at the end, its files and folders untouched.
public sealed class HfsDefragmentTests : IDisposable
{
    private const int Mdb = 1024;
    private readonly string directory = Directory.CreateTempSubdirectory("cm-defrag").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static int U16(byte[] image, int offset) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset));

    private static uint U32(byte[] image, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset));

    private static IReadOnlyList<MacFile> Files(byte[] image) => HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    // Small files with every other one deleted, then a file spread over the gaps: overflow records, free space in pieces.
    internal static byte[] Fragmented(string name = "Frag")
    {
        var image = HfsWriter.Format(2 * 1024 * 1024, name, new MacDate(3_100_000_000));
        for (var i = 0; i < 40; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Pad {i:D2}", new byte[1024], Array.Empty<byte>(), FinderInfo.Empty);
        }

        for (var i = 0; i < 40; i += 2)
        {
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), $"Pad {i:D2}");
        }

        var spread = Enumerable.Range(0, (U16(image, Mdb + 0x22) - 8) * 512).Select(b => (byte)(b * 13)).ToArray();
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Spread", spread, new byte[] { 1, 2, 3 }, FinderInfo.Empty);
        return HfsWriter.CreateFolder(ForkData.FromBytes(image), "Docs");
    }

    private static uint OverflowRecords(byte[] image)
    {
        int blockSize = (int)U32(image, Mdb + 0x14);
        int tree = U16(image, Mdb + 0x1C) * 512 + U16(image, Mdb + 0x86) * blockSize;
        return U32(image, tree + 14 + 6);
    }

    // Whether the blocks in use are the first ones, the free ones all after them.
    private static bool FreeSpaceInOneRun(byte[] image)
    {
        int bitmap = U16(image, Mdb + 0x0E) * 512, blocks = U16(image, Mdb + 0x12), used = blocks - U16(image, Mdb + 0x22);
        return Enumerable.Range(0, blocks).All(b => ((image[bitmap + b / 8] >> (7 - b % 8)) & 1) == (b < used ? 1 : 0));
    }

    [Fact]
    public void Every_fork_is_laid_out_whole_and_the_free_space_in_one_run()
    {
        var source = Fragmented();
        Assert.True(OverflowRecords(source) > 0);
        Assert.False(FreeSpaceInOneRun(source));

        var defragmented = HfsWriter.Defragment(ForkData.FromBytes(source));

        Assert.Equal(source.Length, defragmented.Length);
        foreach (var offset in new[] { 0x0E, 0x12, 0x14, 0x18, 0x1C, 0x22, 0x24, 0x4A, 0x4E, 0x82, 0x92 })
        {
            Assert.Equal(source.AsSpan(Mdb + offset, 2).ToArray(), defragmented.AsSpan(Mdb + offset, 2).ToArray());   // geometry, counts, name
        }

        Assert.Equal(0u, OverflowRecords(defragmented));
        Assert.True(FreeSpaceInOneRun(defragmented));
        Assert.Equal(Files(source).Select(f => (f.MacPath, f.CatalogId, f.Created, f.Modified)), Files(defragmented).Select(f => (f.MacPath, f.CatalogId, f.Created, f.Modified)));
        foreach (var (before, after) in Files(source).Zip(Files(defragmented)))
        {
            Assert.Equal(before.DataFork.ToArray(), after.DataFork.ToArray());
            Assert.Equal(before.ResourceFork.ToArray(), after.ResourceFork.ToArray());
        }

        Assert.Equal(HfsReader.Instance.ReadFolders(ForkData.FromBytes(source), new ContainerContext()).Select(f => f.MacPath),
            HfsReader.Instance.ReadFolders(ForkData.FromBytes(defragmented), new ContainerContext()).Select(f => f.MacPath));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(defragmented)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(defragmented)).Verdict);
        Assert.Equal(source.AsSpan(0, 1024).ToArray(), defragmented.AsSpan(0, 1024).ToArray());                 // the boot blocks
        Assert.Equal(defragmented.AsSpan(Mdb, 512).ToArray(), defragmented.AsSpan(defragmented.Length - 1024, 512).ToArray());
    }

    [Fact]
    public void A_shrink_refused_for_want_of_a_free_run_succeeds_after_it()
    {
        var source = Fragmented();
        for (var i = 1; i < 40; i += 2)
        {
            source = HfsWriter.DeleteFile(ForkData.FromBytes(source), $"Pad {i:D2}");     // free space only in small gaps
        }

        long size = (U16(source, Mdb + 0x1C) + 2 + (U16(source, Mdb + 0x12) - U16(source, Mdb + 0x22)) + 16) * 512L;

        Assert.Contains("defrag", Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), size)).Message);
        var shrunk = HfsWriter.Resize(ForkData.FromBytes(HfsWriter.Defragment(ForkData.FromBytes(source))), size);

        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(shrunk)).Verdict);
    }

    // The smallest size a volume shrinks to: its blocks in use, with nothing to spare once it is defragmented.
    [Fact]
    public void The_smallest_size_holds_the_blocks_in_use()
    {
        var source = HfsWriter.Defragment(ForkData.FromBytes(Fragmented()));
        long used = U16(source, Mdb + 0x12) - U16(source, Mdb + 0x22);

        long smallest = HfsWriter.SmallestSize(ForkData.FromBytes(source));

        Assert.Equal((U16(source, Mdb + 0x1C) + 2 + used) * 512L, smallest);                    // 512-byte blocks
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(HfsWriter.Resize(ForkData.FromBytes(source), smallest))).Verdict);
        Assert.Throws<InvalidDataException>(() => HfsWriter.Resize(ForkData.FromBytes(source), smallest - 512));
    }

    [Fact]
    public void Only_a_plain_volume_image_is_resized()
    {
        var plain = Path.Combine(directory, "plain.img");
        File.WriteAllBytes(plain, Fragmented());
        var partitioned = Path.Combine(directory, "partitioned.img");
        File.WriteAllBytes(partitioned, PartitionMap(("One", "Apple_HFS", Fragmented())));

        var session = InputEditSession.Open(plain);

        Assert.True(session.CanResize);
        Assert.Equal(HfsWriter.SmallestSize(ForkData.FromFile(plain)), session.SmallestSize);
        Assert.False(InputEditSession.Open(partitioned).CanResize);
        session.Resize(4 * 1024 * 1024);
        Assert.Equal(HfsWriter.SmallestSize(ForkData.FromBytes(session.Volume)), session.SmallestSize);   // as edited
    }

    [Fact]
    public void The_session_defragments_one_partition_of_a_disk_with_several()
    {
        var one = Fragmented("One");
        var two = Fragmented("Two");
        var path = Path.Combine(directory, "disk.img");
        File.WriteAllBytes(path, PartitionMap(("One", "Apple_HFS", one), ("Two", "Apple_HFS", two)));
        var session = InputEditSession.Open(path);

        session.Defragment("Two");

        var change = Assert.Single(session.Changes);
        Assert.Equal(("defragment", "Two"), (change.Action, change.Path));
        var target = Path.Combine(directory, "out.img");
        session.SaveAs(target);
        var written = File.ReadAllBytes(target);
        var map = PartitionMapReader.Partitions(ForkData.FromBytes(written));
        byte[] Slice(int i) => written.AsSpan((int)map[i].Offset, (int)map[i].Length).ToArray();
        Assert.Equal(one, Slice(0));
        Assert.True(FreeSpaceInOneRun(Slice(1)));
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromBytes(Slice(1))).Verdict);
        Assert.Throws<InvalidOperationException>(() => InputEditSession.Open(path).Defragment(""));       // which partition?
    }
}
