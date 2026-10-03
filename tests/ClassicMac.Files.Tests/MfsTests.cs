using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

public class MfsTests
{
    private const int Block = 512;
    private const int DirectoryStart = 4;
    private const int FirstAllocationBlock = 6; // after the one-block directory... and a spare
    private const int AllocationBlocks = 20;

    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    // An MFS volume byte by byte (Inside Macintosh II): volume information at 1024, the 12-bit map after it, a
    // directory block at block 4, allocation blocks (512 bytes, numbered from 2) from block 6. Each fork is a chain of
    // the given allocation block numbers.
    private static byte[] Volume(params (string Name, string Type, byte[] Data, int[] DataBlocks, byte[] Resource, int[] ResourceBlocks)[] files)
    {
        var image = new byte[(FirstAllocationBlock + AllocationBlocks) * Block];
        var info = image.AsSpan(1024);
        BinaryPrimitives.WriteUInt16BigEndian(info, 0xD2D7);
        BinaryPrimitives.WriteUInt32BigEndian(info[0x02..], 2_526_595_200);
        BinaryPrimitives.WriteUInt16BigEndian(info[0x0C..], (ushort)files.Length);
        BinaryPrimitives.WriteUInt16BigEndian(info[0x0E..], DirectoryStart);
        BinaryPrimitives.WriteUInt16BigEndian(info[0x10..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(info[0x12..], AllocationBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(info[0x14..], Block);
        BinaryPrimitives.WriteUInt16BigEndian(info[0x1C..], FirstAllocationBlock);
        info[0x24] = 4;
        Encoding.ASCII.GetBytes("Disk").CopyTo(image, 1024 + 0x25);

        void Place(byte[] fork, int[] chain)
        {
            for (var i = 0; i < chain.Length; i++)
            {
                var offset = (FirstAllocationBlock + chain[i] - 2) * Block;
                fork.AsSpan(i * Block, Math.Min(Block, fork.Length - i * Block)).CopyTo(image.AsSpan(offset));
                SetMapEntry(image, chain[i], i + 1 < chain.Length ? chain[i + 1] : 1);
            }
        }

        var at = DirectoryStart * Block;
        uint number = 1;
        foreach (var (name, type, data, dataBlocks, resource, resourceBlocks) in files)
        {
            var e = image.AsSpan(at);
            e[0] = 0x80;
            Encoding.ASCII.GetBytes(type).CopyTo(e[2..]);
            Encoding.ASCII.GetBytes("MACA").CopyTo(e[6..]);
            BinaryPrimitives.WriteUInt32BigEndian(e[18..], number++);
            BinaryPrimitives.WriteUInt16BigEndian(e[22..], (ushort)(dataBlocks.Length > 0 ? dataBlocks[0] : 0));
            BinaryPrimitives.WriteUInt32BigEndian(e[24..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16BigEndian(e[32..], (ushort)(resourceBlocks.Length > 0 ? resourceBlocks[0] : 0));
            BinaryPrimitives.WriteUInt32BigEndian(e[34..], (uint)resource.Length);
            BinaryPrimitives.WriteUInt32BigEndian(e[42..], 2_526_595_200);
            e[50] = (byte)name.Length;
            Encoding.ASCII.GetBytes(name).CopyTo(e[51..]);
            Place(data, dataBlocks);
            Place(resource, resourceBlocks);
            at += 51 + name.Length + ((51 + name.Length) & 1);
        }
        return image;
    }

    // Sets the 12-bit map entry for allocation block b (numbered from 2).
    private static void SetMapEntry(byte[] image, int b, int value)
    {
        var i = b - 2;
        var at = 1024 + 64 + i * 3 / 2;
        if ((i & 1) == 0)
        {
            image[at] = (byte)(value >> 4);
            image[at + 1] = (byte)((image[at + 1] & 0x0F) | (value & 0x0F) << 4);
        }
        else
        {
            image[at] = (byte)((image[at] & 0xF0) | value >> 8);
            image[at + 1] = (byte)value;
        }
    }

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] image)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(MfsReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (MfsReader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics)), diagnostics);
    }

    [Fact]
    public void The_volume_reports_its_creation_and_backup_dates()
    {
        var image = Volume();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x06), 2_600_000_000);  // drLsBkUp
        var info = MfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal(("MFS", new MacDate(2_526_595_200), (MacDate?)null, new MacDate(2_600_000_000)), (info.Format, info.Created, info.Modified, info.BackedUp));
        Assert.Null(MfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(new byte[4096])));
    }

    [Fact]
    public void The_volume_reports_its_name_space_files_and_locks()
    {
        var image = Volume(("A", "TEXT", [1], [2], [], []));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x22), 7);                     // drFreeBks
        var info = MfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;

        Assert.Equal("Disk", info.Name);
        Assert.Equal((Block, (long)AllocationBlocks, 7L, 7L * Block), (info.BlockSize, info.TotalBlocks, info.FreeBlocks, info.FreeBytes));
        Assert.Equal(1L, info.Files);                                                             // drNmFls
        Assert.Null(info.Folders);                                                                // MFS has no folders
        image[1024 + 0x0A] |= 0x80;
        Assert.True(MfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!.SoftwareLocked);
    }

    [Fact]
    public void Files_follow_their_block_chains()
    {
        var image = Volume(
            ("MacWrite", "APPL", [], [], Bytes(1200, 3), [5, 2, 9]), // resource fork in three scattered blocks
            ("Letter", "WORD", Bytes(300, 7), [3], [], []));

        var (files, diagnostics) = Read(image);

        Assert.Empty(diagnostics);
        Assert.Equal(["MacWrite", "Letter"], files.Select(f => f.Name.ToMacRoman()));
        Assert.Equal(Bytes(1200, 3), files[0].ResourceFork.ToArray());
        Assert.Equal(Bytes(300, 7), files[1].DataFork.ToArray());
        Assert.Equal(FourCC.FromString("WORD"), files[1].FinderInfo.Type);
        Assert.Equal(new DateTime(1984, 1, 24), files[1].Created!.Value.ToDateTime());
        Assert.Empty(files[1].FolderPath);
    }

    // The File Manager's scan: any nonzero flags byte is an entry (bit 7 or not); a zero one ends the block.
    [Fact]
    public void Entries_end_at_a_zero_flags_byte()
    {
        var image = Volume(("First", "TEXT", Bytes(10, 1), [3], [], []), ("Second", "TEXT", Bytes(10, 2), [4], [], []));
        var second = DirectoryStart * Block + 52 + 4; // "First": 51 + 5 name bytes, padded to even
        image[second] = 0x01; // no bit 7: still an entry

        Assert.Equal(["First", "Second"], Read(image).Files.Select(f => f.Name.ToMacRoman()));

        image[second] = 0;
        Assert.Equal(["First"], Read(image).Files.Select(f => f.Name.ToMacRoman()));
    }

    [Fact]
    public void A_looping_chain_is_stopped()
    {
        var image = Volume(("Loop", "TEXT", Bytes(1500, 5), [4, 6, 7], [], []));
        SetMapEntry(image, 6, 4); // block 6 now leads back to block 4

        var (files, diagnostics) = Read(image);

        Assert.Equal(1024, Assert.Single(files).DataFork.Length);
        Assert.Contains(diagnostics, d => d.Code == "mfs.bad-chain");
        Assert.Contains(diagnostics, d => d.Code == "mfs.fork-short");
    }

    [Fact]
    public void MFS_volumes_unwrap()
    {
        var image = Volume(("Note", "TEXT", Bytes(10, 1), [2], [], []));
        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("mfs.dsk"), DataFork = ForkData.FromBytes(image) },
            "host file", new ContainerContext());
        Assert.Equal("MFS volume", Assert.Single(root.Children).Format);
    }
}
