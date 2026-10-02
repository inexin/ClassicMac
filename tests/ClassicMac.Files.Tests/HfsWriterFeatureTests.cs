using System.Buffers.Binary;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

public sealed class HfsWriterFeatureTests
{
    [Fact]
    public void ForkReplacementUsesClassicHfsCaseInsensitivePaths()
    {
        var builder = new HfsBuilder();
        uint folder = builder.Folder(HfsBuilder.Root, "Documents");
        builder.File(folder, "Report", "old"u8.ToArray(), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");

        byte[] result = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "documents:report",
            HfsFork.Data, "new"u8.ToArray());

        Assert.Equal("new"u8.ToArray(), Assert.Single(
            HfsReader.Instance.Read(ForkData.FromBytes(result), new ContainerContext()),
            file => file.MacPath == "Documents:Report").DataFork.ToArray());
    }

    [Fact]
    public void ReplacingEmptyResourceForkPreservesDataAndOtherFile()
    {
        byte[] data = Bytes(700, 1);
        byte[] otherData = Bytes(900, 2);
        byte[] otherResource = Bytes(200, 3);
        byte[] resource = Bytes(300, 4);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", data, Array.Empty<byte>(), type: "TEXT", creator: "ttxt");
        builder.File(HfsBuilder.Root, "Other", otherData, otherResource);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Resource, resource);

