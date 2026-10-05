using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.NdifBuilder;

namespace ClassicMac.Files.Tests;

// Editing the HFS disk in an NDIF image (docs/formats/disk-images/ndif.md §3, §5): the disk is edited as a volume and
// the image made again around it, saved in the input's own layout.
public sealed class NdifSessionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-ndif-edit").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static MacFile Image()
    {
        var volume = HfsWriter.Format(400 * 1024, "Test Disk");
        volume = HfsWriter.CreateFile(ForkData.FromBytes(volume), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        var (data, resource) = Build(volume, "Test Disk", (100, Kind.Adc), (700, Kind.Raw));
        return new MacFile
        {
            Name = MacString.FromMacRoman("Test.img"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("rohd"), Creator = FourCC.FromString("ddsk") },
            DataFork = ForkData.FromBytes(data),
            ResourceFork = ForkData.FromBytes(resource),
        };
    }

    // The disk an image file holds, read as the CLI reads it.
    private static ForkData Disk(string path)
    {
        var host = HostFiles.Read(path);
        var file = NdifReader.Instance.CanRead(host.File) ? host.File
            : MacBinaryReader.III.Read(host.File.DataFork, new ContainerContext()).Single();
        return NdifReader.Instance.Read(file, new ContainerContext()).Single().DataFork;
    }

    private static IReadOnlyList<MacFolder> Folders(ForkData disk) => HfsReader.Instance.ReadFolders(disk, new ContainerContext());

    [Fact]
    public void An_AppleDouble_NDIF_image_is_edited_and_saved_as_a_new_pair_and_in_place()
    {
        HostFiles.Write(Image(), directory, new HostWriteOptions { Layout = HostLayout.AppleDouble });
        var path = Path.Combine(directory, "Test.img");
        var original = (File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(directory, "._Test.img")));
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);

        session.Delete("Read Me");
        session.AddFolder("Docs");
        var target = Path.Combine(directory, "out", "Edited.img");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var written = session.SaveAs(target);

        Assert.Contains(target, written);
        Assert.Contains(Folders(Disk(target)), f => f.MacPath == "Docs");
        Assert.Empty(HfsReader.Instance.Read(Disk(target), new ContainerContext()));
        Assert.Equal("rohd", HostFiles.Read(target).File.FinderInfo.Type.ToString());
        Assert.Equal(original.Item1, File.ReadAllBytes(path));                                   // the input never changes
        Assert.Equal(original.Item2, File.ReadAllBytes(Path.Combine(directory, "._Test.img")));

        session.SaveInPlace();
        Assert.Contains(Folders(Disk(path)), f => f.MacPath == "Docs");
        Assert.Equal(original.Item1, File.ReadAllBytes(path + ".orig"));
        Assert.Null(HfsWriter.Check(Disk(path)));
    }

    [Fact]
    public void A_MacBinary_NDIF_image_is_saved_as_MacBinary()
    {
        var path = Path.Combine(directory, "Test.img.bin");
        File.WriteAllBytes(path, MacBinaryWriter.ToArray(Image()));
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);

        session.AddFolder("Docs");
        var target = Path.Combine(directory, "Edited.img.bin");
        session.SaveAs(target);

        Assert.Contains(Folders(Disk(target)), f => f.MacPath == "Docs");
        Assert.Throws<InvalidOperationException>(() => session.Resize(800 * 1024));
    }

    // The session's disk is the image's disk, also when the image's data fork starts with the disk's raw boot blocks and
    // MDB (read-only and compressed images), which once made it look like a plain volume.
    [Theory]
    [InlineData(NdifFormat.ReadOnly)]
    [InlineData(NdifFormat.Adc)]
    [InlineData(NdifFormat.ReadWrite)]
    public void The_session_reads_the_disk_of_every_kind_of_image(NdifFormat format)
    {
        var volume = HfsWriter.Format(400 * 1024, "Test Disk");
        volume = HfsWriter.CreateFile(ForkData.FromBytes(volume), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        HostFiles.Write(NdifWriter.Create(volume, "Kind.img", NdifCreateOptions.Default with { Format = format }), directory,
            new HostWriteOptions { Layout = HostLayout.AppleDouble });

        var session = InputEditSession.Open(Path.Combine(directory, "Kind.img"));

        Assert.Equal(InputEditKind.HfsVolume, session.Kind);
        Assert.Equal(volume, session.Volume);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(session.Volume)));
    }
}
