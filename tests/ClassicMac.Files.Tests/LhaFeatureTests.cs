using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class LhaFeatureTests
{
    [Fact]
    public void LhaLevelZeroStoredEntryKeepsPathAndContents()
    {
        byte[] data = "classic Macintosh archive"u8.ToArray();
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Folder\\Read Me.txt", data);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Folder:Read Me.txt", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Empty(file.ResourceFork.ToArray());
    }

    [Fact]
    public void LhaLevelZeroUsesMacRomanForMacOsNames()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Résumé.txt", "contents"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Résumé.txt", file.Name.ToMacRoman());
    }

    [Fact]
    public void LhaArchiveHonorsTheMaximumEntryCount()
    {
        byte[] first = LhaFixture.BuildLevelZeroStoredFile("first", "one"u8.ToArray());
        byte[] second = LhaFixture.BuildLevelZeroStoredFile("second", "two"u8.ToArray());
        byte[] archive = [.. first.AsSpan(0, first.Length - 1), .. second];
        var context = new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 1 });

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive), context));
    }

    [Fact]
    public void LhaLevelOneCombinesExtendedDirectoryAndFilename()
    {
        byte[] archive = LhaFixture.BuildLevelOneStoredFile("old-name", "Folder/", "New name.txt",
            "extended header payload"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Folder:New name.txt", file.MacPath);
        Assert.Equal("extended header payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaLevelTwoReadsExtendedPathAndVerifiesHeaderAndFileCrcs()
    {
        byte[] archive = LhaFixture.BuildLevelTwoStoredFile("Folder/", "Long filename.txt",
            "level two payload"u8.ToArray(), padding: true);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Folder:Long filename.txt", file.MacPath);
        Assert.Equal("level two payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaLevelTwoRejectsAnInvalidHeaderCrc()
    {
        byte[] archive = LhaFixture.BuildLevelTwoStoredFile("Folder/", "name", "data"u8.ToArray());
        archive[27] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaLevelTwoRejectsAFileWithoutAName()
    {
        byte[] archive = LhaFixture.BuildLevelTwoStoredFile("Folder/", "", "data"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaLevelThreeReadsFourByteExtensionChainSizes()
    {
        byte[] archive = LhaFixture.BuildLevelThreeStoredFile("Folder/", "Long filename.txt",
            "level three payload"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Folder:Long filename.txt", file.MacPath);
        Assert.Equal("level three payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LhaHeaderCrcExtensionCanContainAdditionalInformation(int level)
    {
        byte[] archive = level == 2
            ? LhaFixture.BuildLevelTwoStoredFile("Folder/", "name", "data"u8.ToArray(),
                includeHeaderCrcInfo: true)
            : LhaFixture.BuildLevelThreeStoredFile("Folder/", "name", "data"u8.ToArray(),
                includeHeaderCrcInfo: true);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("Folder:name", file.MacPath);
        Assert.Equal("data"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaDecodesAnInitialDynamicHuffmanLiteral()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh1-", "literal", [0xE6, 0x80], 1,
            checksumData: "A"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaAdaptsTheDynamicHuffmanTreeBetweenLiterals()
    {
        // Initial A code: 111001101; after that code's frequency update: 11000100.
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh1-", "adaptive", [0xE6, 0xE2, 0x00], 2,
            checksumData: "AA"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedDynamicHuffmanCode()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh1-", "truncated", [], 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesAnInitialDynamicHuffmanMatch()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh1-", "match", [0x8C, 0x00, 0x00], 3,
            checksumData: "   "u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("   "u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsADynamicHuffmanMatchPastTheDeclaredOutput()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh1-", "oversized", [0x8C, 0x00, 0x00], 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesALarcLz5Literal()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lz5-", "literal", [0x01, (byte)'X'], 1,
            checksumData: "X"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("X"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaDecodesALarcLz5CopyFromItsInitialSpaceWindow()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lz5-", "copy", [0x00, 0x80, 0xF0], 3,
            checksumData: "   "u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("   "u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedLarcLz5Literal()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lz5-", "truncated", [0x01], 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsALarcLz5MatchThatExceedsTheDeclaredOutput()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lz5-", "oversized", [0x00, 0x80, 0xF0], 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesALarcLzsLiteral()
    {
        byte[] packed = LhaFixture.BuildLzsLiteral((byte)'X');
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "literal", packed, 1,
            checksumData: "X"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("X"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaDecodesALarcLzsCopyFromItsInitialWindow()
    {
        byte[] packed = LhaFixture.BuildLzsMatch(position: 0, length: 2);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "copy", packed, 2,
            checksumData: "  "u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("  "u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaCopiesOverlappingLarcLzsMatchesFromRecentlyExpandedBytes()
    {
        byte[] packed = LhaFixture.BuildLzsLiteralAndMatch((byte)'A', position: 2031, length: 5);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "overlap", packed, 6,
            checksumData: "AAAAAA"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AAAAAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedLarcLzsLiteral()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "truncated", [0x80], 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsATruncatedLarcLzsMatch()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "truncated", [0x00], 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsALarcLzsMatchThatExceedsTheDeclaredOutput()
    {
        byte[] packed = LhaFixture.BuildLzsMatch(position: 0, length: 3);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lzs-", "oversized", packed, 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesLh3BlockWithOneRepeatedLiteralSymbol()
    {
        byte[] packed = LhaFixture.BuildLh3RepeatedLiteralBlock((byte)'A', count: 3);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "repeated", packed, 3,
            checksumData: "AAA"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LhaDecodesLh3BackReferenceWithEitherPositionTree(int positionTreeMode)
    {
        byte[] packed = LhaFixture.BuildLh3LiteralAndOverlappingMatch(positionTreeMode);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "back-reference", packed, 4,
            checksumData: "AAAA"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AAAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedLh3BlockHeader()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "truncated", [], 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsAnLh3MatchPastTheDeclaredOutput()
    {
        byte[] packed = LhaFixture.BuildLh3LiteralAndOverlappingMatch(positionTreeMode: 1);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "oversized", packed, 3);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaReadsTheNextLh3BlockAfterTheDeclaredCommandCount()
    {
        byte[] packed = LhaFixture.BuildLh3TwoLiteralBlocks((byte)'A', (byte)'B');
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "blocks", packed, 2,
            checksumData: "AB"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsAnOversubscribedLh3LiteralTree()
    {
        byte[] packed = LhaFixture.BuildLh3OversubscribedLiteralTree();
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "bad-tree", packed, 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesTheMaximumLengthLh3Match()
    {
        byte[] expected = new byte[257];
        Array.Fill(expected, (byte)'A');
        byte[] packed = LhaFixture.BuildLh3LongMatch(224);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "long-match", packed, expected.Length,
            checksumData: expected);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsAnLh3MatchLongerThanItsMaximum()
    {
        byte[] packed = LhaFixture.BuildLh3LongMatch(225);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh3-", "too-long", packed, 258);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesAnInitialLh2AdaptiveHuffmanLiteral()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "literal", [0x05], 1,
            checksumData: "A"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaUpdatesTheLh2AdaptiveTreeBetweenLiterals()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "adaptive", [0x05, 0xE0], 2,
            checksumData: "AA"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaDecodesAnInitialLh2MatchFromItsPresetWindow()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "match", [0xC4, 0x00], 3,
            checksumData: "   "u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("   "u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedLh2AdaptiveCode()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "truncated", [], 1);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsAnLh2MatchPastTheDeclaredOutput()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "oversized", [0xC4, 0x00], 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDecodesTheMaximumLengthLh2Match()
    {
        byte[] expected = new byte[256];
        Array.Fill(expected, (byte)' ');
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "long-match", [0xE1, 0xE0, 0x00], 256,
            checksumData: expected);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsAnLh2MatchLongerThanItsMaximum()
    {
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "too-long", [0xE1, 0xE1, 0x00], 257);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaAddsLh2PositionSymbolsAsTheExpandedWindowAdvances()
    {
        byte[] expected = new byte[68];
        Array.Fill(expected, (byte)' ');
        byte[] packed = LhaFixture.BuildLh2PositionTreeGrowthStream();
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh2-", "position-tree", packed, expected.Length,
            checksumData: expected);

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void LhaLevelThreeRejectsAnInvalidHeaderCrc()
    {
        byte[] archive = LhaFixture.BuildLevelThreeStoredFile("Folder/", "name", "data"u8.ToArray());
        archive[33] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaDoesNotRecognizeATruncatedLevelThreeHeader()
    {
        byte[] header = new byte[25];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 4);
        Encoding.ASCII.GetBytes("-lh0-").CopyTo(header, 2);
        header[20] = 3;

        Assert.False(LhaReader.Instance.CanRead(ForkData.FromBytes(header)));
    }

    [Fact]
    public void LhaLevelOneSkipsItsExtendedHeadersBeforeReadingPayloadAndNextRecord()
    {
        byte[] first = LhaFixture.BuildLevelOneStoredFile("first", "Folder/", "first", "one"u8.ToArray());
        byte[] second = LhaFixture.BuildLevelZeroStoredFile("second", "two"u8.ToArray());
        byte[] archive = [.. first.AsSpan(0, first.Length - 1), .. second];

        IReadOnlyList<MacFile> files = LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext());

        Assert.Equal(2, files.Count);
        Assert.Equal("Folder:first", files[0].MacPath);
        Assert.Equal("one"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Equal("second", files[1].MacPath);
        Assert.Equal("two"u8.ToArray(), files[1].DataFork.ToArray());
    }

    [Fact]
    public void LhaLevelOneSkipsUnknownExtendedHeaders()
    {
        byte[] archive = LhaFixture.BuildLevelOneWithUnknownExtension("payload"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("name", file.MacPath);
        Assert.Equal("payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedLevelOneExtendedHeader()
    {
        byte[] archive = LhaFixture.BuildLevelOneStoredFile("name", "Folder/", null, "data"u8.ToArray());
        int baseHeaderLength = archive[0] + 2;
        Array.Resize(ref archive, baseHeaderLength + 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void DefaultUnwrapperRecognizesLhaArchives()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Read Me", "contents"u8.ToArray());
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.lzh"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        Assert.Equal("LHA", Assert.Single(result.Children).Format);
        Assert.Equal("contents"u8.ToArray(), Assert.Single(result.Children).File.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperRecognizesLevelOneLhaArchives()
    {
        byte[] archive = LhaFixture.BuildLevelOneStoredFile("old-name", "Folder/", "Read Me",
            "contents"u8.ToArray());
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.lzh"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Folder:Read Me", file.MacPath);
        Assert.Equal("contents"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperRecognizesLevelTwoLhaArchives()
    {
        byte[] archive = LhaFixture.BuildLevelTwoStoredFile("Folder/", "Read Me", "contents"u8.ToArray());
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.lzh"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Folder:Read Me", file.MacPath);
        Assert.Equal("contents"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperRecognizesLevelThreeLhaArchives()
    {
        byte[] archive = LhaFixture.BuildLevelThreeStoredFile("Folder/", "Read Me", "contents"u8.ToArray());
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.lzh"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Folder:Read Me", file.MacPath);
        Assert.Equal("contents"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaLevelZeroRejectsAnInvalidHeaderChecksum()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Read Me", "contents"u8.ToArray());
        archive[1]++;

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaLevelZeroReportsAFileCrcMismatchAndKeepsTheEntry()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Read Me", "contents"u8.ToArray());
        int headerLength = archive[0] + 2;
        archive[headerLength - 3] ^= 0x01;
        archive[1] = LhaFixture.HeaderChecksum(archive.AsSpan(2, headerLength - 2));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("contents"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LhaSkipsUnsupportedMethodsAndContinuesWithLaterEntries()
    {
        byte[] compressed = LhaFixture.BuildLevelZeroEntry("-lhx-", "compressed", [0x01], 100);
        byte[] stored = LhaFixture.BuildLevelZeroStoredFile("stored", "ready"u8.ToArray());
        byte[] archive = [.. compressed.AsSpan(0, compressed.Length - 1), .. stored];
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("stored", file.MacPath);
        Assert.Equal("ready"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("-lh4-")]
    [InlineData("-lh5-")]
    [InlineData("-lh6-")]
    [InlineData("-lh7-")]
    public void LhaDecodesNewStyleLiteralBlocks(string method)
    {
        byte[] payload = LhaFixture.BuildNewStyleLiteralBlock(method, "AB"u8);
        byte[] archive = LhaFixture.BuildLevelZeroEntry(method, "compressed", payload, 2,
            checksumData: "AB"u8.ToArray());
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Theory]
    [InlineData("-lh4-")]
    [InlineData("-lh5-")]
    [InlineData("-lh6-")]
    [InlineData("-lh7-")]
    public void LhaNewStyleCopiesFromTheSlidingWindow(string method)
    {
        byte[] payload = LhaFixture.BuildNewStyleBackReferenceBlock(method);
        byte[] archive = LhaFixture.BuildLevelZeroEntry(method, "repeated", payload, 6,
            checksumData: "ABCABC"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("ABCABC"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaStartsANewTableWhenACompressedBlockEnds()
    {
        byte[] payload = LhaFixture.BuildNewStyleTwoBlockLiteralStream("-lh5-");
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh5-", "blocks", payload, 2,
            checksumData: "AB"u8.ToArray());

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperDecodesCompressedLhaEntries()
    {
        byte[] payload = LhaFixture.BuildNewStyleBackReferenceBlock("-lh5-");
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh5-", "repeat", payload, 6,
            checksumData: "ABCABC"u8.ToArray());
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.lzh"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("ABCABC"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void LhaRejectsATruncatedNewStyleCompressedBlock()
    {
        byte[] payload = LhaFixture.BuildNewStyleLiteralBlock("-lh5-", "AB"u8);
        Array.Resize(ref payload, payload.Length - 1);
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh5-", "truncated", payload, 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsANewStyleMatchThatExceedsTheDeclaredOutputSize()
    {
        byte[] payload = LhaFixture.BuildNewStyleBackReferenceBlock("-lh5-");
        byte[] archive = LhaFixture.BuildLevelZeroEntry("-lh5-", "oversized", payload, 5);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaSkipsNonMacLevelZeroNamesAndContinuesWithLaterEntries()
    {
        byte[] otherOs = LhaFixture.BuildLevelZeroEntry("-lh0-", "dos-name", "ignored"u8.ToArray(), 7,
            osIdentifier: (byte)'M');
        byte[] stored = LhaFixture.BuildLevelZeroStoredFile("Mac name", "ready"u8.ToArray());
        byte[] archive = [.. otherOs.AsSpan(0, otherOs.Length - 1), .. stored];
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Mac name", file.MacPath);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.encoding-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void LhaRejectsATruncatedStoredPayload()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Read Me", "contents"u8.ToArray());
        Array.Resize(ref archive, archive.Length - 2);

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void LhaRejectsAnArchiveLargerThanTheConfiguredInputLimit()
    {
        byte[] archive = LhaFixture.BuildLevelZeroStoredFile("Read Me", "contents"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => LhaReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxExpandedBytesPerInput = 8 })));
    }

    private static class LhaFixture
    {
        public static byte[] BuildNewStyleLiteralBlock(string method, ReadOnlySpan<byte> literals)
        {
            if (literals.IsEmpty || literals.Length > 65)
                throw new ArgumentOutOfRangeException(nameof(literals));
            foreach (byte literal in literals)
                if (literal is not (byte)'A' and not (byte)'B')
                    throw new ArgumentOutOfRangeException(nameof(literals));

            var bits = new LhaBitWriter();
            WriteNewStyleLiteralBlock(bits, method, literals);
            return bits.ToArray();
        }

        public static byte[] BuildNewStyleTwoBlockLiteralStream(string method)
        {
            var bits = new LhaBitWriter();
            WriteNewStyleLiteralBlock(bits, method, "A"u8);
            WriteNewStyleLiteralBlock(bits, method, "B"u8);
            return bits.ToArray();
        }

        public static byte[] BuildLzsLiteral(byte value)
        {
            var bits = new LhaBitWriter();
            bits.Write(1, 1); // LArc marks a literal with a one bit.
            bits.Write(value, 8);
            return bits.ToArray();
        }

        public static byte[] BuildLzsMatch(int position, int length)
        {
            var bits = new LhaBitWriter();
            WriteLzsMatch(bits, position, length);
            return bits.ToArray();
        }

        public static byte[] BuildLzsLiteralAndMatch(byte value, int position, int length)
        {
            var bits = new LhaBitWriter();
            bits.Write(1, 1);
            bits.Write(value, 8);
            WriteLzsMatch(bits, position, length);
            return bits.ToArray();
        }

        public static byte[] BuildLh3RepeatedLiteralBlock(byte value, int count)
        {
            var bits = new LhaBitWriter();
            bits.Write(count, 16);
            bits.Write(1, 1);
            bits.Write(0, 4);
            bits.Write(1, 1);
            bits.Write(0, 4);
            bits.Write(1, 1);
            bits.Write(0, 4);
            bits.Write(value, 9);
            bits.Write(0, 1); // Use the method's ready-made position table.
            return bits.ToArray();
        }

        public static byte[] BuildLh3LiteralAndOverlappingMatch(int positionTreeMode)
        {
            var bits = new LhaBitWriter();
            bits.Write(2, 16); // One literal token and one match token.
            for (int symbol = 0; symbol < 286; symbol++)
            {
                bool hasCode = symbol is (byte)'A' or 256;
                bits.Write(hasCode ? 1 : 0, 1);
                if (hasCode) bits.Write(0, 4); // A one-bit canonical code.
            }

            bits.Write(positionTreeMode == 0 ? 0 : 1, 1);
            if (positionTreeMode == 1)
            {
                bits.Write(1, 4);
                bits.Write(1, 4);
                bits.Write(1, 4); // The three one-bit lengths select the compact form.
                bits.Write(0, 7); // The only position symbol is zero.
            }
            else if (positionTreeMode == 2)
            {
                for (int symbol = 0; symbol < 128; symbol++)
                    bits.Write(symbol < 2 ? 1 : 0, 4); // Position codes zero and one have one-bit codes.
            }

            bits.Write(0, 1); // Literal A.
            bits.Write(1, 1); // Length-three match.
            if (positionTreeMode == 0)
                bits.Write(0, 2); // Ready-made table's code for position symbol zero.
            else if (positionTreeMode == 2)
                bits.Write(0, 1); // Canonical code for position symbol zero.
            bits.Write(0, 6); // Low bits make the absolute ring position zero.
            return bits.ToArray();
        }

        public static byte[] BuildLh3TwoLiteralBlocks(byte first, byte second)
        {
            var bits = new LhaBitWriter();
            WriteLh3SingleLiteralBlock(bits, first);
            WriteLh3SingleLiteralBlock(bits, second);
            return bits.ToArray();
        }

        public static byte[] BuildLh3OversubscribedLiteralTree()
        {
            var bits = new LhaBitWriter();
            bits.Write(1, 16);
            for (int symbol = 0; symbol < 286; symbol++)
            {
                bool hasCode = symbol is 0 or 1 or 2 or 3;
                bits.Write(hasCode ? 1 : 0, 1);
                if (hasCode) bits.Write(symbol == 2 ? 1 : 0, 4);
            }
            return bits.ToArray();
        }

        public static byte[] BuildLh3LongMatch(int extraLength)
        {
            var bits = new LhaBitWriter();
            bits.Write(2, 16);
            for (int symbol = 0; symbol < 286; symbol++)
            {
                bool hasCode = symbol is (byte)'A' or 285;
                bits.Write(hasCode ? 1 : 0, 1);
                if (hasCode) bits.Write(0, 4);
            }
            bits.Write(1, 1); // Transmit the position tree.
            for (int index = 0; index < 3; index++)
            {
                bits.Write(1, 4);
            }
            bits.Write(0, 7); // A degenerate tree for absolute position zero.
            bits.Write(0, 1); // Literal A.
            bits.Write(1, 1); // The longest length symbol.
            bits.Write(extraLength, 8);
            bits.Write(0, 6); // The absolute ring position is zero.
            return bits.ToArray();
        }

        public static byte[] BuildLh2PositionTreeGrowthStream()
        {
            var bits = new LhaBitWriter();
            bits.Write(0b11100001, 8); // Extended length symbol 285.
            bits.Write(33, 8); // Match length 65.
            bits.Write(0, 6); // Initial position symbol zero and offset zero.
            bits.Write(0b11000100, 8); // After the first update, symbol 256 is a length-three match.
            bits.Write(0, 1); // The new high position symbol one has code zero.
            bits.Write(0, 6); // Position 64 points back to the start of the window.
            return bits.ToArray();
        }

        private static void WriteLh3SingleLiteralBlock(LhaBitWriter bits, byte value)
        {
            bits.Write(1, 16);
            for (int index = 0; index < 3; index++)
            {
                bits.Write(1, 1);
                bits.Write(0, 4);
            }
            bits.Write(value, 9);
            bits.Write(0, 1); // Reuse the method's ready-made position tree.
        }

        private static void WriteLzsMatch(LhaBitWriter bits, int position, int length)
        {
            if ((uint)position >= 2048 || length is < 2 or > 17)
                throw new ArgumentOutOfRangeException(nameof(length));
            bits.Write(0, 1); // A zero bit introduces a sliding-window match.
            bits.Write(position, 11);
            bits.Write(length - 2, 4);
        }

        private static void WriteNewStyleLiteralBlock(LhaBitWriter bits, string method, ReadOnlySpan<byte> literals)
        {
            bits.Write(literals.Length, 16);
            WriteTempTreeWithSkipAndLengthOne(bits);
            bits.Write(67, 9);
            for (int index = 0; index < 65; index++) bits.Write(0, 1); // Skip codes 0 through 64.
            bits.Write(1, 1); // Code 65 has length 1.
            bits.Write(1, 1); // Code 66 has length 1.
            int offsetCodeBits = method[3] >= '6' ? 5 : 4;
            bits.Write(0, offsetCodeBits); // One offset code.
            bits.Write(0, offsetCodeBits); // Its value is zero.
            foreach (byte literal in literals) bits.Write(literal == 65 ? 0 : 1, 1);
        }

        public static byte[] BuildNewStyleBackReferenceBlock(string method)
        {
            var bits = new LhaBitWriter();
            bits.Write(4, 16); // Three literals and one match command.
            bits.Write(5, 5); // Temporary codes 0 and 4 have one-bit codes.
            bits.Write(1, 3);
            bits.Write(0, 3);
            bits.Write(0, 3);
            bits.Write(0, 2); // No extra zero-length temporary codes after index 2.
            bits.Write(0, 3);
            bits.Write(1, 3);

            bits.Write(257, 9);
            for (int index = 0; index < 65; index++) bits.Write(0, 1);
            bits.Write(1, 1); // Code 65: length 2.
            bits.Write(1, 1); // Code 66: length 2.
            bits.Write(1, 1); // Code 67: length 2.
            for (int index = 68; index < 256; index++) bits.Write(0, 1);
            bits.Write(1, 1); // Code 256: length 2.

            int offsetCodeBits = method[3] >= '6' ? 5 : 4;
            bits.Write(0, offsetCodeBits); // One offset code.
            bits.Write(2, offsetCodeBits); // Offset code 2, followed by its low bit.
            bits.Write(0, 2); // A
            bits.Write(1, 2); // B
            bits.Write(2, 2); // C
            bits.Write(3, 2); // Copy three bytes from distance three.
            bits.Write(0, 1); // Low offset bit gives distance three.
            return bits.ToArray();
        }

        private static void WriteTempTreeWithSkipAndLengthOne(LhaBitWriter bits)
        {
            bits.Write(4, 5); // Temporary symbols 0 and 4 have one-bit codes.
            bits.Write(1, 3);
            bits.Write(0, 3);
            bits.Write(0, 3);
            bits.Write(0, 2);
            bits.Write(1, 3);
        }

        public static byte[] BuildLevelZeroStoredFile(string name, byte[] data)
            => BuildLevelZeroEntry("-lh0-", name, data, data.Length);

        public static byte[] BuildLevelOneStoredFile(string baseName, string? directory, string? filename, byte[] data)
        {
            var extensions = new List<byte[]>();
            if (filename is not null) extensions.Add(BuildExtension(0x01, MacString.FromMacRoman(filename).Bytes));
            if (directory is not null) extensions.Add(BuildExtension(0x02, MacString.FromMacRoman(directory).Bytes));
            for (int index = 0; index < extensions.Count; index++)
            {
                ushort nextSize = index + 1 == extensions.Count ? (ushort)0 : checked((ushort)extensions[index + 1].Length);
                BinaryPrimitives.WriteUInt16LittleEndian(extensions[index].AsSpan(extensions[index].Length - 2),
                    nextSize);
            }

            byte[] baseNameBytes = MacString.FromMacRoman(baseName).Bytes.ToArray();
            int baseHeaderLength = checked(27 + baseNameBytes.Length);
            byte[] extensionData = [.. extensions.SelectMany(extension => extension)];
            int packedSize = checked(extensionData.Length + data.Length);
            byte[] archive = new byte[baseHeaderLength + extensionData.Length + data.Length + 1];
            archive[0] = checked((byte)(baseHeaderLength - 2));
            Encoding.ASCII.GetBytes("-lh0-").CopyTo(archive, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(7), (uint)packedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(11), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(15), 0x00210000);
            archive[19] = 0x20;
            archive[20] = 1;
            archive[21] = checked((byte)baseNameBytes.Length);
            baseNameBytes.CopyTo(archive, 22);
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(22 + baseNameBytes.Length), Crc16Ibm(data));
            archive[24 + baseNameBytes.Length] = (byte)'m';
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(baseHeaderLength - 2),
                extensions.Count == 0 ? (ushort)0 : checked((ushort)extensions[0].Length));
            archive[1] = HeaderChecksum(archive.AsSpan(2, archive[0]));
            extensionData.CopyTo(archive, baseHeaderLength);
            data.CopyTo(archive, baseHeaderLength + extensionData.Length);
            return archive;
        }

        public static byte[] BuildLevelTwoStoredFile(string directory, string filename, byte[] data,
            bool padding = false, bool includeHeaderCrcInfo = false)
        {
            byte[] nameBytes = MacString.FromMacRoman(filename).Bytes.ToArray();
            byte[] directoryBytes = MacString.FromMacRoman(directory).Bytes.ToArray();
            byte[] crcExtension = new byte[5 + (includeHeaderCrcInfo ? 1 : 0)];
            crcExtension[0] = 0;
            if (includeHeaderCrcInfo) crcExtension[3] = 0xA5;
            byte[] nameExtension = BuildExtension(0x01, nameBytes);
            byte[] directoryExtension = BuildExtension(0x02, directoryBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(crcExtension.AsSpan(crcExtension.Length - 2),
                checked((ushort)nameExtension.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(nameExtension.AsSpan(nameExtension.Length - 2),
                checked((ushort)directoryExtension.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(directoryExtension.AsSpan(directoryExtension.Length - 2), 0);
            byte[] extensions = [.. crcExtension, .. nameExtension, .. directoryExtension];
            int headerLength = checked(26 + extensions.Length + (padding ? 1 : 0));
            byte[] archive = new byte[headerLength + data.Length + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(archive, checked((ushort)headerLength));
            Encoding.ASCII.GetBytes("-lh0-").CopyTo(archive, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(7), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(11), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(15), 0x00210000);
            archive[19] = 0x20;
            archive[20] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(21), Crc16Ibm(data));
            archive[23] = (byte)'m';
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(24), checked((ushort)crcExtension.Length));
            extensions.CopyTo(archive, 26);
            ushort headerCrc = Crc16Ibm(archive.AsSpan(0, headerLength));
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(27), headerCrc);
            data.CopyTo(archive, headerLength);
            return archive;
        }

        public static byte[] BuildLevelThreeStoredFile(string directory, string filename, byte[] data,
            bool includeHeaderCrcInfo = false)
        {
            byte[] nameBytes = MacString.FromMacRoman(filename).Bytes.ToArray();
            byte[] directoryBytes = MacString.FromMacRoman(directory).Bytes.ToArray();
            byte[] crcExtension = new byte[7 + (includeHeaderCrcInfo ? 1 : 0)];
            crcExtension[0] = 0;
            if (includeHeaderCrcInfo) crcExtension[3] = 0xA5;
            byte[] nameExtension = BuildExtension32(0x01, nameBytes);
            byte[] directoryExtension = BuildExtension32(0x02, directoryBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(crcExtension.AsSpan(crcExtension.Length - 4),
                checked((uint)nameExtension.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(nameExtension.AsSpan(nameExtension.Length - 4),
                checked((uint)directoryExtension.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(directoryExtension.AsSpan(directoryExtension.Length - 4), 0);
            byte[] extensions = [.. crcExtension, .. nameExtension, .. directoryExtension];
            int headerLength = checked(32 + extensions.Length);
            byte[] archive = new byte[headerLength + data.Length + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(archive, 4);
            Encoding.ASCII.GetBytes("-lh0-").CopyTo(archive, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(7), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(11), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(15), 0x00210000);
            archive[19] = 0x20;
            archive[20] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(21), Crc16Ibm(data));
            archive[23] = (byte)'m';
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(24), checked((uint)headerLength));
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(28), checked((uint)crcExtension.Length));
            extensions.CopyTo(archive, 32);
            ushort headerCrc = Crc16Ibm(archive.AsSpan(0, headerLength));
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(33), headerCrc);
            data.CopyTo(archive, headerLength);
            return archive;
        }

        public static byte[] BuildLevelOneWithUnknownExtension(byte[] data)
        {
            byte[] extension = BuildExtension(0x7F, "metadata"u8);
            BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(extension.Length - 2), 0);
            byte[] entry = BuildLevelOneStoredFile("name", null, null, data);
            int headerLength = entry[0] + 2;
            int oldExtensionLength = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(headerLength - 2));
            if (oldExtensionLength != 0) throw new InvalidOperationException("Expected an empty extension chain.");

            byte[] result = new byte[entry.Length + extension.Length];
            entry.AsSpan(0, headerLength).CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(headerLength - 2), checked((ushort)extension.Length));
            entry.AsSpan(headerLength, entry.Length - headerLength - 1).CopyTo(result.AsSpan(headerLength + extension.Length));
            extension.CopyTo(result, headerLength);
            result[^1] = 0;
            int packedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(7)) + extension.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(7), checked((uint)packedSize));
            result[1] = HeaderChecksum(result.AsSpan(2, result[0]));
            return result;
        }

        private static byte[] BuildExtension(byte type, ReadOnlySpan<byte> data)
        {
            var extension = new byte[data.Length + 3];
            extension[0] = type;
            data.CopyTo(extension.AsSpan(1));
            return extension;
        }

        private static byte[] BuildExtension32(byte type, ReadOnlySpan<byte> data)
        {
            var extension = new byte[data.Length + 5];
            extension[0] = type;
            data.CopyTo(extension.AsSpan(1));
            return extension;
        }

        public static byte[] BuildLevelZeroEntry(string method, string name, byte[] packedData, int expandedSize,
            byte osIdentifier = (byte)'m', byte[]? checksumData = null)
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length > byte.MaxValue || Encoding.ASCII.GetByteCount(method) != 5)
                throw new ArgumentOutOfRangeException(nameof(name));

            int headerLength = checked(25 + nameBytes.Length);
            byte[] archive = new byte[headerLength + packedData.Length + 1];
            archive[0] = checked((byte)(headerLength - 2));
            Encoding.ASCII.GetBytes(method).CopyTo(archive, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(7), (uint)packedData.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(11), (uint)expandedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(15), 0x00210000);
            archive[19] = 0x20;
            archive[20] = 0;
            archive[21] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(archive, 22);
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(22 + nameBytes.Length),
                Crc16Ibm(checksumData ?? packedData));
            archive[24 + nameBytes.Length] = osIdentifier;
            archive[1] = HeaderChecksum(archive.AsSpan(2, archive[0]));
            packedData.CopyTo(archive, headerLength);
            return archive;
        }

        public static byte HeaderChecksum(ReadOnlySpan<byte> header)
        {
            byte checksum = 0;
            foreach (byte value in header) checksum = unchecked((byte)(checksum + value));
            return checksum;
        }

        private static ushort Crc16Ibm(ReadOnlySpan<byte> data)
        {
            ushort crc = 0;
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }
    }

    private sealed class LhaBitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitCount;

        public void Write(int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--)
            {
                if (_bitCount == 0) _bytes.Add(0);
                _bytes[^1] |= (byte)(((value >> bit) & 1) << (7 - _bitCount));
                _bitCount = (_bitCount + 1) & 7;
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
