using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class CompactProFeatureTests
{
    [Fact]
    public void CompactProStoredRleFilePreservesForksAndFinderMetadata()
    {
        byte[] resource = [0x81, 0x82];
        byte[] data = "aaa"u8.ToArray();
        byte[] archive = CompactProFixture.BuildFile("Read Me", resource, data,
            encodedResource: [0x81, 0x82, 0], encodedData: [(byte)'a', 0x81, 0x82, 3]);
        var diagnostics = new List<Diagnostic>();
        ForkData input = ForkData.FromBytes(archive);

        Assert.True(CompactProReader.Instance.CanRead(input));
        MacFile file = Assert.Single(CompactProReader.Instance.Read(input,
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CompactProArchiveCommentIsReportedAsMacRomanText()
    {
        byte[] archive = CompactProFixture.BuildFile("payload", [], [], [], [], comment: [0x43, 0x61, 0x66, 0x8E]);
        var diagnostics = new List<Diagnostic>();

        _ = CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.comment" &&
            diagnostic.Severity == DiagnosticSeverity.Info && diagnostic.Message == "Compact Pro comment: Café");
    }

    [Fact]
    public void DefaultUnwrapperRecognizesCompactProArchives()
    {
        byte[] archive = CompactProFixture.BuildFile("payload", [], "inside"u8.ToArray(),
            encodedResource: [], encodedData: "inside"u8.ToArray());
        var input = new MacFile { Name = MacString.FromMacRoman("archive.cpt"), DataFork = ForkData.FromBytes(archive) };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(input, "host", new ContainerContext());

        MacFile unpacked = Assert.Single(result.Children).File;
        Assert.Equal("payload", unpacked.MacPath);
        Assert.Equal("inside"u8.ToArray(), unpacked.DataFork.ToArray());
    }

    [Fact]
    public void CompactProNestedDirectoryEntriesBecomeMacFileFolderPaths()
    {
        byte[] archive = CompactProFixture.BuildNestedFile();

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("Docs:Read Me", file.MacPath);
        Assert.Equal("inside"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void CompactProReadsForkPayloadsFromSiblingVolumes()
    {
        byte[] resource = "resource"u8.ToArray();
        byte[] data = "inside"u8.ToArray();
        byte[] archive = CompactProFixture.BuildFile("Read Me", resource, data, resource,
            "inside"u8.ToArray(), dataOffsetOverride: 8, entryVolume: 2);
        byte[] secondVolume = new byte[8 + resource.Length + data.Length];
        secondVolume[0] = 1;
        secondVolume[1] = 2;
        resource.CopyTo(secondVolume.AsSpan(8));
        data.CopyTo(secondVolume.AsSpan(8 + resource.Length));
        var sibling = new MacFile
        {
            Name = MacString.FromMacRoman("archive.002"),
            DataFork = ForkData.FromBytes(secondVolume),
        };
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics, siblings: () => [sibling])));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume");
    }

    [Fact]
    public void CompactProOpensTheFinalSegmentAndReadsPayloadFromAnEarlierSegment()
    {
        byte[] resource = "resource"u8.ToArray();
        byte[] data = "segment payload"u8.ToArray();
        byte[] finalSegment = CompactProFixture.BuildFile("Read Me", resource, data, resource, data,
            dataOffsetOverride: 8, entryVolume: 1);
        finalSegment[1] = 2;
        byte[] firstSegment = new byte[8 + resource.Length + data.Length];
        firstSegment[0] = 1;
        firstSegment[1] = 1;
        resource.CopyTo(firstSegment.AsSpan(8));
        data.CopyTo(firstSegment.AsSpan(8 + resource.Length));
        var firstSegmentFile = new MacFile
        {
            Name = MacString.FromMacRoman("archive.cpt.#1"),
            DataFork = ForkData.FromBytes(firstSegment),
        };
        var finalSegmentFile = new MacFile
        {
            Name = MacString.FromMacRoman("archive.cpt.#2"),
            DataFork = ForkData.FromBytes(finalSegment),
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(finalSegmentFile, "Host file",
            new ContainerContext(siblings: () => [firstSegmentFile]));
        MacFile file = Assert.Single(result.Children).File;

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(data, file.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperUsesCompactProSiblingVolumes()
    {
        byte[] resource = "resource"u8.ToArray();
        byte[] data = "inside"u8.ToArray();
        byte[] archive = CompactProFixture.BuildFile("Read Me", resource, data, resource,
            data, dataOffsetOverride: 8, entryVolume: 2);
        byte[] secondVolume = new byte[8 + resource.Length + data.Length];
        secondVolume[0] = 1;
        secondVolume[1] = 2;
        resource.CopyTo(secondVolume.AsSpan(8));
        data.CopyTo(secondVolume.AsSpan(8 + resource.Length));
        var host = new MacFile
        {
            Name = MacString.FromMacRoman("archive.cpt"),
            DataFork = ForkData.FromBytes(archive),
        };
        var sibling = new MacFile
        {
            Name = MacString.FromMacRoman("archive.002"),
            DataFork = ForkData.FromBytes(secondVolume),
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(host, "Host file",
            new ContainerContext(siblings: () => [sibling]));

        MacFile unpacked = Assert.Single(result.Children).File;
        Assert.Equal("Read Me", unpacked.MacPath);
        Assert.Equal(resource, unpacked.ResourceFork.ToArray());
        Assert.Equal(data, unpacked.DataFork.ToArray());
    }

    [Fact]
    public void CompactProReportsAnUnavailableSiblingVolume()
    {
        byte[] archive = CompactProFixture.BuildFile("Read Me", [], "inside"u8.ToArray(), [],
            "inside"u8.ToArray(), dataOffsetOverride: 8, entryVolume: 2);
        var diagnostics = new List<Diagnostic>();

        Assert.Empty(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void CompactProRejectsAmbiguousSiblingVolumeNumbers()
    {
        byte[] archive = CompactProFixture.BuildFile("Read Me", [], "inside"u8.ToArray(), [],
            "inside"u8.ToArray(), dataOffsetOverride: 8, entryVolume: 2);
        byte[] secondVolume = [1, 2, 0, 0, 0, 0, 0, 0, .. "inside"u8.ToArray()];
        MacFile[] siblings =
        [
            new() { Name = MacString.FromMacRoman("archive.002"), DataFork = ForkData.FromBytes(secondVolume) },
            new() { Name = MacString.FromMacRoman("duplicate.002"), DataFork = ForkData.FromBytes(secondVolume) },
        ];

        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(siblings: () => siblings)));
    }

    [Fact]
    public void CompactProSiblingVolumesShareTheConfiguredInputSizeLimit()
    {
        byte[] archive = CompactProFixture.BuildFile("Read Me", [], "inside"u8.ToArray(), [],
            "inside"u8.ToArray(), dataOffsetOverride: 8, entryVolume: 2);
        byte[] secondVolume = [1, 2, 0, 0, 0, 0, 0, 0, .. "inside"u8.ToArray()];
        var sibling = new MacFile
        {
            Name = MacString.FromMacRoman("archive.002"),
            DataFork = ForkData.FromBytes(secondVolume),
        };
        var context = new ContainerContext(
            options: new ContainerReadOptions { MaxExpandedBytesPerInput = archive.Length + secondVolume.Length - 1 },
            siblings: () => [sibling]);

        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(ForkData.FromBytes(archive), context));
    }

    [Fact]
    public void CompactProForkDataCannotOverlapItsDirectory()
    {
        byte[] archive = CompactProFixture.BuildFile("Read Me", [], "abc"u8.ToArray(),
            encodedResource: [], encodedData: "abc"u8.ToArray(), dataOffsetOverride: 12);

        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void CompactProProbeRejectsMalformedDirectoryRecords()
    {
        byte[] archive = CompactProFixture.BuildFile("a", [], [], encodedResource: [], encodedData: []);
        archive[15] = 0;

        Assert.False(CompactProReader.Instance.CanRead(ForkData.FromBytes(archive)));
    }

    [Fact]
    public void CompactProLzhAndRleDataForkDecodesALiteral()
    {
        byte[] encodedData = CompactProFixture.BuildLzhLiteral((byte)'A');
        byte[] archive = CompactProFixture.BuildFile("compressed", [], "A"u8.ToArray(), [], encodedData,
            flags: 4);

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void CompactProLzhAndRleResourceForkDecodesALiteral()
    {
        byte[] encodedResource = CompactProFixture.BuildLzhLiteral((byte)'R');
        byte[] archive = CompactProFixture.BuildFile("resource", "R"u8.ToArray(), [], encodedResource, [],
            flags: 2);

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("R"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Empty(file.DataFork.ToArray());
    }

    [Fact]
    public void CompactProLzhForkRejectsTruncatedTreeData()
    {
        byte[] archive = CompactProFixture.BuildFile("truncated", [], "A"u8.ToArray(), [], [0], flags: 4);

        Assert.Throws<InvalidDataException>(() => CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void CompactProLzhMatchCopiesFromTheOverlappingHistoryWindow()
    {
        byte[] archive = CompactProFixture.BuildFile("match", [], "AAA"u8.ToArray(), [],
            CompactProFixture.BuildLzhLiteralThenMatch((byte)'A', 2, 1), flags: 4);

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void CompactProLzhStartsANewHuffmanBlockAtTheDocumentedBoundary()
    {
        byte[] expected = Enumerable.Repeat((byte)'A', 65_529).ToArray();
        byte[] encodedData = CompactProFixture.BuildLzhLiteralBlocks((byte)'A', 65_528, 1);
        byte[] archive = CompactProFixture.BuildFile("two blocks", [], expected, [], encodedData, flags: 4);

        MacFile file = Assert.Single(CompactProReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    private static class CompactProFixture
    {
        private const uint CreationDate = 2_500_000_000;
        private const uint ModificationDate = 2_600_000_000;
        private const int ArchiveHeaderLength = 8;

        public static byte[] BuildFile(string name, byte[] resource, byte[] data,
            byte[] encodedResource, byte[] encodedData, int? dataOffsetOverride = null, ushort flags = 0,
            byte[]? comment = null, byte entryVolume = 1)
        {
            comment ??= [];
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            int metadataLength = 2 + 1 + comment.Length + 1 + nameBytes.Length + 45;
            int metadataOffset = ArchiveHeaderLength;
            int dataOffset = metadataOffset + 4 + metadataLength;
            byte[] archive = new byte[dataOffset + encodedResource.Length + encodedData.Length];
            archive[0] = 1;
            archive[1] = 1;
            U16(archive, 2, 0);
            U32(archive, 4, checked((uint)metadataOffset));

            Span<byte> metadata = archive.AsSpan(metadataOffset + 4, metadataLength);
            U16(metadata, 0, 1);
            metadata[2] = checked((byte)comment.Length);
            comment.CopyTo(metadata[3..]);
            int nameTypeOffset = 3 + comment.Length;
            metadata[nameTypeOffset] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(metadata[(nameTypeOffset + 1)..]);
            Span<byte> entry = metadata.Slice(nameTypeOffset + 1 + nameBytes.Length, 45);
            entry[0] = entryVolume;
            U32(entry, 1, checked((uint)(dataOffsetOverride ?? dataOffset)));
            "TEXTttxt"u8.CopyTo(entry[5..]);
            U32(entry, 13, CreationDate);
            U32(entry, 17, ModificationDate);
            U16(entry, 21, 0x4000);
            U32(entry, 23, ~Crc32([.. resource, .. data]));
            U16(entry, 27, flags);
            U32(entry, 29, checked((uint)resource.Length));
            U32(entry, 33, checked((uint)data.Length));
            U32(entry, 37, checked((uint)encodedResource.Length));
            U32(entry, 41, checked((uint)encodedData.Length));
            U32(archive, metadataOffset, Crc32Raw(metadata));

            encodedResource.CopyTo(archive, dataOffset);
            encodedData.CopyTo(archive, dataOffset + encodedResource.Length);
            return archive;
        }

        public static byte[] BuildLzhLiteral(byte value)
        {
            var bits = new List<bool>();
            int symbolCount = ((value + 2) / 2) * 2;
            WriteBits(bits, symbolCount / 2, 8);
            for (int symbol = 0; symbol < symbolCount; symbol++) WriteBits(bits, symbol == value ? 1 : 0, 4);
            WriteBits(bits, 0, 8); // No match-length symbols are needed for a literal-only stream.
            WriteBits(bits, 0, 8); // No displacement symbols are needed for a literal-only stream.
            WriteBits(bits, 1, 1); // Literal token.
            WriteBits(bits, 0, 1); // The only defined symbol has the canonical one-bit code 0.
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] BuildLzhLiteralThenMatch(byte value, int length, int distance)
        {
            if (length is < 0 or >= 4) throw new ArgumentOutOfRangeException(nameof(length));
            if (distance != 1) throw new ArgumentOutOfRangeException(nameof(distance));
            var bits = new List<bool>();
            WriteBits(bits, 33, 8);
            for (int symbol = 0; symbol < 66; symbol++) WriteBits(bits, symbol == value ? 1 : 0, 4);
            WriteBits(bits, 2, 8); // Four length symbols, with the requested length represented by one code.
            for (int symbol = 0; symbol < 4; symbol++) WriteBits(bits, symbol == length ? 1 : 0, 4);
            WriteBits(bits, 1, 8); // Two displacement symbols, with the upper displacement 0 defined.
            WriteBits(bits, 1, 4);
            WriteBits(bits, 0, 4);
            WriteBits(bits, 1, 1); // Literal value.
            WriteBits(bits, 0, 1); // The one-symbol literal tree's code.
            WriteBits(bits, 0, 1); // Match token.
            WriteBits(bits, 0, 1); // Length code.
            WriteBits(bits, 0, 1); // Upper displacement code.
            WriteBits(bits, distance, 6); // Lower six displacement bits.
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] BuildLzhLiteralBlocks(byte value, int firstBlockLiterals, int secondBlockLiterals)
        {
            var bits = new List<bool>();
            WriteLzhLiteralTree(bits, value);
            for (int index = 0; index < firstBlockLiterals; index++) WriteBits(bits, 2, 2);
            while ((bits.Count & 15) != 0) bits.Add(false);
            for (int index = 0; index < 16; index++) bits.Add(false); // A block boundary discards 16..31 bits.
            WriteLzhLiteralTree(bits, value);
            for (int index = 0; index < secondBlockLiterals; index++) WriteBits(bits, 2, 2);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        private static void WriteLzhLiteralTree(List<bool> bits, byte value)
        {
            WriteBits(bits, 33, 8);
            for (int symbol = 0; symbol < 66; symbol++) WriteBits(bits, symbol == value ? 1 : 0, 4);
            WriteBits(bits, 0, 8);
            WriteBits(bits, 0, 8);
        }

        private static void WriteBits(List<bool> bits, int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--) bits.Add((value & (1 << bit)) != 0);
        }

        public static byte[] BuildNestedFile()
        {
            const int metadataOffset = ArchiveHeaderLength;
            const int metadataLength = 2 + 1 + 7 + 1 + 7 + 45;
            const int dataOffset = metadataOffset + 4 + metadataLength;
            byte[] data = "inside"u8.ToArray();
            byte[] archive = new byte[dataOffset + data.Length];
            archive[0] = 1;
            archive[1] = 1;
            U32(archive, 4, metadataOffset);
            Span<byte> metadata = archive.AsSpan(metadataOffset + 4, metadataLength);
            U16(metadata, 0, 2);
            metadata[2] = 0;
            metadata[3] = 0x84;
            "Docs"u8.CopyTo(metadata[4..]);
            U16(metadata, 8, 1);
            int fileEntry = 10;
            metadata[fileEntry] = 7;
            "Read Me"u8.CopyTo(metadata[(fileEntry + 1)..]);
            Span<byte> file = metadata.Slice(fileEntry + 8, 45);
            file[0] = 1;
            U32(file, 1, dataOffset);
            "TEXTttxt"u8.CopyTo(file[5..]);
            U32(file, 13, CreationDate);
            U32(file, 17, ModificationDate);
            U32(file, 23, ~Crc32(data));
            U32(file, 33, checked((uint)data.Length));
            U32(file, 41, checked((uint)data.Length));
            U32(archive, metadataOffset, Crc32Raw(metadata));
            data.CopyTo(archive, dataOffset);
            return archive;
        }

        private static uint Crc32(ReadOnlySpan<byte> bytes) => ~Crc32Raw(bytes);

        private static uint Crc32Raw(ReadOnlySpan<byte> bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
            return crc;
        }

        private static void U16(Span<byte> bytes, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

        private static void U32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }
}
