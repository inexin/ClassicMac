using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class PackItFeatureTests
{
    [Fact]
    public void DefaultUnwrapperReadsPackItStoredEntryWithBothForksAndFinderMetadata()
    {
        byte[] data = "data fork"u8.ToArray();
        byte[] resource = "resource"u8.ToArray();
        byte[] archive = PackItFixture.BuildStoredFile("Read Me", data, resource);
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.pit"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test",
            new ContainerContext());

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
    public void PackItChecksumMismatchesAreReportedAndTheEntryIsRetained()
    {
        byte[] archive = PackItFixture.BuildStoredFile("payload", "data"u8.ToArray(), []);
        archive[98] ^= 0x20;
        archive[0x60] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(new byte[] { (byte)'D', (byte)'a', (byte)'t', (byte)'a' }, file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.header-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void PackItEncryptedEntriesAreReportedAsUnsupported()
    {
        byte[] archive = PackItFixture.BuildStoredFile("compressed", [], []);
        "PMa5"u8.CopyTo(archive);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void PackItHuffmanEntryDecodesBothForksAndFinderMetadata()
    {
        byte[] data = "compressed data"u8.ToArray();
        byte[] resource = "compressed resource"u8.ToArray();
        byte[] archive = PackItFixture.BuildHuffmanFile("Huffman", data, resource);

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("Huffman", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
    }

    [Fact]
    public void PackItHuffmanRecordEndsAtItsByteAlignedBoundaryBeforeTheNextEntry()
    {
        byte[] first = PackItFixture.BuildHuffmanFile("first", "one"u8.ToArray(), []);
        byte[] second = PackItFixture.BuildStoredFile("second", "two"u8.ToArray(), []);
        byte[] archive = new byte[first.Length - 4 + second.Length];
        first.AsSpan(0, first.Length - 4).CopyTo(archive);
        second.CopyTo(archive, first.Length - 4);

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        Assert.Equal(new[] { "first", "second" }, files.Select(file => file.MacPath));
        Assert.Equal("one"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Equal("two"u8.ToArray(), files[1].DataFork.ToArray());
    }

    [Fact]
    public void PackItHuffmanEntryRejectsTruncatedForkBits()
    {
        byte[] archive = PackItFixture.BuildHuffmanFile("truncated", "payload"u8.ToArray(), []);
        Array.Resize(ref archive, archive.Length - 7);

        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(
            ForkData.FromBytes(archive), new ContainerContext()));
    }

    [Fact]
    public void PackItStoredEntryRejectsTruncatedHeader()
    {
        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(
            ForkData.FromBytes("PMag\x01"u8.ToArray()), new ContainerContext()));
    }

    private static class PackItFixture
    {
        private const int HeaderLength = 98;

        public static byte[] BuildStoredFile(string name, byte[] data, byte[] resource)
        {
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            if (nameBytes.Length > 63) throw new ArgumentOutOfRangeException(nameof(name));
            byte[] archive = new byte[HeaderLength + data.Length + resource.Length + 2 + 4];
            "PMag"u8.CopyTo(archive);
            archive[4] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(archive, 5);
            "TEXTttxt"u8.CopyTo(archive.AsSpan(0x44));
            U16(archive, 0x4C, 0x4000);
            U32(archive, 0x50, checked((uint)data.Length));
            U32(archive, 0x54, checked((uint)resource.Length));
            U32(archive, 0x58, 2_500_000_000);
            U32(archive, 0x5C, 2_600_000_000);
            U16(archive, 0x60, Crc16(archive.AsSpan(4, 0x5C)));
            data.CopyTo(archive, HeaderLength);
            resource.CopyTo(archive, HeaderLength + data.Length);
            U16(archive, HeaderLength + data.Length + resource.Length,
                Crc16(archive.AsSpan(HeaderLength, data.Length + resource.Length)));
            "PEnd"u8.CopyTo(archive.AsSpan(archive.Length - 4));
            return archive;
        }

        public static byte[] BuildHuffmanFile(string name, byte[] data, byte[] resource)
        {
            byte[] stored = BuildStoredFile(name, data, resource);
            byte[] expanded = stored.AsSpan(4, stored.Length - 8).ToArray();
            var bits = new List<bool>();
            WriteHuffmanTree(bits, 0, 0);
            foreach (byte value in expanded) WriteBits(bits, value, 8);
            byte[] compressed = new byte[(bits.Count + 7) / 8 + 8];
            "PMa4"u8.CopyTo(compressed);
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) compressed[4 + bit / 8] |= (byte)(0x80 >> (bit & 7));
            "PEnd"u8.CopyTo(compressed.AsSpan(compressed.Length - 4));
            return compressed;
        }

        private static void WriteHuffmanTree(List<bool> bits, int depth, int prefix)
        {
            if (depth == 8)
            {
                bits.Add(true);
                WriteBits(bits, prefix, 8);
                return;
            }
            bits.Add(false);
            WriteHuffmanTree(bits, depth + 1, prefix << 1);
            WriteHuffmanTree(bits, depth + 1, (prefix << 1) | 1);
        }

        private static void WriteBits(List<bool> bits, int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--) bits.Add((value & (1 << bit)) != 0);
        }

        private static ushort Crc16(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc << 1) ^ ((crc & 0x8000) == 0 ? 0 : 0x1021));
            }
            return crc;
        }

        private static void U16(Span<byte> bytes, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

        private static void U32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }
}
