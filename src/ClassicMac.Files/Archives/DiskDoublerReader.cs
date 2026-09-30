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
    private const int StandaloneHeaderLength = 84;
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
        if (input.Length >= StandaloneHeaderLength && IsValidStandaloneHeader(input.ReadPrefix(StandaloneHeaderLength)))
            return true;
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
        if (archive.Length >= 4 && archive.AsSpan(0, 4).SequenceEqual("DDAR"u8))
            return ReadLegacy(archive, context);
        if (IsValidStandaloneHeader(archive))
            return ReadStandalone(archive, context);
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
                !IsSupportedDelta(dataDelta) || !IsSupportedDelta(resourceDelta))
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
            ReportForkChecksum(archive, header + 44, encodedData, data, dataMethod,
                archive[header + 18], archive[header + 48], "data", name.ToString(), context);
            ReportForkChecksum(archive, header + 46, encodedResource, resource, resourceMethod,
                archive[header + 18], archive[header + 48], "resource", name.ToString(), context);
            ApplyDelta(data, dataDelta);
            ApplyDelta(resource, resourceDelta);
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

    private static IReadOnlyList<MacFile> ReadStandalone(byte[] archive, ContainerContext context)
    {
        int header = 4;
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
        long payloadLength = (long)compressedDataLength + compressedResourceLength;
        if (payloadLength > archive.Length - StandaloneHeaderLength)
            throw new InvalidDataException("A standalone DiskDoubler fork payload extends past the file.");

        MacString name = StandaloneName(context.HostName);
        if (!IsSupportedMethod(dataMethod) || !IsSupportedMethod(resourceMethod) ||
            !IsSupportedDelta(dataDelta) || !IsSupportedDelta(resourceDelta))
        {
            context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                $"DiskDoubler compression methods {dataMethod}/{resourceMethod} or delta methods " +
                $"{dataDelta}/{resourceDelta} are unsupported for '{name}'; the file is skipped.");
            return [];
        }
        if ((dataMethod == 0 && dataLength != compressedDataLength) ||
            (resourceMethod == 0 && resourceLength != compressedResourceLength))
            throw new InvalidDataException("A stored DiskDoubler fork has inconsistent compressed and expanded lengths.");

        if ((long)dataLength + resourceLength > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("DiskDoubler extraction exceeds the configured expanded-size limit.");
        ReadOnlySpan<byte> encodedData = archive.AsSpan(StandaloneHeaderLength, compressedDataLength);
        ReadOnlySpan<byte> encodedResource = archive.AsSpan(StandaloneHeaderLength + compressedDataLength,
            compressedResourceLength);
        byte info1 = archive[header + 18];
        byte info2 = archive[header + 48];
        byte[] data = DecodeFork(encodedData, dataLength, dataMethod, info1, info2);
        byte[] resource = DecodeFork(encodedResource, resourceLength, resourceMethod, info1, info2);
        ReportForkChecksum(archive, header + 44, encodedData, data, dataMethod,
            info1, info2, "data", name.ToString(), context);
        ReportForkChecksum(archive, header + 46, encodedResource, resource, resourceMethod,
            info1, info2, "resource", name.ToString(), context);
        ApplyDelta(data, dataDelta);
        ApplyDelta(resource, resourceDelta);

        return [new MacFile
        {
            Name = name,
            FinderInfo = new FinderInfo { Type = type, Creator = creator, Flags = finderFlags },
            Created = Date(creation),
            Modified = Date(modification),
            DataFork = ForkData.FromBytes(data),
            ResourceFork = ForkData.FromBytes(resource),
        }];
    }

    private static MacString StandaloneName(MacString? hostName)
    {
        if (hostName is not { } supplied || supplied.Bytes.IsEmpty)
            return MacString.FromMacRoman("DiskDoubler file");

        ReadOnlySpan<byte> bytes = supplied.Bytes;
        if (bytes.Length >= 3 && bytes[^3] == '.' &&
            (bytes[^2] is (byte)'d' or (byte)'D') && (bytes[^1] is (byte)'d' or (byte)'D'))
            bytes = bytes[..^3];
        return new MacString(bytes);
    }

    private static void ReportForkChecksum(ReadOnlySpan<byte> header, int checksumOffset,
        ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> decoded, int method, byte info1, byte info2,
        string forkName, string fileName, ContainerContext context)
    {
        ushort calculated;
        ushort expected = U16(header, checksumOffset);
        string code;
        switch (method)
        {
            case 1:
                calculated = MacCompressChecksum(decoded, encoded, info1, info2);
                code = "archive.fork-checksum";
                break;
            case 2:
            case 5:
            case 4:
                calculated = ByteSum(decoded);
                code = "archive.fork-checksum";
                break;
            case 7:
                // [Fitted] Method 7 stores an XOR sum; even-length forks adjust the stored byte for the outer 0xff XOR.
                calculated = ByteXor(decoded);
                if ((decoded.Length & 1) == 0) expected ^= 0x00FF;
                code = "archive.fork-checksum";
                break;
            case 8:
                calculated = Crc16Ibm(decoded);
                code = "archive.fork-crc";
                break;
            default:
                return;
        }

        if (expected != calculated)
            context.Report(DiagnosticSeverity.Error, code,
                $"The DiskDoubler {forkName}-fork checksum is incorrect for '{fileName}'.", checksumOffset);
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

    private static bool IsSupportedMethod(int method) => method is 0 or 1 or 2 or 4 or 5 or 6 or 7 or 8 or 9 or 10;

    private static bool IsSupportedDelta(int delta) => delta is 0 or 1;

    private static void ApplyDelta(Span<byte> bytes, int delta)
    {
        if (delta == 0) return;

        // [Fitted] XADMaster applies its default distance-1 delta filter after fork decompression.
        for (int index = 1; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)(bytes[index] + bytes[index - 1]));
    }

    private static byte[] DecodeFork(ReadOnlySpan<byte> input, int outputLength, int method,
        byte info1, byte info2)
    {
        if (method == 0) return input.ToArray();
        if (method == 1) return DecodeMacCompress(input, outputLength, info1, info2);
        if (method == 2) return DecodeAdaptiveHuffman(input, outputLength, info1, info2);
        if (method == 5)
        {
            if (input.IsEmpty)
                throw new InvalidDataException("A DiskDoubler method-5 fork is missing its adaptive-tree count.");
            int treeCount = input[0] == 0 ? 256 : input[0];
            return DecodeAdaptiveHuffman(input[1..], outputLength, info1, info2, treeCount);
        }
        if (method == 4) return DecodeHuffman(input, outputLength, info1, info2);
        if (method == 7) return DecodeStacLzs(input, outputLength);
        if (method is 6 or 9) return DiskDoublerAdnDecoder.Decode(input, outputLength);
        if (method == 10) return DiskDoublerMethod10Decoder.Decode(input, outputLength);
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

    private static byte[] DecodeStacLzs(ReadOnlySpan<byte> input, int outputLength)
    {
        if (input.Length < 10)
            throw new InvalidDataException("A DiskDoubler method-7 fork has a truncated Stac LZS header.");
        uint entryCount = U32(input, 6);
        long streamOffsetLong = 18L + 2L * entryCount;
        if (streamOffsetLong > input.Length)
            throw new InvalidDataException("A DiskDoubler method-7 fork has a truncated Stac LZS dictionary.");

        int streamOffset = (int)streamOffsetLong;
        var transformed = new byte[input.Length - streamOffset];
        for (int index = 0; index < transformed.Length; index++)
            transformed[index] = (byte)(input[streamOffset + index] ^ 0xFF);

        var bits = new StacLzsBitReader(transformed);
        byte[] output = DecodeStacLzsStream(ref bits, outputLength);
        for (int index = 0; index < output.Length; index++) output[index] ^= 0xFF;
        return output;
    }

    private static byte[] DecodeStacLzsStream(ref StacLzsBitReader bits, int outputLength)
    {
        var output = new byte[outputLength];
        int written = 0;
        while (true)
        {
            if (bits.ReadBit() == 0)
            {
                if (written == output.Length)
                    throw new InvalidDataException("A DiskDoubler method-7 fork expands beyond its declared length.");
                output[written++] = checked((byte)bits.ReadBits(8));
                continue;
            }

            bool shortOffset = bits.ReadBit() != 0;
            int offset = bits.ReadBits(shortOffset ? 7 : 11);
            if (shortOffset && offset == 0) break;
            if (offset == 0 || offset > written)
                throw new InvalidDataException("A DiskDoubler method-7 fork contains an invalid Stac LZS offset.");

            int length = ReadStacLzsLength(ref bits);
            if (length > output.Length - written)
                throw new InvalidDataException("A DiskDoubler method-7 fork expands beyond its declared length.");
            for (int index = 0; index < length; index++)
            {
                output[written] = output[written - offset];
                written++;
            }
        }

        if (written != output.Length)
            throw new InvalidDataException("A DiskDoubler method-7 fork ends before its declared expanded length.");
        return output;
    }

    private static int ReadStacLzsLength(ref StacLzsBitReader bits)
    {
        int prefix = bits.ReadBits(2);
        if (prefix < 3) return prefix + 2;

        prefix = bits.ReadBits(2);
        if (prefix < 3) return prefix + 5;

        int lengthCode = bits.ReadBits(4);
        if (lengthCode < 15) return lengthCode + 8;

        int length = 23;
        while (true)
        {
            int extension = bits.ReadBits(4);
            if (extension > int.MaxValue - length)
                throw new InvalidDataException("A DiskDoubler method-7 fork contains an excessive match length.");
            length += extension;
            if (extension < 15) return length;
        }
    }

    private ref struct StacLzsBitReader
    {
        private readonly ReadOnlySpan<byte> _input;
        private long _bitOffset;

        public StacLzsBitReader(ReadOnlySpan<byte> input)
        {
            _input = input;
            _bitOffset = 0;
        }

        public int ReadBit()
        {
            if (_bitOffset >= _input.Length * 8L)
                throw new InvalidDataException("A DiskDoubler method-7 fork ends inside a Stac LZS code.");
            int bit = (_input[(int)(_bitOffset >> 3)] >> (7 - (int)(_bitOffset & 7))) & 1;
            _bitOffset++;
            return bit;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int bit = 0; bit < count; bit++) value = (value << 1) | ReadBit();
            return value;
        }
    }

    private static byte[] DecodeAdaptiveHuffman(ReadOnlySpan<byte> input, int outputLength, byte info1, byte info2,
        int numberOfTrees = 256)
    {
        byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
        var trees = new AdaptiveHuffmanTree?[numberOfTrees];
        var output = new byte[outputLength];
        long bitOffset = 0;
        int currentTree = 0;
        for (int index = 0; index < output.Length; index++)
        {
            AdaptiveHuffmanTree tree = trees[currentTree] ??= new AdaptiveHuffmanTree();
            byte decoded = tree.ReadSymbol(input, ref bitOffset);
            tree.Update(decoded);
            output[index] = (byte)(decoded ^ xor);
            currentTree = decoded % numberOfTrees;
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

    private static byte ByteXor(ReadOnlySpan<byte> output)
    {
        byte xor = 0;
        foreach (byte value in output) xor ^= value;
        return xor;
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

    private static bool IsValidStandaloneHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < StandaloneHeaderLength || U32(header, 0) != FileHeaderMagic) return false;
        ushort checksum = U16(header, 82);
        return checksum == 0 || checksum == Crc16Xmodem(header[..82]);
    }

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
