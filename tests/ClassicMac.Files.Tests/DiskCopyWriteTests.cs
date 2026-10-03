using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

// Writing the HFS volume in a Disk Copy 4.2 image (docs/formats/disk-images/diskcopy42.md §3): the disk edited in place,
// the data checksum made again, the header's other fields and the tags kept.
public sealed class DiskCopyWriteTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-dc42").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static byte[] Volume()
    {
        var image = HfsWriter.Format(800 * 1024, "Floppy");
        return HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
    }

    [Fact]
    public void The_disk_is_edited_and_the_image_keeps_its_header_and_tags_with_a_new_checksum()
    {
        var path = Path.Combine(directory, "floppy.image");
        File.WriteAllBytes(path, DiskCopy42("Floppy", Volume(), withTags: true));
        var original = File.ReadAllBytes(path);
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);

        session.Delete("Read Me");
        session.AddFolder("Docs");
        var target = Path.Combine(directory, "out.image");
        session.SaveAs(target);

        var written = File.ReadAllBytes(target);
        Assert.Equal(original.Length, written.Length);
        Assert.Equal(original.AsSpan(0, 0x48).ToArray(), written.AsSpan(0, 0x48).ToArray());          // name, sizes
        Assert.Equal(original.AsSpan(0x4C).Slice(0, 8).ToArray(), written.AsSpan(0x4C, 8).ToArray());  // tag checksum, formats
        Assert.Equal(original.AsSpan(0x54 + 800 * 1024).ToArray(), written.AsSpan(0x54 + 800 * 1024).ToArray());   // the tags
        var diagnostics = new List<Diagnostic>();
        var disk = DiskCopy42Reader.Instance.Read(ForkData.FromBytes(written), new ContainerContext(diagnostics: diagnostics)).Single();
        Assert.Empty(diagnostics);                                                                     // the checksums agree
        Assert.Contains(HfsReader.Instance.ReadFolders(disk.DataFork, new ContainerContext()), f => f.MacPath == "Docs");
        Assert.Empty(HfsReader.Instance.Read(disk.DataFork, new ContainerContext()));
        Assert.Null(HfsWriter.Check(disk.DataFork));
    }

    [Fact]
    public void A_Disk_Copy_image_is_not_resized()
    {
        var path = Path.Combine(directory, "floppy.image");
        File.WriteAllBytes(path, DiskCopy42("Floppy", Volume()));

        Assert.Throws<InvalidOperationException>(() => InputEditSession.Open(path).Resize(1440 * 1024));
    }
}
