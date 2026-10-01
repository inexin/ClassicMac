using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads PackIt archives containing stored, Huffman-compressed, and encrypted Huffman entries.</summary>
/// <remarks>The stream layout, encryption transforms, and CRCs are fitted against published format notes and
/// XADMaster's independent reader; interoperability with the original application remains to be verified.</remarks>
public sealed class PackItReader : IContainerReader
{
    private const int EntryHeaderLength = 98;
    private const int EntryMetadataLength = 94;

    /// <summary>The built-in reader.</summary>
    public static PackItReader Instance { get; } = new();

    private PackItReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "PackIt archive";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < 4) return false;
        byte[] signature = input.ReadPrefix(4);
        return IsPackItSignature(signature);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (input.Length > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("The PackIt archive exceeds the configured input-size limit.");

        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        var files = new List<MacFile>();
        long expandedBytes = 0;
        int entryCount = 0;
        int offset = 0;
        bool ended = false;
        while (offset <= archive.Length - 4)
        {
            ReadOnlySpan<byte> signature = archive.AsSpan(offset, 4);
            if (signature.SequenceEqual("PEnd"u8))
            {
                offset += 4;
                ended = true;
                break;
            }
            if (++entryCount > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The PackIt archive exceeds the configured entry limit.");

            bool huffman = signature.SequenceEqual("PMa4"u8) || signature.SequenceEqual("PMa5"u8) ||
                signature.SequenceEqual("PMa6"u8);
            bool xorEncrypted = signature.SequenceEqual("PMa1"u8) || signature.SequenceEqual("PMa5"u8);
            bool desEncrypted = signature.SequenceEqual("PMa2"u8) || signature.SequenceEqual("PMa6"u8);
            bool encrypted = xorEncrypted || desEncrypted;
            if (!signature.SequenceEqual("PMag"u8) && !huffman && !encrypted)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                    $"PackIt entry method '{System.Text.Encoding.ASCII.GetString(signature)}' is not supported; " +
                    "the remainder of the archive is not read.", offset);
                break;
            }
            if (encrypted && context.Options.ArchivePassword is null)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                    $"PackIt method '{System.Text.Encoding.ASCII.GetString(signature)}' requires an archive password; " +
                    "the remainder of the archive is not read.",
                    offset);
                break;
            }
            byte[] metadataBytes;
            byte[]? decodedData = null;
            byte[]? decodedResource = null;
            ushort storedForkCrc;
            int nextOffset;
            if (huffman || encrypted)
            {
                byte[]? desDecoded = desEncrypted
                    ? DecodeDesBlocks(archive.AsSpan(offset + 4), GetDesKey(context.Options.ArchivePassword!))
                    : null;
                byte[]? xorDecoded = xorEncrypted && !huffman
                    ? DecodeXorBytes(archive.AsSpan(offset + 4),
                        DeriveXorKey(MacString.FromMacRoman(context.Options.ArchivePassword!).Bytes))
                    : null;
                ReadOnlySpan<byte> entryInput = desDecoded ?? xorDecoded ?? archive.AsSpan(offset + 4);
                if (huffman)
                {
                    DecodedHuffmanEntry decoded = DecodeHuffmanEntry(entryInput,
                        context.Options.MaxExpandedBytesPerInput - expandedBytes,
                        xorEncrypted ? DeriveXorKey(MacString.FromMacRoman(context.Options.ArchivePassword!).Bytes) : null);
                    metadataBytes = decoded.Metadata;
                    decodedData = decoded.Data;
                    decodedResource = decoded.Resource;
                    storedForkCrc = decoded.StoredForkCrc;
                    int encodedBytesConsumed = encrypted
                        ? checked((decoded.BytesConsumed + 7) & ~7)
                        : decoded.BytesConsumed;
                    nextOffset = checked(offset + 4 + encodedBytesConsumed);
                }
                else
                {
                    if (entryInput.Length < EntryMetadataLength)
                        throw new InvalidDataException("A PackIt encrypted file header is truncated.");
                    metadataBytes = entryInput[..EntryMetadataLength].ToArray();
                    int storedDataLength = ReadLength(U32(metadataBytes, 0x4C), "data fork");
                    int storedResourceLength = ReadLength(U32(metadataBytes, 0x50), "resource fork");
                    int encryptedForksLength = checked(storedDataLength + storedResourceLength);
                    int crcOffset = checked(EntryMetadataLength + encryptedForksLength);
                    if (crcOffset > entryInput.Length - 2)
                        throw new InvalidDataException("A PackIt encrypted fork payload or checksum is truncated.");
                    if (encryptedForksLength > context.Options.MaxExpandedBytesPerInput - expandedBytes)
                        throw new InvalidDataException("PackIt extraction exceeds the configured expanded-size limit.");
                    decodedData = entryInput.Slice(EntryMetadataLength, storedDataLength).ToArray();
                    decodedResource = entryInput.Slice(EntryMetadataLength + storedDataLength,
                        storedResourceLength).ToArray();
                    storedForkCrc = U16(entryInput, crcOffset);
                    nextOffset = checked(offset + 4 + ((crcOffset + 2 + 7) & ~7));
                }
            }
            else
            {
                if (offset > archive.Length - EntryHeaderLength)
                    throw new InvalidDataException("A PackIt file header is truncated.");
                ReadOnlySpan<byte> header = archive.AsSpan(offset, EntryHeaderLength);
                metadataBytes = header.Slice(4, EntryMetadataLength).ToArray();
                int storedDataLength = ReadLength(U32(header, 0x50), "data fork");
                int storedResourceLength = ReadLength(U32(header, 0x54), "resource fork");
                int storedForkLength = checked(storedDataLength + storedResourceLength);
                int payloadOffset = checked(offset + EntryHeaderLength);
                int crcOffset = checked(payloadOffset + storedForkLength);
                if (crcOffset > archive.Length - 2)
                    throw new InvalidDataException("A PackIt fork payload or checksum extends past the archive.");
                storedForkCrc = U16(archive, crcOffset);
                nextOffset = checked(crcOffset + 2);
            }

            ReadOnlySpan<byte> metadata = metadataBytes;
            int nameLength = metadata[0];
            if (nameLength == 0 || nameLength > 63)
                throw new InvalidDataException("A PackIt file name length is invalid.");
            ushort storedHeaderCrc = U16(metadata, 0x5C);
            ushort actualHeaderCrc = Crc16(metadata[..0x5C]);
            if (storedHeaderCrc != actualHeaderCrc)
                context.Report(DiagnosticSeverity.Error, "archive.header-crc",
                    $"The PackIt header checksum is incorrect for '{new MacString(metadata.Slice(1, nameLength))}'.",
                    offset + 0x60);

            int dataLength = ReadLength(U32(metadata, 0x4C), "data fork");
            int resourceLength = ReadLength(U32(metadata, 0x50), "resource fork");
            int forksLength = checked(dataLength + resourceLength);
            expandedBytes = checked(expandedBytes + forksLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                throw new InvalidDataException("PackIt extraction exceeds the configured expanded-size limit.");

            ReadOnlySpan<byte> data;
            ReadOnlySpan<byte> resource;
            ForkData dataFork;
            ForkData resourceFork;
            if (huffman || encrypted)
            {
                data = decodedData!;
                resource = decodedResource!;
                dataFork = ForkData.FromBytes(decodedData!);
                resourceFork = ForkData.FromBytes(decodedResource!);
            }
            else
            {
                int payloadOffset = offset + EntryHeaderLength;
                data = archive.AsSpan(payloadOffset, dataLength);
                resource = archive.AsSpan(payloadOffset + dataLength, resourceLength);
                dataFork = ForkData.FromBytes(archive.AsMemory(payloadOffset, dataLength));
                resourceFork = ForkData.FromBytes(archive.AsMemory(payloadOffset + dataLength, resourceLength));
            }
            ushort actualForkCrc = Crc16(data);
            actualForkCrc = Crc16(resource, actualForkCrc);
            var name = new MacString(metadata.Slice(1, nameLength));
            if (storedForkCrc != actualForkCrc)
            {
                if (encrypted)
                    throw new InvalidDataException($"The PackIt password is incorrect or the encrypted forks are corrupt for '{name}'.");
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The PackIt fork checksum is incorrect for '{name}'.", nextOffset - 2);
            }

            var type = new FourCC(metadata.Slice(0x40, 4));
            var creator = new FourCC(metadata.Slice(0x44, 4));
            files.Add(new MacFile
            {
                Name = name,
                FinderInfo = new FinderInfo { Type = type, Creator = creator, Flags = (FinderFlags)U16(metadata, 0x48) },
                Created = Date(U32(metadata, 0x54)),
                Modified = Date(U32(metadata, 0x58)),
                DataFork = dataFork,
                ResourceFork = resourceFork,
            });
            offset = nextOffset;
        }
        if (!ended)
            context.Report(DiagnosticSeverity.Warning, "archive.truncated",
                "The PackIt archive has no PEnd marker.", offset);
        return files;
    }

    private static bool IsPackItSignature(ReadOnlySpan<byte> signature) =>
        signature.SequenceEqual("PMag"u8) || signature.SequenceEqual("PMa1"u8) ||
        signature.SequenceEqual("PMa2"u8) || signature.SequenceEqual("PMa3"u8) ||
        signature.SequenceEqual("PMa4"u8) || signature.SequenceEqual("PMa5"u8) ||
        signature.SequenceEqual("PMa6"u8) || signature.SequenceEqual("PMa7"u8) ||
        signature.SequenceEqual("PEnd"u8);

    private static DecodedHuffmanEntry DecodeHuffmanEntry(ReadOnlySpan<byte> input, long maxForkBytes,
        byte[]? xorKey)
    {
        var bits = new HuffmanBitReader(input, xorKey);
        int nodeCount = 0;
        int leafCount = 0;
        HuffmanNode root = ReadHuffmanNode(ref bits, 0, ref nodeCount, ref leafCount);
        var metadata = new byte[EntryMetadataLength];
        for (int index = 0; index < metadata.Length; index++) metadata[index] = ReadSymbol(ref bits, root);

        int nameLength = metadata[0];
        if (nameLength == 0 || nameLength > 63)
            throw new InvalidDataException("A PackIt file name length is invalid.");
        int dataLength = ReadLength(U32(metadata, 0x4C), "data fork");
        int resourceLength = ReadLength(U32(metadata, 0x50), "resource fork");
        int forksLength = checked(dataLength + resourceLength);
        if (forksLength > maxForkBytes)
            throw new InvalidDataException("PackIt extraction exceeds the configured expanded-size limit.");

        var data = new byte[dataLength];
        var resource = new byte[resourceLength];
        for (int index = 0; index < data.Length; index++) data[index] = ReadSymbol(ref bits, root);
        for (int index = 0; index < resource.Length; index++) resource[index] = ReadSymbol(ref bits, root);
        ushort storedForkCrc = (ushort)((ReadSymbol(ref bits, root) << 8) | ReadSymbol(ref bits, root));
        return new DecodedHuffmanEntry(metadata, data, resource, storedForkCrc, bits.BytesConsumed);
    }

    private static byte[] DeriveXorKey(ReadOnlySpan<byte> password)
    {
        // PackIt III expands the first eight MacRoman password bytes through the DES PC-1 bit selection table,
        // then XORs with the first seven resulting key bytes, cycling each archive byte over those seven bytes.
        ReadOnlySpan<byte> pc1 = [57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35,27,19,11,3,
            60,52,44,36,63,55,47,39,31,23,15,7,62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,28,20,12,4];
        Span<byte> passwordBytes = stackalloc byte[8];
        password[..Math.Min(password.Length, passwordBytes.Length)].CopyTo(passwordBytes);
        byte[] key = new byte[8];
        for (int index = 0; index < pc1.Length; index++)
        {
            int sourceBit = pc1[index] - 1;
            key[index / 8] |= (byte)(((passwordBytes[sourceBit / 8] << (sourceBit % 8)) & 0x80) >> (index % 8));
        }
        return key;
    }

    private static byte[] DecodeXorBytes(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key)
    {
        byte[] decoded = new byte[input.Length];
        for (int index = 0; index < input.Length; index++)
            decoded[index] = (byte)(input[index] ^ key[index % 7]);
        return decoded;
    }

    private static byte[] GetDesKey(string password)
    {
        ReadOnlySpan<byte> passwordBytes = MacString.FromMacRoman(password).Bytes;
        var key = new byte[8];
        passwordBytes[..Math.Min(passwordBytes.Length, key.Length)].CopyTo(key);
        return key;
    }

    private static byte[] DecodeDesBlocks(ReadOnlySpan<byte> input, byte[] key)
    {
        int blockLength = input.Length & ~7;
        if (blockLength == 0)
            throw new InvalidDataException("A PackIt DES-encrypted Huffman entry is truncated.");
        byte[] source = input[..blockLength].ToArray();
        byte[] decoded = new byte[blockLength];
        try
        {
            using TripleDES des = TripleDES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            // [Fitted] XADMaster decodes PMa6 blocks with DES_encrypt. Use an equivalent three-key DES schedule so
            // the BCL accepts weak DES keys. Flipping a key parity bit
            // makes the byte keys distinct without changing any effective DES key bits: E(K, D(K, E(K, block))).
            byte[] threeDesKey = new byte[24];
            key.CopyTo(threeDesKey, 0);
            key.CopyTo(threeDesKey, 8);
            threeDesKey[15] ^= 1;
            key.CopyTo(threeDesKey, 16);
            using ICryptoTransform transform = des.CreateEncryptor(threeDesKey, new byte[8]);
            _ = transform.TransformBlock(source, 0, source.Length, decoded, 0);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The PackIt DES password cannot be used to decode this archive.", exception);
        }
        return decoded;
    }

    private static byte ReadSymbol(ref HuffmanBitReader bits, HuffmanNode root)
    {
        HuffmanNode node = root;
        while (!node.IsLeaf) node = bits.ReadBit() ? node.One! : node.Zero!;
        return node.Symbol;
    }

    private static HuffmanNode ReadHuffmanNode(ref HuffmanBitReader bits, int depth, ref int nodeCount,
        ref int leafCount)
    {
        if (depth > 255 || ++nodeCount > 511)
            throw new InvalidDataException("A PackIt Huffman code tree is too large.");
        if (bits.ReadBit())
        {
            if (++leafCount > 256)
                throw new InvalidDataException("A PackIt Huffman code tree has too many symbols.");
            return new HuffmanNode(bits.ReadByte());
        }
        HuffmanNode zero = ReadHuffmanNode(ref bits, depth + 1, ref nodeCount, ref leafCount);
        HuffmanNode one = ReadHuffmanNode(ref bits, depth + 1, ref nodeCount, ref leafCount);
        return new HuffmanNode(zero, one);
    }

    private static ushort Crc16(ReadOnlySpan<byte> bytes, ushort crc = 0)
    {
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc << 1) ^ ((crc & 0x8000) == 0 ? 0 : 0x1021));
        }
        return crc;
    }

    private sealed record DecodedHuffmanEntry(byte[] Metadata, byte[] Data, byte[] Resource, ushort StoredForkCrc,
        int BytesConsumed);

    private sealed class HuffmanNode
    {
        public HuffmanNode(byte symbol)
        {
            Symbol = symbol;
            IsLeaf = true;
        }

        public HuffmanNode(HuffmanNode zero, HuffmanNode one)
        {
            Zero = zero;
            One = one;
        }

        public bool IsLeaf { get; }
        public byte Symbol { get; }
        public HuffmanNode? Zero { get; }
        public HuffmanNode? One { get; }
    }

    private ref struct HuffmanBitReader(ReadOnlySpan<byte> input, byte[]? xorKey)
    {
        private readonly ReadOnlySpan<byte> input = input;
        private readonly byte[]? xorKey = xorKey;
        private int bitPosition;

        public int BytesConsumed => (bitPosition + 7) >> 3;

        public bool ReadBit()
        {
            if ((long)bitPosition >= (long)input.Length * 8)
                throw new InvalidDataException("A PackIt Huffman entry is truncated.");
            int byteIndex = bitPosition >> 3;
            byte valueByte = input[byteIndex];
            if (xorKey is not null) valueByte ^= xorKey[byteIndex % 7];
            bool value = (valueByte & (0x80 >> (bitPosition & 7))) != 0;
            bitPosition++;
            return value;
        }

        public byte ReadByte()
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++) value = (byte)((value << 1) | (ReadBit() ? 1 : 0));
            return value;
        }
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private static int ReadLength(uint value, string what)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"A PackIt {what} length exceeds the supported size.");
        return (int)value;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => new BigEndianReader(bytes).ReadUInt16At(offset);

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => new BigEndianReader(bytes).ReadUInt32At(offset);
}
