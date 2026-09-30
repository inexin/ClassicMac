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
        private const int Block = 4096;

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
            byte? catalogKeyCompareType = null, uint? nextCatalogId = null, bool catalogIdsReused = false)
        {
            byte[] image = new byte[(fragmentedData ? 32 : 16) * Block];
            Span<byte> volume = image.AsSpan(1024, 512);
            U16(volume, 0, hfsX ? (ushort)0x4858 : (ushort)0x482B);
            U16(volume, 2, hfsX ? (ushort)5 : (ushort)4);
            U32(volume, 4, catalogIdsReused ? 0x1000u : 0);
            U32(volume, 32, 1); // fileCount
            U32(volume, 36, 1); // folderCount excludes root
            U32(volume, 40, Block);
            U32(volume, 44, fragmentedData ? 32u : 16u);
            U32(volume, 48, fragmentedData ? 19u : 10u);
            U32(volume, 64, nextCatalogId ?? (additionalFolderParent is null ? 18u : 19u));
            Fork(volume.Slice(272, 80), (multiLeafCatalog ? 4 : 2) * Block, 2,
                multiLeafCatalog ? 4u : 2u);
            if (fragmentedData) Fork(volume.Slice(192, 80), 2 * Block, 4, 2);

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
                uint dataExtentCount = unsortedOverflowKeys ? 10u : 9u;
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
                    unsortedOverflowFileIds, unsortedOverflowForkTypes);
            }
            else
            {
                uint dataBlocks = dataExtentBlockCount ?? 1;
                uint dataStart = invalidDataExtent ? 16u : multiLeafCatalog ? 6u : 4u;
                Fork(file.AsSpan(88, 80), "HFS Plus data"u8.Length, dataStart, dataBlocks);
                if (dataBlockCount is { } count) U32(file.AsSpan(88), 12, count);
                uint resourceStart = invalidDataExtent ? 5u : dataStart + dataBlocks;
                Fork(file.AsSpan(168, 80), "Resource fork"u8.Length, resourceStart, 1);
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
            if (unknownCatalogRecord) U16(records[4], 0, 0x1234);
            if (reverseCatalogRecords) records.Reverse();
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

        private static void WriteExtentsTree(byte[] image, bool duplicateRecord, bool unsortedKeys,
            bool unsortedFileIds, bool unsortedForkTypes)
        {
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
            header[14 + 106 + 128] = 0xC0;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
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
