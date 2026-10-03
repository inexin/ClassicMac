using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.Format (docs/formats/file-systems/hfs.md §3.1): a new, empty HFS volume that the reader, the writer's checks
// and the writer's edits all take.
public sealed class HfsFormatTests
{
    private const int Mdb = 1024;

    private static int U16(byte[] image, int offset) => BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(offset));

    private static uint U32(byte[] image, int offset) => BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(offset));

    [Theory]
    [InlineData(400 * 1024, 512)]
    [InlineData(800 * 1024, 512)]
    [InlineData(20 * 1024 * 1024, 512)]
    [InlineData(100 * 1024 * 1024, 2048)]
    [InlineData(500 * 1024 * 1024, 8192)]                                              // as Mac OS 9.hfv, made by Mac OS
    public void A_new_volume_is_empty_sound_and_uses_the_smallest_block_size_that_fits(int size, int blockSize)
    {
        var image = HfsWriter.Format(size, "Untitled");

        Assert.Equal(size, image.Length);
        Assert.Equal(0x4244, U16(image, Mdb));
        Assert.Equal((uint)blockSize, U32(image, Mdb + 0x14));                         // drAlBlkSiz
        Assert.True(U16(image, Mdb + 0x12) <= 65535);
        var info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal(("Untitled", 0L, 0L), (info.Name, info.Files, info.Folders));
        Assert.True(info.FreeBlocks > info.TotalBlocks * 9 / 10);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics)));
        Assert.Empty(diagnostics);
        Assert.Equal(image.AsSpan(Mdb, 162).ToArray(), image.AsSpan(size - 1024, 162).ToArray());   // the alternate MDB
    }

    [Fact]
    public void The_root_folder_has_the_volume_s_name_and_the_dates_given()
    {
        var date = new MacDate(3_100_000_000);
        var image = HfsWriter.Format(800 * 1024, "Blank", date);

        var root = HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext()).Single(f => f.IsRoot);
        Assert.Equal(("Blank", date, date), (root.Name.ToMacRoman(), root.Created, root.Modified));
        var info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal((date, date), (info.Created, info.Modified));
        Assert.Equal(16u, U32(image, Mdb + 0x1E));                                     // drNxtCNID
    }

    [Fact]
    public void A_new_volume_takes_files_and_folders_and_grows_its_catalog()
    {
        var image = HfsWriter.Format(2 * 1024 * 1024, "Disk");
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "Docs");
        for (var i = 0; i < 150; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Docs:File {i:D3}", new byte[1000], Array.Empty<byte>(), FinderInfo.Empty);
        }

        Assert.Equal(150, HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()).Count);
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
    }

    [Theory]
    [InlineData(100 * 1024, "Disk")]                                                    // too small
    [InlineData(800 * 1024 + 100, "Disk")]                                              // not whole 512-byte blocks
    [InlineData(800 * 1024, "")]                                                        // no name
    [InlineData(800 * 1024, "A:B")]                                                     // a colon
    [InlineData(800 * 1024, "A name that is longer than twenty-seven")]                 // over 27 bytes
    public void A_size_or_name_HFS_cannot_hold_is_refused(long size, string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => HfsWriter.Format(size, name));
    }
}
