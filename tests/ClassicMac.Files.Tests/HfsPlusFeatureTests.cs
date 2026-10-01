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
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-free-blocks");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-counts");
    }

    [Fact]
    public void HfsPlusCatalogHonorsTheConfiguredVolumeEntryLimit()
    {
        byte[] image = HfsPlusFixture.Build();

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 2 })));
    }

    [Fact]
    public void HfsPlusCatalogAcceptsAVolumeAtItsConfiguredEntryLimit()
    {
        byte[] image = HfsPlusFixture.Build();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 3 })));

        Assert.Equal("Documents:Read Me", file.MacPath);
    }

    [Fact]
    public void HfsPlusCatalogBtreeHonorsTheConfiguredExpandedByteLimit()
    {
        byte[] image = HfsPlusFixture.Build();

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxExpandedBytesPerInput = 4096 })));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(36)]
    public void HfsPlusReportsWhenCatalogCountsDifferFromTheVolumeHeader(int countFieldOffset)
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + countFieldOffset), 2);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-counts" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusVolumeCountsIncludeCatalogFilesHiddenByHardLinkResolution()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink();
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-counts");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusVolumeCountsIncludePrivateDirectoryInodeFolders(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX);
        var diagnostics = new List<Diagnostic>();

        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-counts");
    }

    [Fact]
    public void HfsPlusReportsWhenFreeBlockCountDoesNotMatchTheAllocationBitmap()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 48), 1);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-free-blocks" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusAlternateHeaderMayOccupyTheTrailingPartialAllocationUnit()
    {
        byte[] image = HfsPlusFixture.Build();
        int originalLength = image.Length;
        Array.Resize(ref image, originalLength + 512);
        image.AsSpan(originalLength - 1024, 512).Clear();
        image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024, 512));
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header");
    }

    [Fact]
    public void HfsXReportsAnActiveFolderCountThatDiffersFromItsDirectSubfolders()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, catalogFolderFlags: 0x0010,
            catalogFolderCount: 1);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsXAcceptsAnActiveFolderCountThatMatchesItsDirectSubfolders()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, catalogFolderFlags: 0x0010,
            catalogFolderCount: 0);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count");
    }

    [Fact]
    public void HfsXFolderCountCountsSubfoldersRatherThanAllCatalogChildren()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, additionalFolderParent: 16,
            documentsFolderValence: 2, catalogFolderFlags: 0x0010, catalogFolderCount: 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count");
    }

    [Fact]
    public void HfsXFolderCountIncludesDirectoryHardLinkAliases()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, directoryHardLinkAlias: true,
            catalogFolderFlags: 0x0010, catalogFolderCount: 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count");
    }

    [Fact]
    public void HfsXReportsAFolderCountMismatchEvenWhenItsFlagIsClear()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, catalogFolderCount: 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusDoesNotInterpretTheHfsxOnlyFolderCountField()
    {
        byte[] image = HfsPlusFixture.Build(catalogFolderFlags: 0x0010, catalogFolderCount: 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-folder-count");
    }

    [Fact]
    public void HfsPlusAcceptsAFreeBlockCountThatMatchesTheAllocationBitmap()
    {
        byte[] image = HfsPlusFixture.Build();
        image[9 * HfsPlusFixture.Block + 1] &= 0xDF; // Leave unused allocation block 13 free.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 48), 1);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-free-blocks");
    }

    [Fact]
    public void HfsPlusReportsCatalogTextEncodingsMissingFromTheVolumeBitmap()
    {
        byte[] image = HfsPlusFixture.Build(fileTextEncoding: 1);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-encoding-bitmap" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusRequiresTextEncodingsUsedByFolderRecords()
    {
        byte[] image = HfsPlusFixture.Build(folderTextEncoding: 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-encoding-bitmap" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Theory]
    [InlineData(140, 49)] // MacFarsi uses bitmap bit 49.
    [InlineData(152, 48)] // MacUkrainian uses bitmap bit 48.
    public void HfsPlusMapsHighTextEncodingValuesToTheirDefinedBitmapBits(uint textEncoding, int bitmapBit)
    {
        ulong bitmap = 1UL | (1UL << bitmapBit);
        byte[] image = HfsPlusFixture.Build(fileTextEncoding: textEncoding, encodingBitmap: bitmap);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-encoding-bitmap");
    }

    [Theory]
    [InlineData(140)]
    [InlineData(152)]
    public void HfsPlusReportsMissingSpecialTextEncodingBitmapBits(uint textEncoding)
    {
        byte[] image = HfsPlusFixture.Build(fileTextEncoding: textEncoding);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-encoding-bitmap" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusAllowsUnusedTextEncodingBitmapBits()
    {
        byte[] image = HfsPlusFixture.Build(encodingBitmap: 1UL | (1UL << 63));
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-encoding-bitmap");
    }

    [Fact]
    public void HfsPlusFreeBlockCountIgnoresPaddingBitsAfterTheLastAllocationBlock()
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-free-blocks");
    }

    [Fact]
    public void HfsPlusReportsAMissingAlternateVolumeHeaderButReadsFromThePrimaryHeader()
    {
        byte[] image = HfsPlusFixture.Build();
        image.AsSpan(image.Length - 1024, 512).Clear();
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusReportsAnInvalidAlternateVolumeHeaderSignature()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(image.Length - 1024), 0x1234);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusReportsAnInvalidAlternateVolumeHeaderVersion()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(image.Length - 1024 + 2), 5);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusReportsAnImageTooSmallToContainAnAlternateVolumeHeader()
    {
        byte[] image = new byte[1536];
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024), 0x482B);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1026), 4);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 40), 512);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 44), 1);
        var diagnostics = new List<Diagnostic>();

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusUsesThePrimaryHeaderWhenTheAlternateHeaderIsStale()
    {
        byte[] image = HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(image.Length - 1024 + 32), 99);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header");
    }

    [Fact]
    public void HfsPlusAlternateHeaderMayBePastTheLastWholeAllocationBlock()
    {
        byte[] alignedVolume = HfsPlusFixture.Build();
        byte[] volumeWithPartialTail = new byte[alignedVolume.Length + 1500];
        alignedVolume.AsSpan(0, alignedVolume.Length - 1024).CopyTo(volumeWithPartialTail);
        alignedVolume.AsSpan(alignedVolume.Length - 1024, 512)
            .CopyTo(volumeWithPartialTail.AsSpan(volumeWithPartialTail.Length - 1024));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(volumeWithPartialTail),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-alternate-header");
    }

    [Fact]
    public void HfsPlusRequiresAnExtentsOverflowBtreeEvenWhenThereAreNoOverflowRecords()
    {
        byte[] image = HfsPlusFixture.Build();
        image.AsSpan(1024 + 192, 80).Clear();

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
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
        var context = new ContainerContext();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), context);
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal("shared file data"u8.ToArray(), link.DataFork.ToArray());
        Assert.Equal("shared resource"u8.ToArray(), link.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), link.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), link.FinderInfo.Creator);
        Assert.Equal(123u, link.HardLinkReference);
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, file => file.Name.ToString() == "iNode123");
        Assert.DoesNotContain(files, file => file.MacPath.Contains("HFS+ Private Data", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-file-link-count-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxHardLinkAliasWithDataForkIsReportedButUsesItsIndirectNode(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(hfsX: hfsX, aliasHasDataFork: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal("shared file data"u8.ToArray(), link.DataFork.ToArray());
        Assert.Equal("shared resource"u8.ToArray(), link.ResourceFork.ToArray());
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-hardlink-alias-has-data" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxRegularFileWithDirectoryHardLinkFlagIsRetainedAndReported(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build(hfsX: hfsX, fileHasUnexpectedLinkChainFlag: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-hardlink-chain-flag-unexpected" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxRegularFileWithMultipleLinksIsRetainedAndReported(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build(hfsX: hfsX, catalogFileMode: 0x81A4,
            catalogFileLinkCount: 2);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-file-link-count-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxJournalFileIsExcludedFromRegularFileLinkCountWarning(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build(hfsX: hfsX, fileName: ".journal",
            rootFolderValence: 2, documentsFolderValence: 0, catalogFileParentId: 2,
            catalogFileMode: 0x81A4, catalogFileLinkCount: 2, volumeAttributes: 0x2100);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal(".journal", file.MacPath);
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-file-link-count-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinksExposeInodeContentsAtTheirVisiblePath(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX);

        var context = new ContainerContext();
        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), context);

        Assert.True(files.Count == 1, string.Join(Environment.NewLine, context.Diagnostics));
        MacFile file = Assert.Single(files);
        Assert.Equal("Shared Folder:Inside", file.MacPath);
        Assert.Equal("directory data"u8.ToArray(), file.DataFork.ToArray());
        Assert.DoesNotContain(files, item => item.MacPath.Contains("Private Directory Data", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinksExposeEveryVisibleAlias(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, secondAlias: true);

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

        Assert.Equal(["Shared Copy:Inside", "Shared Folder:Inside"],
            files.Select(file => file.MacPath).Order(StringComparer.Ordinal));
        Assert.All(files, file => Assert.Equal("directory data"u8.ToArray(), file.DataFork.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkAncestorsWithChildLinkFlagAreValid(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX,
            aliasesInsideDocuments: true, documentsHasChildLink: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Documents:Shared Folder:Inside");
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-hardlink-ancestor-flag-missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkReportsAncestorMissingChildLinkFlag(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX,
            aliasesInsideDocuments: true, documentsHasChildLink: false);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Documents:Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-hardlink-ancestor-flag-missing" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkWithPartialFinderSignatureIsRetainedAndReported(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, validFinderInfo: false);
        var context = new ContainerContext();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), context);

        MacFile alias = Assert.Single(files);
        Assert.Equal("Shared Folder", alias.MacPath);
        Assert.Contains(context.Diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-hardlink-signature-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkMapsNestedContentsThroughEveryAlias(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, secondAlias: true, nestedContents: true);

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

        MacFile[] nestedFiles = files.Where(file => file.Name.ToString() == "Deep").ToArray();
        Assert.Equal(["Shared Copy:Nested:Deep", "Shared Folder:Nested:Deep"],
            nestedFiles.Select(file => file.MacPath).Order(StringComparer.Ordinal));
        Assert.All(nestedFiles, file => Assert.Equal("nested data"u8.ToArray(), file.DataFork.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkRequiresInodeLinkChainFlag(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, directoryInodeHasLinkChain: false);
        var context = new ContainerContext();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), context);

        MacFile alias = Assert.Single(files);
        Assert.Equal("Shared Folder", alias.MacPath);
        Assert.Contains(context.Diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-hardlink-target-missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkRequiresAValidFirstLinkAttribute(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, firstLinkAttributeValue: null);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkUsesItsFirstLinkAttribute(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, firstLinkAttributeValue: "21");
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkRejectsAnInvalidFirstLinkAttributeValue(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX,
            firstLinkAttributeValue: "not-a-catalog-id");
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkFollowsItsCompleteLinkChain(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, secondAlias: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(files, file => file.MacPath == "Shared Copy:Inside");
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkReportsABrokenLinkChain(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, secondAlias: true,
            breakDirectoryLinkChain: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinkReportsAnIncorrectInodeLinkCount(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, directoryHardLinkCount: 2);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-chain-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinksRequireAnImmutablePrivateDirectory(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, privateDirectoryOwnerFlags: 0);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-hardlink-private-directory-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxDirectoryHardLinksRequireAStickyPrivateDirectory(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithDirectoryHardLink(hfsX, privateDirectoryMode: 0x4000);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(files, file => file.MacPath == "Shared Folder:Inside");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "hfs.plus-hardlink-private-directory-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusFileWithOnlyPartOfTheHardLinkFinderSignatureIsRetainedAndReported()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(validHardLinkFinderInfo: false);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile file = Assert.Single(files, item => item.MacPath == "Documents:Shared Alias");

        Assert.Null(file.HardLinkReference);
        Assert.Empty(file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-signature-invalid" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Warning);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusHardLinkDoesNotResolveAnIndirectNodeNameWithLeadingZeroes(bool hfsX)
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(hfsX: hfsX, indirectNodeName: "iNode0123");
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal(123u, link.HardLinkReference);
        Assert.Empty(link.DataFork.ToArray());
        Assert.Empty(link.ResourceFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-indirect-name-invalid" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-target-missing" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusHardLinkReportsAnIndirectNodeLinkCountThatDiffersFromVisibleLinks()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(indirectLinkCount: 2);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Equal("shared file data"u8.ToArray(), link.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-count-mismatch" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusHardLinkReportsAnIndirectNodeWithNoVisibleAliases()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(includeHardLinkAlias: false, indirectLinkCount: 0);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.DoesNotContain(files, file => file.Name.ToString() == "iNode123");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-indirect-orphan" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void HfsPlusHardLinkDoesNotResolveAnIndirectNodeDirectory()
    {
        byte[] image = HfsPlusFixture.BuildWithHardLink(indirectNodeIsFolder: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));
        MacFile link = Assert.Single(files, file => file.MacPath == "Documents:Shared Alias");

        Assert.Empty(link.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-hardlink-indirect-not-file" &&
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

    [Fact]
    public void HfsPlusAttributesFileWithAllocatedBlocksCannotHaveAnEmptyBtree()
    {
        byte[] image = HfsPlusFixture.Build(includeAttributeFile: true);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(1024 + 352), 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusZeroLengthStartupForkStillOwnsItsAllocatedBlocks()
    {
        byte[] image = HfsPlusFixture.Build(includeStartupFile: true);
        Span<byte> startupFork = image.AsSpan(1024 + 432, 80);
        BinaryPrimitives.WriteUInt64BigEndian(startupFork, 0);
        BinaryPrimitives.WriteUInt32BigEndian(startupFork[16..], 4);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusStartupForkLogicalSizeMustFitItsAllocatedBlocks()
    {
        byte[] image = HfsPlusFixture.Build(includeStartupFile: true);
        Span<byte> startupFork = image.AsSpan(1024 + 432, 80);
        BinaryPrimitives.WriteUInt64BigEndian(startupFork, HfsPlusFixture.Block + 1UL);

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
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void HfsPlusExtentsBtreeAcceptsLegalSmallNodeSizes(ushort nodeSize)
    {
        byte[] image = HfsPlusFixture.Build();
        uint allocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 40));
        uint treeStartBlock = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 192 + 16));
        int treeOffset = checked((int)(treeStartBlock * allocationBlockSize));
        Span<byte> tree = image.AsSpan(treeOffset, HfsPlusFixture.Block);
        uint totalNodes = checked((uint)(HfsPlusFixture.Block / nodeSize));
        BinaryPrimitives.WriteUInt16BigEndian(tree[32..], nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(tree[36..], totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(tree[40..], totalNodes - 1);
        tree[^8..].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(tree[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(tree[(nodeSize - 4)..], 14 + 106);
        BinaryPrimitives.WriteUInt16BigEndian(tree[(nodeSize - 6)..], 14 + 106 + 128);
        BinaryPrimitives.WriteUInt16BigEndian(tree[(nodeSize - 8)..], checked((ushort)(nodeSize - 8)));

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:Read Me", file.MacPath);
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

    [Fact]
    public void HfsPlusBtreeFreeNodesMustBeZeroFilled()
    {
        byte[] image = HfsPlusFixture.Build(extraCatalogForkNode: true, catalogTotalNodes: 3);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(2 * HfsPlusFixture.Block + 14 + 26), 1);
        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        image[4 * HfsPlusFixture.Block] = 0x01;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusExtentsTreeDoesNotRequireFreeNodesToBeZeroFilled()
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);
        image[4 * HfsPlusFixture.Block + 3 * 512] = 0x01;

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesTreeDoesNotRequireFreeNodesToBeZeroFilled()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(10 * HfsPlusFixture.Block + 14 + 26), 1);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(10 * HfsPlusFixture.Block + 36), 5);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(1024 + 352), 5UL * HfsPlusFixture.Block);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 352 + 12), 5);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 352 + 16 + 4), 5);
        image[14 * HfsPlusFixture.Block] = 0x01;

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeNodeMapMustNotMarkUnreachableNodesAsAllocated()
    {
        byte[] image = HfsPlusFixture.Build(extraCatalogForkNode: true, catalogTotalNodes: 3);
        image[2 * HfsPlusFixture.Block + 14 + 106 + 128] = 0xE0;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeMustUseTheControlTreeType()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false);
        image[10 * HfsPlusFixture.Block + 14 + 36] = 0x80;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributesBtreeAcceptsAndReportsTheReservedHeaderValuesWrittenByMacOsX()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false);
        int headerRecordOffset = 10 * HfsPlusFixture.Block + 14;
        image[headerRecordOffset + 36] = 0xFF;
        image[headerRecordOffset + 37] = 0xBC;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-btree-type" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
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

    [Theory]
    [InlineData(266, true)]
    [InlineData(267, false)]
    [InlineData(268, false)]
    public void HfsPlusAttributesBtreeMaximumKeyLengthMustMatchItsDefinedKeyFormat(ushort maxKeyLength,
        bool isValid)
    {
        byte[] image = HfsPlusFixture.Build(includeAttributeFile: true);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(10 * HfsPlusFixture.Block + 34), maxKeyLength);

        if (isValid)
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        else
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
    [InlineData(true)]
    [InlineData(false)]
    public void HfsPlusCatalogFolderAndFileRecordsMustHaveExactLengths(bool folderRecord)
    {
        byte[] image = HfsPlusFixture.Build(catalogFolderDataHasTrailingByte: folderRecord,
            catalogFileDataHasTrailingByte: !folderRecord);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(520, true)]
    [InlineData(521, false)]
    public void HfsPlusCatalogThreadRecordsMustFitTheDefinedMaximum(int recordLength, bool valid)
    {
        byte[] image = HfsPlusFixture.Build(catalogThreadDataLength: recordLength);

        if (valid)
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        else
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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void HfsPlusBtreeLeafRecordCountMustMatchItsLeafNodes(bool extentsTree, uint leafRecords)
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: extentsTree);
        int headerNodeOffset = (extentsTree ? 4 : 2) * 4096;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(headerNodeOffset + 14 + 6), leafRecords);

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

    [Fact]
    public void HfsPlusBtreeRequiresUnusedHeaderMapBytesToBeZero()
    {
        const int nodeSize = 4096;
        byte[] image = HfsPlusFixture.Build();
        image[2 * nodeSize + 14 + 106 + 128 + 1] = 0x01;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeRequiresUnusedChainedMapBytesToBeZero()
    {
        const int blockSize = 4096;
        const int nodeSize = 512;
        byte[] image = BuildOversizedExtentsTree(addMapNode: true);
        image[4 * blockSize + 2 * nodeSize + 14 + 1] = 0x01;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeRejectsMapNodesBeyondThoseNeededToCoverTheTree()
    {
        byte[] image = BuildOversizedExtentsTree(addMapNode: true, addUnneededMapNode: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
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

    private static byte[] BuildOversizedExtentsTree(bool addMapNode, bool addUnneededMapNode = false)
    {
        const int blockSize = 4096;
        const int nodeSize = 512;
        int totalNodes = addUnneededMapNode ? 2050 : 2049;
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        const int totalBlocks = 300;
        const int allocatedBlocks = 257;
        Array.Resize(ref image, totalBlocks * blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 44), totalBlocks);
        Span<byte> extentsFork = image.AsSpan(1024 + 192, 80);
        BinaryPrimitives.WriteUInt64BigEndian(extentsFork, (ulong)nodeSize * (uint)totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[12..], allocatedBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[16..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(extentsFork[20..], allocatedBlocks);
        Span<byte> allocationFork = image.AsSpan(1024 + 112, 80);
        BinaryPrimitives.WriteUInt64BigEndian(allocationFork, (ulong)(totalBlocks + 7) / 8);
        BinaryPrimitives.WriteUInt32BigEndian(allocationFork[16..], 282);
        image.AsSpan(282 * blockSize, (totalBlocks + 7) / 8).Fill(0xFF);
        image[282 * blockSize + (totalBlocks - 1) / 8] = 0xF0; // Clear unused low bits after block 299.

        int treeOffset = 4 * blockSize;
        byte[] headerNode = image.AsSpan(treeOffset, nodeSize).ToArray();
        byte[] leaf = image.AsSpan(5 * blockSize, nodeSize).ToArray();
        image.AsSpan(treeOffset, totalNodes * nodeSize).Clear();
        headerNode.CopyTo(image, treeOffset);
        Span<byte> header = image.AsSpan(treeOffset, nodeSize);
        BinaryPrimitives.WriteUInt16BigEndian(header[32..], nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[36..], (uint)totalNodes);
        BinaryPrimitives.WriteUInt32BigEndian(header[40..],
            checked((uint)totalNodes - (addMapNode ? addUnneededMapNode ? 4u : 3u : 2u)));
        header.Slice(248, nodeSize - 256).Clear();
        header[248] = 0xC0; // Nodes 0 and 1 allocated; no map node links follow.
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 2)..], 14);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 4)..], 14 + 106);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 6)..], 14 + 106 + 128);
        BinaryPrimitives.WriteUInt16BigEndian(header[(nodeSize - 8)..], nodeSize - 8);

        if (addMapNode)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 2); // First map node follows header and leaf nodes.
            header[248] = addUnneededMapNode ? (byte)0xF0 : (byte)0xE0;
            // Nodes 0, 1 and 2 (and optionally 3) are allocated.
            Span<byte> mapNode = image.AsSpan(treeOffset + 2 * nodeSize, nodeSize);
            BinaryPrimitives.WriteUInt32BigEndian(mapNode, addUnneededMapNode ? 3u : 0u);
            mapNode[8] = 2;
            mapNode[10..12].Clear();
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[10..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[(nodeSize - 2)..], 14);
            BinaryPrimitives.WriteUInt16BigEndian(mapNode[(nodeSize - 4)..], nodeSize - 6);
            if (addUnneededMapNode)
            {
                Span<byte> extraMapNode = image.AsSpan(treeOffset + 3 * nodeSize, nodeSize);
                extraMapNode[8] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(extraMapNode[10..], 1);
                BinaryPrimitives.WriteUInt16BigEndian(extraMapNode[(nodeSize - 2)..], 14);
                BinaryPrimitives.WriteUInt16BigEndian(extraMapNode[(nodeSize - 4)..], nodeSize - 6);
            }
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

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    public void HfsPlusReusedCatalogIdsStillKeepNextCatalogIdOutsideTheReservedRange(uint nextCatalogId,
        bool valid)
    {
        byte[] image = HfsPlusFixture.Build(nextCatalogId: nextCatalogId, catalogIdsReused: true);

        if (valid)
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        else
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HfsPlusFileAndNonrootFolderIdsMustNotUseReservedCatalogIds(bool file)
    {
        byte[] image = file
            ? HfsPlusFixture.Build(catalogFileId: 15)
            : HfsPlusFixture.Build(catalogFolderId: 15);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    public void HfsPlusFolderRecordsMustNotSetFileOnlyFlags(ushort folderFlags)
    {
        byte[] image = HfsPlusFixture.Build(catalogFolderFlags: folderFlags);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(true, false, 0x4000)]
    [InlineData(false, false, 0x8000)]
    [InlineData(true, true, 0x4000)]
    [InlineData(false, true, 0x8000)]
    [InlineData(true, false, 0x3000)]
    [InlineData(false, false, 0x3000)]
    public void HfsPlusReportsCatalogBsdObjectTypeMismatches(bool file, bool hfsX, ushort mode)
    {
        byte[] image = file
            ? HfsPlusFixture.Build(catalogFileMode: mode, hfsX: hfsX)
            : HfsPlusFixture.Build(catalogFolderMode: mode, hfsX: hfsX);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics)));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-invalid-bsd-mode" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0x8000)]
    [InlineData(false, false, 0x4000)]
    [InlineData(true, true, 0x8000)]
    [InlineData(false, true, 0x4000)]
    public void HfsPlusAcceptsUninitializedOrMatchingCatalogBsdObjectTypes(bool file, bool hfsX, ushort mode)
    {
        byte[] image = file
            ? HfsPlusFixture.Build(catalogFileMode: mode, hfsX: hfsX)
            : HfsPlusFixture.Build(catalogFolderMode: mode, hfsX: hfsX);
        var diagnostics = new List<Diagnostic>();

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics)));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-invalid-bsd-mode");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HfsPlusFileAndFolderRecordsMustHaveNonemptyNames(bool folder)
    {
        byte[] image = folder
            ? HfsPlusFixture.Build(catalogFolderName: "")
            : HfsPlusFixture.Build(fileName: "");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("An HFS Plus file or folder catalog key has an empty name.", exception.Message);
    }

    [Fact]
    public void HfsPlusRootFolderMustUseTheReservedRootParentId()
    {
        byte[] image = HfsPlusFixture.Build(rootFolderParentId: 0);

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

    [Theory]
    [InlineData("caf\u00E9", false)]
    [InlineData("cafe\u0301", true)]
    [InlineData("\u00C5", false)]
    [InlineData("\u2126", true)]
    [InlineData("\uF900", true)]
    [InlineData("\U0001F600", true)]
    [InlineData("\uAC01", false)]
    [InlineData("\u1100\u1161\u11A8", true)]
    [InlineData("a\u0301\u0327", false)]
    [InlineData("a\u0327\u0301", true)]
    public void HfsPlusCatalogNamesMustUseCanonicalDecomposition(string fileName, bool valid)
    {
        byte[] image = HfsPlusFixture.Build(fileName);

        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
            return;
        }

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("Documents:" + fileName.Normalize(NormalizationForm.FormC), file.MacPath);
    }

    [Theory]
    [InlineData(false, "e\u0301\u0323", false)] // Acute (230) must move after dot below (220).
    [InlineData(false, "e\u0323\u0301", true)]
    [InlineData(false, "e\u0301\u0307", true)] // Equal combining classes retain their input order.
    [InlineData(true, "e\u0301\u0323", false)]
    [InlineData(true, "e\u0323\u0301", true)]
    [InlineData(true, "e\u0301\u0307", true)]
    public void HfsPlusAndHfsxCatalogNamesUseCanonicalCombiningClassOrder(bool hfsX, string fileName, bool valid)
    {
        byte[] image = HfsPlusFixture.Build(fileName, hfsX: hfsX);

        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
            return;
        }

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("Documents:" + fileName.Normalize(NormalizationForm.FormC), file.MacPath);
    }

    [Theory]
    [InlineData(".", false, false)]
    [InlineData("..", false, false)]
    [InlineData(".", true, false)]
    [InlineData("..", true, false)]
    [InlineData(".", false, true)]
    [InlineData("..", false, true)]
    [InlineData(".", true, true)]
    [InlineData("..", true, true)]
    public void HfsCatalogObjectsMustNotUseDotOrDotDotNames(string name, bool folder, bool hfsX)
    {
        byte[] image = folder
            ? HfsPlusFixture.Build(catalogFolderName: name, hfsX: hfsX)
            : HfsPlusFixture.Build(fileName: name, hfsX: hfsX);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(".hidden", false, false)]
    [InlineData("...", false, false)]
    [InlineData(".hidden", true, false)]
    [InlineData("...", true, false)]
    [InlineData(".hidden", false, true)]
    [InlineData("...", false, true)]
    [InlineData(".hidden", true, true)]
    [InlineData("...", true, true)]
    public void HfsCatalogObjectsMayUseNamesThatStartWithDots(string name, bool folder, bool hfsX)
    {
        byte[] image = folder
            ? HfsPlusFixture.Build(catalogFolderName: name, hfsX: hfsX)
            : HfsPlusFixture.Build(fileName: name, hfsX: hfsX);

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void HfsPlusCatalogNamesMustNotContainUnpairedSurrogates(ushort surrogate)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", catalogFileNameCodeUnitOverride: surrogate);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("An HFS Plus catalog name is not canonically decomposed.", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsNamesOrderCombiningMarksByCodePointAcrossSupplementaryCharacters(bool hfsX)
    {
        // Unicode 3.2 assigns U+1D165 combining class 216 and U+0300 class 230.
        byte[] canonicalImage = HfsPlusFixture.Build("a\U0001D165\u0300", hfsX: hfsX);
        byte[] nonCanonicalImage = HfsPlusFixture.Build("a\u0300\U0001D165", hfsX: hfsX);

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(canonicalImage), new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(nonCanonicalImage), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsNamesAreLimitedTo255Utf16CodeUnits(bool hfsX)
    {
        string maximumLengthName = new string('a', 253) + "\U0001F600";
        string overMaximumName = new string('a', 254) + "\U0001F600";
        byte[] maximumLengthImage = HfsPlusFixture.Build(maximumLengthName, hfsX: hfsX);
        byte[] overMaximumImage = HfsPlusFixture.Build(overMaximumName, hfsX: hfsX);

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(maximumLengthImage), new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(overMaximumImage), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusCatalogThreadNamesMustNotContainUnpairedSurrogates()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", catalogFileThreadNameCodeUnitOverride: 0xD800);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("An HFS Plus catalog thread name is not canonically decomposed.", exception.Message);
    }

    [Fact]
    public void HfsPlusCatalogThreadNamesMustUseCanonicalDecomposition()
    {
        byte[] image = HfsPlusFixture.Build("cafe\u0301", catalogFileThreadName: "caf\u00E9");

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData("\u01F8", false)] // Latin extended
    [InlineData("\u0400", false)] // Cyrillic
    [InlineData("\u0622", false)] // Arabic
    [InlineData("\u0A33", false)] // Gurmukhi
    [InlineData("\u0DDA", false)] // Sinhala
    [InlineData("\u1026", false)] // Myanmar
    [InlineData("\uFB1D", false)] // Hebrew presentation form
    [InlineData("\u01F9", false)]
    [InlineData("\u0218", false)]
    [InlineData("\u0219", false)]
    [InlineData("\u021A", false)]
    [InlineData("\u021B", false)]
    [InlineData("\u021E", false)]
    [InlineData("\u021F", false)]
    [InlineData("\u0226", false)]
    [InlineData("\u0227", false)]
    [InlineData("\u0228", false)]
    [InlineData("\u0229", false)]
    [InlineData("\u022A", false)]
    [InlineData("\u022B", false)]
    [InlineData("\u022C", false)]
    [InlineData("\u022D", false)]
    [InlineData("\u022E", false)]
    [InlineData("\u022F", false)]
    [InlineData("\u0230", false)]
    [InlineData("\u0231", false)]
    [InlineData("\u0232", false)]
    [InlineData("\u0233", false)]
    [InlineData("\u040D", false)]
    [InlineData("\u0450", false)]
    [InlineData("\u045D", false)]
    [InlineData("\u04EC", false)]
    [InlineData("\u04ED", false)]
    [InlineData("\u0623", false)]
    [InlineData("\u0624", false)]
    [InlineData("\u0625", false)]
    [InlineData("\u0626", false)]
    [InlineData("\u06C0", false)]
    [InlineData("\u06C2", false)]
    [InlineData("\u06D3", false)]
    [InlineData("\u0A36", false)]
    [InlineData("\u0DDC", false)]
    [InlineData("\u0DDD", false)]
    [InlineData("\u0DDE", false)]
    [InlineData("\u01F8", true)]
    [InlineData("\u0400", true)]
    [InlineData("\u0622", true)]
    [InlineData("\u0A33", true)]
    [InlineData("\u0DDA", true)]
    [InlineData("\u1026", true)]
    [InlineData("\uFB1D", true)]
    [InlineData("\u01F9", true)]
    [InlineData("\u0218", true)]
    [InlineData("\u0219", true)]
    [InlineData("\u021A", true)]
    [InlineData("\u021B", true)]
    [InlineData("\u021E", true)]
    [InlineData("\u021F", true)]
    [InlineData("\u0226", true)]
    [InlineData("\u0227", true)]
    [InlineData("\u0228", true)]
    [InlineData("\u0229", true)]
    [InlineData("\u022A", true)]
    [InlineData("\u022B", true)]
    [InlineData("\u022C", true)]
    [InlineData("\u022D", true)]
    [InlineData("\u022E", true)]
    [InlineData("\u022F", true)]
    [InlineData("\u0230", true)]
    [InlineData("\u0231", true)]
    [InlineData("\u0232", true)]
    [InlineData("\u0233", true)]
    [InlineData("\u040D", true)]
    [InlineData("\u0450", true)]
    [InlineData("\u045D", true)]
    [InlineData("\u04EC", true)]
    [InlineData("\u04ED", true)]
    [InlineData("\u0623", true)]
    [InlineData("\u0624", true)]
    [InlineData("\u0625", true)]
    [InlineData("\u0626", true)]
    [InlineData("\u06C0", true)]
    [InlineData("\u06C2", true)]
    [InlineData("\u06D3", true)]
    [InlineData("\u0A36", true)]
    [InlineData("\u0DDC", true)]
    [InlineData("\u0DDD", true)]
    [InlineData("\u0DDE", true)]
    public void CatalogThreadNamesFollowTheVolumeUnicodeDecompositionVersion(string legacyThreadName, bool hfsX)
    {
        if (hfsX)
        {
            // Keep the object name current and the thread name legacy so the assertion reaches thread validation.
            byte[] hfsXImage = HfsPlusFixture.Build(legacyThreadName.Normalize(NormalizationForm.FormD), hfsX: true,
                catalogFileThreadName: legacyThreadName);
            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(hfsXImage), new ContainerContext()));
            Assert.Equal("An HFS Plus catalog thread name is not canonically decomposed.", exception.Message);
            return;
        }

        byte[] hfsPlusImage = HfsPlusFixture.Build(legacyThreadName, catalogFileThreadName: legacyThreadName);
        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(hfsPlusImage), new ContainerContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusAndHfsxCatalogThreadNamesUseCanonicalCombiningClassOrder(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: hfsX, catalogFileThreadName: "e\u0301\u0323");

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsXCatalogNamesMustUseCanonicalDecomposition()
    {
        byte[] image = HfsPlusFixture.Build("caf\u00E9", hfsX: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData("a\u0307\u0307", true)] // Equal-class marks retain order; repeated marks are not a fixup sequence.
    [InlineData("\u0306\u0307", false)] // Legacy breve + dot-above maps to U+0310.
    [InlineData("\u0308\u030D", false)] // Legacy dialytika + vertical line above maps to dialytika + tonos.
    [InlineData("\u03B9\u0308\u030D", false)] // Legacy dialytika + perispomeni maps to dialytika + tonos.
    [InlineData("\u03B9\u0308\u0301", true)]
    [InlineData("a\u0310", true)]
    [InlineData("\u00A8\u030D", false)] // Legacy dialytika tonos representation.
    [InlineData("\u00A8\u0301", true)]
    [InlineData("\u0391\u030D", false)] // Legacy Greek tonos representations.
    [InlineData("\u0391\u0301", true)]
    [InlineData("\u0395\u030D", false)]
    [InlineData("\u0395\u0301", true)]
    [InlineData("\u0397\u030D", false)]
    [InlineData("\u0397\u0301", true)]
    [InlineData("\u0399\u030D", false)]
    [InlineData("\u0399\u0301", true)]
    [InlineData("\u039F\u030D", false)]
    [InlineData("\u039F\u0301", true)]
    [InlineData("\u03A5\u030D", false)]
    [InlineData("\u03A5\u0301", true)]
    [InlineData("\u03A9\u030D", false)]
    [InlineData("\u03A9\u0301", true)]
    [InlineData("\u03B1\u030D", false)]
    [InlineData("\u03B1\u0301", true)]
    [InlineData("\u03B5\u030D", false)]
    [InlineData("\u03B5\u0301", true)]
    [InlineData("\u03B7\u030D", false)]
    [InlineData("\u03B7\u0301", true)]
    [InlineData("\u03B9\u030D", false)]
    [InlineData("\u03B9\u0301", true)]
    [InlineData("\u03BF\u030D", false)]
    [InlineData("\u03BF\u0301", true)]
    [InlineData("\u03C5\u030D", false)]
    [InlineData("\u03C5\u0301", true)]
    [InlineData("\u03C9\u030D", false)]
    [InlineData("\u03C9\u0301", true)]
    [InlineData("\u03D2\u030D", false)]
    [InlineData("\u03D2\u0301", true)]
    [InlineData("\u09AC\u09BC", false)] // Bengali BA + NUKTA is corrected to RA with middle diagonal.
    [InlineData("\u09B0", true)]
    [InlineData("\u0B2F\u0B3C", false)] // Odia YA + NUKTA is corrected to its precomposed letter.
    [InlineData("\u0B5F", true)]
    [InlineData("\u0A21\u0A3C", false)] // Gurmukhi DDA + NUKTA is corrected to RRA.
    [InlineData("\u0A5C", true)]
    [InlineData("\u0E4D\u0E32", false)] // Thai NIKHAHIT + SARA AA is corrected to SARA AM.
    [InlineData("\u0E33", true)]
    [InlineData("\u0ECD\u0EB2", false)] // Lao NIGGAHITA + VOWEL SIGN AA is corrected to AM.
    [InlineData("\u0EB3", true)]
    [InlineData("\u0FB2\u0F80\u0F71", false)]
    [InlineData("\u0F77", true)]
    [InlineData("\u0FB3\u0F80\u0F71", false)]
    [InlineData("\u0F79", true)]
    public void HfsXCatalogNamesMustUsePostJaguarCanonicalDecompositions(string fileName, bool valid)
    {
        byte[] image = HfsPlusFixture.Build(fileName, hfsX: true);

        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
                ForkData.FromBytes(image), new ContainerContext()));
            return;
        }

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
        Assert.Equal("Documents:" + fileName.Normalize(NormalizationForm.FormC), file.MacPath);
    }

    [Theory]
    [InlineData("\u0306\u0307")]
    [InlineData("\u0308\u030D")]
    [InlineData("\u03B9\u0308\u030D")]
    [InlineData("\u03B1\u030D")]
    [InlineData("\u03B5\u030D")]
    [InlineData("\u03C9\u030D")]
    [InlineData("\u09AC\u09BC")]
    [InlineData("\u0B2F\u0B3C")]
    [InlineData("\u0A21\u0A3C")]
    [InlineData("\u0E4D\u0E32")]
    [InlineData("\u0ECD\u0EB2")]
    [InlineData("\u0FB2\u0F80\u0F71")]
    [InlineData("\u0FB3\u0F80\u0F71")]
    public void HfsXCatalogThreadNamesMustUsePostJaguarCanonicalDecompositions(string threadName)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: true, catalogFileThreadName: threadName);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData("a\u0307\u0307")]
    [InlineData("\u0306\u0307")]
    [InlineData("\u0308\u030D")]
    [InlineData("\u03B9\u0308\u030D")]
    [InlineData("\u00A8\u030D")]
    [InlineData("\u03B1\u030D")]
    [InlineData("\u03B5\u030D")]
    [InlineData("\u0391\u030D")]
    [InlineData("\u03C9\u030D")]
    [InlineData("\u09AC\u09BC")]
    [InlineData("\u0B2F\u0B3C")]
    [InlineData("\u0A21\u0A3C")]
    [InlineData("\u0E4D\u0E32")]
    [InlineData("\u0ECD\u0EB2")]
    [InlineData("\u0FB2\u0F80\u0F71")]
    [InlineData("\u0FB3\u0F80\u0F71")]
    public void HfsPlusAcceptsLegacyCanonicalNameDecompositions(string fileName)
    {
        byte[] image = HfsPlusFixture.Build(fileName);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.StartsWith("Documents:", file.MacPath, StringComparison.Ordinal);
    }

    [Fact]
    public void HfsXCatalogAndThreadNamesMayContainRepeatedEqualClassCombiningMarks()
    {
        const string fileName = "a\u0307\u0307";
        byte[] image = HfsPlusFixture.Build(fileName, hfsX: true, catalogFileThreadName: fileName);

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:" + fileName.Normalize(NormalizationForm.FormC), file.MacPath);
    }

    [Theory]
    [InlineData("\u01F8")]
    [InlineData("\u01F9")]
    [InlineData("\u0218")]
    [InlineData("\u0219")]
    [InlineData("\u021A")]
    [InlineData("\u021B")]
    [InlineData("\u021E")]
    [InlineData("\u021F")]
    [InlineData("\u0226")]
    [InlineData("\u0227")]
    [InlineData("\u0228")]
    [InlineData("\u0229")]
    [InlineData("\u022A")]
    [InlineData("\u022B")]
    [InlineData("\u022C")]
    [InlineData("\u022D")]
    [InlineData("\u022E")]
    [InlineData("\u022F")]
    [InlineData("\u0230")]
    [InlineData("\u0231")]
    [InlineData("\u0232")]
    [InlineData("\u0233")]
    [InlineData("\u0400")]
    [InlineData("\u040D")]
    [InlineData("\u0450")]
    [InlineData("\u045D")]
    [InlineData("\u04EC")]
    [InlineData("\u04ED")]
    [InlineData("\u0622")]
    [InlineData("\u0623")]
    [InlineData("\u0624")]
    [InlineData("\u0625")]
    [InlineData("\u0626")]
    [InlineData("\u06C0")]
    [InlineData("\u06C2")]
    [InlineData("\u06D3")]
    [InlineData("\u0A33")]
    [InlineData("\u0A36")]
    [InlineData("\u0DDA")]
    [InlineData("\u0DDC")]
    [InlineData("\u0DDD")]
    [InlineData("\u0DDE")]
    [InlineData("\u1026")]
    [InlineData("\uFB1D")]
    public void HfsPlusAcceptsANameThatWasCanonicalUnderUnicode21(string fileName)
    {
        byte[] image = HfsPlusFixture.Build(fileName);

        Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData("\u01F8")]
    [InlineData("\u01F9")]
    [InlineData("\u0218")]
    [InlineData("\u0219")]
    [InlineData("\u021A")]
    [InlineData("\u021B")]
    [InlineData("\u021E")]
    [InlineData("\u021F")]
    [InlineData("\u0226")]
    [InlineData("\u0227")]
    [InlineData("\u0228")]
    [InlineData("\u0229")]
    [InlineData("\u022A")]
    [InlineData("\u022B")]
    [InlineData("\u022C")]
    [InlineData("\u022D")]
    [InlineData("\u022E")]
    [InlineData("\u022F")]
    [InlineData("\u0230")]
    [InlineData("\u0231")]
    [InlineData("\u0232")]
    [InlineData("\u0233")]
    [InlineData("\u0400")]
    [InlineData("\u040D")]
    [InlineData("\u0450")]
    [InlineData("\u045D")]
    [InlineData("\u04EC")]
    [InlineData("\u04ED")]
    [InlineData("\u0622")]
    [InlineData("\u0623")]
    [InlineData("\u0624")]
    [InlineData("\u0625")]
    [InlineData("\u0626")]
    [InlineData("\u06C0")]
    [InlineData("\u06C2")]
    [InlineData("\u06D3")]
    [InlineData("\u0A33")]
    [InlineData("\u0A36")]
    [InlineData("\u0DDA")]
    [InlineData("\u0DDC")]
    [InlineData("\u0DDD")]
    [InlineData("\u0DDE")]
    [InlineData("\u1026")]
    [InlineData("\uFB1D")]
    public void HfsXRejectsANameThatWasCanonicalOnlyUnderUnicode21(string fileName)
    {
        byte[] image = HfsPlusFixture.Build(fileName, hfsX: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void CaseSensitiveHfsXVolumeListsItsFiles()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: true);

        Assert.True(HfsReader.Instance.CanRead(ForkData.FromBytes(image)));
        Assert.Equal("Documents:Read Me",
            Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0x0900u)]
    [InlineData(0x4100u)]
    public void HfsPlusVolumeWithInconsistentMountFlagsIsReportedButReadable(uint volumeAttributes)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: volumeAttributes);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-volume-inconsistent" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusVolumeWithCleanUnmountFlagsIsReadableWithoutAnInconsistencyWarning(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: hfsX);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.DoesNotContain(diagnostics,
            diagnostic => diagnostic.Code == "hfs.plus-volume-inconsistent");
    }

    [Theory]
    [InlineData(0x00000101u, false)]
    [InlineData(0x00010100u, false)]
    [InlineData(0x00000101u, true)]
    [InlineData(0x00010100u, true)]
    public void HfsPlusAndHfsxVolumesIgnoreReservedAttributeBits(uint volumeAttributes, bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: hfsX, volumeAttributes: volumeAttributes);

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournaledHfsPlusAndHfsxVolumesAreReadWithoutJournalReplayAndReported(bool hfsX)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", hfsX: hfsX, volumeAttributes: 0x2100,
            includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-not-replayed" &&
                                                   diagnostic.Severity == DiagnosticSeverity.Info);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid");
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalHeaderChecksumIsInvalid()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        HfsPlusFixture.CorruptJournalHeaderChecksum(image);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(4, 0UL)]
    [InlineData(8, 0UL)]
    [InlineData(16, 4097UL)]
    [InlineData(24, 0UL)]
    [InlineData(32, 0UL)]
    [InlineData(32, 16UL)]
    [InlineData(40, 0UL)]
    public void HfsPlusWarnsWhenJournalHeaderFieldsAreInvalid(int fieldOffset, ulong invalidValue)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        HfsPlusFixture.SetJournalHeaderField(image, fieldOffset, invalidValue);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusAcceptsJournalTransactionsThatWrapAroundTheJournalBuffer()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        HfsPlusFixture.SetJournalHeader(image, start: 3500, end: 800);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusChecksTheEntire2048ByteJournalHeaderSector(bool corruptUnusedSectorByte)
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        HfsPlusFixture.SetJournalHeader(image, start: 2048, end: 2048, headerSectorSize: 2048);
        if (corruptUnusedSectorByte) image[13 * HfsPlusFixture.Block + 1024] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.Equal(corruptUnusedSectorByte, diagnostics.Any(diagnostic =>
            diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning));
    }

    [Fact]
    public void HfsPlusSkipsJournalHeaderValidationWhenJournalNeedsInitialization()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block, flags: 5);
        HfsPlusFixture.ClearJournalHeader(image);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Single(files, file => file.MacPath == "Documents:Read Me");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid");
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalInfoBlockDoesNotPointToTheJournalFileExtent()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 14 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalFileUsesMoreThanOneExtent()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: 2 * HfsPlusFixture.Block);
        HfsPlusFixture.FragmentJournalFile(image);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalInfoBlockFileDoesNotMatchTheHeaderPointer()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100, includeJournalFiles: true);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        HfsPlusFixture.SetCatalogFileDataExtent(image, fileId: 18, allocationBlock: 11);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalInfoBlockHasNoMatchingRootJournalFiles()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalInfoBlockIsOutsideTheAllocationArea()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 16, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block, writeBlock: false);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalRangeExtendsPastTheVolume()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 15 * HfsPlusFixture.Block,
            journalSize: 2 * HfsPlusFixture.Block);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenJournalInfoBlockIsNotMarkedAllocated()
    {
        byte[] image = HfsPlusFixture.Build("Read Me", volumeAttributes: 0x2100);
        HfsPlusFixture.SetJournalInfoBlock(image, allocationBlock: 12, journalOffset: 13 * HfsPlusFixture.Block,
            journalSize: HfsPlusFixture.Block);
        image[9 * HfsPlusFixture.Block + 1] &= 0xF7;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-journal-info-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
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
        Assert.Contains("index key does not match the first key in its child subtree", exception.Message);
    }

    [Fact]
    public void HfsPlusCatalogIndexSeparatorMustNotSortAfterItsChildRecords()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), uint.MaxValue);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("index key does not match the first key in its child subtree", exception.Message);
    }

    [Fact]
    public void HfsPlusCatalogIndexSeparatorMustRemainAfterThePreviousChild()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(3 * 4096 + secondRecord + 2), 2);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("index key does not match the first key in its child subtree", exception.Message);
    }

    [Fact]
    public void HfsPlusCatalogIndexCannotPointToAnEmptyLeaf()
    {
        byte[] image = HfsPlusFixture.Build(multiLeafCatalog: true, emptySecondCatalogLeaf: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("empty child subtree", exception.Message);
    }

    [Fact]
    public void HfsPlusEmptyExtentsRootLeafIsValid()
    {
        byte[] image = HfsPlusFixture.Build(emptyExtentsRootLeaf: true);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
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
    public void HfsPlusExtentsBtreeIndexCannotPointToAnEmptyLeaf()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, indexedOverflowTree: true,
            emptyOverflowSecondLeaf: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("empty child subtree", exception.Message);
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
        Assert.Contains("index key does not match the first key in its child subtree", exception.Message);
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
        Assert.Contains("index key does not match the first key in its child subtree", exception.Message);
    }

    [Fact]
    public void CaseFoldingHfsXIndexSeparatorMayUseDifferentBytesForAnEqualChildKey()
    {
        byte[] image = HfsPlusFixture.Build(hfsX: true, multiLeafCatalog: true,
            catalogKeyCompareType: 0xCF, catalogSplitIndex: 2);
        int secondRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(3 * 4096 + 4096 - 4));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(3 * 4096 + secondRecord + 8), 'd');

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Documents:Read Me", file.MacPath);
    }

    [Fact]
    public void HfsWrapperReadsTheEmbeddedHfsPlusVolume()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Equal("HFS Plus data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("Resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.wrapper-extent-unallocated");
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
    public void HfsWrapperReportsAnEmbeddedVolumeExtentMarkedFreeAndStillReadsIt()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();
        int bitmapOffset = 3 * HfsBuilder.Block;
        image[bitmapOffset] &= unchecked((byte)~0x08); // Allocation block 4 starts the embedded volume.
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.wrapper-extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsWrapperReportsWhenItsVolumeBitmapIsTruncatedAndStillReadsTheEmbeddedVolume()
    {
        byte[] image = HfsPlusFixture.BuildWrapped();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x0E), ushort.MaxValue);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bitmap-truncated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
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
    public void HfsPlusForkCannotUseOverflowBeforeItsFirstEightExtents()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true, fragmentedPrimaryExtentCount: 7);

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
    public void EmptyForkCannotRetainAnExtentDescriptorWhenItClaimsNoAllocationBlocks()
    {
        byte[] image = HfsPlusFixture.Build(emptyResourceForkHasExtent: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusForkCannotHaveAnExtentAfterAnUnusedDescriptor()
    {
        byte[] image = HfsPlusFixture.Build(sparseDataExtentDescriptors: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusUnusedForkExtentDescriptorMustBeAllZero()
    {
        byte[] image = HfsPlusFixture.Build(unusedDataExtentHasStartBlock: true);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusUnusedOverflowExtentDescriptorMustBeAllZero()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(5 * HfsPlusFixture.Block + 36), 24);

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
    public void HfsPlusRejectsOverflowExtentsThatDoNotBelongToAFork()
    {
        byte[] image = HfsPlusFixture.BuildWithUnreferencedOverflowExtent(23, markAllocated: true);

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
    [InlineData(0)]
    [InlineData(15)]
    public void HfsPlusForksCannotClaimBlocksReservedForVolumeHeaders(uint allocationBlock)
    {
        byte[] image = HfsPlusFixture.BuildWithFileDataExtent(allocationBlock);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void HfsPlusForkMayUseBlocksImmediatelyInsideTheVolumeDataArea(uint allocationBlock)
    {
        byte[] image = HfsPlusFixture.BuildWithFileDataExtent(allocationBlock);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusBadBlockRecordMayIdentifyADamagedAlternateHeaderBlock()
    {
        byte[] image = HfsPlusFixture.BuildWithBadBlockExtent(31);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HfsPlusReportsSparedBlocksFlagMismatches(bool hasBadBlockRecords, bool setFlag)
    {
        byte[] image = hasBadBlockRecords
            ? HfsPlusFixture.Build(fragmentedData: true, badBlockExtent: true)
            : HfsPlusFixture.Build();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 4), setFlag ? 0x200u : 0u);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-spared-blocks" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusDoesNotReportSparedBlocksWhenHeaderMatches(bool hasBadBlockRecords)
    {
        byte[] image = hasBadBlockRecords
            ? HfsPlusFixture.Build(fragmentedData: true, badBlockExtent: true)
            : HfsPlusFixture.Build();
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-spared-blocks");
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
    public void HfsPlusReportsAttributeRecordsForMissingCatalogObjects()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeFork(dataBlock: 12);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(11 * HfsPlusFixture.Block + 18), 99);
        HfsPlusFixture.SetCatalogFileHasAttributesFlag(image, fileId: 17, enabled: false);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-attribute-orphan" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenAnObjectHasAttributeRecordsButNoHasAttributesFlag()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeFork(dataBlock: 12);
        HfsPlusFixture.SetCatalogFileHasAttributesFlag(image, fileId: 17, enabled: false);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-attribute-flag-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenHasAttributesFlagHasNoAttributeRecords()
    {
        byte[] image = HfsPlusFixture.Build();
        HfsPlusFixture.SetCatalogFileHasAttributesFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-attribute-flag-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenHasSecurityFlagHasNoAclAttribute()
    {
        byte[] image = HfsPlusFixture.Build();
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-security-flag-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenAclAttributeHasNoHasSecurityFlag()
    {
        byte[] inlineData = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0, inlineData)
        ]);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-security-flag-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusAcceptsMatchingHasSecurityFlagAndAclAttribute()
    {
        byte[] inlineData = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0, inlineData)
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-security-flag-mismatch");
    }

    [Fact]
    public void HfsPlusAcceptsWellFormedEmptyAclAttribute()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(HfsPlusFixture.BuildAcl(entryCount: 0)))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid");
    }

    [Fact]
    public void HfsPlusAcceptsTheNoAclFileSecuritySentinel()
    {
        byte[] acl = HfsPlusFixture.BuildAcl(entryCount: 0);
        BinaryPrimitives.WriteUInt32BigEndian(acl.AsSpan(36), uint.MaxValue);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(acl))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid");
    }

    [Fact]
    public void HfsPlusWarnsWhenAclAttributeHasInvalidFileSecurityMagic()
    {
        byte[] acl = HfsPlusFixture.BuildAcl(entryCount: 0);
        BinaryPrimitives.WriteUInt32BigEndian(acl, 0);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(acl))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenAclEntryCountDoesNotMatchPayloadLength()
    {
        byte[] acl = HfsPlusFixture.BuildAcl(entryCount: 0);
        BinaryPrimitives.WriteUInt32BigEndian(acl.AsSpan(36), 1);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(acl))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusWarnsWhenAclHasMoreThan128Entries()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(HfsPlusFixture.BuildAcl(entryCount: 129)))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusAcceptsAclWith128Entries()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(HfsPlusFixture.BuildAcl(entryCount: 128)))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HfsPlusAcceptsDefinedAclEntryKinds(uint kind)
    {
        byte[] acl = HfsPlusFixture.BuildAcl(entryCount: 1);
        BinaryPrimitives.WriteUInt32BigEndian(acl.AsSpan(60), kind);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(acl))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        _ = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    public void HfsPlusWarnsWhenAclEntryKindIsUndefined(uint kind)
    {
        byte[] acl = HfsPlusFixture.BuildAcl(entryCount: 1);
        BinaryPrimitives.WriteUInt32BigEndian(acl.AsSpan(60), kind);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "com.apple.system.Security", 0,
                HfsPlusFixture.InlineAttributeData(acl))
        ]);
        HfsPlusFixture.SetCatalogFileHasSecurityFlag(image, fileId: 17, enabled: true);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(HfsReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Documents:Read Me", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.plus-acl-invalid" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void HfsPlusAttributeForkMustAccountForEveryDeclaredAllocationBlock()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(includeOverflow: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkCanUseOverflowRecordsToAccountForItsDeclaredBlocks()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(includeOverflow: true);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributeForkCanContinueAcrossMultipleOverflowRecords()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(includeOverflow: true,
            overflowRecordCount: 2, totalBlocks: 17, logicalSize: 16UL * HfsPlusFixture.Block + 1);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributeForkOverflowMustStartAtTheFirstUnallocatedLogicalBlock()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(
            includeOverflow: true, overflowStartBlock: 9);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkLogicalSizeMustFitItsAllocatedBlocks()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(
            includeOverflow: true, logicalSize: 9UL * HfsPlusFixture.Block + 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkCannotUseOverflowBeforeItsFirstEightExtents()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(includeOverflow: true,
            primaryExtentCount: 7, totalBlocks: 8, logicalSize: 7UL * HfsPlusFixture.Block + 1,
            overflowStartBlock: 7);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeForkOverflowRecordsBeforeTheLastMustBeFull()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(includeOverflow: true,
            overflowRecordCount: 2, firstOverflowExtentCount: 7, totalBlocks: 16,
            logicalSize: 15UL * HfsPlusFixture.Block + 1);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusAttributeExtensionRequiresItsForkDataRecord()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeForkAndOverflow(
            includeOverflow: true, includeForkData: false);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
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
    public void HfsPlusEmptyAttributesRootLeafIsValid()
    {
        byte[] image = HfsPlusFixture.BuildWithEmptyAttributesRootLeaf();

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusAttributesBtreeIndexCannotPointToAnEmptyLeaf()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            emptySecondLeaf: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
        Assert.Contains("empty child subtree", exception.Message);
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
    public void HfsPlusAttributesBtreeIndexSeparatorMustMatchItsChildFirstKey()
    {
        byte[] image = HfsPlusFixture.BuildWithIndexedAttributesTree(invalidChild: false,
            separatorFileId: 17, separatorName: "omega");

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
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
    public void HfsPlusAttributesBtreeKeyPaddingMustBeZero()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecord(17, "alpha", 0, 0x40)
        ]);
        image[11 * HfsPlusFixture.Block + 14 + 2] = 1;

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusBtreeRecordOffsetsMustHaveEvenBoundaries()
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeRecord(0x40, dataBlock: 4);
        int freeSpaceOffsetPosition = 11 * HfsPlusFixture.Block + HfsPlusFixture.Block - 4;
        ushort freeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition), checked((ushort)(freeSpaceOffset + 1)));

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(0x20u)]
    [InlineData(0x30u)]
    public void HfsPlusDefinedAttributePayloadsMustHaveTheirExactLengths(uint recordType)
    {
        byte[] image = HfsPlusFixture.BuildWithAttributeRecord(recordType, dataBlock: 12);
        int freeSpaceOffsetPosition = 11 * HfsPlusFixture.Block + HfsPlusFixture.Block - 4;
        ushort freeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition), checked((ushort)(freeSpaceOffset + 2)));

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusInlineAttributePayloadMayUseTheBtreeAlignmentByte()
    {
        byte[] inlineData = new byte[18];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(inlineData.AsSpan(12), 1);
        inlineData[16] = 0xA5;
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "inline", 0, inlineData)
        ]);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusInlineAttributeReservedWordsAreIgnored()
    {
        byte[] inlineData = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(inlineData.AsSpan(4), 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(inlineData.AsSpan(8), uint.MaxValue);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "inline", 0, inlineData)
        ]);

        Assert.Equal("Documents:Read Me", Assert.Single(HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext())).MacPath);
    }

    [Fact]
    public void HfsPlusInlineAttributeRejectsARecordShorterThanItsHeader()
    {
        byte[] inlineData = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "inline", 0, inlineData)
        ]);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusInlineAttributeRejectsADeclaredPayloadLargerThanItsRecord()
    {
        byte[] inlineData = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(inlineData, 0x10);
        BinaryPrimitives.WriteUInt32BigEndian(inlineData.AsSpan(12), 3);
        byte[] image = HfsPlusFixture.BuildWithAttributeLeaf([
            HfsPlusFixture.AttributeRecordWithData(17, "inline", 0, inlineData)
        ]);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void HfsPlusOverflowExtentPayloadMustHaveItsExactLength()
    {
        byte[] image = HfsPlusFixture.Build(fragmentedData: true);
        int freeSpaceOffsetPosition = 5 * HfsPlusFixture.Block + HfsPlusFixture.Block - 4;
        ushort freeSpaceOffset = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(freeSpaceOffsetPosition), checked((ushort)(freeSpaceOffset + 2)));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfsPlusFileForkLogicalSizeMustFitItsAllocatedBlocks(bool resourceFork)
    {
        byte[] image = HfsPlusFixture.Build();
        int logicalSizeOffset = FindCatalogFileForkLogicalSize(image, resourceFork);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(logicalSizeOffset), HfsPlusFixture.Block + 1UL);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    private static int FindCatalogFileForkLogicalSize(byte[] image, bool resourceFork)
    {
        const int volumeHeaderOffset = 1024;
        const int catalogForkOffsetInVolumeHeader = 272;
        uint allocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(volumeHeaderOffset + 40));
        uint catalogStartBlock = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(
            volumeHeaderOffset + catalogForkOffsetInVolumeHeader + 16));
        int treeOffset = checked((int)(catalogStartBlock * allocationBlockSize));
        int nodeSize = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(treeOffset + 32));
        uint firstLeafNode = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(treeOffset + 24));
        int leafOffset = checked(treeOffset + (int)firstLeafNode * nodeSize);
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(leafOffset + 10));

        for (int index = 0; index < recordCount; index++)
        {
            int recordStartPosition = leafOffset + nodeSize - 2 * (index + 1);
            int recordStart = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(recordStartPosition));
            ushort keyLength = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(leafOffset + recordStart));
            int recordDataStart = (recordStart + 2 + keyLength + 1) & ~1;
            ushort recordType = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(leafOffset + recordDataStart));
            if (recordType == 2)
                return checked(leafOffset + recordDataStart + (resourceFork ? 112 : 88));
        }

        throw new InvalidOperationException("The fixture catalog has no file record.");
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

    [Fact]
    public void HfsPlusCatalogFolderParentChainCannotCycleAcrossTwoFolders()
    {
        byte[] image = HfsPlusFixture.Build(additionalFolderParent: 16, catalogFolderParentId: 18,
            rootFolderValence: 0);

        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(17)]
    public void HfsPlusFileParentMustBeAnExistingFolder(uint parentId)
    {
        byte[] image = HfsPlusFixture.Build(catalogFileParentId: parentId);

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
            bool badBlockOverlapsFileExtent = false, bool extraCatalogForkNode = false,
            bool catalogFolderDataHasTrailingByte = false, bool catalogFileDataHasTrailingByte = false,
            int? catalogThreadDataLength = null, uint? catalogFolderId = null, uint? catalogFileId = null,
            ushort catalogFolderFlags = 0, string? catalogFileThreadName = null,
            string catalogFolderName = "Documents", uint rootFolderParentId = 1,
            uint? fragmentedPrimaryExtentCount = null, bool emptyResourceForkHasExtent = false,
            bool sparseDataExtentDescriptors = false, bool unusedDataExtentHasStartBlock = false,
            uint? catalogFileParentId = null, ushort? catalogFileNameCodeUnitOverride = null,
            ushort? catalogFileThreadNameCodeUnitOverride = null, ushort catalogFileMode = 0,
            ushort catalogFolderMode = 0, uint? volumeAttributes = null,
            uint fileTextEncoding = 0, uint folderTextEncoding = 0, ulong encodingBitmap = 1,
            uint? catalogFolderCount = null, bool directoryHardLinkAlias = false,
            bool fileHasUnexpectedLinkChainFlag = false, uint? catalogFileLinkCount = null,
            uint catalogFolderParentId = 2, int? catalogSplitIndex = null,
            bool emptySecondCatalogLeaf = false, bool emptyExtentsRootLeaf = false,
            bool emptyOverflowSecondLeaf = false, bool includeJournalFiles = false)
        {
            uint volumeBlocks = deepCatalogTree ? 40u : indexedOverflowTree ? 48u : fragmentedData ? 32u : 16u;
            byte[] image = new byte[checked((int)volumeBlocks * Block)];
            Span<byte> volume = image.AsSpan(1024, 512);
            U16(volume, 0, hfsX ? (ushort)0x4858 : (ushort)0x482B);
            U16(volume, 2, hfsX ? (ushort)5 : (ushort)4);
            U32(volume, 4, volumeAttributes ?? (0x100u | (catalogIdsReused ? 0x1000u : 0) |
                (badBlockExtent ? 0x200u : 0)));
            U32(volume, 32, includeJournalFiles ? 3u : 1u); // fileCount
            U32(volume, 36, 1); // folderCount excludes root
            U32(volume, 40, Block);
            U32(volume, 44, volumeBlocks);
            U32(volume, 48, includeAllocationFile ? 0u : fragmentedData ? 19u : 10u);
            U32(volume, 64, nextCatalogId ?? (includeJournalFiles ? 20u : additionalFolderParent is null ? 18u : 19u));
            BinaryPrimitives.WriteUInt64BigEndian(volume[72..], encodingBitmap);
            int catalogForkNodes = deepCatalogTree ? 8 : multiLeafCatalog ? 4 : extraCatalogForkNode ? 3 : 2;
            Fork(volume.Slice(272, 80), catalogForkNodes * Block,
                deepCatalogTree ? 26u : 2u, checked((uint)catalogForkNodes));
            if (fragmentedData)
                Fork(volume.Slice(192, 80), (indexedOverflowTree ? 4 : 2) * Block,
                    indexedOverflowTree ? 26u : 4u, indexedOverflowTree ? 4u : 2u);
            else
            {
                Fork(volume.Slice(192, 80), (emptyExtentsRootLeaf ? 2 : 1) * Block, 8,
                    emptyExtentsRootLeaf ? 2u : 1u);
                WriteEmptyExtentsTree(image.AsSpan(8 * Block, Block), emptyExtentsRootLeaf);
                if (emptyExtentsRootLeaf)
                    WriteBTreeNode(image.AsSpan(9 * Block, Block), 0xFF, 1, 0, 0, []);
            }
            if (includeAllocationFile)
            {
                int bitmapLength = checked((int)((volumeBlocks + 7) / 8));
                uint allocationStartBlock = emptyExtentsRootLeaf ? 12u : 9u;
                Fork(volume.Slice(112, 80), bitmapLength, allocationStartBlock, 1);
                image.AsSpan(checked((int)allocationStartBlock * Block), bitmapLength).Fill(0xFF);
                if (!markDataForkAllocated) image[checked((int)allocationStartBlock * Block)] &= 0xF7;
            }
            if (includeAttributeFile)
            {
                Fork(volume.Slice(352, 80), Block, 10, 1);
                WriteEmptyAttributesTree(image.AsSpan(10 * Block, Block));
            }
            if (includeStartupFile) Fork(volume.Slice(432, 80), 1, 11, 1);

            byte[] root = new byte[88];
            U16(root, 0, 1);
            U32(root, 4, includeJournalFiles ? checked(rootFolderValence + 2) : rootFolderValence);
            U32(root, 8, 2);
            U32(root, 84, hfsX ? 1u : 0u);
            byte[] folder = new byte[88];
            U16(folder, 0, 1);
            U16(folder, 2, catalogFolderFlags);
            U32(folder, 4, documentsFolderValence);
            uint folderId = catalogFolderId ?? 16;
            U32(folder, 8, folderId);
            U32(folder, 80, folderTextEncoding);
            U32(folder, 84, catalogFolderCount ?? 0);
            U16(folder, 42, catalogFolderMode);
            byte[] file = new byte[248];
            U16(file, 0, 2);
            U16(file, 2, missingFileThreadFlag ? (ushort)0 : (ushort)2); // file thread exists
            if (fileHasUnexpectedLinkChainFlag)
                U16(file, 2, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(2)) | 0x0020));
            uint fileId = catalogFileId ?? (duplicateCatalogId ? 16u : 17u);
            U32(file, 8, fileId);
            if (catalogFileLinkCount is { } linkCount) U32(file, 44, linkCount);
            U32(file, 80, fileTextEncoding);
            U16(file, 42, catalogFileMode);
            if (directoryHardLinkAlias)
            {
                U16(file, 2, 0x0022);
            }
            U32(file, 12, 2_500_000_000);
            U32(file, 16, 2_600_000_000);
            "TEXTttxt"u8.CopyTo(file.AsSpan(48));
            if (directoryHardLinkAlias) "alisMACS"u8.CopyTo(file.AsSpan(48));
            if (fragmentedData)
            {
                uint primaryExtentCount = fragmentedPrimaryExtentCount ?? 8u;
                uint dataExtentCount = indexedOverflowTree ? 17u : fragmentedPrimaryExtentCount is not null
                    ? checked(primaryExtentCount + 1)
                    : badBlockExtent ? 8u : unsortedOverflowKeys ? 10u : 9u;
                BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(88), (ulong)dataExtentCount * Block);
                U32(file, 88 + 12, dataExtentCount);
                for (uint index = 0; index < primaryExtentCount; index++)
                {
                    U32(file, 88 + 16 + checked((int)index * 8), checked(6u + index * 2));
                    U32(file, 88 + 20 + checked((int)index * 8), 1);
                }
                for (int index = 0; index < dataExtentCount; index++)
                {
                    int physicalBlock = indexedOverflowTree && index >= 8
                        ? 30 + index - 8
                        : 6 + index * 2;
                    image.AsSpan(physicalBlock * Block, Block).Fill(checked((byte)(index + 1)));
                }
                Fork(file.AsSpan(168, 80), "Resource fork"u8.Length, 25, 1);
                "Resource fork"u8.CopyTo(image.AsSpan(25 * Block));
                WriteExtentsTree(image, duplicateOverflowExtent, unsortedOverflowKeys,
                    unsortedOverflowFileIds, unsortedOverflowForkTypes, indexedOverflowTree,
                    emptyOverflowSecondLeaf,
                    badBlockExtent, badBlockOverlapsFileExtent, primaryExtentCount,
                    6 + primaryExtentCount * 2);
            }
            else
            {
                uint dataBlocks = dataExtentBlockCount ?? 1;
                uint dataStart = invalidDataExtent ? 16u : multiLeafCatalog ? 6u : extraCatalogForkNode ? 5u : 4u;
                Fork(file.AsSpan(88, 80), "HFS Plus data"u8.Length, dataStart, dataBlocks);
                if (dataBlockCount is { } count) U32(file.AsSpan(88), 12, count);
                uint resourceStart = invalidDataExtent ? 5u : overlappingFileForks ? dataStart : dataStart + dataBlocks;
                Fork(file.AsSpan(168, 80), zeroLengthResourceFork ? 0 : "Resource fork"u8.Length, resourceStart, 1);
                if (sparseDataExtentDescriptors)
                {
                    U32(file.AsSpan(88), 32, 24);
                    U32(file.AsSpan(88), 36, 1);
                }
                if (unusedDataExtentHasStartBlock) U32(file.AsSpan(88), 24, 24);
                if (emptyResourceForkHasExtent)
                {
                    file.AsSpan(168, 8).Clear();
                    U32(file.AsSpan(168), 12, 0);
                }
                uint dataStorageBlock = invalidDataExtent ? 4u : dataStart;
                "HFS Plus data"u8.CopyTo(image.AsSpan((int)dataStorageBlock * Block));
                "Resource fork"u8.CopyTo(image.AsSpan((int)resourceStart * Block));
            }

            if (catalogFolderDataHasTrailingByte) folder = [.. folder, 0];
            if (catalogFileDataHasTrailingByte) file = [.. file, 0];

            var records = new List<byte[]>
            {
                Record(rootFolderParentId, "Volume", root),
                Record(2, "", Thread(rootFolderParentId, "Volume", 3)),
                Record(catalogFolderParentId, catalogFolderName, folder),
            };
            if (!omitFolderThread)
            {
                records.Add(Record(folderId, "", Thread(catalogFolderParentId, catalogFolderName, 3)));
                if (duplicateFolderThread)
                    records.Add(Record(folderId, "", Thread(catalogFolderParentId, catalogFolderName, 3)));
            }
            uint fileParentId = catalogFileParentId ?? folderId;
            byte[] catalogFileRecord = Record(fileParentId, fileName, file);
            if (catalogFileNameCodeUnitOverride is { } codeUnit)
                U16(catalogFileRecord, 8, codeUnit);
            if (fileParentId == 2)
                records.Insert(fileName.StartsWith(".", StringComparison.Ordinal) ? 2 : 3, catalogFileRecord);
            else records.Add(catalogFileRecord);
            if (!omitFileThread)
            {
                byte[] fileThread = Record(fileId, nonEmptyFileThreadKey ? "Thread" : "",
                    Thread(invalidFileThread ? 2u : fileParentId,
                        invalidFileThread ? "Other" : catalogFileThreadName ?? fileName,
                        wrongFileThreadKind ? (ushort)3 : (ushort)4));
                if (fileId < 16) records.Insert(3, fileThread);
                else records.Add(fileThread);
                ushort? threadCodeUnitOverride = catalogFileThreadNameCodeUnitOverride ??
                    catalogFileNameCodeUnitOverride;
                if (threadCodeUnitOverride is { } threadCodeUnit && fileId >= 16)
                    U16(records[^1], 18, threadCodeUnit);
            }
            if (includeJournalFiles)
            {
                records.Insert(2, JournalFileRecord(18, ".journal_info_block", startBlock: 12, logicalSize: 180));
                records.Insert(2, JournalFileRecord(19, ".journal", startBlock: 13, logicalSize: Block));
                records.Add(Record(18, "", Thread(2, ".journal_info_block", 4)));
                records.Add(Record(19, "", Thread(2, ".journal", 4)));
            }
            if (additionalFolderParent is { } additionalParent)
            {
                byte[] additionalFolder = new byte[88];
                U16(additionalFolder, 0, 1);
                U32(additionalFolder, 4, additionalParent == 18 ? 1u : 0u);
                U32(additionalFolder, 8, 18);
                byte[] additionalFolderRecord = Record(additionalParent, "zzzz", additionalFolder);
                int insertion = records.FindIndex(record =>
                    BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(2)) > additionalParent);
                records.Insert(insertion < 0 ? records.Count : insertion, additionalFolderRecord);
                records.Add(Record(18, "", Thread(additionalParent, "zzzz", 3)));
            }
            if (orphanFileThread) records.Add(Record(42, "", Thread(16, "Missing", 4)));
            if (catalogThreadDataLength is { } threadDataLength)
            {
                byte[] threadRecord = records[1];
                int dataOffset = 2 + BinaryPrimitives.ReadUInt16BigEndian(threadRecord);
                int currentDataLength = threadRecord.Length - dataOffset;
                if (threadDataLength < currentDataLength)
                    throw new ArgumentOutOfRangeException(nameof(catalogThreadDataLength));
                byte[] paddedThread = new byte[dataOffset + threadDataLength];
                threadRecord.AsSpan(0, threadRecord.Length).CopyTo(paddedThread);
                records[1] = paddedThread;
            }
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
                image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024, 512));
                return image;
            }
            if (multiLeafCatalog)
            {
                int split = catalogSplitIndex ?? records.Count / 2;
                WriteBTreeNode(image.AsSpan(3 * Block, Block), 0, 2,
                    invalidCatalogIndexForwardLink ? 1u : 0u,
                    invalidCatalogIndexBackwardLink ? 1u : 0u,
                    [IndexRecord(records[0], invalidCatalogIndexChild ? 4u : 2u),
                        IndexRecord(records[emptySecondCatalogLeaf ? records.Count - 1 : split], 3)]);
                WriteBTreeNode(image.AsSpan(4 * Block, Block), 0xFF, 1, 3, 0,
                    emptySecondCatalogLeaf ? records.ToArray() : records.Take(split).ToArray());
                WriteBTreeNode(image.AsSpan(5 * Block, Block), 0xFF, 1, 0, 2,
                    emptySecondCatalogLeaf ? [] : records.Skip(split).ToArray());
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
            image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024, 512));
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
            U16(mdb, 0x0E, 3); // volume bitmap starts in logical block 3
            U16(mdb, 0x1C, 2); // drAlBlSt is measured in 512-byte blocks
            U16(mdb, 0x12, checked((ushort)(wrapper.Length / 512 - 2)));
            U16(mdb, 0x7C, 0x482B); // drEmbedSigWord: HFS Plus
            U16(mdb, 0x7E, 4); // embedded volume starts at allocation block 4
            U16(mdb, 0x80, checked((ushort)(embedded.Length / 512)));
            for (uint block = 4; block < 4 + embedded.Length / 512; block++)
                wrapper[3 * 512 + (int)(block / 8)] |= (byte)(0x80 >> (int)(block & 7));
            return wrapper;
        }

        public static byte[] BuildWithAttributeFork(uint dataBlock) =>
            BuildWithAttributeTree(dataBlock, 0x20);

        public static void SetCatalogFileHasAttributesFlag(byte[] image, uint fileId, bool enabled)
            => SetCatalogFileFlag(image, fileId, 0x0004, enabled);

        public static void SetCatalogFileHasSecurityFlag(byte[] image, uint fileId, bool enabled)
            => SetCatalogFileFlag(image, fileId, 0x0008, enabled);

        public static void SetJournalInfoBlock(byte[] image, uint allocationBlock, ulong journalOffset,
            ulong journalSize, uint flags = 1, bool writeBlock = true)
        {
            U32(image, 1024 + 12, allocationBlock);
            if (!writeBlock) return;
            Span<byte> journalInfo = image.AsSpan(checked((int)allocationBlock * Block), 180);
            U32(journalInfo, 0, flags);
            BinaryPrimitives.WriteUInt64BigEndian(journalInfo[36..], journalOffset);
            BinaryPrimitives.WriteUInt64BigEndian(journalInfo[44..], journalSize);
            if (journalOffset <= (ulong)image.Length && journalSize >= 512 &&
                journalOffset <= (ulong)image.Length - 512)
                SetJournalHeader(image, journalOffset, journalSize, start: 512, end: 512);
        }

        public static void SetJournalHeader(byte[] image, ulong start, ulong end, int headerSectorSize = 512)
        {
            SetJournalHeader(image, 13UL * Block, Block, start, end, headerSectorSize);
        }

        private static void SetJournalHeader(byte[] image, ulong journalOffset, ulong journalSize,
            ulong start, ulong end, int headerSectorSize = 512)
        {
            Span<byte> header = image.AsSpan(checked((int)journalOffset), headerSectorSize);
            header.Clear();
            U32(header, 0, 0x4A4E4C78);
            U32(header, 4, 0x12345678);
            BinaryPrimitives.WriteUInt64BigEndian(header[8..], start);
            BinaryPrimitives.WriteUInt64BigEndian(header[16..], end);
            BinaryPrimitives.WriteUInt64BigEndian(header[24..], journalSize);
            U32(header, 32, 4096);
            U32(header, 40, checked((uint)headerSectorSize));
            U32(header, 36, JournalChecksum(header));
        }

        public static void CorruptJournalHeaderChecksum(byte[] image)
        {
            int checksumOffset = checked(13 * Block + 36);
            uint checksum = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(checksumOffset, 4));
            U32(image, checksumOffset, checksum ^ 1);
        }

        public static void SetJournalHeaderField(byte[] image, int fieldOffset, ulong value)
        {
            Span<byte> header = image.AsSpan(13 * Block, 512);
            if (fieldOffset is 8 or 16 or 24)
                BinaryPrimitives.WriteUInt64BigEndian(header[fieldOffset..], value);
            else
                U32(header, fieldOffset, checked((uint)value));
            U32(header, 36, 0);
            uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(header[40..]);
            U32(header, 36, JournalChecksum(header[..checked((int)headerSize)]));
        }

        public static void ClearJournalHeader(byte[] image)
        {
            image.AsSpan(13 * Block, 512).Clear();
        }

        private static uint JournalChecksum(ReadOnlySpan<byte> bytes)
        {
            uint checksum = 0;
            foreach (byte value in bytes)
                checksum = unchecked((checksum << 8) ^ (checksum + value));
            return ~checksum;
        }

        public static void FragmentJournalFile(byte[] image)
        {
            Span<byte> file = FindCatalogFileData(image, fileId: 19);
            Span<byte> fork = file[88..168];
            BinaryPrimitives.WriteUInt64BigEndian(fork, 2UL * Block);
            U32(fork, 12, 2);
            U32(fork, 16 + 8, 14);
            U32(fork, 20 + 8, 1);
        }

        public static void SetCatalogFileDataExtent(byte[] image, uint fileId, uint allocationBlock)
        {
            Span<byte> file = FindCatalogFileData(image, fileId);
            U32(file, 88 + 16, allocationBlock);
        }

        private static Span<byte> FindCatalogFileData(byte[] image, uint fileId)
        {
            Span<byte> leaf = image.AsSpan(3 * Block, Block);
            int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
            for (int index = 0; index < recordCount; index++)
            {
                int recordStart = BinaryPrimitives.ReadUInt16BigEndian(leaf[(Block - 2 * (index + 1))..]);
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(leaf[recordStart..]);
                int dataStart = recordStart + 2 + keyLength;
                if (BinaryPrimitives.ReadUInt16BigEndian(leaf[dataStart..]) == 2 &&
                    BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataStart + 8)..]) == fileId)
                    return leaf[dataStart..(dataStart + 248)];
            }
            throw new InvalidOperationException($"The HFS Plus fixture has no file record {fileId}.");
        }

        private static byte[] JournalFileRecord(uint fileId, string name, uint startBlock, int logicalSize)
        {
            byte[] file = new byte[248];
            U16(file, 0, 2);
            U16(file, 2, 2);
            U32(file, 8, fileId);
            Fork(file.AsSpan(88, 80), logicalSize, startBlock, 1);
            return Record(2, name, file);
        }

        private static void SetCatalogFileFlag(byte[] image, uint fileId, ushort flag, bool enabled)
        {
            Span<byte> leaf = image.AsSpan(3 * Block, Block);
            int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
            for (int index = 0; index < recordCount; index++)
            {
                int recordStart = BinaryPrimitives.ReadUInt16BigEndian(leaf[(Block - 2 * (index + 1))..]);
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(leaf[recordStart..]);
                int dataStart = recordStart + 2 + keyLength;
                if (BinaryPrimitives.ReadUInt16BigEndian(leaf[dataStart..]) != 2 ||
                    BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataStart + 8)..]) != fileId) continue;

                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(leaf[(dataStart + 2)..]);
                U16(leaf, dataStart + 2, enabled ? (ushort)(flags | flag) : (ushort)(flags & ~flag));
                return;
            }

            throw new InvalidOperationException($"The HFS Plus fixture has no file record for CNID {fileId}.");
        }

        private static void SetCatalogObjectHasAttributesFlagIfPresent(byte[] image, uint objectId)
        {
            Span<byte> leaf = image.AsSpan(3 * Block, Block);
            int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
            for (int index = 0; index < recordCount; index++)
            {
                int recordStart = BinaryPrimitives.ReadUInt16BigEndian(leaf[(Block - 2 * (index + 1))..]);
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(leaf[recordStart..]);
                int dataStart = recordStart + 2 + keyLength;
                if (dataStart + 12 > Block) continue;

                ushort kind = BinaryPrimitives.ReadUInt16BigEndian(leaf[dataStart..]);
                if (kind is not (1 or 2) ||
                    BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataStart + 8)..]) != objectId) continue;

                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(leaf[(dataStart + 2)..]);
                U16(leaf, dataStart + 2, (ushort)(flags | 0x0004));
                return;
            }
        }

        private static void SetCatalogAttributeFlagsForRecords(byte[] image, IEnumerable<byte[]> records)
        {
            foreach (byte[] record in records)
                if (record.Length >= 8)
                    SetCatalogObjectHasAttributesFlagIfPresent(image,
                        BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4)));
        }

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
            bool includeIndirectNode = true, string indirectNodeName = "iNode123", uint indirectLinkCount = 1,
            bool includeHardLinkAlias = true, bool indirectNodeIsFolder = false,
            bool validHardLinkFinderInfo = true, bool aliasHasDataFork = false)
        {
            const string privateDirectory = "\0\0\0\0HFS+ Private Data";
            const string data = "shared file data";
            const string resource = "shared resource";
            byte[] image = Build(hfsX: hfsX);
            byte[] root = FolderData(2, 2);
            if (hfsX) U32(root, 84, 2);
            byte[] documents = FolderData(16, includeHardLinkAlias ? 2u : 1u);
            byte[] privateFolder = FolderData(18, includeIndirectNode ? 1u : 0u);
            if (hfsX && includeIndirectNode && indirectNodeIsFolder) U32(privateFolder, 84, 1);
            byte[] readMe = FileData(17, 4, "HFS Plus data"u8, 5, "Resource fork"u8);
            byte[] inode = FileData(19, 6, Encoding.UTF8.GetBytes(data), 7, Encoding.UTF8.GetBytes(resource));
            U32(inode, 44, indirectLinkCount);
            byte[] link = new byte[248];
            U16(link, 0, 2);
            U16(link, 2, 2);
            U32(link, 8, 20);
            U32(link, 44, linkReference);
            if (validHardLinkFinderInfo)
                "hlnkhfs+"u8.CopyTo(link.AsSpan(48));
            else
                "hlnkBAD!"u8.CopyTo(link.AsSpan(48));
            if (aliasHasDataFork)
            {
                Fork(link.AsSpan(88, 80), "unexpected alias data"u8.Length, 12, 1);
                "unexpected alias data"u8.CopyTo(image.AsSpan(12 * Block));
            }
            byte[][] hardLinkEntry = includeHardLinkAlias ? [Record(16, "Shared Alias", link)] : [];
            byte[][] hardLinkThread = includeHardLinkAlias
                ? [Record(20, "", Thread(16, "Shared Alias", 4))]
                : [];

            byte[][] rootEntries = hfsX
                ? [Record(2, "", Thread(1, "Volume", 3)), Record(2, privateDirectory, privateFolder),
                    Record(2, "Documents", documents)]
                : [Record(2, "", Thread(1, "Volume", 3)), Record(2, "Documents", documents),
                    Record(2, privateDirectory, privateFolder)];
            byte[][] indirectNodeRecords = !includeIndirectNode
                ? []
                : indirectNodeIsFolder
                    ? [Record(18, indirectNodeName, FolderData(19, 0)),
                        Record(19, "", Thread(18, indirectNodeName, 3))]
                    : [Record(18, indirectNodeName, inode), Record(19, "", Thread(18, indirectNodeName, 4))];
            byte[][] records =
            [
                Record(1, "Volume", root),
                .. rootEntries,
                Record(16, "", Thread(2, "Documents", 3)),
                Record(16, "Read Me", readMe),
                .. hardLinkEntry,
                Record(17, "", Thread(16, "Read Me", 4)),
                Record(18, "", Thread(2, privateDirectory, 3)),
                .. indirectNodeRecords,
                .. hardLinkThread,
            ];

            WriteBTreeNode(image.AsSpan(3 * Block, Block), 0xFF, 1, 0, 0, records);
            U32(image.AsSpan(2 * Block, Block), 20, checked((uint)records.Length));
            Span<byte> volume = image.AsSpan(1024, 512);
            U32(volume, 32, 1u + (includeIndirectNode && !indirectNodeIsFolder ? 1u : 0u) +
                (includeHardLinkAlias ? 1u : 0u));
            U32(volume, 36, 2u + (includeIndirectNode && indirectNodeIsFolder ? 1u : 0u));
            U32(volume, 64, 21);
            Encoding.UTF8.GetBytes(data).CopyTo(image.AsSpan(6 * Block));
            Encoding.UTF8.GetBytes(resource).CopyTo(image.AsSpan(7 * Block));
            return image;
        }

        public static byte[] BuildWithDirectoryHardLink(bool hfsX, bool secondAlias = false,
            bool validFinderInfo = true, bool nestedContents = false, bool directoryInodeHasLinkChain = true,
            string? firstLinkAttributeValue = "21", bool breakDirectoryLinkChain = false,
            uint? directoryHardLinkCount = null, byte privateDirectoryOwnerFlags = 0x02,
            ushort privateDirectoryMode = 0x4200, bool aliasesInsideDocuments = false,
            bool documentsHasChildLink = true)
        {
            const string privateDirectory = ".HFS+ Private Directory Data\r";
            const string directoryInode = "dir_19";
            byte[] image = Build(hfsX: hfsX, includeAttributeFile: true);
            uint aliasCount = secondAlias ? 2u : 1u;
            byte[] root = FolderData(2, aliasesInsideDocuments ? 2u : secondAlias ? 4u : 3u);
            byte[] documents = FolderData(16, aliasesInsideDocuments ? aliasCount : 0u);
            if (aliasesInsideDocuments && documentsHasChildLink) U16(documents, 2, 0x0040);
            byte[] privateFolder = FolderData(18, 1);
            privateFolder[41] = privateDirectoryOwnerFlags; // UF_IMMUTABLE.
            U16(privateFolder, 42, privateDirectoryMode); // S_ISVTX.
            byte[] inodeFolder = FolderData(19, nestedContents ? 2u : 1u);
            if (directoryInodeHasLinkChain) U16(inodeFolder, 2, 0x0020);
            U32(inodeFolder, 44, directoryHardLinkCount ?? (secondAlias ? 2u : 1u));
            byte[] contents = FileData(20, 4, "directory data"u8, 5, []);
            contents.AsSpan(168, 80).Clear();
            byte[] nestedFolder = FolderData(24, 1);
            byte[] nestedFile = FileData(25, 6, "nested data"u8, 7, []);
            nestedFile.AsSpan(168, 80).Clear();
            byte[] MakeAlias(uint fileId)
            {
                byte[] alias = FileData(fileId, 6, [], 7, []);
                U16(alias, 2, 0x0022); // Thread exists; hard-link chain.
                U32(alias, 32, fileId == 21 ? 0u : breakDirectoryLinkChain ? 0u : 21u);
                U32(alias, 36, fileId == 21 && secondAlias ? 22u : 0u);
                U32(alias, 44, 19); // Directory inode catalog ID.
                (validFinderInfo ? "alisMACS"u8 : "alisMISS"u8).CopyTo(alias.AsSpan(48));
                U16(alias, 56, (ushort)FinderFlags.IsAlias);
                alias.AsSpan(88, 80).Clear();
                alias.AsSpan(168, 80).Clear();
                return alias;
            }

            byte[] alias = MakeAlias(21);
            if (hfsX)
            {
                U32(root, 84, aliasesInsideDocuments ? 2u : secondAlias ? 4u : 3u);
                U32(documents, 84, aliasesInsideDocuments ? aliasCount : 0u);
                U32(privateFolder, 84, 1);
                U32(inodeFolder, 84, nestedContents ? 1u : 0u);
            }

            uint aliasParent = aliasesInsideDocuments ? 16u : 2u;
            uint documentsParent = 2;

            var records = new List<byte[]>
            {
                Record(1, "Volume", root),
                Record(2, "", Thread(1, "Volume", 3)),
                Record(2, privateDirectory, privateFolder),
                Record(2, "Documents", documents),
                Record(16, "", Thread(documentsParent, "Documents", 3)),
                Record(18, "", Thread(2, privateDirectory, 3)),
                Record(18, directoryInode, inodeFolder),
                Record(19, "", Thread(18, directoryInode, 3)),
                Record(19, "Inside", contents),
                Record(20, "", Thread(19, "Inside", 4)),
                Record(21, "", Thread(aliasParent, "Shared Folder", 4)),
            };
            int aliasRecordIndex = aliasesInsideDocuments ? 5 : 4;
            records.Insert(aliasRecordIndex, Record(aliasParent, "Shared Folder", alias));
            if (nestedContents) records.Insert(10, Record(19, "Nested", nestedFolder));
            if (secondAlias)
            {
                records.Insert(aliasRecordIndex, Record(aliasParent, "Shared Copy", MakeAlias(22)));
                records.Add(Record(22, "", Thread(aliasParent, "Shared Copy", 4)));
            }
            if (nestedContents)
            {
                records.Add(Record(24, "", Thread(19, "Nested", 3)));
                records.Add(Record(24, "Deep", nestedFile));
                records.Add(Record(25, "", Thread(24, "Deep", 4)));
            }
            WriteBTreeNode(image.AsSpan(3 * Block, Block), 0xFF, 1, 0, 0, records.ToArray());
            if (firstLinkAttributeValue is not null)
            {
                byte[] value = [.. Encoding.ASCII.GetBytes(firstLinkAttributeValue), 0];
                byte[] inlineData = new byte[16 + value.Length + (value.Length & 1)];
                U32(inlineData, 0, 0x10);
                U32(inlineData, 12, checked((uint)value.Length));
                value.CopyTo(inlineData, 16);
                image = BuildWithAttributeLeaf(image,
                    [AttributeRecordWithData(19, "com.apple.system.hfs.firstlink", 0, inlineData)]);
            }
            U32(image.AsSpan(2 * Block, Block), 20, checked((uint)records.Count));
            Span<byte> volume = image.AsSpan(1024, 512);
            U32(volume, 32, (secondAlias ? 3u : 2u) + (nestedContents ? 1u : 0u));
            U32(volume, 36, nestedContents ? 4u : 3u);
            U32(volume, 64, nestedContents ? 26u : secondAlias ? 23u : 22u);
            "directory data"u8.CopyTo(image.AsSpan(4 * Block));
            if (nestedContents) "nested data"u8.CopyTo(image.AsSpan(6 * Block));
            volume.CopyTo(image.AsSpan(image.Length - 1024, 512));
            return image;
        }

        public static byte[] BuildWithUnreferencedOverflowExtent(uint physicalBlock, bool markAllocated = false)
        {
            byte[] image = Build(fragmentedData: true);
            Span<byte> leaf = image.AsSpan(5 * Block, Block);
            U16(leaf, 10, 2);
            U16(leaf, Block - 4, 90);
            U16(leaf, Block - 6, 166);
            WriteExtentRecord(leaf, 90, 0, physicalBlock, fileId: 99);
            U32(image.AsSpan(4 * Block), 20, 2);
            if (!markAllocated)
                image[9 * Block + (int)(physicalBlock / 8)] &=
                    unchecked((byte)~(1 << (7 - (int)(physicalBlock % 8))));
            return image;
        }

        public static byte[] BuildWithFileDataExtent(uint allocationBlock)
        {
            byte[] image = Build();
            Span<byte> leaf = image.AsSpan(3 * Block, Block);
            int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
            for (int index = 0; index < recordCount; index++)
            {
                int start = BinaryPrimitives.ReadUInt16BigEndian(leaf[(Block - 2 * (index + 1))..]);
                int keyLength = BinaryPrimitives.ReadUInt16BigEndian(leaf[start..]);
                int data = start + 2 + keyLength;
                if (BinaryPrimitives.ReadUInt16BigEndian(leaf[data..]) != 2) continue;
                U32(leaf, data + 88 + 16, allocationBlock);
                return image;
            }

            throw new InvalidOperationException("The HFS Plus fixture has no file record.");
        }

        public static byte[] BuildWithBadBlockExtent(uint allocationBlock)
        {
            byte[] image = Build(fragmentedData: true, badBlockExtent: true);
            U32(image.AsSpan(5 * Block), 14 + 12, allocationBlock);
            return image;
        }

        public static byte[] BuildWithIndexedAttributesTree(bool invalidChild, bool reverseAttributeKeys = false,
            bool invalidSeparator = false, uint? separatorFileId = null, string? separatorName = null,
            bool extraIndexRecordBytes = false, bool emptySecondLeaf = false)
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
            WriteBTreeNode(image.AsSpan(12 * Block, Block), 0xFF, 1, 3, 0,
                emptySecondLeaf ? [first, second] : [first]);
            WriteBTreeNode(image.AsSpan(13 * Block, Block), 0xFF, 1, 0, 2,
                emptySecondLeaf ? [] : [second]);

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
            SetCatalogAttributeFlagsForRecords(image, [first, second]);
            return image;
        }

        public static byte[] BuildWithAttributeLeaf(byte[][] records)
        {
            byte[] image = Build(includeAttributeFile: true);
            return BuildWithAttributeLeaf(image, records);
        }

        public static byte[] BuildWithEmptyAttributesRootLeaf()
        {
            byte[] image = Build(includeAttributeFile: true);
            Fork(image.AsSpan(1024 + 352, 80), 2 * Block, 10, 2);
            WriteEmptyAttributesTree(image.AsSpan(10 * Block, Block), rootLeaf: true);
            WriteBTreeNode(image.AsSpan(11 * Block, Block), 0xFF, 1, 0, 0, []);
            return image;
        }

        private static byte[] BuildWithAttributeLeaf(byte[] image, byte[][] records)
        {
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
            SetCatalogAttributeFlagsForRecords(image, records);
            image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024, 512));
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

        public static byte[] AttributeRecordWithData(uint fileId, string name, uint startBlock, byte[] data)
        {
            byte[] nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
            ushort keyLength = checked((ushort)(12 + nameBytes.Length));
            byte[] record = new byte[2 + keyLength + data.Length];
            U16(record, 0, keyLength);
            U32(record, 4, fileId);
            U32(record, 8, startBlock);
            U16(record, 12, checked((ushort)name.Length));
            nameBytes.CopyTo(record, 14);
            data.CopyTo(record, 2 + keyLength);
            return record;
        }

        public static byte[] InlineAttributeData(byte[] value)
        {
            byte[] data = new byte[16 + value.Length];
            U32(data, 0, 0x10);
            U32(data, 12, checked((uint)value.Length));
            value.CopyTo(data, 16);
            return data;
        }

        public static byte[] BuildAcl(uint entryCount)
        {
            const int fileSecurityAndAclHeaderSize = 44;
            const int aceSize = 24;
            byte[] acl = new byte[checked(fileSecurityAndAclHeaderSize + (int)entryCount * aceSize)];
            U32(acl, 0, 0x012CC16D);
            U32(acl, 36, entryCount);
            for (uint index = 0; index < entryCount; index++)
                U32(acl, fileSecurityAndAclHeaderSize + (int)index * aceSize + 16, 1);
            return acl;
        }

        public static byte[] BuildWithAttributeForkAndOverflow(bool includeOverflow,
            uint? overflowStartBlock = null, uint overflowRecordCount = 1, uint firstOverflowExtentCount = 8,
            uint primaryExtentCount = 8, uint totalBlocks = 9, ulong logicalSize = 8UL * Block + 1,
            bool includeForkData = true)
        {
            byte[] image = ResizeVolume(Build(includeAttributeFile: true), totalBlocks: 32);
            byte[] forkData = new byte[88];
            U32(forkData, 0, 0x20);
            BinaryPrimitives.WriteUInt64BigEndian(forkData.AsSpan(8), logicalSize);
            U32(forkData, 20, totalBlocks);
            for (uint index = 0; index < primaryExtentCount; index++)
            {
                U32(forkData, 24 + checked((int)index * 8), 12 + index);
                U32(forkData, 28 + checked((int)index * 8), 1);
            }

            var records = new List<byte[]>();
            if (includeForkData) records.Add(AttributeRecordWithData(17, "large", 0, forkData));
            if (includeOverflow)
            {
                uint remainingBlocks = totalBlocks > primaryExtentCount ? totalBlocks - primaryExtentCount : 0;
                uint logicalStart = overflowStartBlock ?? primaryExtentCount;
                uint physicalStart = 12 + primaryExtentCount;
                for (uint index = 0; index < overflowRecordCount; index++)
                {
                    byte[] overflowData = new byte[72];
                    U32(overflowData, 0, 0x30);
                    uint extentCount = index == 0
                        ? Math.Min(firstOverflowExtentCount, remainingBlocks)
                        : Math.Min(8u, remainingBlocks);
                    for (uint extent = 0; extent < extentCount; extent++)
                    {
                        U32(overflowData, 8 + checked((int)extent * 8), physicalStart + extent);
                        U32(overflowData, 12 + checked((int)extent * 8), 1);
                    }
                    records.Add(AttributeRecordWithData(17, "large", logicalStart, overflowData));
                    logicalStart += extentCount;
                    physicalStart += extentCount;
                    remainingBlocks -= extentCount;
                }
            }

            return BuildWithAttributeLeaf(image, records.ToArray());
        }

        private static byte[] ResizeVolume(byte[] image, uint totalBlocks)
        {
            Array.Resize(ref image, checked((int)totalBlocks * Block));
            Span<byte> volume = image.AsSpan(1024, 512);
            U32(volume, 44, totalBlocks);
            uint bitmapLength = (totalBlocks + 7) / 8;
            Fork(volume.Slice(112, 80), checked((int)bitmapLength), 9, 1);
            image.AsSpan(9 * Block, Block).Clear();
            image.AsSpan(9 * Block, checked((int)bitmapLength)).Fill(0xFF);
            image.AsSpan(1024, 512).CopyTo(image.AsSpan(image.Length - 1024, 512));
            return image;
        }

        private static byte[] BuildWithAttributeTree(uint dataBlock, uint recordType)
        {
            byte[] image = Build(includeAttributeFile: true);
            SetCatalogObjectHasAttributesFlagIfPresent(image, 17);
            Fork(image.AsSpan(1024 + 352, 80), 2 * Block, 10, 2);

            int dataLength = recordType switch
            {
                0x10 => 18,
                0x20 => 88,
                _ => 72
            };
            byte[] record = new byte[2 + 12 + dataLength];
            U16(record, 0, 12);
            U32(record, 4, 17);
            U32(record, 14, recordType);
            if (recordType == 0x10)
            {
                U32(record, 14 + 12, 2);
            }
            else if (recordType == 0x20)
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
            bool emptyOverflowSecondLeaf,
            bool badBlockExtent, bool badBlockOverlapsFileExtent,
            uint overflowStartBlock, uint overflowPhysicalBlock)
        {
            if (badBlockExtent)
            {
                WriteBadBlockExtentTree(image, badBlockOverlapsFileExtent ? 12u : 23u);
                return;
            }
            if (indexedOverflowTree)
            {
                WriteIndexedExtentsTree(image, emptyOverflowSecondLeaf);
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
                WriteExtentRecord(leaf, 14, overflowStartBlock, overflowPhysicalBlock, fileId: 18);
            else if (unsortedForkTypes)
                WriteExtentRecord(leaf, 14, overflowStartBlock, overflowPhysicalBlock, forkType: 0xFF);
            else
                WriteExtentRecord(leaf, 14, unsortedKeys ? overflowStartBlock + 1 : overflowStartBlock,
                    unsortedKeys ? overflowPhysicalBlock + 2 : overflowPhysicalBlock);
            if (duplicateRecord) leaf.Slice(14, 76).CopyTo(leaf[90..]);
            else if (unsortedKeys) WriteExtentRecord(leaf, 90, overflowStartBlock, overflowPhysicalBlock);
            else if (unsortedFileIds)
                WriteExtentRecord(leaf, 90, overflowStartBlock, overflowPhysicalBlock, fileId: 17);
            else if (unsortedForkTypes)
                WriteExtentRecord(leaf, 90, overflowStartBlock, overflowPhysicalBlock, forkType: 0);

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

        private static void WriteEmptyAttributesTree(Span<byte> header, bool rootLeaf = false)
        {
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, rootLeaf ? (ushort)1 : (ushort)0);
            U32(header, 16, rootLeaf ? 1u : 0u);
            U32(header, 20, 0);
            U32(header, 24, rootLeaf ? 1u : 0u);
            U32(header, 28, rootLeaf ? 1u : 0u);
            U16(header, 32, Block);
            U16(header, 34, 266);
            U32(header, 36, rootLeaf ? 2u : 1u);
            U32(header, 14 + 38, 6);
            header[14 + 106 + 128] = rootLeaf ? (byte)0xC0 : (byte)0x80;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteEmptyExtentsTree(Span<byte> header, bool rootLeaf)
        {
            header[8] = 1;
            U16(header, 10, 3);
            U16(header, 14, rootLeaf ? (ushort)1 : (ushort)0);
            U32(header, 16, rootLeaf ? 1u : 0u);
            U32(header, 20, 0);
            U32(header, 24, rootLeaf ? 1u : 0u);
            U32(header, 28, rootLeaf ? 1u : 0u);
            U16(header, 32, Block);
            U16(header, 34, 10);
            U32(header, 36, rootLeaf ? 2u : 1u);
            U32(header, 14 + 38, 2);
            header[14 + 106 + 128] = rootLeaf ? (byte)0xC0 : (byte)0x80;
            U16(header, Block - 2, 14);
            U16(header, Block - 4, 14 + 106);
            U16(header, Block - 6, 14 + 106 + 128);
            U16(header, Block - 8, Block - 8);
        }

        private static void WriteIndexedExtentsTree(byte[] image, bool emptySecondLeaf)
        {
            const int firstTreeBlock = 26;
            byte[] firstExtent = ExtentRecord(startBlock: 8, physicalBlock: 30, extentCount: 8);
            byte[] secondExtent = ExtentRecord(startBlock: 16, physicalBlock: 38);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 1) * Block, Block), 0, 2, 0, 0,
                [ExtentIndexRecord(firstExtent, 2), ExtentIndexRecord(secondExtent, 3)]);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 2) * Block, Block), 0xFF, 1, 3, 0,
                emptySecondLeaf ? [firstExtent, secondExtent] : [firstExtent]);
            WriteBTreeNode(image.AsSpan((firstTreeBlock + 3) * Block, Block), 0xFF, 1, 0, 2,
                emptySecondLeaf ? [] : [secondExtent]);

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

        private static byte[] ExtentRecord(uint startBlock, uint physicalBlock, uint extentCount = 1)
        {
            byte[] record = new byte[76];
            WriteExtentRecord(record, 0, startBlock, physicalBlock);
            for (uint index = 1; index < extentCount; index++)
            {
                U32(record, 12 + checked((int)index * 8), physicalBlock + index);
                U32(record, 16 + checked((int)index * 8), 1);
            }
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
