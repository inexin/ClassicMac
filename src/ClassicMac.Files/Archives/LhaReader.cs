using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads LHA archives containing stored and compressed files.</summary>
/// <remarks>All documented LHA methods supported by this reader are implemented.</remarks>
public sealed class LhaReader : IContainerReader
{
    private const int LevelZeroHeaderMinimumLength = 25;
    private const int LevelTwoHeaderMinimumLength = 26;
    private const int LevelThreeHeaderMinimumLength = 32;
    private const int MaximumHeaderLength = byte.MaxValue + 2;

    /// <summary>The built-in LHA reader.</summary>
    public static LhaReader Instance { get; } = new();

    private LhaReader() { }

    /// <inheritdoc />
    public string FormatName => "LHA";

    /// <inheritdoc />
    public bool CanRead(ForkData input)
    {
        if (input.Length < LevelZeroHeaderMinimumLength) return false;
        ReadOnlySpan<byte> header = input.ReadPrefix((int)Math.Min(input.Length, ushort.MaxValue));
        byte level = header[20];
        if (level == 3)
        {
            if (header.Length < LevelThreeHeaderMinimumLength) return false;
            uint levelThreeHeaderLength = U32(header, 24);
            return U16(header, 0) == 4 && levelThreeHeaderLength >= LevelThreeHeaderMinimumLength &&
                levelThreeHeaderLength <= input.Length && IsLhaMethod(header.Slice(2, 5));
        }
        if (level == 2)
        {
            int levelTwoHeaderLength = U16(header, 0);
            return levelTwoHeaderLength >= LevelTwoHeaderMinimumLength && levelTwoHeaderLength <= header.Length &&
                IsLhaMethod(header.Slice(2, 5));
        }

        if (level is not 0 and not 1) return false;
        int headerLength = header[0] + 2;
        if (headerLength < LevelZeroHeaderMinimumLength || headerLength > header.Length)
            return false;
        int nameLength = header[21];
        if (level == 0 && headerLength != LevelZeroHeaderMinimumLength + nameLength ||
            level == 1 && headerLength < 27 + nameLength)
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
            if (archive[offset] == 0)
            {
                foundEndMarker = true;
                break;
            }

            if (archive.Length - offset < 21)
                throw new InvalidDataException("An LHA header is truncated.");
            byte headerLevel = archive[offset + 20];
            int fixedHeaderLength = headerLevel switch
            {
                2 => LevelTwoHeaderMinimumLength,
                3 => LevelThreeHeaderMinimumLength,
                _ => LevelZeroHeaderMinimumLength
            };
            if (archive.Length - offset < fixedHeaderLength)
                throw new InvalidDataException("An LHA header is truncated.");
            uint levelThreeLength = headerLevel == 3 ? U32(archive.AsSpan(offset), 24) : 0;
            if (levelThreeLength > int.MaxValue)
                throw new InvalidDataException("An LHA level-3 header exceeds the supported in-memory size.");
            int headerLength = headerLevel switch
            {
                2 => U16(archive.AsSpan(offset), 0),
                3 => (int)levelThreeLength,
                _ => archive[offset] + 2
            };
            int minimumHeaderLength = headerLevel switch
            {
                2 => LevelTwoHeaderMinimumLength,
                3 => LevelThreeHeaderMinimumLength,
                _ => LevelZeroHeaderMinimumLength
            };
            if (headerLength < minimumHeaderLength ||
                headerLevel is not 2 and not 3 && headerLength > MaximumHeaderLength ||
                headerLength > archive.Length - offset)
                throw new InvalidDataException("An LHA header is truncated or has an invalid size.");

            ReadOnlySpan<byte> header = archive.AsSpan(offset, headerLength);
            if (headerLevel > 3)
                throw new InvalidDataException("Only LHA level-0 through level-3 headers are supported.");
            if (headerLevel < 2 && HeaderChecksum(header.Slice(2, headerLength - 2)) != header[1])
                throw new InvalidDataException("An LHA header checksum is invalid.");

            ReadOnlySpan<byte> method = header.Slice(2, 5);
            int nameLength = header[21];
            uint packedSizeValue = U32(header, 7);
            uint expandedSizeValue = U32(header, 11);
            if (packedSizeValue > int.MaxValue || expandedSizeValue > int.MaxValue)
                throw new InvalidDataException("An LHA file exceeds the supported in-memory size.");
            int declaredPackedSize = (int)packedSizeValue;
            int packedSize = declaredPackedSize;
            int expandedSize = (int)expandedSizeValue;
            int payloadOffset;
            byte[] pathData;
            byte osIdentifier;
            if (headerLevel == 0)
            {
                if (headerLength != LevelZeroHeaderMinimumLength + nameLength)
                    throw new InvalidDataException("An LHA level-0 header has an invalid filename length.");
                pathData = header.Slice(22, nameLength).ToArray();
                osIdentifier = header[24 + nameLength];
                payloadOffset = checked(offset + headerLength);
            }
            else if (headerLevel == 1)
            {
                if (headerLength < 27 + nameLength)
                    throw new InvalidDataException("An LHA level-1 header has an invalid filename length.");
                osIdentifier = header[24 + nameLength];
                ushort firstExtensionSize = U16(header, headerLength - 2);
                LhaExtendedHeaders extensions = ReadLevelOneExtensions(archive,
                    checked(offset + headerLength), firstExtensionSize, declaredPackedSize);
                if (extensions.Length > declaredPackedSize)
                    throw new InvalidDataException("An LHA level-1 header's extended headers exceed its skip size.");
                packedSize = declaredPackedSize - extensions.Length;
                pathData = CombinePath(extensions.DirectoryName, extensions.FileName ?? header.Slice(22, nameLength));
                payloadOffset = checked(offset + headerLength + extensions.Length);
            }
            else
            {
                osIdentifier = header[23];
                LhaExtendedHeaders extensions = headerLevel == 2
                    ? ReadLevelTwoExtensions(header)
                    : ReadLevelThreeExtensions(header);
                ValidateExtendedHeaderCrc(header, extensions.HeaderCrcOffset);
                if (extensions.FileName is not { Length: > 0 })
                    throw new InvalidDataException($"An LHA level-{headerLevel} file has an empty filename.");
                pathData = CombinePath(extensions.DirectoryName, extensions.FileName);
                payloadOffset = checked(offset + headerLength);
            }

            if (expandedBytes > context.Options.MaxExpandedBytesPerInput - expandedSize)
                throw new InvalidDataException("LHA extraction exceeds the configured expanded-size limit.");
            if (packedSize > archive.Length - payloadOffset)
                throw new InvalidDataException("An LHA file payload is truncated.");

            if (method.SequenceEqual("-lh0-"u8) || method.SequenceEqual("-lh1-"u8) ||
                method.SequenceEqual("-lzs-"u8) || method.SequenceEqual("-lz5-"u8) ||
                method.SequenceEqual("-lh2-"u8) || method.SequenceEqual("-lh3-"u8) || IsNewStyleMethod(method))
            {
                if (osIdentifier != (byte)'m')
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.encoding-unsupported",
                        "The LHA entry does not use the Mac OS filename encoding; it is skipped.", offset);
                    offset = checked(payloadOffset + packedSize);
                    continue;
                }

