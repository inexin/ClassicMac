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
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes("DDA2"u8.ToArray())));
        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(DiskDoublerFixture.BuildArchive())));
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
            DiskDoublerFixture.BuildFile("Unsupported", 0, [1], [], dataMethod: 1),
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
            byte resourceMethod = 0, byte[]? encodedData = null, byte[]? encodedResource = null)
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
            U32(bytes, header + 20, 2_600_000_000);
            U32(bytes, header + 24, 2_500_000_000);
            "TEXTttxt"u8.CopyTo(bytes.AsSpan(header + 28));
            U16(bytes, header + 36, 0x4000);
            if (dataMethod == 8) U16(bytes, header + 44, Crc16Ibm(data));
            if (resourceMethod == 8) U16(bytes, header + 46, Crc16Ibm(resource));
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
