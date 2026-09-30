using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class StuffItFeatureTests
{
    [Fact]
    public void StoredStuffItV5FilePreservesBothForksAndFinderMetadata()
    {
        byte[] image = StuffItFixture.BuildFile("Read Me", "data fork"u8.ToArray(), "resource fork"u8.ToArray());
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItV5FolderEntriesBecomeMacFileFolderPaths()
    {
        byte[] image = StuffItFixture.BuildNestedFile();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Docs:Read Me", file.MacPath);
        Assert.Equal("inside"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItV5Utf8NamesRemainAvailableInMacPaths()
    {
        byte[] image = StuffItFixture.BuildFile("文書", "text"u8.ToArray(), []);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("文書", file.MacPath);
    }

    [Fact]
    public void DefaultUnwrapperRecognizesStuffItArchives()
    {
        byte[] image = StuffItFixture.BuildFile("Read Me", "inside"u8.ToArray(), []);
        var input = new MacFile { Name = MacString.FromMacRoman("archive.sit"), DataFork = ForkData.FromBytes(image) };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(input, "host", new ContainerContext());

        MacFile unpacked = Assert.Single(result.Children).File;
        Assert.Equal("Read Me", unpacked.MacPath);
        Assert.Equal("inside"u8.ToArray(), unpacked.DataFork.ToArray());
    }

    [Fact]
    public void EncryptedStuffItEntryIsReportedAndNotOpened()
    {
        byte[] image = StuffItFixture.BuildFile("Locked", "secret"u8.ToArray(), [], encrypted: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.encrypted" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void UnsupportedStuffItCompressionIsReportedAndNotReturnedAsAnEmptyFile()
    {
        byte[] image = StuffItFixture.BuildFile("Compressed", "encoded"u8.ToArray(), [], dataMethod: 4);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.compression-unsupported");
    }

    [Fact]
    public void StuffItCompressMethodDecodesLzwKwKwKCaseInBothForks()
    {
        byte[] expected = "ABABABA"u8.ToArray();
        byte[] encoded = StuffItFixture.EncodeCompressCodes(65, 66, 257, 259);
        byte[] image = StuffItFixture.BuildFile("LZW", expected, expected, dataMethod: 2, encodedData: encoded,
            resourceMethod: 2, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodRejectsAnInvalidLzwCode()
    {
        byte[] image = StuffItFixture.BuildFile("Bad LZW", "A"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressCodes(65, 300));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItCompressMethodRejectsLzwOutputLongerThanTheForkLength()
    {
        byte[] image = StuffItFixture.BuildFile("Long LZW", "AB"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressCodes(65, 66, 257, 259));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItCompressMethodIncreasesCodeWidthWhenTheDictionaryReaches512Entries()
    {
        byte[] expected = [.. Enumerable.Range(0, 256).Select(static value => (byte)value), 0];
        byte[] image = StuffItFixture.BuildFile("Wide LZW", expected, [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressWidthBoundary());

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodCanClearAndRestartItsDictionary()
    {
        byte[] image = StuffItFixture.BuildFile("Reset LZW", "ABC"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressWithClear());

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABC"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodSupportsItsFullFourteenBitDictionary()
    {
        const int outputLength = 16_129;
        byte[] expected = new byte[outputLength];
        byte[] image = StuffItFixture.BuildFile("Full LZW", expected, [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressRepeatedLiterals(outputLength));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItHuffmanMethodDecodesATreeWrittenInTheBitStream()
    {
        byte[] encoded = StuffItFixture.EncodeHuffman("ABA", ((byte)'A', (byte)'B'));
        byte[] image = StuffItFixture.BuildFile("Huffman", "ABA"u8.ToArray(), "ABA"u8.ToArray(), dataMethod: 3,
            encodedData: encoded, resourceMethod: 3, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("ABA"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItHuffmanMethodRejectsATruncatedPrefixTree()
    {
        byte[] image = StuffItFixture.BuildFile("Bad Huffman", [0], [], dataMethod: 3, encodedData: [0]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItHuffmanMethodRejectsTruncatedSymbolData()
    {
        byte[] encoded = StuffItFixture.EncodeHuffman("A", ((byte)'A', (byte)'B'));
        byte[] image = StuffItFixture.BuildFile("Short Huffman", "AAAAAAA"u8.ToArray(), [], dataMethod: 3,
            encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItLzahMethodDecodesAdaptiveLiteralsAndCopiesFromItsInitialWindow()
    {
        byte[] encodedData = StuffItFixture.EncodeLzah(65, 66, 65, 66, 65, 66, 65);
        byte[] encodedResource = StuffItFixture.EncodeLzah(256);
        byte[] image = StuffItFixture.BuildFile("LZAH", "ABABABA"u8.ToArray(), "   "u8.ToArray(),
            dataMethod: 5, encodedData: encodedData, resourceMethod: 5, encodedResource: encodedResource);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABABABA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("   "u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItLzahMethodRejectsInputThatEndsBeforeTheDeclaredForkLength()
    {
        byte[] image = StuffItFixture.BuildFile("Truncated LZAH", "A"u8.ToArray(), [], dataMethod: 5,
            encodedData: []);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRle90MethodDecodesBothForks()
    {
        byte[] data = [.. Enumerable.Repeat((byte)'A', 3), (byte)'B', 0x90, 0x90];
        byte[] encodedData = [(byte)'A', 0x90, 3, (byte)'B', 0x90, 0, 0x90, 2];
        byte[] resource = [(byte)'R', (byte)'R'];
        byte[] encodedResource = [(byte)'R', 0x90, 2];
        byte[] image = StuffItFixture.BuildFile("Runs", data, resource, dataMethod: 1,
            encodedData: encodedData, resourceMethod: 1, encodedResource: encodedResource);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItRle90RejectsARunBeforeAnyLiteralByte()
    {
        byte[] image = StuffItFixture.BuildFile("Malformed", [0x90], [], dataMethod: 1,
            encodedData: [0x90, 2]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRle90RejectsATrailingRunMarker()
    {
        byte[] image = StuffItFixture.BuildFile("Malformed", [0x90], [], dataMethod: 1,
            encodedData: [(byte)'A', 0x90]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItV5FileWhoseForkExceedsItsArchiveIsRejected()
    {
        byte[] image = StuffItFixture.BuildFile("Truncated", "data"u8.ToArray(), []);
        Array.Resize(ref image, image.Length - 1);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItArchiveRejectsRootMemberOffsetBeyondAddressableInput()
    {
        byte[] image = StuffItFixture.BuildFile("file", [], []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(94), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItForkRejectsLengthBeyondAddressableInput()
    {
        byte[] image = StuffItFixture.BuildFile("file", "x"u8.ToArray(), []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100 + 38), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItHeaderAndForkChecksumMismatchesAreReportedWithoutDiscardingDecodedBytes()
    {
        byte[] image = StuffItFixture.BuildFile("damaged", "payload"u8.ToArray(), []);
        image[100 + 8] ^= 1;
        image[^1] ^= 1;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("payloae"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.header-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItMemberChainCycleIsRejected()
    {
        byte[] image = StuffItFixture.BuildFile("loop", [], []);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(92), 2);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100 + 22), 100);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMemberNameMustBeValidUtf8()
    {
        byte[] image = StuffItFixture.BuildFile("name", [], []);
        image[100 + 48] = 0xFF;

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRespectsTheConfiguredEntryLimit()
    {
        byte[] image = StuffItFixture.BuildFile("file", [], []);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(options: new ContainerReadOptions { MaxVolumeEntries = 0 })));
    }

    [Fact]
    public void StuffItProbeRequiresTheV5SignatureAndVersion()
    {
        byte[] valid = StuffItFixture.BuildFile("file", [], []);
        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[82] = 4;

        Assert.True(StuffItReader.Instance.CanRead(ForkData.FromBytes(valid)));
        Assert.False(StuffItReader.Instance.CanRead(ForkData.FromBytes(wrongVersion)));
        Assert.False(StuffItReader.Instance.CanRead(ForkData.FromBytes("StuffIt "u8.ToArray())));
    }

    private static class StuffItFixture
    {
        private const int ArchiveHeaderLength = 100;
        private const uint CreateSeconds = 2_500_000_000;
        private const uint ModifySeconds = 2_600_000_000;

        public static byte[] BuildFile(string name, byte[] data, byte[] resource, bool encrypted = false,
            byte dataMethod = 0, byte[]? encodedData = null, byte resourceMethod = 0, byte[]? encodedResource = null)
        {
            encodedData ??= data;
            encodedResource ??= resource;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int memberHeaderLength = 48 + nameBytes.Length;
            int finderInfoLength = 36;
            int resourceInfoLength = encodedResource.Length == 0 ? 0 : 14;
            int dataOffset = ArchiveHeaderLength + memberHeaderLength + finderInfoLength + resourceInfoLength + encodedResource.Length;
            byte[] image = new byte[dataOffset + encodedData.Length];
            WriteArchiveHeader(image, 1, ArchiveHeaderLength);

            Span<byte> member = image.AsSpan(ArchiveHeaderLength, memberHeaderLength);
            U32(member, 0, 0xA5A5A5A5);
            U16(member, 6, checked((ushort)memberHeaderLength));
            member[9] = encrypted ? (byte)0x20 : (byte)0;
            U32(member, 10, CreateSeconds);
            U32(member, 14, ModifySeconds);
            U32(member, 22, 0); // no following member
            U16(member, 30, checked((ushort)nameBytes.Length));
            U32(member, 34, checked((uint)data.Length));
            U32(member, 38, checked((uint)encodedData.Length));
            U16(member, 42, Crc16Arc(data));
            member[46] = dataMethod;
            nameBytes.CopyTo(member[48..]);
            U16(member, 32, HeaderCrc(member));

            Span<byte> finder = image.AsSpan(ArchiveHeaderLength + memberHeaderLength, finderInfoLength);
            if (encodedResource.Length != 0) U16(finder, 0, 1);
            "TEXTttxt"u8.CopyTo(finder[4..]);
            U16(finder, 12, 0x4000);
            int forksAt = ArchiveHeaderLength + memberHeaderLength + finderInfoLength;
            if (encodedResource.Length != 0)
            {
                U32(image.AsSpan(forksAt), 0, checked((uint)resource.Length));
                U32(image.AsSpan(forksAt), 4, checked((uint)encodedResource.Length));
                U16(image.AsSpan(forksAt), 8, Crc16Arc(resource));
                image[forksAt + 12] = resourceMethod;
                encodedResource.CopyTo(image.AsSpan(forksAt + resourceInfoLength, encodedResource.Length));
            }
            encodedData.CopyTo(image, dataOffset);
            U32(image, 84, checked((uint)image.Length));
            return image;
        }

        public static byte[] BuildNestedFile()
        {
            byte[] folderName = "Docs"u8.ToArray();
            byte[] fileName = "Read Me"u8.ToArray();
            byte[] child = BuildFile("Read Me", "inside"u8.ToArray(), []);
            int childOffset = ArchiveHeaderLength + 48 + folderName.Length;
            int childLength = child.Length - ArchiveHeaderLength;
            byte[] image = new byte[childOffset + childLength];
            WriteArchiveHeader(image, 1, ArchiveHeaderLength);

            Span<byte> folder = image.AsSpan(ArchiveHeaderLength, 48 + folderName.Length);
            U32(folder, 0, 0xA5A5A5A5);
            U16(folder, 6, checked((ushort)folder.Length));
            folder[9] = 0x40;
            U32(folder, 10, CreateSeconds);
            U32(folder, 14, ModifySeconds);
            U32(folder, 22, 0);
            U16(folder, 30, checked((ushort)folderName.Length));
            U32(folder, 34, checked((uint)childOffset));
            U16(folder, 46, 1);
            folderName.CopyTo(folder[48..]);
            U16(folder, 32, HeaderCrc(folder));
            child.AsSpan(ArchiveHeaderLength).CopyTo(image.AsSpan(childOffset));
            U32(image, childOffset + 22, 0);
            U16(image.AsSpan(childOffset + 32), 0, HeaderCrc(image.AsSpan(childOffset, 48 + fileName.Length)));
            U32(image, 84, checked((uint)image.Length));
            return image;
        }

        private static void WriteArchiveHeader(Span<byte> image, ushort rootEntries, uint firstEntry)
        {
            "StuffIt "u8.CopyTo(image);
            image[82] = 5;
            U32(image, 84, 0);
            U16(image, 92, rootEntries);
            U32(image, 94, firstEntry);
        }

        public static byte[] EncodeCompressCodes(params ushort[] codes)
        {
            const int CodeBits = 9;
            int codeBytes = (codes.Length * CodeBits + 7) / 8;
            byte[] encoded = new byte[9 + codeBytes];
            encoded[1] = 1; // Block-mode clear code 256, padded to the next 8-code group.
            for (int index = 0; index < codes.Length; index++)
            {
                int bitOffset = index * CodeBits;
                for (int bit = 0; bit < CodeBits; bit++)
                    if ((codes[index] & (1 << bit)) != 0)
                        encoded[9 + (bitOffset + bit) / 8] |= (byte)(1 << ((bitOffset + bit) & 7));
            }
            return encoded;
        }

        public static byte[] EncodeCompressWidthBoundary()
        {
            byte[] encoded = new byte[9 + 256 * 9 / 8 + 2];
            encoded[1] = 1;
            for (int code = 0; code < 256; code++)
                for (int bit = 0; bit < 9; bit++)
                    if ((code & (1 << bit)) != 0)
                    {
                        int bitOffset = code * 9 + bit;
                        encoded[9 + bitOffset / 8] |= (byte)(1 << (bitOffset & 7));
            }
            return encoded;
        }

        public static byte[] EncodeCompressWithClear()
        {
            byte[] encoded = new byte[20];
            encoded[1] = 1;
            WriteCode(encoded, 72, 65, 9);
            WriteCode(encoded, 81, 66, 9);
            WriteCode(encoded, 90, 256, 9);
            WriteCode(encoded, 144, 67, 9);
            return encoded;
        }

        public static byte[] EncodeCompressRepeatedLiterals(int count)
        {
            var codes = new List<(ushort Value, int Width)>(count);
            int width = 9;
            int nextCode = 257;
            bool hasPrevious = false;
            for (int index = 0; index < count; index++)
            {
                codes.Add((0, width));
                if (hasPrevious && nextCode < 1 << 14)
                {
                    nextCode++;
                    if (width < 14 && nextCode == 1 << width) width++;
                }
                hasPrevious = true;
            }

            int bitCount = codes.Sum(static code => code.Width);
            byte[] encoded = new byte[9 + (bitCount + 7) / 8];
            encoded[1] = 1;
            int bitOffset = 72;
            foreach ((ushort value, int codeWidth) in codes)
            {
                WriteCode(encoded, bitOffset, value, codeWidth);
                bitOffset += codeWidth;
            }
            return encoded;
        }

        public static byte[] EncodeHuffman(string text, (byte Zero, byte One) symbols)
        {
            var bits = new List<bool>();
            bits.Add(false); // Root internal node.
            WriteTreeLeaf(bits, symbols.Zero);
            WriteTreeLeaf(bits, symbols.One);
            foreach (char value in text) bits.Add(value == symbols.Zero ? false : true);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int index = 0; index < bits.Count; index++)
                if (bits[index]) encoded[index / 8] |= (byte)(0x80 >> (index & 7));
            return encoded;
        }

        public static byte[] EncodeLzah(params ushort[] symbols)
        {
            const int LeafCount = 314;
            const int TreeSize = LeafCount * 2 - 1;
            var frequencies = new int[TreeSize + 1];
            var forward = new int[TreeSize];
            var backward = new int[TreeSize + LeafCount];
            for (int symbol = 0; symbol < LeafCount; symbol++)
            {
                frequencies[symbol] = 1;
                forward[symbol] = symbol + TreeSize;
                backward[symbol + TreeSize] = symbol;
            }
            for (int node = LeafCount, child = 0; node < TreeSize; node++, child += 2)
            {
                frequencies[node] = frequencies[child] + frequencies[child + 1];
                forward[node] = child;
                backward[child] = backward[child + 1] = node;
            }
            frequencies[TreeSize] = ushort.MaxValue;

            var bits = new List<bool>();
            foreach (ushort symbol in symbols)
            {
                int child = backward[symbol + TreeSize];
                var path = new List<bool>();
                for (int parent = backward[child]; parent != 0; child = parent, parent = backward[child])
                    path.Add(forward[parent] + 1 == child);
                path.Reverse();
                bits.AddRange(path);
                UpdateLzahTree(symbol, frequencies, forward, backward, TreeSize);
                if (symbol >= 256)
                    for (int bit = 0; bit < 9; bit++) bits.Add(false); // Zero-distance offset.
            }

            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        private static void UpdateLzahTree(ushort symbol, int[] frequencies, int[] forward, int[] backward,
            int treeSize)
        {
            int node = backward[symbol + treeSize];
            while (node != 0)
            {
                int weight = ++frequencies[node];
                int swap = node + 1;
                if (frequencies[swap] < weight)
                {
                    while (frequencies[++swap] < weight) { }
                    swap--;
                    frequencies[node] = frequencies[swap];
                    frequencies[swap] = weight;

                    int child = forward[node];
                    backward[child] = swap;
                    if (child < treeSize) backward[child + 1] = swap;
                    forward[node] = forward[swap];
                    forward[swap] = child;
                    child = forward[node];
                    backward[child] = node;
                    if (child < treeSize) backward[child + 1] = node;
                    node = swap;
                }
                node = backward[node];
            }
        }

        private static void WriteTreeLeaf(List<bool> bits, byte symbol)
        {
            bits.Add(true);
            for (int bit = 7; bit >= 0; bit--) bits.Add((symbol & (1 << bit)) != 0);
        }

        private static void WriteCode(Span<byte> output, int bitOffset, ushort value, int width)
        {
            for (int bit = 0; bit < width; bit++)
                if ((value & (1 << bit)) != 0)
                    output[(bitOffset + bit) / 8] |= (byte)(1 << ((bitOffset + bit) & 7));
        }

        private static ushort HeaderCrc(ReadOnlySpan<byte> header)
        {
            byte[] copy = header.ToArray();
            copy[32] = copy[33] = 0;
            return Crc16Arc(copy);
        }

        private static ushort Crc16Arc(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }

        private static void U16(Span<byte> data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);

        private static void U32(Span<byte> data, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);

    }
}
