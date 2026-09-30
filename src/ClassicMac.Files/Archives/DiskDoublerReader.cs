using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads legacy DDAR and DiskDoubler DDA2 archives.</summary>
/// <remarks>The record layouts and compressed-method behavior are fitted against XADMaster's independent DiskDoubler parser.
/// Other DDA2 compression methods are reported as unsupported.</remarks>
public sealed class DiskDoublerReader : IContainerReader
{
    private const int ArchiveHeaderLength = 62;
    private const int RecordHeaderLength = 46;
    private const int FileHeaderLength = 80;
    private const uint FileHeaderMagic = 0xABCD0054;

    /// <summary>The built-in reader.</summary>
    public static DiskDoublerReader Instance { get; } = new();

    private DiskDoublerReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "DiskDoubler archive";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length >= ArchiveHeaderLength)
        {
            byte[] header = input.ReadPrefix(ArchiveHeaderLength);
            if (IsValidDda2Header(header)) return true;
        }
        if (input.Length < 4) return false;
        return input.ReadPrefix(4).AsSpan().SequenceEqual("DDAR"u8) && input.Length >= 78;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (input.Length > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("The DiskDoubler archive exceeds the configured input-size limit.");

        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        if (archive.AsSpan(0, 4).SequenceEqual("DDAR"u8))
            return ReadLegacy(archive, context);
        if (!IsValidDda2Header(archive))
            throw new InvalidDataException("Not a DiskDoubler archive.");

        var files = new List<MacFile>();
        var folders = new List<MacString>();
        long expandedBytes = 0;
        int entryCount = 0;
        int offset = ArchiveHeaderLength;
        bool ended = false;
        while (offset <= archive.Length - 6)
        {
            if (!archive.AsSpan(offset, 4).SequenceEqual("DDA2"u8))
                throw new InvalidDataException("A DiskDoubler DDA2 record has an invalid signature.");
            ushort entryType = U16(archive, offset + 4);
            if (entryType == 0xBBBB)
            {
                ended = true;
                offset += 6;
                break;
            }

            entryCount++;
            if (entryCount > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The DiskDoubler archive exceeds the configured entry limit.");
            if (offset > archive.Length - RecordHeaderLength)
                throw new InvalidDataException("A DiskDoubler DDA2 record header is truncated.");

            int nameLength = archive[offset + 6];
            if (nameLength is 0 or > 31)
                throw new InvalidDataException("A DiskDoubler DDA2 name length is invalid.");
            var name = new MacString(archive.AsSpan(offset + 7, nameLength));
            uint rawDepth = U32(archive, offset + 38);
            int recordLength = ReadLength(U32(archive, offset + 42), "record");
            if (recordLength < RecordHeaderLength || recordLength > archive.Length - offset)
                throw new InvalidDataException("A DiskDoubler DDA2 record extends past the archive.");
            int recordEnd = checked(offset + recordLength);
            if (rawDepth < 2)
            {
                offset = recordEnd;
                continue;
            }

            uint rawFolderDepth = rawDepth - 2;
            if (rawFolderDepth > int.MaxValue)
                throw new InvalidDataException("A DiskDoubler DDA2 folder depth is outside the supported range.");
            int depth = (int)rawFolderDepth;
            if (depth > context.Options.MaxNestingDepth)
                throw new InvalidDataException("The DiskDoubler folder nesting exceeds the configured depth limit.");
            if (folders.Count < depth)
                throw new InvalidDataException("A DiskDoubler DDA2 record skips a folder level.");
            if (folders.Count > depth) folders.RemoveRange(depth, folders.Count - depth);

            if ((entryType & 0x8000) != 0)
            {
                const int directoryMetadataLength = 16;
                if (recordLength < RecordHeaderLength + directoryMetadataLength)
                    throw new InvalidDataException("A DiskDoubler DDA2 directory record is truncated.");
                folders.Add(name);
                offset = recordEnd;
                continue;
            }

            int fileHeaderOffset = checked(offset + RecordHeaderLength + 10);
            if (recordLength < RecordHeaderLength + 10 + 4 + FileHeaderLength)
                throw new InvalidDataException("A DiskDoubler DDA2 file header is truncated.");
            if (U32(archive, fileHeaderOffset) != FileHeaderMagic)
                throw new InvalidDataException("A DiskDoubler DDA2 file has an invalid file-header marker.");
            int header = fileHeaderOffset + 4;
            int dataLength = ReadLength(U32(archive, header), "data fork");
            int compressedDataLength = ReadLength(U32(archive, header + 4), "compressed data fork");
            int resourceLength = ReadLength(U32(archive, header + 8), "resource fork");
            int compressedResourceLength = ReadLength(U32(archive, header + 12), "compressed resource fork");
            int dataMethod = archive[header + 16] & 0x7F;
            int resourceMethod = archive[header + 17] & 0x7F;
            uint modification = U32(archive, header + 20);
            uint creation = U32(archive, header + 24);
            var type = new FourCC(archive.AsSpan(header + 28, 4));
            var creator = new FourCC(archive.AsSpan(header + 32, 4));
            var finderFlags = (FinderFlags)U16(archive, header + 36);
            int dataDelta = U16(archive, header + 50);
            int resourceDelta = U16(archive, header + 52);
            int payloadOffset = checked(header + FileHeaderLength);
            long payloadLength = (long)compressedDataLength + compressedResourceLength;
            if (payloadLength > recordEnd - payloadOffset)
                throw new InvalidDataException("A DiskDoubler DDA2 fork payload extends past its record.");

            if (!IsSupportedMethod(dataMethod) || !IsSupportedMethod(resourceMethod) ||
                dataDelta != 0 || resourceDelta != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                    $"DiskDoubler compression methods {dataMethod}/{resourceMethod} or delta methods " +
                    $"{dataDelta}/{resourceDelta} are unsupported for '{name}'; the entry is skipped.", offset);
                offset = recordEnd;
                continue;
            }
            if ((dataMethod == 0 && dataLength != compressedDataLength) ||
                (resourceMethod == 0 && resourceLength != compressedResourceLength))
                throw new InvalidDataException("A stored DiskDoubler fork has inconsistent compressed and expanded lengths.");

            expandedBytes = checked(expandedBytes + dataLength + resourceLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                throw new InvalidDataException("DiskDoubler extraction exceeds the configured expanded-size limit.");
            ReadOnlySpan<byte> encodedData = archive.AsSpan(payloadOffset, compressedDataLength);
            ReadOnlySpan<byte> encodedResource = archive.AsSpan(payloadOffset + compressedDataLength,
                compressedResourceLength);
            byte[] data = DecodeFork(encodedData, dataLength, dataMethod,
                archive[header + 18], archive[header + 48]);
            byte[] resource = DecodeFork(encodedResource, resourceLength, resourceMethod,
                archive[header + 18], archive[header + 48]);
            if (dataMethod == 8 && U16(archive, header + 44) != Crc16Ibm(data))
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The DiskDoubler data-fork checksum is incorrect for '{name}'.", header + 44);
            if (dataMethod == 2 && U16(archive, header + 44) != ByteSum(data))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler data-fork checksum is incorrect for '{name}'.", header + 44);
            if (dataMethod == 1 && U16(archive, header + 44) != MacCompressChecksum(data, encodedData,
                archive[header + 18], archive[header + 48]))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler data-fork checksum is incorrect for '{name}'.", header + 44);
            if (dataMethod == 4 && U16(archive, header + 44) != ByteSum(data))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler data-fork checksum is incorrect for '{name}'.", header + 44);
            if (resourceMethod == 8 && U16(archive, header + 46) != Crc16Ibm(resource))
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The DiskDoubler resource-fork checksum is incorrect for '{name}'.", header + 46);
            if (resourceMethod == 2 && U16(archive, header + 46) != ByteSum(resource))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler resource-fork checksum is incorrect for '{name}'.", header + 46);
            if (resourceMethod == 1 && U16(archive, header + 46) != MacCompressChecksum(resource, encodedResource,
                archive[header + 18], archive[header + 48]))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler resource-fork checksum is incorrect for '{name}'.", header + 46);
            if (resourceMethod == 4 && U16(archive, header + 46) != ByteSum(resource))
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"The DiskDoubler resource-fork checksum is incorrect for '{name}'.", header + 46);
            files.Add(new MacFile
            {
                Name = name,
                FolderPath = folders.ToArray(),
                FinderInfo = new FinderInfo { Type = type, Creator = creator, Flags = finderFlags },
                Created = Date(creation),
                Modified = Date(modification),
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
            });
            offset = recordEnd;
        }

        if (!ended)
            context.Report(DiagnosticSeverity.Warning, "archive.truncated",
                "The DiskDoubler DDA2 archive has no end marker.", offset);
        return files;
    }

    private static IReadOnlyList<MacFile> ReadLegacy(byte[] archive, ContainerContext context)
    {
        const int archiveHeaderLength = 78;
        const int recordHeaderLength = 124;
        if (archive.Length < archiveHeaderLength)
            throw new InvalidDataException("The DiskDoubler DDAR archive header is truncated.");

        var files = new List<MacFile>();
        var folders = new List<MacString>();
        long expandedBytes = 0;
        int entryCount = 0;
        int offset = archiveHeaderLength;
        while (offset < archive.Length)
        {
            if (archive.Length - offset < 4)
                throw new InvalidDataException("A DiskDoubler DDAR record marker is truncated.");
            uint marker = U32(archive, offset);
            if (marker == FileHeaderMagic)
            {
                const int redundantHeaderLength = 84;
                if (archive.Length - offset < redundantHeaderLength)
                    throw new InvalidDataException("A trailing DiskDoubler DDAR file header is truncated.");
                offset += redundantHeaderLength;
                continue;
            }
            if (marker != 0x44444152)
                throw new InvalidDataException("A DiskDoubler DDAR record has an invalid signature.");
            if (archive.Length - offset < recordHeaderLength)
                throw new InvalidDataException("A DiskDoubler DDAR record header is truncated.");

            entryCount++;
            if (entryCount > context.Options.MaxVolumeEntries)
                throw new InvalidDataException("The DiskDoubler DDAR archive exceeds the configured entry limit.");
            int nameLength = Math.Min((int)archive[offset + 8], 63);
            var name = new MacString(archive.AsSpan(offset + 9, nameLength));
            bool isDirectory = archive[offset + 72] != 0;
            bool isEndDirectory = archive[offset + 73] != 0;
            int dataLength = ReadLength(U32(archive, offset + 74), "data fork");
            int resourceLength = ReadLength(U32(archive, offset + 78), "resource fork");
            long payloadLength = (long)dataLength + resourceLength;
            int payloadOffset = checked(offset + recordHeaderLength);
            if (payloadLength > archive.Length - payloadOffset)
                throw new InvalidDataException("A DiskDoubler DDAR fork payload extends past the archive.");
            int recordEnd = checked(payloadOffset + (int)payloadLength);

            if (isEndDirectory)
            {
                if (folders.Count == 0)
                    throw new InvalidDataException("A DiskDoubler DDAR directory end marker has no open folder.");
                folders.RemoveAt(folders.Count - 1);
            }
            else if (isDirectory)
            {
                if (folders.Count >= context.Options.MaxNestingDepth)
                    throw new InvalidDataException("The DiskDoubler DDAR folder nesting exceeds the configured depth limit.");
                folders.Add(name);
            }
            else
            {
                expandedBytes = checked(expandedBytes + payloadLength);
                if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                    throw new InvalidDataException("DiskDoubler DDAR extraction exceeds the configured expanded-size limit.");
                var type = new FourCC(archive.AsSpan(offset + 90, 4));
                var creator = new FourCC(archive.AsSpan(offset + 94, 4));
                var finderFlags = (FinderFlags)U16(archive, offset + 98);
                uint creation = U32(archive, offset + 82);
                uint modification = U32(archive, offset + 86);
                files.Add(new MacFile
                {
                    Name = name,
                    FolderPath = folders.ToArray(),
                    FinderInfo = new FinderInfo { Type = type, Creator = creator, Flags = finderFlags },
                    Created = Date(creation),
                    Modified = Date(modification),
                    DataFork = ForkData.FromBytes(archive.AsSpan(payloadOffset, dataLength).ToArray()),
                    ResourceFork = ForkData.FromBytes(archive.AsSpan(payloadOffset + dataLength, resourceLength).ToArray()),
                });
            }
            offset = recordEnd;
        }
        return files;
    }

    private static bool IsSupportedMethod(int method) => method is 0 or 1 or 2 or 4 or 8;

    private static byte[] DecodeFork(ReadOnlySpan<byte> input, int outputLength, int method,
        byte info1, byte info2)
    {
        if (method == 0) return input.ToArray();
        if (method == 1) return DecodeMacCompress(input, outputLength, info1, info2);
        if (method == 2) return DecodeAdaptiveHuffman(input, outputLength, info1, info2);
        if (method == 4) return DecodeHuffman(input, outputLength, info1, info2);
        if (input.Length < 16)
            throw new InvalidDataException("A DiskDoubler Compact Pro fork is missing its 16-byte method header.");
        int headerSum = 0;
        foreach (byte value in input[..16]) headerSum += value;
        ReadOnlySpan<byte> compressed = input[16..];
        // [Fitted] DiskDoubler method 8 uses the Compact Pro RLE decoder, preceded by an optional LZH stage.
        return headerSum == 0
            ? CompactProLzhDecoder.Decode(compressed, outputLength)
            : CompactProReader.DecodeRle8182(compressed, outputLength);
    }

    private static byte[] DecodeAdaptiveHuffman(ReadOnlySpan<byte> input, int outputLength, byte info1, byte info2)
    {
        byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
        var trees = new AdaptiveHuffmanTree?[256];
        var output = new byte[outputLength];
        long bitOffset = 0;
        int currentTree = 0;
        for (int index = 0; index < output.Length; index++)
        {
            AdaptiveHuffmanTree tree = trees[currentTree] ??= new AdaptiveHuffmanTree();
            byte decoded = tree.ReadSymbol(input, ref bitOffset);
            tree.Update(decoded);
            output[index] = (byte)(decoded ^ xor);
            currentTree = decoded;
        }
        return output;
    }

    private sealed class AdaptiveHuffmanTree
    {
        private readonly byte[] _parents = new byte[512];
        private readonly ushort[] _leftChildren = new ushort[256];
        private readonly ushort[] _rightChildren = new ushort[256];

        public AdaptiveHuffmanTree()
        {
            for (int node = 0; node < 256; node++)
            {
                _parents[node * 2] = checked((byte)node);
                _parents[node * 2 + 1] = checked((byte)node);
                _leftChildren[node] = checked((ushort)(node * 2));
                _rightChildren[node] = checked((ushort)(node * 2 + 1));
            }
        }

        public byte ReadSymbol(ReadOnlySpan<byte> input, ref long bitOffset)
        {
            int node = 1;
            while (node < 256)
            {
                if (bitOffset >= (long)input.Length * 8)
                    throw new InvalidDataException("A DiskDoubler method-2 fork ends inside an adaptive Huffman code.");
                int bitInByte = 7 - (int)(bitOffset & 7);
                int bit = (input[(int)(bitOffset >> 3)] >> bitInByte) & 1;
                bitOffset++;
                node = bit == 0 ? _leftChildren[node] : _rightChildren[node];
            }
            return checked((byte)(node - 256));
        }

        public void Update(byte value)
        {
            int node = value + 256;
            while (true)
            {
                int parent = _parents[node];
                if (parent == 1) break;
                int grandparent = _parents[parent];
                int uncle;
                if (_leftChildren[grandparent] == parent)
                {
                    uncle = _rightChildren[grandparent];
                    _rightChildren[grandparent] = checked((ushort)node);
                }
                else
                {
                    uncle = _leftChildren[grandparent];
                    _leftChildren[grandparent] = checked((ushort)node);
                }

                if (_leftChildren[parent] != node) _rightChildren[parent] = checked((ushort)uncle);
                else _leftChildren[parent] = checked((ushort)uncle);
                _parents[node] = checked((byte)grandparent);
                _parents[uncle] = checked((byte)parent);
                node = grandparent;
                if (node == 1) break;
            }
        }
    }

    private static byte[] DecodeHuffman(ReadOnlySpan<byte> input, int outputLength, byte info1, byte info2)
    {
        // [Fitted] XADMaster uses the same tree-described Huffman stream for DDA2 method 4 and StuffIt's
        // Huffman method. DDA2's Info1/Info2-selected XOR is applied to decoded bytes before its byte-sum check.
        byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
        byte[] output = StuffItReader.DecodeHuffman(input, outputLength);
        if (xor != 0)
            for (int index = 0; index < output.Length; index++) output[index] ^= xor;
        return output;
    }

    private static ushort ByteSum(ReadOnlySpan<byte> output)
    {
        uint sum = 0;
        foreach (byte value in output) sum += value;
        return (ushort)sum;
    }

    private static byte[] DecodeMacCompress(ReadOnlySpan<byte> input, int outputLength, byte info1, byte info2)
    {
        if (input.Length < 3)
            throw new InvalidDataException("A DiskDoubler MacCompress fork is missing its three-byte header.");

        // [Fitted] XADMaster's DiskDoubler parser identifies the optional 0x5A output transform
        // from Info1/Info2 and treats the first three fork bytes as checksum contributions and flags.
        byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
        int flags = input[2] ^ xor;
        int maximumBits = flags & 0x1F;
        bool blockMode = (flags & 0x80) != 0;
        if ((flags & 0x60) != 0 || maximumBits is < 9 or > 16)
            throw new InvalidDataException("A DiskDoubler MacCompress fork has invalid LZW flags.");

        int maximumCodes = 1 << maximumBits;
        var prefix = new int[maximumCodes];
        Array.Fill(prefix, -1);
        var suffix = new byte[maximumCodes];
        for (int code = 0; code < 256; code++) suffix[code] = (byte)code;
        var phrase = new byte[maximumCodes];
        byte[] output = new byte[outputLength];
        ReadOnlySpan<byte> compressed = input[3..];
        long bitOffset = 0;
        int codeBits = 9;
        int nextCode = blockMode ? 257 : 256;
        int previousCode = -1;
        int written = 0;

        while (written < output.Length && TryReadLzwCode(compressed, ref bitOffset, codeBits, out int code))
        {
            if (blockMode && code == 256)
            {
                AlignLzwCodeGroup(ref bitOffset, codeBits);
                Array.Fill(prefix, -1, 257, maximumCodes - 257);
                nextCode = 257;
                codeBits = 9;
                previousCode = -1;
                continue;
            }

            if (previousCode < 0)
            {
                if (code > 255)
                    throw new InvalidDataException("A DiskDoubler MacCompress fork starts with an invalid LZW code.");
                WriteMacCompressByte((byte)code, output, ref written, xor);
                previousCode = code;
                continue;
            }

            if (code > nextCode || code >= maximumCodes)
                throw new InvalidDataException("A DiskDoubler MacCompress fork contains an invalid LZW code.");
            bool nextCodeCase = code == nextCode;
            int currentCode = nextCodeCase ? previousCode : code;
            int phraseLength = 0;
            if (nextCodeCase) phrase[phraseLength++] = FirstByte(previousCode, prefix);
            while (currentCode >= 256)
            {
                if (currentCode >= nextCode || prefix[currentCode] < 0 || phraseLength == phrase.Length)
                    throw new InvalidDataException("A DiskDoubler MacCompress fork has an invalid LZW dictionary chain.");
                phrase[phraseLength++] = suffix[currentCode];
                currentCode = prefix[currentCode];
            }
            if (phraseLength == phrase.Length)
                throw new InvalidDataException("A DiskDoubler MacCompress LZW phrase is too long.");
            phrase[phraseLength++] = (byte)currentCode;
            byte firstByte = (byte)currentCode;
            if (phraseLength > output.Length - written)
                throw new InvalidDataException("DiskDoubler MacCompress output exceeds its declared fork length.");
            while (phraseLength > 0) WriteMacCompressByte(phrase[--phraseLength], output, ref written, xor);

            if (nextCode < maximumCodes)
            {
                prefix[nextCode] = previousCode;
                suffix[nextCode] = firstByte;
                nextCode++;
                if (codeBits < maximumBits && nextCode > (1 << codeBits) - 1)
                {
                    AlignLzwCodeGroup(ref bitOffset, codeBits);
                    codeBits++;
                }
            }
            previousCode = code;
        }

        if (written != output.Length)
            throw new InvalidDataException(
                $"DiskDoubler MacCompress produced {written} of {output.Length} declared bytes.");
        return output;
    }

    private static void WriteMacCompressByte(byte value, byte[] output, ref int written, byte xor)
    {
        if (written == output.Length)
            throw new InvalidDataException("DiskDoubler MacCompress output exceeds its declared fork length.");
        output[written++] = (byte)(value ^ xor);
    }

    private static ushort MacCompressChecksum(ReadOnlySpan<byte> output, ReadOnlySpan<byte> input,
        byte info1, byte info2)
    {
        byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
        uint sum = (uint)(input[0] ^ xor) + (uint)(input[1] ^ xor) + (uint)(input[2] ^ xor);
        foreach (byte value in output) sum += value;
        return (ushort)sum;
    }

    private static bool TryReadLzwCode(ReadOnlySpan<byte> input, ref long bitOffset, int codeBits, out int code)
    {
        if (bitOffset > (long)input.Length * 8 - codeBits)
        {
            code = 0;
            return false;
        }
        code = 0;
        for (int bit = 0; bit < codeBits; bit++)
            if ((input[(int)((bitOffset + bit) >> 3)] & (1 << (int)((bitOffset + bit) & 7))) != 0)
                code |= 1 << bit;
        bitOffset += codeBits;
        return true;
    }

    private static void AlignLzwCodeGroup(ref long bitOffset, int codeBits)
    {
        int groupSize = codeBits * 8;
        bitOffset = checked((bitOffset + groupSize - 1) / groupSize * groupSize);
    }

    private static byte FirstByte(int code, int[] prefix)
    {
        while (code >= 256)
        {
            if (prefix[code] < 0)
                throw new InvalidDataException("A DiskDoubler MacCompress fork has an invalid LZW dictionary chain.");
            code = prefix[code];
        }
        return (byte)code;
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

    private static bool IsValidDda2Header(ReadOnlySpan<byte> header) =>
        header.Length >= ArchiveHeaderLength && header[..4].SequenceEqual("DDA2"u8) &&
        U16(header, 60) == Crc16Xmodem(header[..60]);

    private static ushort Crc16Xmodem(ReadOnlySpan<byte> bytes)
    {
        // [Fitted] XADMaster validates the DDA2 header CRC using its reversed 0x1021 table; the stored
        // big-endian result is equivalent to CRC-16/XMODEM over the preceding 60 bytes.
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021);
        }
        return crc;
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private static int ReadLength(uint value, string what)
    {
        if (value > int.MaxValue)
            throw new InvalidDataException($"A DiskDoubler {what} length exceeds the supported size.");
        return (int)value;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
}
