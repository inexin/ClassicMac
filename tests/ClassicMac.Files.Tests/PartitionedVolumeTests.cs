using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
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

    [Fact]
    public void An_image_with_two_HFS_partitions_is_not_written()
    {
        var path = Image(("One", "Apple_HFS", HfsVolume("One")), ("Two", "Apple_HFS", HfsVolume("Two")));

        Assert.Equal(InputEditKind.ReadOnly, InputEditSession.Open(path).Kind);
    }
}
