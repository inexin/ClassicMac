using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads Compact Pro archives with RLE and LZH+RLE fork encodings.</summary>
/// <remarks>
/// The directory layout and both compression methods are fitted against independent published format descriptions.
/// Fork payloads may reside in sibling files identified by their Compact Pro volume numbers.
/// </remarks>
public sealed class CompactProReader : IContainerReader
{
    private const int ArchiveHeaderLength = 8;

    /// <summary>The built-in reader.</summary>
    public static CompactProReader Instance { get; } = new();

    private CompactProReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "Compact Pro archive";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < ArchiveHeaderLength + 7) return false;
        try
        {
            using Stream stream = input.Open();
            if (stream.ReadByte() != 1) return false;
            stream.Seek(4, SeekOrigin.Begin);
            Span<byte> offsetBytes = stackalloc byte[4];
            stream.ReadExactly(offsetBytes);
            uint offset = new BigEndianReader(offsetBytes.ToArray()).ReadUInt32();
            if (offset < ArchiveHeaderLength || offset > input.Length - 7 || offset > int.MaxValue) return false;
            CompactProDirectory directory = ReadDirectory(stream, input.Length, (int)offset);
            return directory.StoredCrc == directory.ComputedCrc;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
            OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (input.Length > context.Options.MaxExpandedBytesPerInput)
            throw new InvalidDataException("The Compact Pro archive exceeds the configured input-size limit.");
        byte[] archive = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        if (archive.Length < ArchiveHeaderLength || archive[0] != 1)
            throw new InvalidDataException("Not a Compact Pro archive.");

        uint directoryOffsetRaw = U32(archive, 4);
        if (directoryOffsetRaw > int.MaxValue)
            throw new InvalidDataException("The Compact Pro directory offset is too large.");
        int directoryOffset = (int)directoryOffsetRaw;
        using var stream = new MemoryStream(archive, writable: false);
        CompactProDirectory directory = ReadDirectory(stream, archive.Length, directoryOffset);
        if (directory.StoredCrc != directory.ComputedCrc)
            context.Report(DiagnosticSeverity.Error, "archive.header-crc",
                "The Compact Pro directory checksum is incorrect.", directoryOffset);
        if (directory.Comment.Length > 0)
            context.Report(DiagnosticSeverity.Info, "archive.comment",
                $"Compact Pro comment: {directory.Comment}", directoryOffset);
        if (directory.Entries.Count > context.Options.MaxVolumeEntries)
            throw new InvalidDataException("The Compact Pro archive exceeds the configured entry limit.");

        var files = new List<MacFile>();
        var folders = new List<FolderScope>();
        var otherVolumes = new Dictionary<byte, byte[]>();
        var unavailableVolumes = new HashSet<byte>();
        long volumeBytes = archive.Length;
        long expandedBytes = 0;
        for (int index = 0; index < directory.Entries.Count; index++)
        {
            while (folders.Count > 0 && folders[^1].LastEntry < index) folders.RemoveAt(folders.Count - 1);
            CompactProEntry entry = directory.Entries[index];
            if (entry.IsDirectory)
            {
                int lastChildEntry = checked(index + entry.ChildEntryCount);
                if (entry.ChildEntryCount > 0 && lastChildEntry >= directory.Entries.Count)
                    throw new InvalidDataException("A Compact Pro directory's child count extends past the entry table.");
                if (entry.ChildEntryCount > 0)
                    folders.Add(new FolderScope(lastChildEntry, entry.Name));
                continue;
            }

            ReadOnlyMemory<byte> metadata = entry.Metadata;
            int volume = metadata.Span[0];
            byte[] dataVolume = archive;
            int minimumDataOffset = directory.TableEnd;
            if (volume != archive[1])
            {
                byte siblingVolumeNumber = checked((byte)volume);
                if (otherVolumes.TryGetValue(siblingVolumeNumber, out byte[]? cachedVolume))
                {
                    dataVolume = cachedVolume;
                }
                else if (unavailableVolumes.Contains(siblingVolumeNumber))
                {
                    context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                        $"Compact Pro entry '{entry.Name}' refers to unavailable volume {volume}.", entry.Offset);
                    continue;
                }
                else if (ReadSiblingVolume(siblingVolumeNumber, context, ref volumeBytes) is { } siblingVolume)
                {
                    dataVolume = siblingVolume;
                    otherVolumes.Add(siblingVolumeNumber, siblingVolume);
                }
                else
                {
                    unavailableVolumes.Add(siblingVolumeNumber);
                    context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                        $"Compact Pro entry '{entry.Name}' refers to unavailable volume {volume}.", entry.Offset);
                    continue;
                }
                minimumDataOffset = ArchiveHeaderLength;
            }

            uint fileOffsetRaw = U32(metadata, 1);
            if (fileOffsetRaw > int.MaxValue)
                throw new InvalidDataException("A Compact Pro fork data offset is too large.");
            int fileOffset = (int)fileOffsetRaw;
            if (fileOffset < minimumDataOffset)
                throw new InvalidDataException("A Compact Pro fork overlaps its volume header or directory.");
            FourCC type = new(metadata.Span.Slice(5, 4));
            FourCC creator = new(metadata.Span.Slice(9, 4));
            uint createdRaw = U32(metadata, 13);
            uint modifiedRaw = U32(metadata, 17);
            var finderFlags = (FinderFlags)U16(metadata, 21);
            uint expectedCrc = U32(metadata, 23);
            ushort flags = U16(metadata, 27);
            int resourceLength = ReadLength(U32(metadata, 29), "resource fork");
            int dataLength = ReadLength(U32(metadata, 33), "data fork");
            int resourceCompressedLength = ReadLength(U32(metadata, 37), "compressed resource fork");
            int dataCompressedLength = ReadLength(U32(metadata, 41), "compressed data fork");
            Require(dataVolume, fileOffset, checked(resourceCompressedLength + dataCompressedLength),
                "Compact Pro fork data");

            if ((flags & 1) != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                    $"The encrypted Compact Pro entry '{entry.Name}' is listed but not opened.", entry.Offset);
                continue;
            }
            if ((flags & ~7) != 0)
                context.Report(DiagnosticSeverity.Warning, "archive.flags-unknown",
                    $"The Compact Pro entry '{entry.Name}' has unknown flags 0x{flags:X4}.", entry.Offset);
            expandedBytes = checked(expandedBytes + resourceLength + dataLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
                throw new InvalidDataException("Compact Pro extraction exceeds the configured expanded-size limit.");

            ReadOnlySpan<byte> resourceInput = dataVolume.AsSpan(fileOffset, resourceCompressedLength);
            byte[] resource = (flags & 2) != 0
                ? CompactProLzhDecoder.Decode(resourceInput, resourceLength)
                : DecodeRle8182(resourceInput, resourceLength);
            int dataOffset = checked(fileOffset + resourceCompressedLength);
            ReadOnlySpan<byte> dataInput = dataVolume.AsSpan(dataOffset, dataCompressedLength);
            byte[] data = (flags & 4) != 0
                ? CompactProLzhDecoder.Decode(dataInput, dataLength)
                : DecodeRle8182(dataInput, dataLength);
            if (~Crc32(resource, data) != expectedCrc)
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The Compact Pro data/resource checksum is incorrect for '{entry.Name}'.", entry.Offset);

            files.Add(new MacFile
            {
                Name = entry.Name,
                FolderPath = [.. folders.Select(folder => folder.Name)],
                FinderInfo = new FinderInfo { Type = type, Creator = creator, Flags = finderFlags },
                Created = createdRaw == 0 ? null : new MacDate(createdRaw),
                Modified = modifiedRaw == 0 ? null : new MacDate(modifiedRaw),
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
            });
        }
        return files;
    }

    private static byte[]? ReadSiblingVolume(byte volumeNumber, ContainerContext context, ref long totalVolumeBytes)
    {
        byte[]? foundVolume = null;
        foreach (MacFile sibling in context.Siblings?.Invoke() ?? [])
        {
            ForkData data = sibling.DataFork;
            if (data.Length < ArchiveHeaderLength) continue;
            byte[] header = data.Slice(0, ArchiveHeaderLength).ToArray(ArchiveHeaderLength);
            if (header[0] != 1 || header[1] != volumeNumber) continue;
            if (foundVolume is not null)
                throw new InvalidDataException($"More than one Compact Pro sibling identifies volume {volumeNumber}.");
            if (data.Length > context.Options.MaxExpandedBytesPerInput - totalVolumeBytes)
                throw new InvalidDataException("The Compact Pro volume set exceeds the configured input-size limit.");
            foundVolume = data.ToArray(context.Options.MaxExpandedBytesPerInput);
            totalVolumeBytes = checked(totalVolumeBytes + data.Length);
        }

        return foundVolume;
    }

    private static CompactProDirectory ReadDirectory(Stream stream, long archiveLength, int offset)
    {
        if (offset < ArchiveHeaderLength || offset > archiveLength - 7)
            throw new InvalidDataException("The Compact Pro directory lies outside the archive.");
        stream.Seek(offset, SeekOrigin.Begin);
        uint storedCrc = ReadU32(stream);
        uint crc = uint.MaxValue;
        ushort entryCount = ReadU16AndUpdate(stream, ref crc);
        int commentLength = ReadByteAndUpdate(stream, ref crc);
        MacString comment = new(ReadBytesAndUpdate(stream, commentLength, archiveLength, ref crc));
        var entries = new List<CompactProEntry>(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            int nameType = ReadByteAndUpdate(stream, ref crc);
            int nameLength = nameType & 0x7F;
            if (nameLength == 0) throw new InvalidDataException("A Compact Pro entry has an empty name.");
            byte[] nameBytes = ReadBytesAndUpdate(stream, nameLength, archiveLength, ref crc);
            var name = new MacString(nameBytes);
            if ((nameType & 0x80) != 0)
            {
                ushort children = ReadU16AndUpdate(stream, ref crc);
                entries.Add(new CompactProEntry(name, true, children, [], checked((int)stream.Position - 2)));
            }
            else
            {
                byte[] metadata = ReadBytesAndUpdate(stream, 45, archiveLength, ref crc);
                entries.Add(new CompactProEntry(name, false, 0, metadata, checked((int)stream.Position - 45)));
            }
        }
        return new CompactProDirectory(storedCrc, crc, checked((int)stream.Position), comment, entries);
    }

    internal static byte[] DecodeRle8182(ReadOnlySpan<byte> input, int outputLength)
    {
        var output = new byte[outputLength];
        int source = 0;
        int written = 0;
        bool hasPrevious = false;
        byte previous = 0;
        while (source < input.Length && written < output.Length)
        {
            byte value = input[source++];
            if (value != 0x81)
            {
                Emit(value);
                continue;
            }
            if (source == input.Length)
                throw new InvalidDataException("A Compact Pro RLE stream ends after an escape byte.");
            byte escape = input[source++];
            if (escape != 0x82)
            {
                Emit(0x81);
                if (escape == 0x81)
                {
                    if (source == input.Length)
                        throw new InvalidDataException("A Compact Pro RLE stream ends after an escape byte.");
                    byte second = input[source++];
                    if (second == 0x82)
                    {
                        if (source == input.Length)
                            throw new InvalidDataException("A Compact Pro RLE run has no count byte.");
                        EmitRun(input[source++]);
                    }
                    else
                    {
                        Emit(0x81);
                        Emit(second);
                    }
                }
                else Emit(escape);
                continue;
            }
            if (source == input.Length)
                throw new InvalidDataException("A Compact Pro RLE run has no count byte.");
            byte count = input[source++];
            if (count == 0)
            {
                Emit(0x81);
                Emit(0x82);
            }
            else EmitRun(count);
        }
        if (written != output.Length)
            throw new InvalidDataException($"Compact Pro RLE produced {written} of {output.Length} declared bytes.");
        return output;

        void Emit(byte value)
        {
            if (written == output.Length)
                throw new InvalidDataException("Compact Pro RLE output exceeds its declared fork length.");
            output[written++] = previous = value;
            hasPrevious = true;
        }

        void EmitRun(byte count)
        {
            int repeats = count switch { <= 3 => count - 1, _ => count - 1 };
            if (repeats == 0) return;
            if (!hasPrevious)
                throw new InvalidDataException("A Compact Pro RLE run has no preceding byte.");
            if (repeats > output.Length - written)
                throw new InvalidDataException("Compact Pro RLE output exceeds its declared fork length.");
            output.AsSpan(written, repeats).Fill(previous);
            written += repeats;
        }
    }

    private static ushort ReadU16AndUpdate(Stream stream, ref uint crc)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        crc = UpdateCrc(crc, bytes);
        return new BigEndianReader(bytes.ToArray()).ReadUInt16();
    }

    private static uint ReadU32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return new BigEndianReader(bytes.ToArray()).ReadUInt32();
    }

    private static int ReadByteAndUpdate(Stream stream, ref uint crc)
    {
        int value = stream.ReadByte();
        if (value < 0) throw new InvalidDataException("The Compact Pro directory is truncated.");
        crc = UpdateCrcByte(crc, (byte)value);
        return value;
    }

    private static byte[] ReadBytesAndUpdate(Stream stream, int length, long archiveLength, ref uint crc)
    {
        if (length < 0 || stream.Position > archiveLength - length)
            throw new InvalidDataException("A Compact Pro directory entry extends past the archive.");
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        crc = UpdateCrc(crc, bytes);
        return bytes;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) crc = UpdateCrcByte(crc, value);
        return crc;
    }

    private static uint UpdateCrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        return crc;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        ~UpdateCrc(UpdateCrc(uint.MaxValue, first), second);

    private static int ReadLength(uint value, string what)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"A Compact Pro {what} exceeds the supported size.");
        return (int)value;
    }

    private static ushort U16(ReadOnlyMemory<byte> bytes, int offset) => new BigEndianReader(bytes).ReadUInt16At(offset);

    private static uint U32(ReadOnlyMemory<byte> bytes, int offset) => new BigEndianReader(bytes).ReadUInt32At(offset);

    private static void Require(byte[] archive, int offset, int length, string what)
    {
        if (offset < 0 || length < 0 || offset > archive.Length - length)
            throw new InvalidDataException($"The {what} lies outside the Compact Pro archive.");
    }

    private sealed record CompactProDirectory(uint StoredCrc, uint ComputedCrc, int TableEnd, MacString Comment,
        List<CompactProEntry> Entries);
    private sealed record CompactProEntry(MacString Name, bool IsDirectory, int ChildEntryCount, byte[] Metadata,
        int Offset);
    private readonly record struct FolderScope(int LastEntry, MacString Name);
}
