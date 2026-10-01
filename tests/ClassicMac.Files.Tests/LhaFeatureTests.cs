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
        int headerLength = archive[0] + 1;
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
        byte[] compressed = LhaFixture.BuildLevelZeroEntry("-lh5-", "compressed", [0x01], 100);
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
        public static byte[] BuildLevelZeroStoredFile(string name, byte[] data)
            => BuildLevelZeroEntry("-lh0-", name, data, data.Length);

        public static byte[] BuildLevelZeroEntry(string method, string name, byte[] packedData, int expandedSize,
            byte osIdentifier = (byte)'m')
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length > byte.MaxValue || Encoding.ASCII.GetByteCount(method) != 5)
                throw new ArgumentOutOfRangeException(nameof(name));

            int headerLength = checked(25 + nameBytes.Length);
            byte[] archive = new byte[headerLength + packedData.Length + 1];
            archive[0] = checked((byte)(headerLength - 1));
            Encoding.ASCII.GetBytes(method).CopyTo(archive, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(7), (uint)packedData.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(11), (uint)expandedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(15), 0x00210000);
            archive[19] = 0x20;
            archive[20] = 0;
            archive[21] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(archive, 22);
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(22 + nameBytes.Length), Crc16Ibm(packedData));
            archive[24 + nameBytes.Length] = osIdentifier;
            archive[1] = HeaderChecksum(archive.AsSpan(2, headerLength - 2));
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
}