                ushort expectedCrc = headerLevel >= 2 ? U16(header, 21) : U16(header, 22 + nameLength);
                ReadOnlySpan<byte> packedData = archive.AsSpan(payloadOffset, packedSize);
                byte[] decodedData;
                if (method.SequenceEqual("-lh0-"u8))
                {
                    if (packedSize != expandedSize)
                        throw new InvalidDataException("An uncompressed LHA file has different packed and expanded sizes.");
                    decodedData = packedData.ToArray();
                }
                else if (IsNewStyleMethod(method))
                {
                    decodedData = LhaNewDecoder.Decode(method, packedData, expandedSize);
                }
                else if (method.SequenceEqual("-lh1-"u8))
                {
                    decodedData = LhaOldDecoder.DecodeLh1(packedData, expandedSize);
                }
                else if (method.SequenceEqual("-lz5-"u8))
                {
                    decodedData = LhaLarcDecoder.DecodeLz5(packedData, expandedSize);
                }
                else if (method.SequenceEqual("-lzs-"u8))
                {
                    decodedData = LhaLarcDecoder.DecodeLzs(packedData, expandedSize);
                }
                else if (method.SequenceEqual("-lh3-"u8))
                {
                    decodedData = LhaLegacyStaticDecoder.DecodeLh3(packedData, expandedSize);
                }
                else if (method.SequenceEqual("-lh2-"u8))
                {
                    decodedData = LhaLh2Decoder.Decode(packedData, expandedSize);
                }
                else
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.method-unsupported",
                        $"LHA compression method '{Encoding.ASCII.GetString(method)}' is not supported; the entry is skipped.",
                        offset);
                    offset = checked(payloadOffset + packedSize);
                    continue;
                }

                if (Crc16Ibm(decodedData) != expectedCrc)
                    context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                        "An LHA file has a CRC-16 mismatch; its decoded data is retained.", payloadOffset);

                if (pathData.Length == 0)
                    throw new InvalidDataException("An LHA file has an empty filename.");
                MacFile? file = MakeFile(pathData, decodedData);
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

    private static LhaExtendedHeaders ReadLevelOneExtensions(ReadOnlySpan<byte> archive, int offset,
        int nextHeaderSize, int packedSize)
    {
        int totalLength = 0;
        byte[]? directoryName = null;
        byte[]? fileName = null;
        while (nextHeaderSize != 0)
        {
            if (nextHeaderSize < 3 || nextHeaderSize > packedSize - totalLength)
                throw new InvalidDataException("An LHA level-1 extended header has an invalid size.");
            int extensionOffset = checked(offset + totalLength);
            if (extensionOffset > archive.Length || nextHeaderSize > archive.Length - extensionOffset)
                throw new InvalidDataException("An LHA level-1 extended header is truncated.");

            ReadOnlySpan<byte> extension = archive.Slice(extensionOffset, nextHeaderSize);
            int dataLength = nextHeaderSize - 3;
            switch (extension[0])
            {
                case 0x01:
                    fileName = extension.Slice(1, dataLength).ToArray();
                    break;
                case 0x02:
                    directoryName = extension.Slice(1, dataLength).ToArray();
                    break;
            }

            nextHeaderSize = U16(extension, nextHeaderSize - 2);
            totalLength = checked(totalLength + extension.Length);
        }

        return new LhaExtendedHeaders(totalLength, directoryName, fileName);
    }

    private static LhaExtendedHeaders ReadLevelTwoExtensions(ReadOnlySpan<byte> header)
    {
        int extensionBytesRemaining = header.Length - LevelTwoHeaderMinimumLength;
        int nextHeaderSize = U16(header, 24);
        int totalLength = 0;
        int headerCrcOffset = -1;
        byte[]? directoryName = null;
        byte[]? fileName = null;

        while (nextHeaderSize != 0)
        {
            if (nextHeaderSize < 3 || nextHeaderSize > extensionBytesRemaining - totalLength)
                throw new InvalidDataException("An LHA level-2 extended header has an invalid size.");

            int extensionOffset = LevelTwoHeaderMinimumLength + totalLength;
            ReadOnlySpan<byte> extension = header.Slice(extensionOffset, nextHeaderSize);
            int dataLength = nextHeaderSize - 3;
            switch (extension[0])
            {
                case 0x00:
                    if (dataLength < 2 || headerCrcOffset >= 0)
                        throw new InvalidDataException("An LHA level-2 header has an invalid header-CRC extension.");
                    headerCrcOffset = extensionOffset + 1;
                    break;
                case 0x01:
                    fileName = extension.Slice(1, dataLength).ToArray();
                    break;
                case 0x02:
                    directoryName = extension.Slice(1, dataLength).ToArray();
                    break;
            }

            nextHeaderSize = U16(extension, nextHeaderSize - 2);
            totalLength = checked(totalLength + extension.Length);
        }

        int padding = extensionBytesRemaining - totalLength;
        if (padding is not 0 and not 1)
            throw new InvalidDataException("An LHA level-2 header has an invalid padding length.");
        if (headerCrcOffset < 0)
            throw new InvalidDataException("An LHA level-2 header has no header-CRC extension.");

        return new LhaExtendedHeaders(totalLength, directoryName, fileName, headerCrcOffset);
    }

    private static LhaExtendedHeaders ReadLevelThreeExtensions(ReadOnlySpan<byte> header)
    {
        int totalLength = 0;
        uint nextHeaderSizeValue = U32(header, 28);
        if (nextHeaderSizeValue > int.MaxValue)
            throw new InvalidDataException("An LHA level-3 extended header exceeds the supported size.");
        int nextHeaderSize = (int)nextHeaderSizeValue;
        int headerCrcOffset = -1;
        byte[]? directoryName = null;
        byte[]? fileName = null;

        while (nextHeaderSize != 0)
        {
            if (nextHeaderSize < 5 || nextHeaderSize > header.Length - LevelThreeHeaderMinimumLength - totalLength)
                throw new InvalidDataException("An LHA level-3 extended header has an invalid size.");

            int extensionOffset = LevelThreeHeaderMinimumLength + totalLength;
            ReadOnlySpan<byte> extension = header.Slice(extensionOffset, nextHeaderSize);
            int dataLength = nextHeaderSize - 5;
            switch (extension[0])
            {
                case 0x00:
                    if (dataLength < 2 || headerCrcOffset >= 0)
                        throw new InvalidDataException("An LHA level-3 header has an invalid header-CRC extension.");
                    headerCrcOffset = extensionOffset + 1;
                    break;
                case 0x01:
                    fileName = extension.Slice(1, dataLength).ToArray();
                    break;
                case 0x02:
                    directoryName = extension.Slice(1, dataLength).ToArray();
                    break;
            }

            uint nextSizeValue = U32(extension, nextHeaderSize - 4);
            if (nextSizeValue > int.MaxValue)
                throw new InvalidDataException("An LHA level-3 extended header exceeds the supported size.");
            nextHeaderSize = (int)nextSizeValue;
            totalLength = checked(totalLength + extension.Length);
        }

        if (header.Length - LevelThreeHeaderMinimumLength - totalLength != 0)
            throw new InvalidDataException("An LHA level-3 header has an invalid padding length.");
        if (headerCrcOffset < 0)
            throw new InvalidDataException("An LHA level-3 header has no header-CRC extension.");

        return new LhaExtendedHeaders(totalLength, directoryName, fileName, headerCrcOffset);
    }

    private static void ValidateExtendedHeaderCrc(ReadOnlySpan<byte> header, int crcOffset)
    {
        ushort expectedCrc = U16(header, crcOffset);
        ushort actualCrc = Crc16IbmWithZeroedRange(header, crcOffset, 2);
        if (actualCrc != expectedCrc)
            throw new InvalidDataException("An LHA level-2 header CRC is invalid.");
    }

    private static byte[] CombinePath(byte[]? directoryName, ReadOnlySpan<byte> fileName)
    {
        if (directoryName is null || directoryName.Length == 0) return fileName.ToArray();
        bool hasSeparator = directoryName[^1] is (byte)'/' or (byte)'\\';
        byte[] path = new byte[directoryName.Length + (hasSeparator ? 0 : 1) + fileName.Length];
        directoryName.CopyTo(path, 0);
        int filenameOffset = directoryName.Length;
        if (!hasSeparator) path[filenameOffset++] = (byte)'/';
        fileName.CopyTo(path.AsSpan(filenameOffset));
        return path;
    }

    private static bool IsLhaMethod(ReadOnlySpan<byte> method) => method.SequenceEqual("-lh0-"u8) ||
        method.SequenceEqual("-lh1-"u8) || method.SequenceEqual("-lh2-"u8) || method.SequenceEqual("-lh3-"u8) ||
        method.SequenceEqual("-lh4-"u8) || method.SequenceEqual("-lh5-"u8) || method.SequenceEqual("-lh6-"u8) ||
        method.SequenceEqual("-lh7-"u8) || method.SequenceEqual("-lzs-"u8) || method.SequenceEqual("-lz4-"u8) ||
        method.SequenceEqual("-lz5-"u8) || method.SequenceEqual("-lhd-"u8);

    private static bool IsNewStyleMethod(ReadOnlySpan<byte> method) => method.SequenceEqual("-lh4-"u8) ||
        method.SequenceEqual("-lh5-"u8) || method.SequenceEqual("-lh6-"u8) || method.SequenceEqual("-lh7-"u8);

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

    private static ushort Crc16IbmWithZeroedRange(ReadOnlySpan<byte> bytes, int zeroOffset, int zeroLength)
    {
        ushort crc = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = index >= zeroOffset && index - zeroOffset < zeroLength ? (byte)0 : bytes[index];
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

    private readonly record struct LhaExtendedHeaders(int Length, byte[]? DirectoryName, byte[]? FileName,
        int HeaderCrcOffset = -1);
}
