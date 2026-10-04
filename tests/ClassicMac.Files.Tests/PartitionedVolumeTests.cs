using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

// Writing to the HFS partition of an Apple partition map image (docs/formats/file-systems/partition-map.md §5): the
// partition is edited as a volume and put back in place; the map, drivers and other partitions stay byte for byte.
public sealed class PartitionedVolumeTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-partitioned").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static byte[] HfsVolume(string name = "Disk")
    {
        var builder = new HfsBuilder();
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        builder.File(docs, "Letter", "data"u8.ToArray(), []);
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var image = builder.Build(name);

        // Room for new files: 1,600 allocation blocks, the alternate MDB in the image's last block but one.
        int oldBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12));
        int oldFree = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22));
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + 1600 + 2) * HfsBuilder.Block);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), 1600);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22), checked((ushort)(oldFree + 1600 - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }

    private static readonly byte[] Driver = Enumerable.Range(0, 1024).Select(i => (byte)(i * 7)).ToArray();

    private string Image(params (string Name, string Type, byte[] Data)[] partitions)
    {
        var path = Path.Combine(directory, "disk.img");
        File.WriteAllBytes(path, PartitionMap(partitions));
        return path;
    }

    private static IReadOnlyList<MacFile> Files(byte[] volume) => HfsReader.Instance.Read(ForkData.FromBytes(volume), new ContainerContext());

    [Fact]
    public void The_map_lists_its_Mac_volume_partitions_with_where_they_are()
    {
        var volume = HfsVolume();
        var image = PartitionMap(("Driver", "Apple_Driver43", Driver), ("Macintosh HD", "Apple_HFS", volume));

        var partition = Assert.Single(PartitionMapReader.Partitions(ForkData.FromBytes(image)));

        Assert.Equal(("Macintosh HD", "Apple_HFS", 3), (partition.Name, partition.Type, partition.Number));    // the map is 1, the driver 2
        Assert.Equal(image.Length - volume.Length, partition.Offset);
        Assert.Equal(volume.Length, partition.Length);
        Assert.Empty(PartitionMapReader.Partitions(ForkData.FromBytes(new byte[4096])));
    }

    [Fact]
    public void The_HFS_partition_is_edited_and_saved_back_in_place()
    {
        var volume = HfsVolume();
        var path = Image(("Driver", "Apple_Driver43", Driver), ("Macintosh HD", "Apple_HFS", volume));
        var original = File.ReadAllBytes(path);
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);

        session.Delete("Read Me");
        session.AddFolder("Docs:New");
        session.AddFile("Docs:Note", new MacFile { Name = MacString.FromMacRoman("Note"), DataFork = ForkData.FromBytes("note"u8.ToArray()) });
        var target = Path.Combine(directory, "out.img");
        session.SaveAs(target);

        var written = File.ReadAllBytes(target);
        Assert.Equal(original.Length, written.Length);
        var offset = original.Length - volume.Length;
        Assert.Equal(original.AsSpan(0, offset).ToArray(), written.AsSpan(0, offset).ToArray());   // the map and the driver
        var edited = written.AsSpan(offset).ToArray();
        Assert.Equal(["Docs:Letter", "Docs:Note"], Files(edited).Select(f => f.MacPath).Order());
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(edited)));
        Assert.Equal(original, File.ReadAllBytes(path));                                           // the input never changes

        session.SaveInPlace();
        Assert.Equal(written, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".orig"));
    }

    // A disk with several Mac volume partitions: each is a folder named after it, so a path in the session starts with
    // the partition's name (partition-map.md §5).
    [Fact]
    public void A_disk_with_two_HFS_partitions_edits_each_by_its_name()
    {
        var one = HfsVolume("One");
        var two = HfsVolume("Two");
        var path = Image(("Driver", "Apple_Driver43", Driver), ("One", "Apple_HFS", one), ("Two", "Apple_HFS", two));
        var original = File.ReadAllBytes(path);
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);
        Assert.Equal(["One", "Two"], session.PartitionNames);
        Assert.Null(session.Partition);

        session.Delete("One:Read Me");
        session.AddFolder("Two:Docs:New");
        session.SetResource("Two:Read Me", FourCC.FromString("STR "), 128, "hi"u8.ToArray());
        var target = Path.Combine(directory, "out.img");
        session.SaveAs(target);

        var written = File.ReadAllBytes(target);
        Assert.Equal(original.Length, written.Length);
        var partitions = PartitionMapReader.Partitions(ForkData.FromBytes(original));
        var first = partitions[0].Offset;
        Assert.Equal(original.AsSpan(0, (int)first).ToArray(), written.AsSpan(0, (int)first).ToArray());   // the map and the driver
        byte[] Slice(byte[] image, MacPartition p) => image.AsSpan((int)p.Offset, (int)p.Length).ToArray();
        Assert.Equal(["Docs:Letter"], Files(Slice(written, partitions[0])).Select(f => f.MacPath));
        Assert.Equal(["Docs:Letter", "Read Me"], Files(Slice(written, partitions[1])).Select(f => f.MacPath).Order());
        Assert.Contains(HfsReader.Instance.ReadFolders(ForkData.FromBytes(Slice(written, partitions[1])), new ContainerContext()), f => f.MacPath == "Docs:New");
        Assert.NotNull(ResourceFork.Read(Files(Slice(written, partitions[1])).Single(f => f.MacPath == "Read Me").ResourceFork.ToArray()).Find(FourCC.FromString("STR "), 128));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(Slice(written, partitions[0]))));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(Slice(written, partitions[1]))));
        var current = session.Current()!.File.DataFork.ToArray();                  // as saved (its fork edits made again, at a new time)
        Assert.Equal(["Docs:Letter", "Read Me"], Files(Slice(current, partitions[1])).Select(f => f.MacPath).Order());
        Assert.Equal(Slice(written, partitions[0]), Slice(current, partitions[0]));
        Assert.Contains(HfsReader.Instance.ReadFolders(ForkData.FromBytes(session.VolumeOf("Two")), new ContainerContext()), f => f.MacPath == "Docs:New");

        session.SaveInPlace();
        Assert.Equal(written, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(path + ".orig"));
    }

    [Fact]
    public void A_path_on_a_disk_with_several_partitions_must_name_one()
    {
        var path = Image(("One", "Apple_HFS", HfsVolume("One")), ("Two", "Apple_HFS", HfsVolume("Two")));
        var session = InputEditSession.Open(path);

        Assert.Contains("One, Two", Assert.Throws<InvalidOperationException>(() => session.AddFolder("Three:New")).Message);
        Assert.Throws<InvalidOperationException>(() => session.Volume);
        Assert.Throws<InvalidOperationException>(() => session.Move("One:Read Me", "Two:Docs"));
        Assert.Throws<InvalidOperationException>(() => session.Resize(10_000_000));
        session.AddFolder("one:New");                                        // names compare as the catalog compares them
        Assert.Equal(InputEditKind.HfsVolume, session.KindOf("Two:Docs"));
        Assert.Equal(InputEditKind.ReadOnly, session.KindOf("Three"));
    }

    [Fact]
    public void An_HFS_Plus_partition_beside_an_HFS_one_is_repaired_by_its_name()
    {
        var builder = new HfsPlusBuilder();
        builder.File(HfsPlusBuilder.Root, "Plus File", "plus"u8.ToArray(), []);
        var plus = builder.Build("Plus");
        Array.Clear(plus, plus.Length - 1024, 512);                          // the alternate volume header lost
        var path = Image(("One", "Apple_HFS", HfsVolume("One")), ("Plus", "Apple_HFS", plus));
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsPlusVolume, session.KindOf("Plus"));

        Assert.Throws<InvalidOperationException>(() => session.AddFolder("Plus:New"));
        Assert.Equal(FirstAidVerdict.AppearsOk, session.Repair("One").After.Verdict);
        var repaired = session.Repair("Plus");

        Assert.True(repaired.Written);
        Assert.All(session.Changes, c => Assert.StartsWith("Plus", c.Path, StringComparison.Ordinal));
        var target = Path.Combine(directory, "out.img");
        session.SaveAs(target);
        var partition = PartitionMapReader.Partitions(ForkData.FromBytes(File.ReadAllBytes(target)))[1];
        Assert.Equal(FirstAidVerdict.AppearsOk,
            HfsFirstAid.Verify(ForkData.FromBytes(File.ReadAllBytes(target).AsSpan((int)partition.Offset, (int)partition.Length).ToArray())).Verdict);
    }
}
