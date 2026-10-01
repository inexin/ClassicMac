using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads level-0 LHA archives containing stored files.</summary>
/// <remarks>Compressed methods and the level-1 through level-3 header variants are not supported yet.</remarks>
public sealed class LhaReader : IContainerReader
{
    private const int LevelZeroHeaderMinimumLength = 25;
    private const int MaximumHeaderLength = byte.MaxValue + 1;

    /// <summary>The built-in LHA reader.</summary>
    public static LhaReader Instance { get; } = new();

    private LhaReader() { }

    /// <inheritdoc />
    public string FormatName => "LHA";

    /// <inheritdoc />
    public bool CanRead(ForkData input)
    {
        if (input.Length < LevelZeroHeaderMinimumLength) return false;
        ReadOnlySpan<byte> header = input.ReadPrefix((int)Math.Min(input.Length, MaximumHeaderLength));
        int headerLength = header[0] + 1;
        if (headerLength < LevelZeroHeaderMinimumLength || headerLength > header.Length || header[20] != 0)
            return false;
        return IsLhaMethod(header.Slice(2, 5));
    }

    /// <inheritdoc />
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        if (input.Length > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("The LHA archive exceeds the configured input-size limit.");
        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        var files = new List<MacFile>();
        long expandedBytes = 0;
        int offset = 0;
        bool foundEndMarker = false;

        while (offset < archive.Length)
        {
            int headerSize = archive[offset];
            if (headerSize == 0)
            {
                foundEndMarker = true;
                break;
            }

            int headerLength = headerSize + 1;
            if (headerLength < LevelZeroHeaderMinimumLength || headerLength > MaximumHeaderLength ||
                headerLength > archive.Length - offset)
                throw new InvalidDataException("An LHA level-0 header is truncated or has an invalid size.");

            ReadOnlySpan<byte> header = archive.AsSpan(offset, headerLength);
            if (header[20] != 0)
                throw new InvalidDataException("Only LHA level-0 headers are supported.");
            if (HeaderChecksum(header[2..]) != header[1])
                throw new InvalidDataException("An LHA level-0 header checksum is invalid.");

            int nameLength = header[21];
            if (headerLength != LevelZeroHeaderMinimumLength + nameLength)
                throw new InvalidDataException("An LHA level-0 header has an invalid filename length.");

            uint packedSizeValue = U32(header, 7);
            uint expandedSizeValue = U32(header, 11);
            if (packedSizeValue > int.MaxValue || expandedSizeValue > int.MaxValue)
                throw new InvalidDataException("An LHA file exceeds the supported in-memory size.");
            int packedSize = (int)packedSizeValue;
            int expandedSize = (int)expandedSizeValue;
            int payloadOffset = checked(offset + headerLength);
            if (packedSize > archive.Length - payloadOffset)
                throw new InvalidDataException("An LHA file payload is truncated.");
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput - expandedSize)
                throw new InvalidDataException("LHA extraction exceeds the configured expanded-size limit.");

            ReadOnlySpan<byte> method = header.Slice(2, 5);
            if (method.SequenceEqual("-lh0-"u8))
            {
                if (packedSize != expandedSize)
                    throw new InvalidDataException("An uncompressed LHA file has different packed and expanded sizes.");

                if (header[24 + nameLength] != (byte)'m')
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.encoding-unsupported",
                        "The LHA entry does not use the Mac OS filename encoding; it is skipped.", offset);
                    offset = checked(payloadOffset + packedSize);
                    continue;
                }

                ushort expectedCrc = U16(header, 22 + nameLength);
                ReadOnlySpan<byte> data = archive.AsSpan(payloadOffset, packedSize);
                if (Crc16Ibm(data) != expectedCrc)
                    context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                        "An LHA file has a CRC-16 mismatch; its decoded data is retained.", payloadOffset);

                ReadOnlySpan<byte> path = header.Slice(22, nameLength);
                if (path.IsEmpty)
                    throw new InvalidDataException("An LHA file has an empty filename.");
                MacFile? file = MakeFile(path, data);
                if (file is not null) files.Add(file);
                expandedBytes += expandedSize;
            }
            else if (method.SequenceEqual("-lhd-"u8))
            {
                if (packedSize != 0 || expandedSize != 0)
                    throw new InvalidDataException("An LHA directory record has a nonempty payload.");
            }
            else
            {
                context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                    $"LHA compression method '{Encoding.ASCII.GetString(method)}' is not supported; the entry is skipped.",
                    offset);
            }

            offset = checked(payloadOffset + packedSize);
        }

        if (!foundEndMarker)
            context.Report(DiagnosticSeverity.Warning, "archive.end-marker-missing",
                "The LHA archive has no end marker.", offset);
        return files;
    }

    private static MacFile? MakeFile(ReadOnlySpan<byte> path, ReadOnlySpan<byte> data)
    {
        var components = new List<MacString>();
        int start = 0;
        for (int index = 0; index <= path.Length; index++)
        {
            if (index != path.Length && path[index] is not ((byte)'\\') and not ((byte)'/')) continue;
            if (index > start) components.Add(new MacString(path[start..index]));
            start = index + 1;
        }
        if (components.Count == 0) return null;
        var folders = components.GetRange(0, components.Count - 1).ToArray();
        return new MacFile
        {
            Name = components[^1],
            FolderPath = folders,
            DataFork = ForkData.FromBytes(data.ToArray())
        };
    }

    private static bool IsLhaMethod(ReadOnlySpan<byte> method) => method.SequenceEqual("-lh0-"u8) ||
        method.SequenceEqual("-lh1-"u8) || method.SequenceEqual("-lh2-"u8) || method.SequenceEqual("-lh3-"u8) ||
        method.SequenceEqual("-lh4-"u8) || method.SequenceEqual("-lh5-"u8) || method.SequenceEqual("-lh6-"u8) ||
        method.SequenceEqual("-lh7-"u8) || method.SequenceEqual("-lzs-"u8) || method.SequenceEqual("-lz4-"u8) ||
        method.SequenceEqual("-lz5-"u8) || method.SequenceEqual("-lhd-"u8);

    private static byte HeaderChecksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0;
        foreach (byte value in bytes) checksum = unchecked((byte)(checksum + value));
        return checksum;
    }

    private static ushort Crc16Ibm(ReadOnlySpan<byte> bytes)
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

    private static ushort U16(ReadOnlySpan<byte> source, int offset) =>
        (ushort)(source[offset] | (source[offset + 1] << 8));

    private static uint U32(ReadOnlySpan<byte> source, int offset) =>
        (uint)(source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16) | (source[offset + 3] << 24));
}
