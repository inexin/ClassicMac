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
