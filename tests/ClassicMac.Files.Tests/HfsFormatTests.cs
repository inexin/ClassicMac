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

    // Mac OS 9.0's initializer (hfs.md §3.1), traced in its code: the allocation block size, the bitmap, the B-tree files
    // and the free count for each size, as the trace's table gives them.
    [Theory]
    [InlineData(400 * 1024, 512, 4, 794, 3072, 782)]
    [InlineData(800 * 1024, 512, 4, 1594, 6144, 1570)]
    [InlineData(1440 * 1024, 512, 4, 2874, 11264, 2830)]
    [InlineData(20 * 1024 * 1024, 512, 13, 40945, 163840, 40305)]
    [InlineData(32 * 1024 * 1024, 1024, 0, 0, 0, 0)]                                    // not the smallest size that fits
    [InlineData(64 * 1024 * 1024, 1536, 0, 0, 0, 0)]
    [InlineData(100 * 1024 * 1024, 2048, 16, 51195, 819200, 50395)]
    [InlineData(500 * 1024 * 1024, 8192, 19, 63998, 1048576, 63742)]                    // as Mac OS 9.hfv, made by Mac OS
    public void A_new_volume_is_laid_out_as_Mac_OS_9_lays_it_out(int size, int blockSize, int allocationStart, int blocks, int treeBytes, int free)
    {
        var image = HfsWriter.Format(size, "Untitled");

        Assert.Equal(size, image.Length);
        Assert.Equal(0x4244, U16(image, Mdb));
        Assert.Equal((uint)blockSize, U32(image, Mdb + 0x14));                         // drAlBlkSiz
        if (blocks != 0)
        {
            Assert.Equal((allocationStart, blocks, free), (U16(image, Mdb + 0x1C), U16(image, Mdb + 0x12), U16(image, Mdb + 0x22)));
            Assert.Equal(((uint)treeBytes, (uint)treeBytes), (U32(image, Mdb + 0x82), U32(image, Mdb + 0x92)));   // drXTFlSize, drCTFlSize
            Assert.Equal(((uint)treeBytes, (uint)treeBytes), (U32(image, Mdb + 0x4A), U32(image, Mdb + 0x4E)));   // the clumps
            var catalog = U16(image, Mdb + 0x1C) * 512 + treeBytes;
            Assert.Equal((uint)treeBytes, U32(image, catalog + 0x2E));                // the header node's clump size
            Assert.Equal((uint)(treeBytes / 512 - 2), U32(image, catalog + 14 + 26)); // bthFree: header and leaf in use
        }

        Assert.Equal(2u, U32(image, Mdb + 0x46));                                       // drWrCnt
        Assert.Equal(0x0100, U16(image, Mdb + 0x0A));                                   // drAtrb
        uint clump = U32(image, Mdb + 0x18);
        Assert.Equal(4 * (uint)blockSize > 1024 * 1024 ? (uint)blockSize : 4 * (uint)blockSize, clump);   // drClpSiz
        var info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal(("Untitled", 0L, 0L), (info.Name, info.Files, info.Folders));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics)));
        Assert.Empty(diagnostics);
        Assert.Equal(image.AsSpan(Mdb, 512).ToArray(), image.AsSpan(size - 1024, 512).ToArray());   // the alternate MDB
    }

    // Past 2 GB the volume is built over zeros, only its MDB, bitmap and B-trees written, and saved by writing those.
    [Fact]
    public void A_volume_past_2_GB_is_laid_out_and_written_without_holding_it()
    {
        const long size = 4L << 30;
        var volume = HfsWriter.FormatVolume(size, "Large");

        Assert.Equal(size, volume.Length);
        Assert.InRange(volume.Sectors.Count * 512L, 1, 8L << 20);                       // a few MB of metadata
        var mdb = new byte[512];
        volume.Read(Mdb, mdb);
        Assert.Equal(66048u, U32(mdb, 0x14));                                           // ((N >> 16) + 1) × 512
        Assert.Null(HfsWriter.Check(volume.AsForkData()));
        var info = HfsReader.Instance.ReadVolumeInfo(volume.AsForkData())!;
        Assert.Equal(("Large", 0L), (info.Name, info.Files));
        var edited = HfsWriter.CreateFile(volume, "Note", new byte[300_000], new byte[100], FinderInfo.Empty);   // edits past 2 GB
        edited = HfsWriter.CreateFolder(edited, "Docs");
        Assert.Null(HfsWriter.Check(edited.AsForkData()));
        Assert.Equal(300_000, HfsReader.Instance.Read(edited.AsForkData(), new ContainerContext()).Single().DataFork.Length);

        var path = Path.Combine(Path.GetTempPath(), $"cm-format-{Guid.NewGuid():N}.img");
        try
        {
            HfsWriter.FormatTo(path, 20 * 1024 * 1024, "Small");
            Assert.Equal(HfsWriter.Format(20 * 1024 * 1024, "Small", HfsReader.Instance.ReadVolumeInfo(ForkData.FromFile(path))!.Created).AsSpan(),
                File.ReadAllBytes(path).AsSpan());
        }
        finally
        {
            ForkData.CloseHostFile(path);
            File.Delete(path);
        }
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