        Assert.Equal(original, source);
        Assert.Equal(data, File(output, "Target").DataFork.ToArray());
        Assert.Equal(resource, File(output, "Target").ResourceFork.ToArray());
        Assert.Equal(otherData, File(output, "Other").DataFork.ToArray());
        Assert.Equal(otherResource, File(output, "Other").ResourceFork.ToArray());
        AssertFinderInfoUnchanged(source, output, "Target");
    }

    [Fact]
    public void ClearingDataForkReleasesBlocksAndPreservesResourceFork()
    {
        byte[] resource = Bytes(330, 5);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(1100, 6), resource);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        int freeBefore = FreeBlocks(source);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, Array.Empty<byte>());

        Assert.Equal(original, source);
        Assert.Empty(File(output, "Target").DataFork.ToArray());
        Assert.Equal(resource, File(output, "Target").ResourceFork.ToArray());
        Assert.Equal(freeBefore + 3, FreeBlocks(output));
    }

    [Fact]
    public void GrowingPastThreeExtentsKeepsBothFilesReadable()
    {
        byte[] oldResource = Bytes(180, 7);
        byte[] otherData = Bytes(640, 8);
        byte[] otherResource = Bytes(270, 9);
        byte[] replacement = Bytes(5 * HfsBuilder.Block, 10);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(3 * HfsBuilder.Block, 11), oldResource, fragments: 3);
        builder.File(HfsBuilder.Root, "Other", otherData, otherResource);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, replacement);

        Assert.Equal(original, source);
        Assert.Equal(replacement, File(output, "Target").DataFork.ToArray());
        Assert.Equal(oldResource, File(output, "Target").ResourceFork.ToArray());
        Assert.Equal(otherData, File(output, "Other").DataFork.ToArray());
        Assert.Equal(otherResource, File(output, "Other").ResourceFork.ToArray());
        Assert.Equal(FreeBlocks(source) - 2, FreeBlocks(output));
    }

    [Fact]
    public void ClearingFragmentedForkReclaimsOverflowExtents()
    {
        byte[] resource = Bytes(260, 22);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(5 * HfsBuilder.Block, 23), resource, fragments: 5);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        int freeBefore = FreeBlocks(source);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, Array.Empty<byte>());

        Assert.Equal(original, source);
        Assert.Empty(File(output, "Target").DataFork.ToArray());
        Assert.Equal(resource, File(output, "Target").ResourceFork.ToArray());
        Assert.Equal(freeBefore + 5, FreeBlocks(output));
        AssertBitmapMatchesFreeCount(output);
        AssertEmptyExtentsTree(output);
    }

    [Fact]
    public void ShrinkingIntoCatalogExtentsRemovesTheLastOverflowRecord()
    {
        byte[] replacement = Bytes(3 * HfsBuilder.Block, 24);
        byte[] resource = Bytes(200, 25);
        byte[] otherData = Bytes(700, 26);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(5 * HfsBuilder.Block, 27), resource, fragments: 5);
        builder.File(HfsBuilder.Root, "Other", otherData, Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        int freeBefore = FreeBlocks(source);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, replacement);

        Assert.Equal(replacement, File(output, "Target").DataFork.ToArray());
        Assert.Equal(resource, File(output, "Target").ResourceFork.ToArray());
        Assert.Equal(otherData, File(output, "Other").DataFork.ToArray());
        Assert.Equal(freeBefore + 2, FreeBlocks(output));
        AssertBitmapMatchesFreeCount(output);
        AssertEmptyExtentsTree(output);
    }

    [Fact]
    public void RepeatedInterleavedEditsPreserveEveryForkAcrossReopens()
    {
        byte[] firstData = Bytes(6 * HfsBuilder.Block, 28);
        byte[] firstResource = Bytes(2 * HfsBuilder.Block + 11, 29);
        byte[] secondData = Bytes(7 * HfsBuilder.Block, 30);
        byte[] secondResource = Bytes(2 * HfsBuilder.Block + 19, 31);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "First", Bytes(4 * HfsBuilder.Block, 32), Bytes(100, 33), fragments: 4);
        builder.File(HfsBuilder.Root, "Second", Bytes(4 * HfsBuilder.Block, 34), Bytes(100, 35), fragments: 4);
        byte[] source = builder.Build("Volume");

        byte[] image = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "First", HfsFork.Data, firstData);
        image = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Second", HfsFork.Resource, secondResource);
        image = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "First", HfsFork.Resource, firstResource);
        image = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Second", HfsFork.Data, secondData);

        Assert.Equal(firstData, File(image, "First").DataFork.ToArray());
        Assert.Equal(firstResource, File(image, "First").ResourceFork.ToArray());
        Assert.Equal(secondData, File(image, "Second").DataFork.ToArray());
        Assert.Equal(secondResource, File(image, "Second").ResourceFork.ToArray());
        AssertBitmapMatchesFreeCount(image);
    }

    [Theory]
    [InlineData(36)]
    [InlineData(3)]
    public void ShrinkingAcrossMultipleOverflowLeavesKeepsTheTreeReadable(int remainingBlocks)
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 4 };
        builder.File(HfsBuilder.Root, "Fragmented", Bytes(72 * HfsBuilder.Block, 36), Bytes(150, 37), fragments: 72);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        byte[] replacement = Bytes(remainingBlocks * HfsBuilder.Block, 38);
        int freeBefore = FreeBlocks(source);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Fragmented", HfsFork.Data, replacement);

        Assert.Equal(original, source);
        Assert.Equal(replacement, File(output, "Fragmented").DataFork.ToArray());
        Assert.Equal(Bytes(150, 37), File(output, "Fragmented").ResourceFork.ToArray());
        Assert.Equal(freeBefore + 72 - remainingBlocks, FreeBlocks(output));
        AssertBitmapMatchesFreeCount(output);
        if (remainingBlocks == 3)
        {
            AssertEmptyExtentsTree(output);
        }
    }

    [Fact]
    public void EarlierFileCanGainOverflowExtentsAfterAnotherFileHasIndexedTheTree()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 4 };
        byte[] laterData = Bytes(72 * HfsBuilder.Block, 39);
        builder.File(HfsBuilder.Root, "Earlier", Bytes(3 * HfsBuilder.Block, 40), Array.Empty<byte>(), fragments: 3);
        builder.File(HfsBuilder.Root, "Later", laterData, Array.Empty<byte>(), fragments: 72);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        byte[] replacement = Bytes(5 * HfsBuilder.Block, 41);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Earlier", HfsFork.Data, replacement);

        Assert.Equal(original, source);
        Assert.Equal(replacement, File(output, "Earlier").DataFork.ToArray());
        Assert.Equal(laterData, File(output, "Later").DataFork.ToArray());
        Assert.Equal(FreeBlocks(source) - 2, FreeBlocks(output));
        AssertBitmapMatchesFreeCount(output);
    }

    [Fact]
    public void GrowingAFragmentedForkCanExpandAFullExtentsTree()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 2 };
        builder.File(HfsBuilder.Root, "Target", Bytes(66 * HfsBuilder.Block, 42), Array.Empty<byte>(), fragments: 66);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        byte[] replacement = Bytes(71 * HfsBuilder.Block, 43);

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, replacement);

        Assert.Equal(original, source);
        Assert.Equal(replacement, File(output, "Target").DataFork.ToArray());
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(2 * HfsBuilder.Block + 0x82, 4)) >
            BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(2 * HfsBuilder.Block + 0x82, 4)));
        Assert.Equal(FreeBlocks(source) - 7, FreeBlocks(output));
        AssertBitmapMatchesFreeCount(output);
    }

    [Fact]
    public void ExtentsTreeGrowthUpdatesAnExistingAlternateMdb()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 2 };
        builder.File(HfsBuilder.Root, "Target", Bytes(66 * HfsBuilder.Block, 44), Array.Empty<byte>(), fragments: 66);
        byte[] source = builder.Build("Volume");
        Array.Resize(ref source, source.Length + 2 * HfsBuilder.Block);
        source.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block)
            .CopyTo(source.AsSpan(source.Length - 2 * HfsBuilder.Block));
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data,
            Bytes(71 * HfsBuilder.Block, 45));

        Assert.Equal(original, source);
        Assert.Equal(output.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).ToArray(),
            output.AsSpan(output.Length - 2 * HfsBuilder.Block, HfsBuilder.Block).ToArray());
    }

    [Fact]
    public void EditsToBothForksComposeAcrossSavedImages()
    {
        byte[] newData = Bytes(840, 12);
        byte[] newResource = Bytes(610, 13);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(300, 14), Bytes(100, 15), type: "TEXT", creator: "ttxt");
        builder.File(HfsBuilder.Root, "Other", Bytes(120, 16), Bytes(70, 17));
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] first = HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Resource, newResource);
        byte[] second = HfsWriter.ReplaceFork(ForkData.FromBytes(first), "Target", HfsFork.Data, newData);

        Assert.Equal(original, source);
        Assert.Equal(newResource, File(first, "Target").ResourceFork.ToArray());
        Assert.Equal(newData, File(second, "Target").DataFork.ToArray());
        Assert.Equal(newResource, File(second, "Target").ResourceFork.ToArray());
        Assert.Equal(File(source, "Other").DataFork.ToArray(), File(second, "Other").DataFork.ToArray());
        Assert.Equal(File(source, "Other").ResourceFork.ToArray(), File(second, "Other").ResourceFork.ToArray());
        AssertFinderInfoUnchanged(source, second, "Target");
        Assert.Equal(File(source, "Target").Created, File(second, "Target").Created);
    }

    [Fact]
    public void MissingFileFailsWithoutChangingSource()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Present", Bytes(50, 18), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() =>
            HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Missing", HfsFork.Data, Bytes(80, 19)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void SoftwareLockedVolumeRejectsEditsWithoutChangingSource()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(80, 20), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        const int mdbOffset = 2 * HfsBuilder.Block;
        const int volumeAttributesOffset = 0x0A;
        ushort attributes = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(mdbOffset + volumeAttributesOffset, 2));
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(mdbOffset + volumeAttributesOffset, 2), (ushort)(attributes | 0x8000));
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() =>
            HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, Bytes(90, 21)));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedHfsPlusWrapperAndNonHfsInputRejectForkEdits(bool hfsPlusWrapper)
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(40, 31), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        if (hfsPlusWrapper)
        {
            BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2 * HfsBuilder.Block + 0x7C), 0x482B);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2 * HfsBuilder.Block), 0x0000);
        }

        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() =>
            HfsWriter.ReplaceFork(ForkData.FromBytes(source), "Target", HfsFork.Data, Bytes(60, 32)));
        Assert.Equal(original, source);
    }

    private static int FreeBlocks(byte[] image) =>
        BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22, 2));

    private static void AssertBitmapMatchesFreeCount(byte[] image)
    {
        const int mdb = 2 * HfsBuilder.Block;
        int bitmapBlock = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdb + 0x0E, 2));
        int allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdb + 0x12, 2));
        int free = 0;
        for (int block = 0; block < allocationBlocks; block++)
        {
            int bitmapByte = image[bitmapBlock * HfsBuilder.Block + block / 8];
            if ((bitmapByte & (0x80 >> (block % 8))) == 0)
            {
                free++;
            }
        }

        Assert.Equal(FreeBlocks(image), free);
    }

    private static void AssertEmptyExtentsTree(byte[] image)
    {
        int header = HfsBuilder.FirstAllocationBlock * HfsBuilder.Block + 14;
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(header, 2))); // depth
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(header + 2, 4))); // root
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(header + 6, 4))); // leaf records
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(header + 10, 4))); // first leaf
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(header + 14, 4))); // last leaf
    }

    private static void AssertFinderInfoUnchanged(byte[] beforeImage, byte[] afterImage, string path)
    {
        var before = File(beforeImage, path).FinderInfo;
        var after = File(afterImage, path).FinderInfo;
        Assert.Equal(before.Type, after.Type);
        Assert.Equal(before.Creator, after.Creator);
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Location, after.Location);
        Assert.Equal(before.Folder, after.Folder);
        Assert.Equal(before.Extended.ToArray(), after.Extended.ToArray());
    }

    private static MacFile File(byte[] image, string path) =>
        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()),
            file => file.MacPath == path);

    private static byte[] Bytes(int length, int seed) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 37 + seed)).ToArray();
}
