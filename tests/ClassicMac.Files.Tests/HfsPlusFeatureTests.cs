using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

public sealed class HfsPlusFeatureTests
{
    [Fact]
    public void PlainHfsPlusVolumeListsNestedFileWithBothForksAndMetadata()
    {
        byte[] image = HfsPlusFixture.Build();
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFSPLUS_SYNTHETIC_OUTPUT") is { Length: > 0 } output)
            System.IO.File.WriteAllBytes(output, image);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("Resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void HfsPlusVolumeWithoutACatalogTreeIsRejected()
    {
        byte[] image = HfsPlusFixture.Build();
        Array.Clear(image, 2 * 4096, 4096);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusUnicodeNameIsPreservedInTheMacPath()
    {
        byte[] image = HfsPlusFixture.Build("文件");

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:文件", file.MacPath);
    }

    [Fact]
    public void CaseSensitiveHfsXVolumeListsItsFiles()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: true);

        Assert.True(HfsReader.Instance.CanRead(ForkData.FromBytes(image)));
        Assert.Equal("Documents:Read Me",
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsWrapperReadsTheEmbeddedHfsPlusVolume()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("Resource fork"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void HfsWrapperRejectsEmbeddedVolumeExtentPastTheImage()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x80), ushort.MaxValue);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsWrapperRejectsEmbeddedVolumeExtentPastTheDeclaredAllocationArea()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x12), 10);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsWrapperRejectsAnEmbeddedVolumeWithAnUnknownSignature()
    {
        byte[] image = HfsPlusFixture.BuildWrapped(invalidEmbeddedSignature: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void FragmentedHfsPlusForkReadsItsNinthExtentFromTheOverflowTree()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        byte[] expected = Enumerable.Range(1, 9).SelectMany(index => Enumerable.Repeat((byte)index, 4096)).ToArray();
        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Equal("Resource fork"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void OutOfRangeHfsPlusExtentIsRejected()
    {
        byte[] image = HfsPlusFixture.Build(invalidDataExtent: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void CyclicHfsPlusCatalogLeafChainIsRejected()
    {
        byte[] image = HfsPlusFixture.Build(cyclicCatalog: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void UnknownHfsPlusCatalogRecordKindIsRejectedInsteadOfSilentlyOmitted()
    {
        byte[] image = HfsPlusFixture.Build(unknownCatalogRecord: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    // A small HFS+ volume built from the structures in TN1150: 4 KiB allocation and B-tree nodes,
    // one catalog leaf, a root folder, nested folder, file and their threads.
    private static class HfsPlusFixture
    {
        private const int Block = 4096;

        public static byte[] Build(string fileName = "Read Me", bool hfsX = false, bool fragmentedData = false,
            bool invalidDataExtent = false, bool cyclicCatalog = false, bool unknownCatalogRecord = false)
        {
            byte[] image = new byte[(fragmentedData ? 32 : 16) * Block];
            Span<byte> volume = image.AsSpan(1024, 512);
            U16(volume, 0, hfsX ? (ushort)0x4858 : (ushort)0x482B);
            U16(volume, 2, hfsX ? (ushort)5 : (ushort)4);
            U32(volume, 32, 1); // fileCount
            U32(volume, 36, 1); // folderCount excludes root
            U32(volume, 40, Block);
            U32(volume, 44, fragmentedData ? 32u : 16u);
            U32(volume, 48, fragmentedData ? 19u : 10u);
            Fork(volume.Slice(272, 80), 2 * Block, 2, 2);
            if (fragmentedData) Fork(volume.Slice(192, 80), 2 * Block, 4, 2);

            byte[] root = new byte[88];
            U16(root, 0, 1);
            U32(root, 4, 1);
            U32(root, 8, 2);
            byte[] folder = new byte[88];
            U16(folder, 0, 1);
            U32(folder, 4, 1);
            U32(folder, 8, 16);
            byte[] file = new byte[248];
            U16(file, 0, 2);
            U16(file, 2, 2); // file thread exists
            U32(file, 8, 17);
            U32(file, 12, 2_500_000_000);
            U32(file, 16, 2_600_000_000);
            "TEXTttxt"u8.CopyTo(file.AsSpan(48));
            if (fragmentedData)
            {
                BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(88), 9 * Block);
                U32(file, 88 + 12, 9);
                for (int index = 0; index < 8; index++)
                {
                    U32(file, 88 + 16 + index * 8, checked((uint)(6 + index * 2)));
                    U32(file, 88 + 20 + index * 8, 1);
                }
                for (int index = 0; index < 9; index++)
                    image.AsSpan((6 + index * 2) * Block, Block).Fill(checked((byte)(index + 1)));
                Fork(file.AsSpan(168, 80), "Resource fork"u8.Length, 25, 1);
                "Resource fork"u8.CopyTo(image.AsSpan(25 * Block));
                WriteExtentsTree(image);
            }
            else
            {
                Fork(file.AsSpan(88, 80), "HFS Plus data"u8.Length, invalidDataExtent ? 16u : 4u, 1);
                Fork(file.AsSpan(168, 80), "Resource fork"u8.Length, 5, 1);
                "HFS Plus data"u8.CopyTo(image.AsSpan(4 * Block));
                "Resource fork"u8.CopyTo(image.AsSpan(5 * Block));
            }

            byte[] leaf = image.AsSpan(3 * Block, Block).ToArray();
            if (cyclicCatalog) U32(leaf, 0, 1);
            leaf[8] = 0xFF;
            leaf[9] = 1;
            var records = new[]
            {
                Record(1, "Volume", root),
                Record(2, "", Thread(1, "Volume", 3)),
                Record(2, "Documents", folder),
                Record(16, "", Thread(2, "Documents", 3)),
                Record(16, fileName, file),
                Record(17, "", Thread(16, fileName, 4)),
            };
            if (unknownCatalogRecord) U16(records[4], 0, 0x1234);
            U16(leaf, 10, (ushort)records.Length);
            int at = 14;
            for (int index = 0; index < records.Length; index++)
            {
                U16(leaf, Block - 2 * (index + 1), (ushort)at);
                records[index].CopyTo(leaf, at);
                at += records[index].Length;
            }
            U16(leaf, Block - 2 * (records.Length + 1), (ushort)at);
            leaf.CopyTo(image, 3 * Block);

            Span<byte> header = image.AsSpan(2 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 1); // depth
            U32(header, 16, 1); // root node
            U32(header, 20, (uint)records.Length);
            U32(header, 24, 1); // first leaf
            U32(header, 28, 1); // last leaf
            U16(header, 32, Block);
            U16(header, 34, 516);
            U32(header, 36, 2); // total nodes
            header[14 + 106 + 128] = 0xC0; // both nodes in use
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
            return image;
        }

        public static byte[] BuildWrapped(bool invalidEmbeddedSignature = false)
        {
            byte[] embedded = Build();
            const int embeddedOffset = 6 * 512;
            if (invalidEmbeddedSignature) U16(embedded.AsSpan(1024), 0, 0x1234);
            byte[] wrapper = new byte[embeddedOffset + embedded.Length];
            embedded.CopyTo(wrapper, embeddedOffset);

            Span<byte> mdb = wrapper.AsSpan(1024, 162);
            U16(mdb, 0, 0x4244); // HFS master directory block signature
            U32(mdb, 0x14, 512); // allocation block size
            U16(mdb, 0x1C, 2); // drAlBlSt is measured in 512-byte blocks
            U16(mdb, 0x12, checked((ushort)(wrapper.Length / 512 - 2)));
            U16(mdb, 0x7C, 0x482B); // drEmbedSigWord: HFS Plus
            U16(mdb, 0x7E, 4); // embedded volume starts at allocation block 4
            U16(mdb, 0x80, checked((ushort)(embedded.Length / 512)));
            return wrapper;
        }

        private static void WriteExtentsTree(byte[] image)
        {
            Span<byte> leaf = image.AsSpan(5 * Block, Block);
            leaf[8] = 0xFF;
            leaf[9] = 1;
            U16(leaf, 10, 1);
            U16(leaf, Block - 2, 14);
            U16(leaf, Block - 4, 90);
            U16(leaf, 14, 10); // key length excludes this field
            U32(leaf, 18, 17); // file ID
            U32(leaf, 22, 8); // first block after the eight primary extents
            U32(leaf, 26, 22); // ninth extent
            U32(leaf, 30, 1);

            Span<byte> header = image.AsSpan(4 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 1);
            U32(header, 16, 1);
            U32(header, 20, 1);
            U32(header, 24, 1);
            U32(header, 28, 1);
            U16(header, 32, Block);
            U16(header, 34, 10);
            U32(header, 36, 2);
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static byte[] Record(uint parent, string name, byte[] data)
        {
            byte[] chars = Encoding.BigEndianUnicode.GetBytes(name);
            byte[] record = new byte[8 + chars.Length + data.Length];
            U16(record, 0, checked((ushort)(6 + chars.Length)));
            U32(record, 2, parent);
            U16(record, 6, checked((ushort)name.Length));
            chars.CopyTo(record, 8);
            data.CopyTo(record, 8 + chars.Length);
            return record;
        }

        private static byte[] Thread(uint parent, string name, ushort kind)
        {
            byte[] chars = Encoding.BigEndianUnicode.GetBytes(name);
            byte[] record = new byte[10 + chars.Length];
            U16(record, 0, kind);
            U32(record, 4, parent);
            U16(record, 8, checked((ushort)name.Length));
            chars.CopyTo(record, 10);
            return record;
        }

        private static void Fork(Span<byte> destination, int logicalSize, uint startBlock, uint blockCount)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination, checked((ulong)logicalSize));
            U32(destination, 12, blockCount);
            U32(destination, 16, startBlock);
            U32(destination, 20, blockCount);
        }

        private static void U16(Span<byte> bytes, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

        private static void U32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }
}
