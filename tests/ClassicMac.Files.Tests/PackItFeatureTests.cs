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
    public void PackItEnforcesTheEntryLimitBeforeDecodingTheNextEntry()
    {
        byte[] first = PackItFixture.BuildStoredFile("first", "one"u8.ToArray(), []);
        byte[] archive = [.. first.AsSpan(0, first.Length - 4), (byte)'P', (byte)'M', (byte)'a', (byte)'4', 0];
        var context = new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 1 });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            PackItReader.Instance.Read(ForkData.FromBytes(archive), context));

        Assert.Equal("The PackIt archive exceeds the configured entry limit.", exception.Message);
    }

    [Theory]
    [InlineData("PMa3")]
    [InlineData("PMa7")]
    public void PackItUnsupportedEntriesAreReportedAsUnsupported(string method)
    {
        byte[] archive = PackItFixture.BuildStoredFile("compressed", [], []);
        System.Text.Encoding.ASCII.GetBytes(method).CopyTo(archive, 0);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("PMa1")]
    [InlineData("PMa2")]
    public void PackItUncompressedEncryptedEntryUsesTheSuppliedMacRomanPassword(string method)
    {
        const string password = "café";
        byte[] data = "data fork"u8.ToArray();
        byte[] resource = "resource fork"u8.ToArray();
        byte[] archive = PackItFixture.BuildEncryptedStoredFile(method, "secret", data, resource, password);

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = password })));

        Assert.Equal("secret", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
    }

    [Theory]
    [InlineData("PMa1")]
    [InlineData("PMa2")]
    public void PackItUncompressedEncryptedEntryRejectsAnIncorrectPassword(string method)
    {
        byte[] archive = PackItFixture.BuildEncryptedStoredFile(method, "secret", "payload"u8.ToArray(), [], "right");

        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "wrong" })));
    }

    [Theory]
    [InlineData("PMa1")]
    [InlineData("PMa2")]
    [InlineData("PMa5")]
    [InlineData("PMa6")]
    public void PackItEncryptedEntryWithoutAPasswordIsReportedAsUnsupported(string method)
    {
        byte[] archive = method switch
        {
            "PMa1" or "PMa2" => PackItFixture.BuildEncryptedStoredFile(method, "secret", "payload"u8.ToArray(), [],
                "right"),
            "PMa5" => PackItFixture.BuildXorEncryptedHuffmanFile("secret", "payload"u8.ToArray(), [], "right"),
            "PMa6" => PackItFixture.BuildDesEncryptedHuffmanFile("secret", "payload"u8.ToArray(), [], "right"),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
        var diagnostics = new List<Diagnostic>();

        Assert.Empty(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("PMa1")]
    [InlineData("PMa2")]
    public void PackItUncompressedEncryptedEntrySkipsPaddingBeforeTheNextEntry(string method)
    {
        byte[] first = PackItFixture.BuildEncryptedStoredFile(method, "first", "one"u8.ToArray(), [], "password");
        byte[] second = PackItFixture.BuildStoredFile("second", "two"u8.ToArray(), []);
        byte[] archive = new byte[first.Length - 4 + second.Length];
        first.AsSpan(0, first.Length - 4).CopyTo(archive);
        second.CopyTo(archive, first.Length - 4);

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "password" }));

        Assert.Equal(new[] { "first", "second" }, files.Select(file => file.MacPath));
        Assert.Equal("one"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Equal("two"u8.ToArray(), files[1].DataFork.ToArray());
    }

    [Theory]
    [InlineData("PMa1")]
    [InlineData("PMa2")]
    public void PackItUncompressedEncryptedEntryRejectsATruncatedPayload(string method)
    {
        byte[] archive = PackItFixture.BuildEncryptedStoredFile(method, "secret", "payload"u8.ToArray(), [],
            "password");
        Array.Resize(ref archive, 4 + 100); // The encrypted payload needs 103 plaintext bytes.

        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "password" })));
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("café")]
    public void PackItXorEncryptedHuffmanEntryUsesTheSuppliedMacRomanPassword(string password)
    {
        byte[] archive = PackItFixture.BuildXorEncryptedHuffmanFile("secret", "data fork"u8.ToArray(),
            "resource fork"u8.ToArray(), password);

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = password })));

        Assert.Equal("secret", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void PackItXorEncryptedHuffmanEntryNeedsTheCorrectPassword()
    {
        byte[] archive = PackItFixture.BuildXorEncryptedHuffmanFile("secret", "payload"u8.ToArray(), [], "secret");

        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "wrong" })));
    }

    [Fact]
    public void PackItXorEncryptedHuffmanEntrySkipsPaddingBeforeTheNextEntry()
    {
        byte[] first = PackItFixture.BuildXorEncryptedHuffmanFile("secret", "first"u8.ToArray(), [], "secret");
        byte[] second = PackItFixture.BuildStoredFile("second", "next"u8.ToArray(), []);
        byte[] archive = new byte[first.Length - 4 + second.Length];
        first.AsSpan(0, first.Length - 4).CopyTo(archive);
        second.CopyTo(archive, first.Length - 4);

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "secret" }));

        Assert.Equal(new[] { "secret", "second" }, files.Select(file => file.MacPath));
        Assert.Equal("first"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Equal("next"u8.ToArray(), files[1].DataFork.ToArray());
    }

    [Theory]
    [InlineData("password")]
    [InlineData("café")]
    public void PackItDesEncryptedHuffmanEntryUsesTheSuppliedMacRomanPassword(string password)
    {
        byte[] archive = PackItFixture.BuildDesEncryptedHuffmanFile("secret", "data fork"u8.ToArray(),
            "resource fork"u8.ToArray(), password);

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = password })));

        Assert.Equal("secret", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void PackItDesEncryptedHuffmanEntryNeedsTheCorrectPassword()
    {
        byte[] archive = PackItFixture.BuildDesEncryptedHuffmanFile("secret", "payload"u8.ToArray(), [], "password");

        Assert.Throws<InvalidDataException>(() => PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "wrong" })));
    }

    [Fact]
    public void PackItDesEncryptedHuffmanEntryAcceptsAWeakDesPassword()
    {
        byte[] archive = PackItFixture.BuildDesEncryptedHuffmanFile("secret", "payload"u8.ToArray(), [], "");

        MacFile file = Assert.Single(PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "" })));

        Assert.Equal("payload"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void PackItDesEncryptedHuffmanEntrySkipsPaddingBeforeTheNextEntry()
    {
        byte[] first = PackItFixture.BuildDesEncryptedHuffmanFile("secret", "first"u8.ToArray(), [], "password");
        byte[] second = PackItFixture.BuildStoredFile("second", "next"u8.ToArray(), []);
        byte[] archive = new byte[first.Length - 4 + second.Length];
        first.AsSpan(0, first.Length - 4).CopyTo(archive);
        second.CopyTo(archive, first.Length - 4);

        IReadOnlyList<MacFile> files = PackItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { ArchivePassword = "password" }));

        Assert.Equal(new[] { "secret", "second" }, files.Select(file => file.MacPath));
        Assert.Equal("first"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Equal("next"u8.ToArray(), files[1].DataFork.ToArray());
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

        public static byte[] BuildXorEncryptedHuffmanFile(string name, byte[] data, byte[] resource, string password)
        {
            byte[] plain = BuildHuffmanFile(name, data, resource);
            int payloadLength = plain.Length - 8;
            int encryptedLength = (payloadLength + 7) & ~7;
            byte[] archive = new byte[4 + encryptedLength + 4];
            "PMa5"u8.CopyTo(archive);
            byte[] key = DerivePackItXorKey(ClassicMac.Core.MacString.FromMacRoman(password).Bytes);
            for (int index = 0; index < payloadLength; index++)
                archive[4 + index] = (byte)(plain[4 + index] ^ key[index % 7]);
            "PEnd"u8.CopyTo(archive.AsSpan(4 + encryptedLength));
            return archive;
        }

        public static byte[] BuildEncryptedStoredFile(string method, string name, byte[] data, byte[] resource,
            string password)
        {
            byte[] plain = BuildStoredFile(name, data, resource);
            int payloadLength = plain.Length - 8;
            int encryptedLength = (payloadLength + 7) & ~7;
            byte[] archive = new byte[4 + encryptedLength + 4];
            System.Text.Encoding.ASCII.GetBytes(method).CopyTo(archive, 0);
            if (method == "PMa1")
            {
                byte[] key = DerivePackItXorKey(ClassicMac.Core.MacString.FromMacRoman(password).Bytes);
                for (int index = 0; index < payloadLength; index++)
                    archive[4 + index] = (byte)(plain[4 + index] ^ key[index % 7]);
            }
            else if (method == "PMa2")
            {
                byte[] key = ClassicMac.Core.MacString.FromMacRoman(password).Bytes.ToArray();
                Array.Resize(ref key, 8);
                if (System.Security.Cryptography.DES.IsWeakKey(key))
                {
                    using System.Security.Cryptography.TripleDES des = System.Security.Cryptography.TripleDES.Create();
                    des.Mode = System.Security.Cryptography.CipherMode.ECB;
                    des.Padding = System.Security.Cryptography.PaddingMode.None;
                    byte[] threeDesKey = MakeWeakKeyCompatibleTripleDesKey(key);
                    using var transform = des.CreateDecryptor(threeDesKey, new byte[8]);
                    TransformDesPayload(transform, plain, payloadLength, encryptedLength, archive);
                }
                else
                {
                    using System.Security.Cryptography.DES des = System.Security.Cryptography.DES.Create();
                    des.Mode = System.Security.Cryptography.CipherMode.ECB;
                    des.Padding = System.Security.Cryptography.PaddingMode.None;
                    using var transform = des.CreateDecryptor(key, new byte[8]);
                    TransformDesPayload(transform, plain, payloadLength, encryptedLength, archive);
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(method));
            }
            "PEnd"u8.CopyTo(archive.AsSpan(4 + encryptedLength));
            return archive;
        }

        public static byte[] BuildDesEncryptedHuffmanFile(string name, byte[] data, byte[] resource, string password)
        {
            byte[] plain = BuildHuffmanFile(name, data, resource);
            int payloadLength = plain.Length - 8;
            int encryptedLength = (payloadLength + 7) & ~7;
            byte[] key = ClassicMac.Core.MacString.FromMacRoman(password).Bytes.ToArray();
            Array.Resize(ref key, 8);
            byte[] archive = new byte[4 + encryptedLength + 4];
            "PMa6"u8.CopyTo(archive);
            if (System.Security.Cryptography.DES.IsWeakKey(key))
            {
                using System.Security.Cryptography.TripleDES des = System.Security.Cryptography.TripleDES.Create();
                des.Mode = System.Security.Cryptography.CipherMode.ECB;
                des.Padding = System.Security.Cryptography.PaddingMode.None;
                byte[] threeDesKey = MakeWeakKeyCompatibleTripleDesKey(key);
                using var transform = des.CreateDecryptor(threeDesKey, new byte[8]);
                TransformDesPayload(transform, plain, payloadLength, encryptedLength, archive);
            }
            else
            {
                using System.Security.Cryptography.DES des = System.Security.Cryptography.DES.Create();
                des.Mode = System.Security.Cryptography.CipherMode.ECB;
                des.Padding = System.Security.Cryptography.PaddingMode.None;
                using var transform = des.CreateDecryptor(key, new byte[8]);
                TransformDesPayload(transform, plain, payloadLength, encryptedLength, archive);
            }
            "PEnd"u8.CopyTo(archive.AsSpan(4 + encryptedLength));
            return archive;
        }

        private static byte[] MakeWeakKeyCompatibleTripleDesKey(byte[] key)
        {
            byte[] threeDesKey = new byte[24];
            key.CopyTo(threeDesKey, 0);
            key.CopyTo(threeDesKey, 8);
            threeDesKey[15] ^= 1; // DES parity bit: gives 3DES distinct byte keys but identical DES subkeys.
            key.CopyTo(threeDesKey, 16);
            return threeDesKey;
        }

        private static void TransformDesPayload(System.Security.Cryptography.ICryptoTransform transform,
            byte[] plain, int payloadLength, int encryptedLength, byte[] archive)
        {
            byte[] padded = new byte[encryptedLength];
            plain.AsSpan(4, payloadLength).CopyTo(padded);
            _ = transform.TransformBlock(padded, 0, padded.Length, archive, 4);
        }

        private static byte[] DerivePackItXorKey(ReadOnlySpan<byte> password)
        {
            int[] table = [57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,27,19,11,3,60,52,44,36,
                63,55,47,39,31,23,15,7,62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,28,20,12,4];
            Span<byte> passwordBytes = stackalloc byte[8];
            password[..Math.Min(password.Length, 8)].CopyTo(passwordBytes);
            byte[] key = new byte[8];
            for (int index = 0; index < table.Length; index++)
            {
                int source = table[index] - 1;
                key[index / 8] |= (byte)(((passwordBytes[source / 8] << (source % 8)) & 0x80) >> (index % 8));
            }
            return key;
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
