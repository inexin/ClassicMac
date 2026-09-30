using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class DiskDoublerFeatureTests
{
    [Fact]
    public void CanReadRecognizesOnlyACompleteDda2Header()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive();

        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes("DDA2"u8.ToArray())));
        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(archive)));

        archive[12] ^= 0x01;
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(archive)));
    }

    [Fact]
    public void Dda2RejectsAnInvalidArchiveHeaderChecksum()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive();
        archive[12] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(archive), new ContainerContext()));
    }

    [Fact]
    public void CanReadRecognizesLegacyDdarArchivesWithACompleteHeader()
    {
        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(DiskDoublerFixture.BuildLegacyArchive())));
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes("DDAR"u8.ToArray())));
    }

    [Fact]
    public void LegacyDdarReadsStoredForksAndFinderMetadata()
    {
        byte[] data = "legacy data"u8.ToArray();
        byte[] resource = "legacy resource"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("Legacy", data, resource));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile file = Assert.Single(files);
        Assert.Equal("Legacy", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
    }

    [Fact]
    public void LegacyDdarFolderMarkersBuildAndUnwindNestedPaths()
    {
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFolder("Folder"),
            DiskDoublerFixture.BuildLegacyFolder("Nested"),
            DiskDoublerFixture.BuildLegacyFile("Note", "inside"u8.ToArray(), []),
            DiskDoublerFixture.BuildLegacyEndFolder(),
            DiskDoublerFixture.BuildLegacyFile("Sibling", [], []));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        Assert.Equal(new[] { "Folder:Nested:Note", "Folder:Sibling" }, files.Select(file => file.MacPath));
    }

    [Fact]
    public void LegacyDdarSkipsRedundantTrailingSingleFileHeaders()
    {
        byte[] redundantHeader = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(redundantHeader, 0xABCD0054);
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("File", [1], []), redundantHeader);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("File", file.MacPath);
        Assert.Equal(new byte[] { 1 }, file.DataFork.ToArray());
    }

    [Fact]
    public void LegacyDdarRejectsTruncatedHeadersPayloadsAndUnmatchedFolderEnds()
    {
        byte[] validHeader = DiskDoublerFixture.BuildLegacyArchive();
        byte[] truncatedHeader = [.. validHeader, .. "DDAR"u8.ToArray(), 0, 0, 0];
        byte[] payload = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("Truncated", [1], []));
        Array.Resize(ref payload, payload.Length - 1);
        byte[] unmatchedFolderEnd = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyEndFolder());

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncatedHeader),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(payload),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(unmatchedFolderEnd),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyDdarHonorsFolderNestingLimit()
    {
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFolder("Folder"));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxNestingDepth = 0 })));
    }

    [Fact]
    public void DefaultUnwrapperReadsDda2StoredEntryWithBothForksAndFinderMetadata()
    {
        byte[] data = "data fork"u8.ToArray();
        byte[] resource = "resource fork"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Read Me", 0, data, resource));
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.dd"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
    }

    [Fact]
    public void Dda2DirectoryDepthProducesNestedMacPaths()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFolder("Folder", 0),
            DiskDoublerFixture.BuildFolder("Nested", 1),
            DiskDoublerFixture.BuildFile("Note", 2, "inside"u8.ToArray(), []));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile file = Assert.Single(files);
        Assert.Equal("Folder:Nested:Note", file.MacPath);
        Assert.Equal(new[] { "Folder", "Nested" }, file.FolderPath.Select(folder => folder.ToMacRoman()));
        Assert.Empty(file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2UnsupportedCompressionSkipsThatEntryAndContinuesAtTheNextRecord()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Unsupported", 0, [1], [], dataMethod: 7),
            DiskDoublerFixture.BuildFile("Stored", 0, "available"u8.ToArray(), []));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal("Stored", Assert.Single(files).MacPath);
        Assert.Equal("available"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Dda2CompactProMethodDecodesLzhAndRleForks()
    {
        byte[] expectedData = "A"u8.ToArray();
        byte[] expectedResource = "RRR"u8.ToArray();
        byte[] dataStream = [.. new byte[16], .. DiskDoublerFixture.BuildLzhLiteral((byte)'A')];
        byte[] resourceStream = [1, .. new byte[15], (byte)'R', 0x81, 0x82, 3];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("CompactPro", 0,
            expectedData, expectedResource, dataMethod: 8, resourceMethod: 8,
            encodedData: dataStream, encodedResource: resourceStream));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc");
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesLzwFork()
    {
        byte[] expected = "ABC"u8.ToArray();
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 66, 67)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Lzw", 0,
            expected, [], dataMethod: 1, encodedData: compressed));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2Method2DecodesAnInitialAdaptiveHuffmanTreeSymbol()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2Method2UsesThePreviousSymbolToAdaptItsTree()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method2AppliesTheInfoSelectedOutputXor()
    {
        byte[] encoded = [0x41, 0x41, 0xF0];
        byte[] expected = [0x1B, 0x1B, 0x1B];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive XOR", 0,
            expected, [], dataMethod: 2, encodedData: encoded, info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(0x2A, 0x80)]
    [InlineData(0x29, 0x00)]
    public void Dda2Method2LeavesOutputUnchangedWhenInfoDisablesTheXor(byte info1, byte info2)
    {
        byte[] expected = "AAA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive no XOR", 0,
            expected, [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0], info1: info1, info2: info2));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method2DecodesTheResourceFork()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive resource", 0,
            [], "A"u8.ToArray(), resourceMethod: 2, encodedResource: [0x41]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2Method2RejectsTruncatedAdaptiveHuffmanCodes()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "AA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method2ReportsChecksumMismatchAfterDecoding()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad adaptive checksum", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesLzwDictionaryReferences()
    {
        byte[] expected = "AAA"u8.ToArray();
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 256)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Repeated", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodChangesCodeWidthAtDictionaryBoundary()
    {
        byte[] expected = Enumerable.Range(0, 258).Select(index => (byte)(index % 256)).ToArray();
        byte[] compressed = [0, 0, 10, .. DiskDoublerFixture.PackLzwWidthTransition(expected)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Wide codes", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressBlockModeClearsDictionaryAndStartsAnotherCodeGroup()
    {
        byte[] expected = "AB"u8.ToArray();
        byte[] compressed = [0, 0, 0x89, .. DiskDoublerFixture.PackLzwClearAndRestart(65, 66)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Block mode", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodRejectsAnUnassignedLzwCode()
    {
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 300)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad code", 0,
            "AB"u8.ToArray(), [], dataMethod: 1, encodedData: compressed));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2MacCompressMethodAppliesTheArchiveXorVariantToDecodedBytes()
    {
        byte xor = 0x5A;
        byte[] expected = "ABC"u8.ToArray();
        byte[] encrypted = expected.Select(value => (byte)(value ^ xor)).ToArray();
        byte[] compressed = [(byte)(0 ^ xor), (byte)(0 ^ xor), (byte)(9 ^ xor),
            .. DiskDoublerFixture.PackLsbCodes(encrypted[0], encrypted[1], encrypted[2])];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Xor", 0,
            expected, [], dataMethod: 1, encodedData: compressed, info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodReportsChecksumMismatchAfterDecoding()
    {
        byte[] encoded = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 66, 67)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad checksum", 0,
            "ABC"u8.ToArray(), [], dataMethod: 1, encodedData: encoded));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("ABC"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesTheResourceFork()
    {
        byte[] expected = "R"u8.ToArray();
        byte[] encoded = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes((byte)'R')];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Resource", 0,
            [], expected, resourceMethod: 1, encodedResource: encoded));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Empty(file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2HuffmanMethodDecodesBothForksAndChecksTheirByteSums()
    {
        byte[] expectedData = "ABBA"u8.ToArray();
        byte[] expectedResource = "BA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Huffman", 0,
            expectedData, expectedResource, dataMethod: 4, resourceMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork(expectedData),
            encodedResource: DiskDoublerFixture.BuildHuffmanFork(expectedResource)));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2HuffmanMethodRejectsTruncatedTreesAndReportsBadByteSum()
    {
        byte[] truncated = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            [1], [], dataMethod: 4, encodedData: []));
        byte[] mismatch = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad sum", 0,
            "AB"u8.ToArray(), [], dataMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork("AB"u8)));
        mismatch[62 + 60 + 44] ^= 1;
        var diagnostics = new List<Diagnostic>();

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncated),
            new ContainerContext()));
        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(mismatch),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2HuffmanMethodAppliesTheInfoSelectedXorVariant()
    {
        const byte xor = 0x5A;
        byte[] expected = "ABBA"u8.ToArray();
        byte[] transformed = expected.Select(value => (byte)(value ^ xor)).ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Huffman XOR", 0,
            expected, [], dataMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork(transformed, (byte)('A' ^ xor), (byte)('B' ^ xor)),
            info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2CompactProMethodReportsForkChecksumMismatch()
    {
        byte[] encodedData = [1, .. new byte[15], (byte)'X'];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad CRC", 0,
            "X"u8.ToArray(), [], dataMethod: 8, encodedData: encodedData));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("X"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2CompactProMethodRejectsATruncatedMethodHeader()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 8, encodedData: new byte[15]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2MacCompressMethodRejectsInvalidFlagsAndTruncatedHeader()
    {
        byte[] invalidFlags = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad flags", 0,
            [1], [], dataMethod: 1, encodedData: [0, 0, 8]));
        byte[] truncated = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Short header", 0,
            [1], [], dataMethod: 1, encodedData: [0, 0]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(invalidFlags),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncated),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2RejectsTruncatedFileMetadataAndForkPayload()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Truncated", 0, "data"u8.ToArray(), []));
        Array.Resize(ref archive, archive.Length - 9);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2RejectsFolderDepthThatHasNoParentDirectory()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Orphan", 1, "data"u8.ToArray(), []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2HonorsTheConfiguredVolumeEntryLimit()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("First", 0, [], []),
            DiskDoublerFixture.BuildFile("Second", 0, [], []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 1 })));
    }

    private static class DiskDoublerFixture
    {
        private const int ArchiveHeaderLength = 62;
        private const int RecordHeaderLength = 46;
        private const int FileHeaderLength = 80;
        private const int LegacyArchiveHeaderLength = 78;
        private const int LegacyRecordHeaderLength = 124;

        public static byte[] BuildLegacyArchive(params byte[][] records)
        {
            byte[] archive = new byte[LegacyArchiveHeaderLength + records.Sum(record => record.Length)];
            "DDAR"u8.CopyTo(archive);
            int offset = LegacyArchiveHeaderLength;
            foreach (byte[] record in records)
            {
                record.CopyTo(archive, offset);
                offset += record.Length;
            }
            return archive;
        }

        public static byte[] BuildLegacyFolder(string name) => BuildLegacyRecord(name, isDirectory: true,
            isEndDirectory: false, [], []);

        public static byte[] BuildLegacyEndFolder() => BuildLegacyRecord("", isDirectory: false,
            isEndDirectory: true, [], []);

        public static byte[] BuildLegacyFile(string name, byte[] data, byte[] resource) =>
            BuildLegacyRecord(name, isDirectory: false, isEndDirectory: false, data, resource);

        private static byte[] BuildLegacyRecord(string name, bool isDirectory, bool isEndDirectory,
            byte[] data, byte[] resource)
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length > 63) throw new ArgumentOutOfRangeException(nameof(name));
            byte[] bytes = new byte[LegacyRecordHeaderLength + data.Length + resource.Length];
            "DDAR"u8.CopyTo(bytes);
            bytes[8] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(bytes, 9);
            bytes[72] = isDirectory ? (byte)1 : (byte)0;
            bytes[73] = isEndDirectory ? (byte)1 : (byte)0;
            U32(bytes, 74, checked((uint)data.Length));
            U32(bytes, 78, checked((uint)resource.Length));
            U32(bytes, 82, 2_500_000_000);
            U32(bytes, 86, 2_600_000_000);
            "TEXTttxt"u8.CopyTo(bytes.AsSpan(90));
            U16(bytes, 98, 0x4000);
            data.CopyTo(bytes, LegacyRecordHeaderLength);
            resource.CopyTo(bytes, LegacyRecordHeaderLength + data.Length);
            return bytes;
        }

        public static byte[] BuildArchive(params byte[][] records)
        {
            int length = ArchiveHeaderLength + records.Sum(record => record.Length) + 6;
            byte[] archive = new byte[length];
            "DDA2"u8.CopyTo(archive);
            int offset = ArchiveHeaderLength;
            foreach (byte[] record in records)
            {
                record.CopyTo(archive, offset);
                offset += record.Length;
            }
            "DDA2"u8.CopyTo(archive.AsSpan(offset));
            U16(archive, offset + 4, 0xBBBB);
            U16(archive, 60, Crc16Xmodem(archive.AsSpan(0, 60)));
            return archive;
        }

        public static byte[] BuildFolder(string name, int depth)
        {
            byte[] bytes = NewRecord(name, checked((uint)depth + 2), RecordHeaderLength + 16, 0x8000);
            U32(bytes, RecordHeaderLength + 8, 2_500_000_000);
            U32(bytes, RecordHeaderLength + 12, 2_600_000_000);
            return bytes;
        }

        public static byte[] BuildFile(string name, int depth, byte[] data, byte[] resource, byte dataMethod = 0,
            byte resourceMethod = 0, byte[]? encodedData = null, byte[]? encodedResource = null, byte info1 = 0,
            byte info2 = 0)
        {
            encodedData ??= data;
            encodedResource ??= resource;
            int payloadLength = checked(encodedData.Length + encodedResource.Length);
            byte[] bytes = NewRecord(name, checked((uint)depth + 2), RecordHeaderLength + 10 + 4 + FileHeaderLength + payloadLength);
            U32(bytes, RecordHeaderLength + 10, 0xABCD0054);
            int header = RecordHeaderLength + 14;
            U32(bytes, header, checked((uint)data.Length));
            U32(bytes, header + 4, checked((uint)encodedData.Length));
            U32(bytes, header + 8, checked((uint)resource.Length));
            U32(bytes, header + 12, checked((uint)encodedResource.Length));
            bytes[header + 16] = dataMethod;
            bytes[header + 17] = resourceMethod;
            bytes[header + 18] = info1;
            bytes[header + 48] = info2;
            U32(bytes, header + 20, 2_600_000_000);
            U32(bytes, header + 24, 2_500_000_000);
            "TEXTttxt"u8.CopyTo(bytes.AsSpan(header + 28));
            U16(bytes, header + 36, 0x4000);
            if (dataMethod == 8) U16(bytes, header + 44, Crc16Ibm(data));
            if (resourceMethod == 8) U16(bytes, header + 46, Crc16Ibm(resource));
            if (dataMethod == 1 && encodedData.Length >= 3) U16(bytes, header + 44,
                MacCompressChecksum(data, encodedData, info1, info2));
            if (dataMethod == 2) U16(bytes, header + 44, ByteSum(data));
            if (resourceMethod == 1 && encodedResource.Length >= 3) U16(bytes, header + 46,
                MacCompressChecksum(resource, encodedResource, info1, info2));
            if (resourceMethod == 2) U16(bytes, header + 46, ByteSum(resource));
            if (dataMethod == 4) U16(bytes, header + 44, ByteSum(data));
            if (resourceMethod == 4) U16(bytes, header + 46, ByteSum(resource));
            encodedData.CopyTo(bytes, RecordHeaderLength + 14 + FileHeaderLength);
            encodedResource.CopyTo(bytes, RecordHeaderLength + 14 + FileHeaderLength + encodedData.Length);
            return bytes;
        }

        private static ushort Crc16Ibm(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xA001));
            }
            return crc;
        }

        private static ushort Crc16Xmodem(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021);
            }
            return crc;
        }

        private static ushort MacCompressChecksum(ReadOnlySpan<byte> data, ReadOnlySpan<byte> encoded,
            byte info1, byte info2)
        {
            byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
            uint sum = (uint)(encoded[0] ^ xor) + (uint)(encoded[1] ^ xor) + (uint)(encoded[2] ^ xor);
            foreach (byte value in data) sum += value;
            return (ushort)sum;
        }

        private static ushort ByteSum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            foreach (byte value in data) sum += value;
            return (ushort)sum;
        }

        public static byte[] BuildHuffmanFork(ReadOnlySpan<byte> plain, byte symbolA = (byte)'A',
            byte symbolB = (byte)'B')
        {
            var bits = new List<bool>();
            bits.Add(false);
            bits.Add(true);
            WriteBits(bits, symbolA, 8);
            bits.Add(true);
            WriteBits(bits, symbolB, 8);
            foreach (byte value in plain)
                bits.Add(value == symbolB);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] BuildLzhLiteral(byte value)
        {
            var bits = new List<bool>();
            int symbolCount = ((value + 2) / 2) * 2;
            WriteBits(bits, symbolCount / 2, 8);
            for (int symbol = 0; symbol < symbolCount; symbol++) WriteBits(bits, symbol == value ? 1 : 0, 4);
            WriteBits(bits, 0, 8);
            WriteBits(bits, 0, 8);
            WriteBits(bits, 1, 1);
            WriteBits(bits, 0, 1);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] PackLsbCodes(params int[] codes)
        {
            var bits = new List<bool>();
            foreach (int code in codes)
                for (int bit = 0; bit < 9; bit++) bits.Add((code & (1 << bit)) != 0);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        public static byte[] PackLzwWidthTransition(ReadOnlySpan<byte> literals)
        {
            var bits = new List<bool>();
            for (int index = 0; index < literals.Length; index++)
            {
                int width = index < 257 ? 9 : 10;
                int code = literals[index];
                for (int bit = 0; bit < width; bit++) bits.Add((code & (1 << bit)) != 0);
                if (index == 256)
                    while (bits.Count % (9 * 8) != 0) bits.Add(false);
            }
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        public static byte[] PackLzwClearAndRestart(byte first, byte afterClear)
        {
            var bits = new List<bool>();
            AppendCode(bits, first, 9);
            AppendCode(bits, 256, 9);
            while (bits.Count % (9 * 8) != 0) bits.Add(false);
            AppendCode(bits, afterClear, 9);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        private static void AppendCode(List<bool> bits, int code, int width)
        {
            for (int bit = 0; bit < width; bit++) bits.Add((code & (1 << bit)) != 0);
        }

        private static void WriteBits(List<bool> bits, int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--) bits.Add((value & (1 << bit)) != 0);
        }

        private static byte[] NewRecord(string name, uint depth, int totalSize, ushort type = 0)
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length is 0 or > 31) throw new ArgumentOutOfRangeException(nameof(name));
            byte[] bytes = new byte[totalSize];
            "DDA2"u8.CopyTo(bytes);
            U16(bytes, 4, type);
            bytes[6] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(bytes, 7);
            U32(bytes, 38, depth);
            U32(bytes, 42, checked((uint)totalSize));
            return bytes;
        }

        private static void U16(Span<byte> bytes, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

        private static void U32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }
}
