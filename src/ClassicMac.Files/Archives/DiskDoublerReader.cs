using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads DiskDoubler DDA2 archives with stored or Compact Pro compatible forks.</summary>
/// <remarks>The DDA2 record layout is fitted against XADMaster's independent DiskDoubler parser. Other archive
/// variants and DDA2 compression methods are reported as unsupported.</remarks>
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
        return input.Length >= ArchiveHeaderLength && input.ReadPrefix(4).AsSpan().SequenceEqual("DDA2"u8);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (input.Length > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("The DiskDoubler archive exceeds the configured input-size limit.");

        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        if (archive.Length < ArchiveHeaderLength || !archive.AsSpan(0, 4).SequenceEqual("DDA2"u8))
            throw new InvalidDataException("Not a DiskDoubler DDA2 archive.");

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
            byte[] data = DecodeFork(encodedData, dataLength, dataMethod);
            byte[] resource = DecodeFork(encodedResource, resourceLength, resourceMethod);
            if (dataMethod == 8 && U16(archive, header + 44) != Crc16Ibm(data))
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The DiskDoubler data-fork checksum is incorrect for '{name}'.", header + 44);
            if (resourceMethod == 8 && U16(archive, header + 46) != Crc16Ibm(resource))
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
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

    private static bool IsSupportedMethod(int method) => method is 0 or 8;

    private static byte[] DecodeFork(ReadOnlySpan<byte> input, int outputLength, int method)
    {
        if (method == 0) return input.ToArray();
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
