using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.Check (docs/formats/file-systems/hfs.md §5.5): the checks the writer makes before an edit, run on their own.
public sealed class HfsCheckTests
{
    private const int Mdb = 2 * HfsBuilder.Block;

    private static byte[] Volume(bool fixedIndexKeys = false)
    {
        var builder = new HfsBuilder { CatalogLeaves = fixedIndexKeys ? 6 : 2, FixedIndexKeys = fixedIndexKeys };
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        for (var i = 0; i < (fixedIndexKeys ? 12 : 2); i++)
        {
            builder.File(docs, $"File {i:D2}", [(byte)i], []);
        }

        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        return builder.Build("Disk");
    }

    [Fact]
    public void A_sound_volume_has_no_fault()
    {
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(Volume())));
    }

    [Fact]
    public void A_volume_with_Mac_OS_s_fixed_length_index_keys_has_no_fault()
    {
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(Volume(fixedIndexKeys: true))));
    }

    [Fact]
    public void A_free_block_count_that_disagrees_with_the_bitmap_is_the_fault()
    {
        var image = Volume();
        var free = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x22));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(Mdb + 0x22), (ushort)(free - 1));

        Assert.Equal("The HFS volume free-block count disagrees with its allocation bitmap.", HfsWriter.Check(ForkData.FromBytes(image)));
    }

    [Fact]
    public void A_software_locked_volume_is_checked_all_the_same()
    {
        var image = Volume();
        image[Mdb + 0x0A] |= 0x80;
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));

        var free = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x22));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(Mdb + 0x22), (ushort)(free + 1));
        Assert.NotNull(HfsWriter.Check(ForkData.FromBytes(image)));
    }

    [Fact]
    public void A_damaged_catalog_is_the_fault()
    {
        var image = Volume();
        var blockSize = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(Mdb + 0x14));
        var firstBlock = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x1C)) * HfsBuilder.Block;
        var catalogStart = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x96));
        var header = firstBlock + catalogStart * blockSize;
        image[header + 14 + 2] = 0x7F;                                            // the root node, far past the tree's end

        Assert.NotNull(HfsWriter.Check(ForkData.FromBytes(image)));
    }

    [Fact]
    public void A_truncated_image_is_the_fault()
    {
        var image = Volume().AsSpan(0, Mdb + 0x100).ToArray();
        Assert.NotNull(HfsWriter.Check(ForkData.FromBytes(image)));
    }

    [Fact]
    public void Anything_but_a_plain_HFS_volume_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => HfsWriter.Check(ForkData.FromBytes(new byte[4096])));
    }
}
