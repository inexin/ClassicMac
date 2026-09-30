using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

public sealed class HfsPlusFeatureTests
{
    [Theory]
    [InlineData("Macintosh", "MACINTOSH", 0)]
    [InlineData("αρχείο", "ΑΡΧΕΊΟ", 0)]
    [InlineData("Volume", "volume", 0)]
    [InlineData("ab\u200Cc", "ABC", 0)]
    [InlineData("e\u0301", "e", 1)]
    [InlineData("file", "filex", -1)]
    [InlineData("\u0001a", "a", -1)]
    [InlineData("\0a", "a", 1)]
    public void HfsPlusCaseFoldingUsesFixedUnicodeMappingsAndIgnorables(string left, string right,
        int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(HfsPlusUnicodeComparison.Compare(left, right)));
        Assert.Equal(expectedSign, Math.Sign(HfsPlusUnicodeComparison.CompareBigEndian(
            Encoding.BigEndianUnicode.GetBytes(left), Encoding.BigEndianUnicode.GetBytes(right))));
    }

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
        Assert.Null(file.SymbolicLinkTarget);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxSymbolicLinksExposeTheirUtf8TargetAndKeepTheirDataFork(bool hfsX)
    {
        byte[] target = "../漢字/Read Me"u8.ToArray();
        byte[] image = HfsPlusFixture.BuildSymbolicLink(target, hfsX: hfsX);

        MacFile link = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:Shortcut", link.MacPath);
        Assert.Equal("../漢字/Read Me", link.SymbolicLinkTarget);
        Assert.Equal(target, link.DataFork.ToArray());
        Assert.Empty(link.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("slnk"), link.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("rhap"), link.FinderInfo.Creator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxHardLinksUseTheirIndirectNodeAndHidePrivateData(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(hfsX: hfsX);

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal("shared file data"u8.ToArray(), link.DataFork.ToArray());
        Assert.Equal("shared resource"u8.ToArray(), link.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), link.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), link.FinderInfo.Creator);
        Assert.Equal(123u, link.HardLinkReference);
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, file => file.Name.ToString() == "iNode123");
        Assert.DoesNotContain(files, file => file.MacPath.Contains("HFS+ Private Data", StringComparison.Ordinal));
    }

    [Fact]
    public void HfsPlusHardLinkRejectsTheReservedZeroLinkReference()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(linkReference: 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusHardLinkWithoutAnIndirectNodeIsRetainedAndReported()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(includeIndirectNode: false);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal(123u, link.HardLinkReference);
        Assert.Empty(link.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-target-missing" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusSymbolicLinkRejectsAPathWithNullBytes()
    {
        byte[] image = HfsPlusFixture.BuildSymbolicLink([0x61, 0x00, 0x62]);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusSymbolicLinkRejectsInvalidUtf8()
    {
        byte[] image = HfsPlusFixture.BuildSymbolicLink([0xFF]);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusSymbolicLinkRequiresAnEmptyResourceFork()
    {
        byte[] image = HfsPlusFixture.BuildSymbolicLink("target"u8.ToArray(), includeResourceFork: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusSymbolicLinkRequiresTheDocumentedFinderCodes()
    {
        byte[] image = HfsPlusFixture.BuildSymbolicLink("target"u8.ToArray(), validFinderInfo: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusVolumeWithoutACatalogTreeIsRejected()
    {
        byte[] image = HfsPlusFixture.Build();
        Array.Clear(image, 2 * 4096, 4096);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusSystemBtreesMustUseTheControlTreeType(bool extentsTree)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: extentsTree);
        int headerNodeOffset = (extentsTree ? 4 : 2) * 4096;
        image[headerNodeOffset + 50] = 1;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusBtreeHeaderNodeMustHaveThreeRecordsAndNoBackwardLink(bool wrongRecordCount)
    {
        byte[] image = HfsPlusFixture.Build();
        Span<byte> headerNode = image.AsSpan(2 * 4096, 4096);
        if (wrongRecordCount) BinaryPrimitives.WriteUInt16BigEndian(headerNode[10..], 2);
        else BinaryPrimitives.WriteUInt32BigEndian(headerNode[4..], 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeNodeMapMustMarkItsRootAsAllocated()
    {
        byte[] image = HfsPlusFixture.Build();
        image[2 * 4096 + 248] = 0x80; // Keep header node 0 allocated, but mark root node 1 free.

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeNodeMapMustMarkItsIndexNodesAsAllocated()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true);
        image[2 * 4096 + 248] = 0xE0; // Keep nodes 0-2 allocated, but mark root index node 3 free.

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false, 0x00000000)] // The catalog must use 16-bit lengths and variable index keys.
    [InlineData(false, 0x00000002)] // The catalog is missing variable index keys.
    [InlineData(true, 0x00000000)]  // The extents tree must use 16-bit lengths.
    [InlineData(true, 0x00000006)]  // The extents tree must use fixed-width index keys.
    public void HfsPlusBtreeAttributesMustMatchTheTreeKeyLayout(bool extentsTree, uint attributes)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: extentsTree);
        int headerNodeOffset = (extentsTree ? 4 : 2) * 4096;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(headerNodeOffset + 14 + 38), attributes);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false, 514)]
    [InlineData(true, 12)]
    public void HfsPlusBtreeMaximumKeyLengthMustMatchItsDefinedKeyFormat(bool extentsTree, ushort maxKeyLength)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: extentsTree);
        int headerNodeOffset = (extentsTree ? 4 : 2) * 4096;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(headerNodeOffset + 34), maxKeyLength);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusCatalogBtreeNodesMustBeAtLeastFourKilobytes()
    {
        byte[] image = BuildHfsPlusVolumeWithSmallCatalogNodes();

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusCatalogKeyLengthMustExactlyCoverItsName(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build(hfsX: hfsX,
            catalogKeyCompareType: hfsX ? (byte)0xCF : null, catalogKeyHasTrailingByte: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusBtreeFreeNodeCountMustMatchItsNodeMap(bool extentsTree)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: extentsTree);
        int headerNodeOffset = (extentsTree ? 4 : 2) * 4096;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(headerNodeOffset + 14 + 26), 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeWithMoreNodesThanItsHeaderMapRequiresChainedMapNodes()
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeUsesAChainedMapNodeForNodesPastTheHeaderMap()
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusBtreeMapNodesMustBeValidAndAllocated(bool markMapNodeFree)
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);
        if (markMapNodeFree)
            image[4 * 4096 + 248] = 0xC0;
        else
            image[4 * 4096 + 2 * 512 + 8] = 0;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeMapNodeRecordMustExtendToItsSpecifiedBoundary()
    {
        const int nodeSize = 512;
        int mapNodeOffset = 4 * 4096 + 2 * nodeSize;
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mapNodeOffset + nodeSize - 4), nodeSize - 5);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowExtentCanEndWithANonzeroStartAndZeroCount()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        // TN1150's fragmented-fork example ends its final extent record with (startBlock: 1, blockCount: 0).
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(5 * 4096 + 14 + 82), 1);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(Enumerable.Range(1, 9).SelectMany(index => Enumerable.Repeat((byte)index, 4096)),
            file.DataFork.ToArray());
    }

    private static byte[] BuildOversizedExtentsTree(bool addMapNode)
    {
        const int blockSize = 4096;
        const int nodeSize = 512;
        const int totalNodes = 2049;
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        const int totalBlocks = 300;
        const int allocatedBlocks = 257;
        Array.Resize(ref image, totalBlocks * blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 44), totalBlocks);
        Span<byte> extentsFork = image.AsSpan(1024 + 192, 80);
        BinaryPrimitives.WriteUInt64BigEndian(extentsFork, (ulong)nodeSize * totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[12..], allocatedBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[16..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[20..], allocatedBlocks);
        Span<byte> allocationFork = image.AsSpan(1024 + 112, 80);
        BinaryPrimitives.WriteUInt64BigEndian(allocationFork, (ulong)(totalBlocks + 7) / 8);
        BinaryPrimitives.WriteUInt32BigEndian(allocationFork[16..], 282);
        image.AsSpan(282 * blockSize, (totalBlocks + 7) / 8).Fill(0xFF);
        image[282 * blockSize + (totalBlocks - 1) / 8] = 0xF0; // Clear unused low bits after block 299.

        int treeOffset = 4 * blockSize;
        byte[] leaf = image.AsSpan(5 * blockSize, nodeSize).ToArray();
        Span<byte> header = image.AsSpan(treeOffset, nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(header[32..], nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[36..], totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(header[40..], totalNodes - (addMapNode ? 3u : 2u));
        header.Slice(248, nodeSize - 256).Clear();
        header[248] = 0xC0; // Nodes 0 and 1 allocated; no map node links follow.
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 4)..], 14 + 106);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 6)..], 14 + 106 + 128);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 8)..], nodeSize - 8);

        if (addMapNode)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 2); // First map node follows header and leaf nodes.
            header[248] = 0xE0; // Nodes 0, 1 and 2 (the map node) are allocated.
            Span<byte> mapNode = image.AsSpan(treeOffset + 2 * nodeSize, nodeSize);
            mapNode[8] = 2;
            mapNode[10..12].Clear();
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[10..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[(nodeSize - 2)..], 14);
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[(nodeSize - 4)..], nodeSize - 6);
        }

        leaf.AsSpan(0, 14 + 76).CopyTo(image.AsSpan(treeOffset + nodeSize));
        Span<byte> leafNode = image.AsSpan(treeOffset + nodeSize, nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(leafNode[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(leafNode[(nodeSize - 4)..], 90);

        // The enlarged extents tree occupies blocks 4 through 260, so keep the file forks outside its allocation.
        Span<byte> catalogLeaf = image.AsSpan(3 * blockSize, blockSize);
        int catalogRecords = BinaryPrimitives.ReadUInt16BigEndian(catalogLeaf[10..]);
        for (int index = 0; index < catalogRecords; index++)
        {
            int recordStart = BinaryPrimitives.ReadUInt16BigEndian(catalogLeaf[(blockSize - 2 * (index + 1))..]);
            int recordEnd = BinaryPrimitives.ReadUInt16BigEndian(catalogLeaf[(blockSize - 2 * (index + 2))..]);
            int keyLength = BinaryPrimitives.ReadUInt16BigEndian(catalogLeaf[recordStart..]);
            int dataStart = recordStart + 2 + keyLength;
            if (BinaryPrimitives.ReadUInt16BigEndian(catalogLeaf[dataStart..]) != 2) continue;
            for (int extent = 0; extent < 8; extent++)
            {
                uint block = checked((uint)(263 + extent * 2));
                BinaryPrimitives.WriteUInt32BigEndian(catalogLeaf[(dataStart + 88 + 16 + extent * 8)..], block);
                image.AsSpan((int)block * blockSize, blockSize).Fill(checked((byte)(extent + 1)));
            }
            BinaryPrimitives.WriteUInt32BigEndian(catalogLeaf[(dataStart + 168 + 16)..], 281);
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + nodeSize + 14 + 12), 279);
            image.AsSpan(281 * blockSize, blockSize).Clear();
            "Resource fork"u8.CopyTo(image.AsSpan(281 * blockSize));
            break;
        }

        return image;
    }

    private static byte[] BuildHfsPlusVolumeWithSmallCatalogNodes()
    {
        const int blockSize = 4096;
        const int nodeSize = 512;
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 32), 0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 36), 0);

        byte[] sourceLeaf = image.AsSpan(3 * blockSize, blockSize).ToArray();
        Span<byte> header = image.AsSpan(2 * blockSize, nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(header[32..], nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[36..], 16);
        BinaryPrimitives.WriteUInt32BigEndian(header[40..], 14);
        header.Slice(248, nodeSize - 256).Clear();
        header[248] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 4)..], 120);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 6)..], 248);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 8)..], nodeSize - 8);

        Span<byte> leaf = image.AsSpan(2 * blockSize + nodeSize, nodeSize);
        leaf.Clear();
        leaf[8] = 0xFF;
        leaf[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(leaf[10..], 2);
        int at = 14;
        for (int index = 0; index < 2; index++)
        {
            int begin = BinaryPrimitives.ReadUInt16BigEndian(sourceLeaf.AsSpan(blockSize - 2 * (index + 1)));
            int end = BinaryPrimitives.ReadUInt16BigEndian(sourceLeaf.AsSpan(blockSize - 2 * (index + 2)));
            int length = end - begin;
            BinaryPrimitives.WriteUInt16BigEndian(leaf[(nodeSize - 2 * (index + 1))..], checked((ushort)at));
            sourceLeaf.AsSpan(begin, length).CopyTo(leaf[at..]);
            if (index == 0)
            {
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(leaf[at..]);
                BinaryPrimitives.WriteUInt32BigEndian(leaf[(at + 2 + keyLength + 4)..], 0); // Root valence.
            }
            at += length;
        }
        BinaryPrimitives.WriteUInt16BigEndian(leaf[(nodeSize - 6)..], checked((ushort)at));
        return image;
    }

    [Fact]
    public void HfsPlusNextCatalogIdMustExceedEveryExistingCatalogId()
    {
        byte[] image = HfsPlusFixture.Build(nextCatalogId: 17);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusNextCatalogIdMayWrapWhenCatalogIdsHaveBeenReused()
    {
        byte[] image = HfsPlusFixture.Build(nextCatalogId: 17, catalogIdsReused: true);

        Assert.Equal("Documents:Read Me",
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext())).MacPath);
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
    public void HfsXCatalogKeysMustUseCaseSensitiveBTreeOrder()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, reverseCatalogRecords: true,
            catalogKeyCompareType: 0xBC);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsXBinaryIndexKeysMustBeOrderedWithinTheirNode()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsXBinaryCatalogIndexSeparatorMustNotSortAfterItsChildRecords()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true, catalogKeyCompareType: 0xBC);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsXBinaryCatalogIndexSeparatorMustRemainAfterThePreviousChild()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true, catalogKeyCompareType: 0xBC);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), 2);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("child contains a key beyond its index range", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsXBinaryIndexKeysMustBeOrderedAcrossSiblingNodes(bool corruptSiblingKey)
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, deepCatalogTree: true);
        if (corruptSiblingKey)
        {
            int nodeStart = 29 * 4096;
            int firstRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(nodeStart + 4096 - 2));
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(nodeStart + firstRecord + 2), 0);
        }

        if (corruptSiblingKey)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
        }
        else
        {
            Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext())).MacPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusExtentsBtreeIndexKeysMustBeOrdered(bool corruptSecondKey)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, indexedOverflowTree: true);
        if (corruptSecondKey)
        {
            int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(28 * 4096 - 4));
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(26 * 4096 + 4096 + secondRecord + 8), 7);
        }

        if (corruptSecondKey)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
        }
        else
        {
            Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext())).MacPath);
        }
    }

    [Fact]
    public void HfsPlusExtentsIndexSeparatorMustNotSortAfterItsChildRecords()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, indexedOverflowTree: true);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(28 * 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(27 * 4096 + secondRecord + 8), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusExtentsIndexSeparatorMustRemainAfterThePreviousChild()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, indexedOverflowTree: true);
        int firstRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(28 * 4096 - 2));
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(28 * 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(27 * 4096 + firstRecord + 4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(27 * 4096 + secondRecord + 8), 8);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("child contains a key beyond its index range", exception.Message);
    }

    [Fact]
    public void HfsXCatalogRejectsAnUnknownKeyComparisonType()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, catalogKeyCompareType: 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void CaseFoldingHfsXCatalogRemainsReadable()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, catalogKeyCompareType: 0xCF);

        Assert.Equal("Documents:Read Me",
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void CaseFoldingHfsXCatalogKeysMustUseCaseFoldingBTreeOrder()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, reverseCatalogRecords: true,
            catalogKeyCompareType: 0xCF);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HfsPlusCatalogKeysMustUseCaseFoldingBTreeOrder()
    {
        byte[] image = HfsPlusFixture.Build(reverseCatalogRecords: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("catalog", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseFoldingHfsXIndexKeysMustUseCaseFoldingBTreeOrder()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true,
            catalogKeyCompareType: 0xCF);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), 0);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("catalog index", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseFoldingHfsXIndexSeparatorsMustStayWithinTheirChildRanges()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true,
            catalogKeyCompareType: 0xCF);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), 2);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("child contains a key beyond its index range", exception.Message);
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
    public void HfsPlusOverflowExtentStartBlockMustContinueAfterCatalogExtents()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        // The catalog contains eight one-block extents, so TN1150 says the overflow record starts at fork block 8.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(5 * 4096 + 14 + 8), 7);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusForksCannotClaimTheSameAllocationBlock()
    {
        byte[] image = HfsPlusFixture.Build(overlappingFileForks: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void AllocatedZeroLengthForkCannotAliasAnotherFork()
    {
        byte[] image = HfsPlusFixture.Build(overlappingFileForks: true, zeroLengthResourceFork: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationFileMustMarkReferencedForkBlocksAsAllocated()
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true, markDataForkAllocated: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationFileMustMarkEveryOverflowTreeExtentAsAllocated()
    {
        byte[] image = HfsPlusFixture.BuildWithUnreferencedOverflowExtent(23);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusVolumeRequiresAnAllocationFile()
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationFileWithAllReferencedBlocksMarkedIsReadable()
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAllocationFileMustCoverEveryVolumeAllocationBlock()
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(1024 + 112), 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationBitmapRejectsSetBitsBeyondTheVolume()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 44), 15);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationBitmapAllowsClearBitsBeyondTheVolume()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 44), 15);
        image[9 * 4096 + 1] &= 0xFE;

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAllocationBitmapRejectsSetBitsInExtraBytes()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(1024 + 112), 3);
        image[9 * 4096 + 2] = 0x80;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0, 0x80)] // Allocation block 0 contains the first 1,536 reserved bytes.
    [InlineData(1, 0x01)] // Allocation block 15 contains the final 1,024 bytes.
    public void HfsPlusAllocationFileMustMarkReservedVolumeBlocks(int bitmapByte, byte mask)
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true);
        image[9 * 4096 + bitmapByte] &= unchecked((byte)~mask);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HfsPlusAllocationFileMustMarkAttributeAndStartupForks(bool attributesFile, bool startupFile)
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true,
            includeAttributeFile: attributesFile, includeStartupFile: startupFile);
        if (attributesFile) image[9 * 4096 + 1] &= 0xDF;
        if (startupFile) image[9 * 4096 + 1] &= 0xEF;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAllocationFileAcceptsAllocatedAttributeAndStartupForks()
    {
        byte[] image = HfsPlusFixture.Build(includeAllocationFile: true,
            includeAttributeFile: true, includeStartupFile: true);
        image[9 * 4096 + 1] |= 0x30; // Allocation blocks 10 and 11 are marked in the second bitmap byte.

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusBadBlockExtentsParticipateInAllocationOwnership(bool overlapsFileExtent)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, badBlockExtent: true,
            badBlockOverlapsFileExtent: overlapsFileExtent);

        if (overlapsFileExtent)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
        }
        else
        {
            MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
            Assert.Equal(Enumerable.Range(1, 8).SelectMany(index => Enumerable.Repeat((byte)index, 4096)),
                file.DataFork.ToArray());
        }
    }

    [Fact]
    public void HfsPlusBadBlockExtentMustStayInsideTheAllocationArea()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, badBlockExtent: true);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(5 * 4096 + 14 + 12), 32);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBadBlockExtentMustUseTheDataFork()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, badBlockExtent: true);
        image[5 * 4096 + 14 + 2] = 0xFF;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkDataRecordsParticipateInAllocationOwnership()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeFork(dataBlock: 4);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkDataWithDisjointExtentRemainsReadable()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeFork(dataBlock: 12);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributeExtensionExtentsParticipateInAllocationOwnership()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeExtension(dataBlock: 4);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0x10u)]
    [InlineData(0x40u)]
    public void HfsPlusUnknownAndInlineAttributeRecordsDoNotClaimForkExtents(uint recordType)
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeRecord(recordType, dataBlock: 4);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributesBtreeSupportsVariableLengthIndexKeys()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexKeysMustBeOrderedByFileId()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            reverseAttributeKeys: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexSeparatorMustNotSortAfterItsChildRecords()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            invalidSeparator: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexSeparatorMustRemainAfterThePreviousChildRecords()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            separatorFileId: 17, separatorName: "alpha");

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexSeparatorMayFallBetweenChildKeyRanges()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            separatorFileId: 17, separatorName: "omega");

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexRecordMustContainOnlyItsKeyAndChildPointer()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            extraIndexRecordBytes: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(18u, "a", 0u, 17u, "a", 0u)]
    [InlineData(17u, "zz", 0u, 17u, "a", 0u)]
    [InlineData(17u, "b", 0u, 17u, "a", 0u)]
    [InlineData(17u, "a", 2u, 17u, "a", 1u)]
    public void HfsPlusAttributesBtreeLeafKeysUseAppleKeyOrder(uint firstFileId, string firstName,
        uint firstStartBlock, uint secondFileId, string secondName, uint secondStartBlock)
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecord(firstFileId, firstName, firstStartBlock, 0x40),
            HfsPlusFixture.AttributeRecord(secondFileId, secondName, secondStartBlock, 0x40)
        ]);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(17u, "a", 0u, 18u, "a", 0u)]
    [InlineData(17u, "a", 0u, 17u, "zz", 0u)]
    [InlineData(17u, "a", 0u, 17u, "b", 0u)]
    [InlineData(17u, "a", 1u, 17u, "a", 2u)]
    public void HfsPlusAttributesBtreeAcceptsAppleKeyOrder(uint firstFileId, string firstName,
        uint firstStartBlock, uint secondFileId, string secondName, uint secondStartBlock)
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecord(firstFileId, firstName, firstStartBlock, 0x40),
            HfsPlusFixture.AttributeRecord(secondFileId, secondName, secondStartBlock, 0x40)
        ]);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributesBtreeKeyLengthMustMatchItsUnicodeName()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecord(17, "alpha", 0, 0x40)
        ]);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(11 * HfsPlusFixture.Block + 14), 12);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeRejectsAnOutOfRangeIndexChild()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowTreeCannotRepeatAnExtentKey()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, duplicateOverflowExtent: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowTreeKeysMustBeOrderedByFileForkAndStartBlock()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, unsortedOverflowKeys: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowTreeKeysMustBeOrderedByFileIdBeforeForkAndStartBlock()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, unsortedOverflowFileIds: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowTreeKeysMustBeOrderedByForkBeforeStartBlock()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, unsortedOverflowForkTypes: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void OutOfRangeHfsPlusExtentIsRejected()
    {
        byte[] image = HfsPlusFixture.Build(invalidDataExtent: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void HfsPlusForkExtentCountMustMatchItsAllocatedBlockCount(uint recordedBlocks)
    {
        byte[] image = HfsPlusFixture.Build(dataBlockCount: recordedBlocks);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusForkMayOwnMoreBlocksThanItsLogicalLength()
    {
        byte[] image = HfsPlusFixture.Build(dataBlockCount: 2, dataExtentBlockCount: 2);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("Resource fork"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void CyclicHfsPlusCatalogLeafChainIsRejected()
    {
        byte[] image = HfsPlusFixture.Build(cyclicCatalog: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusCatalogLeafChainMustEndAtTheHeaderDeclaredLastLeaf()
    {
        byte[] image = HfsPlusFixture.Build(lastCatalogLeaf: 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void FirstHfsPlusCatalogLeafMustNotLinkBackward()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + 4), 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HfsPlusBTreeNodeCountMustIncludeReferencedLeafNodes(uint totalNodes)
    {
        byte[] image = HfsPlusFixture.Build(catalogTotalNodes: totalNodes);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(4096 - 2, 15)] // The header record must start immediately after the node descriptor.
    [InlineData(4096 - 4, 119)] // The fixed 128-byte user record follows the 106-byte header record.
    [InlineData(4096 - 6, 247)] // The allocation map follows the user data record.
    [InlineData(4096 - 8, 4087)] // The map record extends to the record-offset table.
    public void HfsPlusBTreeHeaderNodeMustHaveTheSpecifiedRecordLayout(int offsetTablePosition,
        ushort malformedOffset)
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * 4096 + offsetTablePosition), malformedOffset);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBTreeHeaderNodeMustHaveHeightZero()
    {
        byte[] image = HfsPlusFixture.Build();
        image[2 * 4096 + 9] = 1;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBTreeForkLengthMustMatchItsDeclaredNodeCount()
    {
        byte[] image = HfsPlusFixture.Build(extraCatalogForkNode: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBTreeLeafRecordsMustStartImmediatelyAfterTheNodeDescriptor()
    {
        const int nodeSize = 4096;
        byte[] image = HfsPlusFixture.Build();
        Span<byte> leaf = image.AsSpan(3 * nodeSize, nodeSize);
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
        int freeSpaceOffsetPosition = nodeSize - 2 * (recordCount + 1);
        int oldFreeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(leaf[freeSpaceOffsetPosition..]);
        leaf.Slice(14, oldFreeSpaceOffset - 14).CopyTo(leaf.Slice(16, oldFreeSpaceOffset - 14));
        for (int entry = 0; entry <= recordCount; entry++)
        {
            int offsetPosition = nodeSize - 2 * (entry + 1);
            ushort offset = BinaryPrimitives.ReadUInt16BigEndian(leaf[offsetPosition..]);
            BinaryPrimitives.WriteUInt16BigEndian(leaf[offsetPosition..], checked((ushort)(offset + 2)));
        }

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBTreeRootMustBeWithinTheNodeRange()
    {
        byte[] image = HfsPlusFixture.Build(catalogRootNode: 2);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusSingleLeafTreeMustDeclareThatLeafAsItsDepthOneRoot()
    {
        byte[] image = HfsPlusFixture.Build(catalogTreeDepth: 2);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusCatalogWithAnIndexRootAndTwoLeavesCanBeRead()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void HfsPlusIndexChildMustReferenceAnExistingNode()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true, invalidCatalogIndexChild: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HfsPlusIndexNodeSiblingLinksMustMatchItsLevel(bool badForward, bool badBackward)
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true,
            invalidCatalogIndexForwardLink: badForward, invalidCatalogIndexBackwardLink: badBackward);

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

    [Fact]
    public void HfsPlusCatalogRejectsDuplicateFileAndFolderIds()
    {
        byte[] image = HfsPlusFixture.Build(duplicateCatalogId: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusFileThreadMustAgreeWithItsCatalogParentAndName()
    {
        byte[] image = HfsPlusFixture.Build(invalidFileThread: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusFileMustHaveItsRequiredThreadRecord()
    {
        byte[] image = HfsPlusFixture.Build(omitFileThread: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusFileThreadFlagMustBeSet()
    {
        byte[] image = HfsPlusFixture.Build(missingFileThreadFlag: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusFolderMustHaveItsRequiredThreadRecord()
    {
        byte[] image = HfsPlusFixture.Build(omitFolderThread: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusThreadMustReferToAnExistingCatalogRecord()
    {
        byte[] image = HfsPlusFixture.Build(orphanFileThread: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusFileThreadMustHaveTheFileThreadRecordType()
    {
        byte[] image = HfsPlusFixture.Build(wrongFileThreadKind: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusThreadKeyMustHaveAnEmptyName()
    {
        byte[] image = HfsPlusFixture.Build(nonEmptyFileThreadKey: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusCatalogCannotContainTwoThreadsForOneNode()
    {
        byte[] image = HfsPlusFixture.Build(duplicateFolderThread: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void HfsPlusFolderValenceMustMatchItsDirectChildren(uint rootValence, uint documentsValence)
    {
        byte[] image = HfsPlusFixture.Build(rootFolderValence: rootValence,
            documentsFolderValence: documentsValence);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(18)]
    public void HfsPlusEmptyFoldersMustHaveExistingAcyclicParentPaths(uint parentId)
    {
        byte[] image = HfsPlusFixture.Build(additionalFolderParent: parentId);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    // A small HFS+ volume built from the structures in TN1150: 4 KiB allocation and B-tree nodes,
    // one catalog leaf, a root folder, nested folder, file and their threads.
    private static class HfsPlusFixture
    {
        public const int Block = 4096;

        public static byte[] Build(string fileName = "Read Me", bool hfsX = false, bool fragmentedData = false,
            bool invalidDataExtent = false, bool cyclicCatalog = false, bool unknownCatalogRecord = false,
            uint? dataBlockCount = null, uint? dataExtentBlockCount = null, uint? lastCatalogLeaf = null,
            uint? catalogTotalNodes = null, bool duplicateCatalogId = false, bool invalidFileThread = false,
            bool omitFileThread = false, bool omitFolderThread = false, bool orphanFileThread = false,
            bool wrongFileThreadKind = false, bool missingFileThreadFlag = false,
            bool nonEmptyFileThreadKey = false, bool duplicateFolderThread = false,
            uint rootFolderValence = 1, uint documentsFolderValence = 1,
            uint? additionalFolderParent = null,
            uint? catalogRootNode = null, ushort? catalogTreeDepth = null, bool multiLeafCatalog = false,
            bool invalidCatalogIndexChild = false, bool invalidCatalogIndexForwardLink = false,
            bool invalidCatalogIndexBackwardLink = false, bool duplicateOverflowExtent = false,
            bool unsortedOverflowKeys = false, bool unsortedOverflowFileIds = false,
            bool unsortedOverflowForkTypes = false, bool reverseCatalogRecords = false,
            byte? catalogKeyCompareType = null, uint? nextCatalogId = null, bool catalogIdsReused = false,
            bool catalogKeyHasTrailingByte = false, bool indexedOverflowTree = false,
            bool deepCatalogTree = false, bool overlappingFileForks = false,
            bool zeroLengthResourceFork = false, bool includeAllocationFile = true,
            bool markDataForkAllocated = true, bool includeAttributeFile = false,
            bool includeStartupFile = false, bool badBlockExtent = false,
            bool badBlockOverlapsFileExtent = false, bool extraCatalogForkNode = false)
        {
            uint volumeBlocks = deepCatalogTree ? 40u : fragmentedData ? 32u : 16u;
            byte[] image = new byte[checked((int)volumeBlocks * Block)];
            Span<byte> volume = image.AsSpan(1024, 512);
            U16(volume, 0, hfsX ? (ushort)0x4858 : (ushort)0x482B);
            U16(volume, 2, hfsX ? (ushort)5 : (ushort)4);
            U32(volume, 4, catalogIdsReused ? 0x1000u : 0);
            U32(volume, 32, 1); // fileCount
            U32(volume, 36, 1); // folderCount excludes root
            U32(volume, 40, Block);
            U32(volume, 44, volumeBlocks);
            U32(volume, 48, includeAllocationFile ? 0u : fragmentedData ? 19u : 10u);
            U32(volume, 64, nextCatalogId ?? (additionalFolderParent is null ? 18u : 19u));
            int catalogForkNodes = deepCatalogTree ? 8 : multiLeafCatalog ? 4 : extraCatalogForkNode ? 3 : 2;
            Fork(volume.Slice(272, 80), catalogForkNodes * Block,
                deepCatalogTree ? 26u : 2u, checked((uint)catalogForkNodes));
            if (fragmentedData)
                Fork(volume.Slice(192, 80), (indexedOverflowTree ? 4 : 2) * Block,
                    indexedOverflowTree ? 26u : 4u, indexedOverflowTree ? 4u : 2u);
            if (includeAllocationFile)
            {
                int bitmapLength = checked((int)((volumeBlocks + 7) / 8));
                Fork(volume.Slice(112, 80), bitmapLength, 9, 1);
                image.AsSpan(9 * Block, bitmapLength).Fill(0xFF);
                if (!markDataForkAllocated) image[9 * Block] &= 0xF7;
            }
            if (includeAttributeFile)
            {
                Fork(volume.Slice(352, 80), Block, 10, 1);
                WriteEmptyAttributesTree(image.AsSpan(10 * Block, Block));
            }
            if (includeStartupFile) Fork(volume.Slice(432, 80), 1, 11, 1);

            byte[] root = new byte[88];
            U16(root, 0, 1);
            U32(root, 4, rootFolderValence);
            U32(root, 8, 2);
            byte[] folder = new byte[88];
            U16(folder, 0, 1);
            U32(folder, 4, documentsFolderValence);
            U32(folder, 8, 16);
            byte[] file = new byte[248];
            U16(file, 0, 2);
            U16(file, 2, missingFileThreadFlag ? (ushort)0 : (ushort)2); // file thread exists
            U32(file, 8, duplicateCatalogId ? 16u : 17u);
            U32(file, 12, 2_500_000_000);
            U32(file, 16, 2_600_000_000);
            "TEXTttxt"u8.CopyTo(file.AsSpan(48));
            if (fragmentedData)
            {
                uint dataExtentCount = badBlockExtent ? 8u : unsortedOverflowKeys ? 10u : 9u;
                BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(88), (ulong)dataExtentCount * Block);
                U32(file, 88 + 12, dataExtentCount);
                for (int index = 0; index < 8; index++)
                {
                    U32(file, 88 + 16 + index * 8, checked((uint)(6 + index * 2)));
                    U32(file, 88 + 20 + index * 8, 1);
                }
                for (int index = 0; index < dataExtentCount; index++)
                    image.AsSpan((6 + index * 2) * Block, Block).Fill(checked((byte)(index + 1)));
                Fork(file.AsSpan(168, 80), "Resource fork"u8.Length, 25, 1);
                "Resource fork"u8.CopyTo(image.AsSpan(25 * Block));
                WriteExtentsTree(image, duplicateOverflowExtent, unsortedOverflowKeys,
                    unsortedOverflowFileIds, unsortedOverflowForkTypes, indexedOverflowTree,
                    badBlockExtent, badBlockOverlapsFileExtent);
            }
            else
            {
                uint dataBlocks = dataExtentBlockCount ?? 1;
                uint dataStart = invalidDataExtent ? 16u : multiLeafCatalog ? 6u : extraCatalogForkNode ? 5u : 4u;
                Fork(file.AsSpan(88, 80), "HFS Plus data"u8.Length, dataStart, dataBlocks);
                if (dataBlockCount is { } count) U32(file.AsSpan(88), 12, count);
                uint resourceStart = invalidDataExtent ? 5u : overlappingFileForks ? dataStart : dataStart + dataBlocks;
                Fork(file.AsSpan(168, 80), zeroLengthResourceFork ? 0 : "Resource fork"u8.Length, resourceStart, 1);
                uint dataStorageBlock = invalidDataExtent ? 4u : dataStart;
                "HFS Plus data"u8.CopyTo(image.AsSpan((int)dataStorageBlock * Block));
                "Resource fork"u8.CopyTo(image.AsSpan((int)resourceStart * Block));
            }

            var records = new List<byte[]>
            {
                Record(1, "Volume", root),
                Record(2, "", Thread(1, "Volume", 3)),
                Record(2, "Documents", folder),
            };
            if (!omitFolderThread)
            {
                records.Add(Record(16, "", Thread(2, "Documents", 3)));
                if (duplicateFolderThread) records.Add(Record(16, "", Thread(2, "Documents", 3)));
            }
            records.Add(Record(16, fileName, file));
            if (!omitFileThread)
                records.Add(Record(duplicateCatalogId ? 16u : 17u, nonEmptyFileThreadKey ? "Thread" : "",
                    Thread(invalidFileThread ? 2u : 16u, invalidFileThread ? "Other" : fileName,
                        wrongFileThreadKind ? (ushort)3 : (ushort)4)));
            if (additionalFolderParent is { } additionalParent)
            {
                byte[] additionalFolder = new byte[88];
                U16(additionalFolder, 0, 1);
                U32(additionalFolder, 4, additionalParent == 18 ? 1u : 0u);
                U32(additionalFolder, 8, 18);
                records.Add(Record(additionalParent, "Empty", additionalFolder));
                records.Add(Record(18, "", Thread(additionalParent, "Empty", 3)));
            }
            if (orphanFileThread) records.Add(Record(42, "", Thread(16, "Missing", 4)));
            if (catalogKeyHasTrailingByte)
            {
                byte[] fileRecord = records[^2];
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(fileRecord);
                byte[] malformedRecord = new byte[fileRecord.Length + 1];
                fileRecord.AsSpan(0, keyLength + 2).CopyTo(malformedRecord);
                fileRecord.AsSpan(keyLength + 2).CopyTo(malformedRecord.AsSpan(keyLength + 3));
                malformedRecord[keyLength + 2] = 0;
                U16(malformedRecord, 0, checked((ushort)(keyLength + 1)));
                records[^2] = malformedRecord;
            }
            if (unknownCatalogRecord) U16(records[4], 0, 0x1234);
            if (reverseCatalogRecords) records.Reverse();
            if (deepCatalogTree)
            {
                WriteDeepCatalogTree(image, records, hfsX);
                return image;
            }
            if (multiLeafCatalog)
            {
                int split = records.Count / 2;
                WriteBTreeNode(image.AsSpan(3 * Block, Block), 0, 2,
                    invalidCatalogIndexForwardLink ? 1u : 0u,
                    invalidCatalogIndexBackwardLink ? 1u : 0u,
                    [IndexRecord(records[0], invalidCatalogIndexChild ? 4u : 2u), IndexRecord(records[split], 3)]);
                WriteBTreeNode(image.AsSpan(4 * Block, Block), 0xFF, 1, 3, 0,
                    records.Take(split).ToArray());
                WriteBTreeNode(image.AsSpan(5 * Block, Block), 0xFF, 1, 0, 2,
                    records.Skip(split).ToArray());
            }
            else
            {
                byte[] leaf = image.AsSpan(3 * Block, Block).ToArray();
                if (cyclicCatalog) U32(leaf, 0, 1);
                leaf[8] = 0xFF;
                leaf[9] = 1;
                U16(leaf, 10, (ushort)records.Count);
                int at = 14;
                for (int index = 0; index < records.Count; index++)
                {
                    U16(leaf, Block - 2 * (index + 1), (ushort)at);
                    records[index].CopyTo(leaf, at);
                    at += records[index].Length;
                }
                U16(leaf, Block - 2 * (records.Count + 1), (ushort)at);
                leaf.CopyTo(image, 3 * Block);
            }

            Span<byte> header = image.AsSpan(2 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, catalogTreeDepth ?? (multiLeafCatalog ? (ushort)2 : (ushort)1)); // depth
            U32(header, 16, catalogRootNode ?? 1); // root node
            U32(header, 20, (uint)records.Count);
            U32(header, 24, multiLeafCatalog ? 2u : 1u); // first leaf
            U32(header, 28, lastCatalogLeaf ?? (multiLeafCatalog ? 3u : 1u)); // last leaf
            U16(header, 32, Block);
            U16(header, 34, 516);
            U32(header, 36, catalogTotalNodes ?? (multiLeafCatalog ? 4u : 2u)); // total nodes
            header[14 + 37] = catalogKeyCompareType ?? (hfsX ? (byte)0xBC : (byte)0);
            U32(header, 14 + 38, 6); // 16-bit key lengths and variable-width catalog index keys.
            header[14 + 106 + 128] = multiLeafCatalog ? (byte)0xF0 : (byte)0xC0; // allocated nodes
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

        public static byte[] BuildWithAttributeFork(uint dataBlock) =>
            BuildWithAttributeTree(dataBlock, 0x20);

        public static byte[] BuildWithAttributeExtension(uint dataBlock) =>
            BuildWithAttributeTree(dataBlock, 0x30);

        public static byte[] BuildWithAttributeRecord(uint recordType, uint dataBlock) =>
            BuildWithAttributeTree(dataBlock, recordType);

        public static byte[] BuildSymbolicLink(byte[] target, bool hfsX = false, bool includeResourceFork = false,
            bool validFinderInfo = true)
        {
            byte[] image = Build(fileName: "Shortcut", hfsX: hfsX);
            Span<byte> catalog = image.AsSpan(3 * Block, Block);
            int recordOffset = BinaryPrimitives.ReadUInt16BigEndian(catalog[(Block - 10)..]);
            int dataOffset = recordOffset + 2 + BinaryPrimitives.ReadUInt16BigEndian(catalog[recordOffset..]);
            Span<byte> file = catalog[dataOffset..];
            U16(file, 42, 0xA000); // BSD S_IFLNK
            if (validFinderInfo) "slnkrhap"u8.CopyTo(file[48..]);
            Fork(file[88..], target.Length, 4, 1);
            if (includeResourceFork)
            {
                Fork(file[168..], 6, 5, 1);
                "unused"u8.CopyTo(image.AsSpan(5 * Block));
            }
            else
            {
                file.Slice(168, 80).Clear();
            }
            target.CopyTo(image, 4 * Block);
            return image;
        }

        public static byte[] BuildWithHardLink(uint linkReference = 123, bool hfsX = false,
            bool includeIndirectNode = true)
        {
            const string privateDirectory = "\0\0\0\0HFS+ Private Data";
            const string data = "shared file data";
            const string resource = "shared resource";
            byte[] image = Build(hfsX: hfsX);
            byte[] root = FolderData(2, 2);
            byte[] documents = FolderData(16, 2);
            byte[] privateFolder = FolderData(18, includeIndirectNode ? 1u : 0u);
            byte[] readMe = FileData(17, 4, "HFS Plus data"u8, 5, "Resource fork"u8);
            byte[] inode = FileData(19, 6, Encoding.UTF8.GetBytes(data), 7, Encoding.UTF8.GetBytes(resource));
            byte[] link = new byte[248];
            U16(link, 0, 2);
            U16(link, 2, 2);
            U32(link, 8, 20);
            U32(link, 44, linkReference);
            "hlnkhfs+"u8.CopyTo(link.AsSpan(48));

            byte[][] rootEntries = hfsX
                ? [Record(2, "", Thread(1, "Volume", 3)), Record(2, privateDirectory, privateFolder),
                    Record(2, "Documents", documents)]
                : [Record(2, "", Thread(1, "Volume", 3)), Record(2, "Documents", documents),
                    Record(2, privateDirectory, privateFolder)];
            byte[][] indirectNodeRecords = includeIndirectNode
                ? [Record(18, "iNode123", inode), Record(19, "", Thread(18, "iNode123", 4))]
                : [];
            byte[][] records =
            [
                Record(1, "Volume", root),
                .. rootEntries,
                Record(16, "", Thread(2, "Documents", 3)),
                Record(16, "Read Me", readMe),
                Record(16, "Shared Alias", link),
                Record(17, "", Thread(16, "Read Me", 4)),
                Record(18, "", Thread(2, privateDirectory, 3)),
                .. indirectNodeRecords,
                Record(20, "", Thread(16, "Shared Alias", 4)),
            ];

            WriteBTreeNode(image.AsSpan(3 * Block, Block), 0xFF, 1, 0, 0, records);
            U32(image.AsSpan(2 * Block, Block), 20, checked((uint)records.Length));
            Span<byte> volume = image.AsSpan(1024, 512);
            U32(volume, 32, includeIndirectNode ? 3u : 2u);
            U32(volume, 36, 2);
            U32(volume, 64, 21);
            Encoding.UTF8.GetBytes(data).CopyTo(image.AsSpan(6 * Block));
            Encoding.UTF8.GetBytes(resource).CopyTo(image.AsSpan(7 * Block));
            return image;
        }

        public static byte[] BuildWithUnreferencedOverflowExtent(uint physicalBlock)
        {
            byte[] image = Build(fragmentedData: true);
            Span<byte> leaf = image.AsSpan(5 * Block, Block);
            U16(leaf, 10, 2);
            U16(leaf, Block - 4, 90);
            U16(leaf, Block - 6, 166);
            WriteExtentRecord(leaf, 90, 0, physicalBlock, fileId: 99);
            U32(image.AsSpan(4 * Block), 20, 2);
            image[9 * Block + (int)(physicalBlock / 8)] &= unchecked((byte)~(1 << (7 - (int)(physicalBlock % 8))));
            return image;
        }

        public static byte[] BuildWithIndexedAttributesTree(bool invalidChild, bool reverseAttributeKeys = false,
            bool invalidSeparator = false, uint? separatorFileId = null, string? separatorName = null,
            bool extraIndexRecordBytes = false)
        {
            byte[] image = Build(includeAttributeFile: true);
            Fork(image.AsSpan(1024 + 352, 80), 4 * Block, 10, 4);
            byte[] first = AttributeRecord(reverseAttributeKeys ? 18u : 17u, "alpha", 0, 0x40);
            byte[] second = AttributeRecord(reverseAttributeKeys ? 17u : 18u, "beta", 0, 0x40);
            byte[] secondIndexKey = separatorFileId is { } fileId && separatorName is { } name
                ? AttributeRecord(fileId, name, 0, 0x40)
                : (byte[])second.Clone();
            if (invalidSeparator) U32(secondIndexKey, 4, 19);
            byte[] secondIndexRecord = IndexRecord(secondIndexKey, 3);
            if (extraIndexRecordBytes) Array.Resize(ref secondIndexRecord, secondIndexRecord.Length + 2);
            WriteBTreeNode(image.AsSpan(11 * Block, Block), 0, 2, 0, 0,
                [IndexRecord(first, invalidChild ? 4u : 2u), secondIndexRecord]);
            WriteBTreeNode(image.AsSpan(12 * Block, Block), 0xFF, 1, 3, 0, [first]);
            WriteBTreeNode(image.AsSpan(13 * Block, Block), 0xFF, 1, 0, 2, [second]);

            Span<byte> header = image.AsSpan(10 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 2);
            U32(header, 16, 1);
            U32(header, 20, 2);
            U32(header, 24, 2);
            U32(header, 28, 3);
            U16(header, 32, Block);
            U16(header, 34, 266);
            U32(header, 36, 4);
            U32(header, 14 + 38, 6);
            header[14 + 106 + 128] = 0xF0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
            return image;
        }

        public static byte[] BuildWithAttributeLeaf(byte[][] records)
        {
            byte[] image = Build(includeAttributeFile: true);
            Fork(image.AsSpan(1024 + 352, 80), 2 * Block, 10, 2);
            WriteBTreeNode(image.AsSpan(11 * Block, Block), 0xFF, 1, 0, 0, records);

            Span<byte> header = image.AsSpan(10 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 1);
            U32(header, 16, 1);
            U32(header, 20, checked((uint)records.Length));
            U32(header, 24, 1);
            U32(header, 28, 1);
            U16(header, 32, Block);
            U16(header, 34, 266);
            U32(header, 36, 2);
            U32(header, 14 + 38, 6);
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
            return image;
        }

        public static byte[] AttributeRecord(uint fileId, string name, uint startBlock, uint recordType)
        {
            byte[] nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
            ushort keyLength = checked((ushort)(12 + nameBytes.Length));
            byte[] record = new byte[2 + keyLength + 4];
            U16(record, 0, keyLength);
            U32(record, 4, fileId);
            U32(record, 8, startBlock);
            U16(record, 12, checked((ushort)name.Length));
            nameBytes.CopyTo(record, 14);
            U32(record, 2 + keyLength, recordType);
            return record;
        }

        private static byte[] BuildWithAttributeTree(uint dataBlock, uint recordType)
        {
            byte[] image = Build(includeAttributeFile: true);
            Fork(image.AsSpan(1024 + 352, 80), 2 * Block, 10, 2);

            int dataLength = recordType == 0x20 ? 88 : 72;
            byte[] record = new byte[2 + 12 + dataLength];
            U16(record, 0, 12);
            U32(record, 4, 17);
            U32(record, 14, recordType);
            if (recordType == 0x20)
            {
                BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(22), 1);
                U32(record, 14 + 8 + 12, 1);
                U32(record, 14 + 8 + 16, dataBlock);
                U32(record, 14 + 8 + 20, 1);
            }
            else
            {
                U32(record, 14 + 8, dataBlock);
                U32(record, 14 + 12, 1);
            }
            WriteBTreeNode(image.AsSpan(11 * Block, Block), 0xFF, 1, 0, 0, [record]);

            Span<byte> header = image.AsSpan(10 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 1);
            U32(header, 16, 1);
            U32(header, 20, 1);
            U32(header, 24, 1);
            U32(header, 28, 1);
            U16(header, 32, Block);
            U16(header, 34, 266);
            U32(header, 36, 2);
            U32(header, 14 + 38, 6); // 16-bit key lengths and variable-width attribute index keys.
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
            return image;
        }

        private static byte[] IndexRecord(byte[] catalogRecord, uint child)
        {
            int keyLength = BinaryPrimitives.ReadUInt16BigEndian(catalogRecord);
            int keySize = keyLength + 2;
            int padding = keySize & 1;
            byte[] record = new byte[keySize + padding + 4];
            catalogRecord.AsSpan(0, keySize).CopyTo(record);
            U32(record, keySize + padding, child);
            return record;
        }

        private static void WriteBTreeNode(Span<byte> node, byte kind, byte height, uint forward,
            uint backward, IReadOnlyList<byte[]> records)
        {
            node.Clear();
            U32(node, 0, forward);
            U32(node, 4, backward);
            node[8] = kind;
            node[9] = height;
            U16(node, 10, checked((ushort)records.Count));
            int at = 14;
            for (int index = 0; index < records.Count; index++)
            {
                U16(node, node.Length - 2 * (index + 1), checked((ushort)at));
                records[index].CopyTo(node[at..]);
                at += records[index].Length;
            }
            U16(node, node.Length - 2 * (records.Count + 1), checked((ushort)at));
        }

        private static void WriteDeepCatalogTree(byte[] image, IReadOnlyList<byte[]> records, bool hfsX)
        {
            if (records.Count != 6) throw new InvalidOperationException("The deep catalog fixture needs six records.");

            int firstNodeOffset = 26 * Block;
            WriteBTreeNode(image.AsSpan(firstNodeOffset + Block, Block), 0, 3, 0, 0,
                [IndexRecord(records[0], 2), IndexRecord(records[3], 3)]);
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 2 * Block, Block), 0, 2, 3, 0,
                [IndexRecord(records[0], 4), IndexRecord(records[2], 5)]);
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 3 * Block, Block), 0, 2, 0, 2,
                [IndexRecord(records[3], 6), IndexRecord(records[4], 7)]);
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 4 * Block, Block), 0xFF, 1, 5, 0,
                records.Take(2).ToArray());
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 5 * Block, Block), 0xFF, 1, 6, 4,
                [records[2]]);
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 6 * Block, Block), 0xFF, 1, 7, 5,
                [records[3]]);
            WriteBTreeNode(image.AsSpan(firstNodeOffset + 7 * Block, Block), 0xFF, 1, 0, 6,
                records.Skip(4).ToArray());

            Span<byte> header = image.AsSpan(firstNodeOffset, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 3);
            U32(header, 16, 1);
            U32(header, 20, (uint)records.Count);
            U32(header, 24, 4);
            U32(header, 28, 7);
            U16(header, 32, Block);
            U16(header, 34, 516);
            U32(header, 36, 8);
            header[14 + 37] = hfsX ? (byte)0xBC : (byte)0;
            U32(header, 14 + 38, 6);
            header[14 + 106 + 128] = 0xFF;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteExtentsTree(byte[] image, bool duplicateRecord, bool unsortedKeys,
            bool unsortedFileIds, bool unsortedForkTypes, bool indexedOverflowTree,
            bool badBlockExtent, bool badBlockOverlapsFileExtent)
        {
            if (badBlockExtent)
            {
                WriteBadBlockExtentTree(image, badBlockOverlapsFileExtent ? 12u : 23u);
                return;
            }
            if (indexedOverflowTree)
            {
                WriteIndexedExtentsTree(image);
                return;
            }

            Span<byte> leaf = image.AsSpan(5 * Block, Block);
            leaf[8] = 0xFF;
            leaf[9] = 1;
            bool twoRecords = duplicateRecord || unsortedKeys || unsortedFileIds || unsortedForkTypes;
            U16(leaf, 10, twoRecords ? (ushort)2 : (ushort)1);
            U16(leaf, Block - 2, 14);
            U16(leaf, Block - 4, 90);
            if (twoRecords) U16(leaf, Block - 6, 166);
            if (unsortedFileIds)
                WriteExtentRecord(leaf, 14, 8, 22, fileId: 18);
            else if (unsortedForkTypes)
                WriteExtentRecord(leaf, 14, 8, 22, forkType: 0xFF);
            else
                WriteExtentRecord(leaf, 14, unsortedKeys ? 9u : 8u, unsortedKeys ? 24u : 22u);
            if (duplicateRecord) leaf.Slice(14, 76).CopyTo(leaf[90..]);
            else if (unsortedKeys) WriteExtentRecord(leaf, 90, 8, 22);
            else if (unsortedFileIds) WriteExtentRecord(leaf, 90, 8, 22, fileId: 17);
            else if (unsortedForkTypes) WriteExtentRecord(leaf, 90, 8, 22, forkType: 0);

            Span<byte> header = image.AsSpan(4 * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 1);
            U32(header, 16, 1);
            U32(header, 20, twoRecords ? 2u : 1u);
            U32(header, 24, 1);
            U32(header, 28, 1);
            U16(header, 32, Block);
            U16(header, 34, 10);
            U32(header, 36, 2);
            U32(header, 14 + 38, 2); // 16-bit key lengths and fixed-width extents index keys.
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteBadBlockExtentTree(byte[] image, uint physicalBlock)
        {
            Span<byte> leaf = image.AsSpan(5 * Block, Block);
            leaf[8] = 0xFF;
            leaf[9] = 1;
            U16(leaf, 10, 1);
            U16(leaf, Block - 2, 14);
            U16(leaf, Block - 4, 90);
            WriteExtentRecord(leaf, 14, 0, physicalBlock, fileId: 5);

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
            U32(header, 14 + 38, 2);
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteEmptyAttributesTree(Span<byte> header)
        {
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 32, Block);
            U16(header, 34, 266);
            U32(header, 36, 1);
            U32(header, 14 + 38, 6);
            header[14 + 106 + 128] = 0x80;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteIndexedExtentsTree(byte[] image)
        {
            const int firstTreeBlock = 26;
            byte[] firstExtent = ExtentRecord(startBlock: 8, physicalBlock: 22);
            byte[] secondExtent = ExtentRecord(startBlock: 9, physicalBlock: 24);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 1) * Block, Block), 0, 2, 0, 0,
                [ExtentIndexRecord(firstExtent, 2), ExtentIndexRecord(secondExtent, 3)]);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 2) * Block, Block), 0xFF, 1, 3, 0, [firstExtent]);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 3) * Block, Block), 0xFF, 1, 0, 2, [secondExtent]);

            Span<byte> header = image.AsSpan(firstTreeBlock * Block, Block);
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, 2);
            U32(header, 16, 1);
            U32(header, 20, 2);
            U32(header, 24, 2);
            U32(header, 28, 3);
            U16(header, 32, Block);
            U16(header, 34, 10);
            U32(header, 36, 4);
            U32(header, 14 + 38, 2);
            header[14 + 106 + 128] = 0xF0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static byte[] ExtentRecord(uint startBlock, uint physicalBlock)
        {
            byte[] record = new byte[76];
            WriteExtentRecord(record, 0, startBlock, physicalBlock);
            return record;
        }

        private static byte[] ExtentIndexRecord(byte[] extentRecord, uint child)
        {
            byte[] record = new byte[16];
            extentRecord.AsSpan(0, 12).CopyTo(record);
            U32(record, 12, child);
            return record;
        }

        private static void WriteExtentRecord(Span<byte> leaf, int offset, uint forkStart, uint physicalBlock,
            uint fileId = 17, byte forkType = 0)
        {
            U16(leaf, offset, 10); // key length excludes this field
            leaf[offset + 2] = forkType;
            U32(leaf, offset + 4, fileId);
            U32(leaf, offset + 8, forkStart);
            U32(leaf, offset + 12, physicalBlock);
            U32(leaf, offset + 16, 1);
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

        private static byte[] FolderData(uint id, uint valence)
        {
            byte[] folder = new byte[88];
            U16(folder, 0, 1);
            U32(folder, 4, valence);
            U32(folder, 8, id);
            return folder;
        }

        private static byte[] FileData(uint id, uint dataBlock, ReadOnlySpan<byte> data,
            uint resourceBlock, ReadOnlySpan<byte> resource)
        {
            byte[] file = new byte[248];
            U16(file, 0, 2);
            U16(file, 2, 2);
            U32(file, 8, id);
            "TEXTttxt"u8.CopyTo(file.AsSpan(48));
            Fork(file.AsSpan(88, 80), data.Length, dataBlock, 1);
            Fork(file.AsSpan(168, 80), resource.Length, resourceBlock, 1);
            return file;
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
