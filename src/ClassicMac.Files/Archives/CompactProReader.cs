using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reads Compact Pro archives with RLE and LZH+RLE fork encodings.</summary>
/// <remarks>
/// The layout, folders, both compression methods, self-extracting archives and segment sets are verified against
/// archives made by Compact Pro 1.52. A segmented archive is opened from its last segment; the earlier segments are
/// found among the context's siblings by their set id.
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
        if (input.Length < ArchiveHeaderLength + 7)
        {
            return false;
        }

        try
        {
            // The header from the fork's first bytes (a probe's shared read); a stream is opened only for a likely archive.
            var reader = new BigEndianReader(input.ReadPrefix(8));
            if (reader.ReadByte() != 1)
            {
                return false;
            }

            uint offset = reader.ReadUInt32At(4);
            if (offset < ArchiveHeaderLength || offset > input.Length - 7 || offset > int.MaxValue)
            {
                return false;
            }

            using Stream stream = input.Open();
            stream.Seek(offset, SeekOrigin.Begin);
            CompactProDirectory directory = ReadDirectory(new BigEndianReader(stream), (int)offset);
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
        {
            throw new InvalidDataException("The Compact Pro archive exceeds the configured input-size limit.");
        }

        byte[] lastSegment = input.ToArray(context.Options.MaxExpandedBytesPerInput);
        var reader = new BigEndianReader(lastSegment);
        if (reader.Length < ArchiveHeaderLength || reader.ReadByteAt(0) != 1)
        {
            throw new InvalidDataException("Not a Compact Pro archive.");
        }

        byte segmentNumber = reader.ReadByteAt(1);
        ushort setId = reader.ReadUInt16At(2);
        uint directoryOffsetRaw = reader.ReadUInt32At(4);
        if (directoryOffsetRaw > int.MaxValue)
        {
            throw new InvalidDataException("The Compact Pro directory offset is too large.");
        }

        int directoryOffset = (int)directoryOffsetRaw;
        if (directoryOffset < ArchiveHeaderLength || directoryOffset > reader.Length - 7)
        {
            throw new InvalidDataException("The Compact Pro directory lies outside the archive.");
        }

        reader.Position = directoryOffset;
        CompactProDirectory directory = ReadDirectory(reader, 0);
        if (directory.StoredCrc != directory.ComputedCrc)
        {
            context.Report(DiagnosticSeverity.Error, "archive.header-crc",
                "The Compact Pro directory checksum is incorrect.", directoryOffset);
        }

        if (directory.Comment.Length > 0)
        {
            context.Report(DiagnosticSeverity.Info, "archive.comment",
                $"Compact Pro comment: {directory.Comment}", directoryOffset);
        }

        if (directory.Entries.Count > context.Options.MaxVolumeEntries)
        {
            throw new InvalidDataException("The Compact Pro archive exceeds the configured entry limit.");
        }

        // [Verified against Compact Pro 1.52] Segment n of a set is the archive's next bytes behind an 8-byte header
        // of its own (1, n, the set's id, a directory offset that is 0 except in the last segment, where it counts
        // from that segment's start). Segmenting leaves the directory untouched, so its fork offsets are into the
        // whole archive: segment 1, then each later segment after its header. A fork may run across segments.
        SegmentSet segments = JoinSegments(lastSegment, segmentNumber, setId, context);
        byte[] archive = segments.Archive;
        // The directory's place in the joined archive; past the end when a segment is missing.
        long joinedDirectory = segments.Complete
            ? archive.Length - ((long)lastSegment.Length - directoryOffset)
            : long.MaxValue;
        long joinedTableEnd = segments.Complete ? joinedDirectory + (directory.TableEnd - directoryOffset) : long.MaxValue;

        var files = new List<MacFile>();
        var folders = new List<FolderScope>();
        long expandedBytes = 0;
        for (int index = 0; index < directory.Entries.Count; index++)
        {
            while (folders.Count > 0 && folders[^1].LastEntry < index)
            {
                folders.RemoveAt(folders.Count - 1);
            }

            CompactProEntry entry = directory.Entries[index];
            if (entry.IsDirectory)
            {
                int lastChildEntry = checked(index + entry.ChildEntryCount);
                if (entry.ChildEntryCount > 0 && lastChildEntry >= directory.Entries.Count)
                {
                    throw new InvalidDataException("A Compact Pro directory's child count extends past the entry table.");
                }

                if (entry.ChildEntryCount > 0)
                {
                    folders.Add(new FolderScope(lastChildEntry, entry.Name));
                }

                continue;
            }

            // The entry's volume byte is 1 in a whole archive and in the segments made from it, so it does not
            // locate the forks; the offset alone does.
            CompactProFile file = entry.File!;
            long fileOffset = file.Offset;
            ushort flags = file.Flags;
            int resourceLength = ReadLength(file.ResourceLength, "resource fork");
            int dataLength = ReadLength(file.DataLength, "data fork");
            int resourceCompressedLength = ReadLength(file.ResourceCompressedLength, "compressed resource fork");
            int dataCompressedLength = ReadLength(file.DataCompressedLength, "compressed data fork");
            // Compact Pro writes the forks first and the directory after them [Verified against Compact Pro 1.52];
            // a fork may lie on either side of the directory but not across it.
            long forksEnd = fileOffset + resourceCompressedLength + dataCompressedLength;
            if (!segments.Complete && forksEnd > archive.Length)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                    $"Compact Pro entry '{entry.Name}' lies in a missing segment.", entry.Offset);
                continue;
            }
            if (fileOffset < ArchiveHeaderLength || fileOffset < joinedTableEnd && forksEnd > joinedDirectory)
            {
                throw new InvalidDataException("A Compact Pro fork overlaps its volume header or directory.");
            }

            if (forksEnd > archive.Length)
            {
                throw new InvalidDataException("The Compact Pro fork data lies outside the Compact Pro archive.");
            }

            if ((flags & 1) != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.encrypted",
                    $"The encrypted Compact Pro entry '{entry.Name}' is listed but not opened.", entry.Offset);
                continue;
            }
            if ((flags & ~7) != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.flags-unknown",
                    $"The Compact Pro entry '{entry.Name}' has unknown flags 0x{flags:X4}.", entry.Offset);
            }

            expandedBytes = checked(expandedBytes + resourceLength + dataLength);
            if (expandedBytes > context.Options.MaxExpandedBytesPerInput)
            {
                throw new InvalidDataException("Compact Pro extraction exceeds the configured expanded-size limit.");
            }

            ReadOnlySpan<byte> resourceInput = archive.AsSpan((int)fileOffset, resourceCompressedLength);
            byte[] resource = (flags & 2) != 0
                ? CompactProLzhDecoder.Decode(resourceInput, resourceLength)
                : DecodeRle8182(resourceInput, resourceLength);
            ReadOnlySpan<byte> dataInput = archive.AsSpan((int)fileOffset + resourceCompressedLength, dataCompressedLength);
            byte[] data = (flags & 4) != 0
                ? CompactProLzhDecoder.Decode(dataInput, dataLength)
                : DecodeRle8182(dataInput, dataLength);
            if (~Crc32(resource, data) != file.Crc)
            {
                context.Report(DiagnosticSeverity.Error, "archive.fork-crc",
                    $"The Compact Pro data/resource checksum is incorrect for '{entry.Name}'.", entry.Offset);
            }

            files.Add(new MacFile
            {
                Name = entry.Name,
                FolderPath = [.. folders.Select(folder => folder.Name)],
                FinderInfo = new FinderInfo { Type = file.Type, Creator = file.Creator, Flags = file.FinderFlags },
                Created = file.Created == 0 ? null : new MacDate(file.Created),
                Modified = file.Modified == 0 ? null : new MacDate(file.Modified),
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
            });
        }
        return files;
    }

    // The archive a last segment ends: the segments before it are the siblings with the same set id and a zero
    // directory offset. Without all of them, the archive is the segments up to the first missing one.
    private static SegmentSet JoinSegments(byte[] lastSegment, byte segmentCount, ushort setId, ContainerContext context)
    {
        if (segmentCount <= 1)
        {
            return new SegmentSet(lastSegment, true);
        }

        var segments = new byte[]?[segmentCount];
        segments[segmentCount - 1] = lastSegment;
        long totalBytes = lastSegment.Length;
        foreach (MacFile sibling in context.Siblings?.Invoke() ?? [])
        {
            ForkData fork = sibling.DataFork;
            if (fork.Length < ArchiveHeaderLength)
            {
                continue;
            }

            var header = new BigEndianReader(fork.Slice(0, ArchiveHeaderLength).ToArray(ArchiveHeaderLength));
            byte number = header.ReadByteAt(1);
            if (header.ReadByteAt(0) != 1 || number == 0 || number >= segmentCount ||
                header.ReadUInt16At(2) != setId || header.ReadUInt32At(4) != 0)
            {
                continue;
            }

            if (segments[number - 1] is not null)
            {
                throw new InvalidDataException($"More than one Compact Pro sibling is segment {number} of the set.");
            }

            if (fork.Length > context.Options.MaxExpandedBytesPerInput - totalBytes)
            {
                throw new InvalidDataException("The Compact Pro segment set exceeds the configured input-size limit.");
            }

            segments[number - 1] = fork.ToArray(context.Options.MaxExpandedBytesPerInput);
            totalBytes += fork.Length;
        }

        int present = Array.IndexOf(segments, null);
        bool complete = present < 0;
        if (!complete)
        {
            string missing = string.Join(", ", Enumerable.Range(1, segmentCount).Where(n => segments[n - 1] is null));
            context.Report(DiagnosticSeverity.Error, "archive.missing-volume",
                $"Compact Pro segment(s) {missing} of {segmentCount} are missing; open the last segment with the others " +
                "beside it.");
            if (present == 0)
            {
                return new SegmentSet([], false);
            }
        }
        int count = complete ? segmentCount : present;
        IEnumerable<byte[]> parts = segments.Take(count).Select(segment => segment!);
        byte[] joined = new byte[parts.Sum(segment => (long)segment.Length) - (count - 1L) * ArchiveHeaderLength];
        int position = 0;
        foreach (byte[] segment in parts)
        {
            ReadOnlySpan<byte> bytes = position == 0 ? segment : segment.AsSpan(ArchiveHeaderLength);
            bytes.CopyTo(joined.AsSpan(position));
            position += bytes.Length;
        }
        return new SegmentSet(joined, complete);
    }

    // The directory at the reader's position, to the end of the archive. baseOffset is where the reader's data starts
    // in the archive (0 when it holds the whole archive), so the entries' offsets are from the archive's start.
    private static CompactProDirectory ReadDirectory(BigEndianReader directory, int baseOffset)
    {
        uint storedCrc = directory.ReadUInt32();
        uint crc = uint.MaxValue;
        ushort entryCount = ReadUInt16(directory, ref crc);
        int commentLength = ReadByte(directory, ref crc);
        MacString comment = new(ReadBlock(directory, commentLength, ref crc).Source.Span);
        var entries = new List<CompactProEntry>(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            int nameType = ReadByte(directory, ref crc);
            int nameLength = nameType & 0x7F;
            if (nameLength == 0)
            {
                throw new InvalidDataException("A Compact Pro entry has an empty name.");
            }

            var name = new MacString(ReadBlock(directory, nameLength, ref crc).Source.Span);
            if ((nameType & 0x80) != 0)
            {
                ushort children = ReadUInt16(directory, ref crc);
                entries.Add(new CompactProEntry(name, children, null, checked(baseOffset + directory.Position - 2)));
            }
            else
            {
                var metadata = ReadBlock(directory, 45, ref crc);
                var file = new CompactProFile(metadata.ReadByte(), metadata.ReadUInt32(), metadata.ReadFourCC(),
                    metadata.ReadFourCC(), metadata.ReadUInt32(), metadata.ReadUInt32(), (FinderFlags)metadata.ReadUInt16(),
                    metadata.ReadUInt32(), metadata.ReadUInt16(), metadata.ReadUInt32(), metadata.ReadUInt32(),
                    metadata.ReadUInt32(), metadata.ReadUInt32());
                entries.Add(new CompactProEntry(name, 0, file, checked(baseOffset + directory.Position - 45)));
            }
        }
        return new CompactProDirectory(storedCrc, crc, checked(baseOffset + directory.Position), comment, entries);
    }

    // [Reference: pmarreck/compact_pro, fixed against real archives; munbox samples] The 0x81 escape is
    // "half-escaped" after 81 81: the second 0x81 is emitted and also starts a new escape, so 81 81 82 n repeats
    // 0x81 and 81 81 81 82 05 is six 0x81. 81 82 00 is 81 82, 81 82 n (n >= 1) adds n - 1 copies of the last byte
    // (81 82 01 adds none), and 81 x is 81 x. The LZH stage feeds the same state machine.
    internal static byte[] DecodeRle8182(ReadOnlySpan<byte> input, int outputLength)
    {
        var output = new CompactProLzhDecoder.RleOutput(outputLength);
        foreach (byte value in input)
        {
            if (output.IsComplete)
            {
                break;
            }

            output.Write(value);
        }
        if (!output.IsComplete)
        {
            throw new InvalidDataException($"Compact Pro RLE produced {output.Written} of {outputLength} declared bytes.");
        }

        return output.Result;
    }

    private static ushort ReadUInt16(BigEndianReader directory, ref uint crc)
    {
        var word = directory.ReadSubReader(2);
        crc = UpdateCrc(crc, word.Source.Span);
        return word.ReadUInt16();
    }

    private static int ReadByte(BigEndianReader directory, ref uint crc)
    {
        if (!directory.TryReadByte(out byte value))
        {
            throw new InvalidDataException("The Compact Pro directory is truncated.");
        }

        crc = UpdateCrcByte(crc, value);
        return value;
    }

    // The next length bytes as a reader of their own, counted in the CRC.
    private static BigEndianReader ReadBlock(BigEndianReader directory, int length, ref uint crc)
    {
        if (length < 0 || length > directory.Remaining)
        {
            throw new InvalidDataException("A Compact Pro directory entry extends past the archive.");
        }

        var block = directory.ReadSubReader(length);
        crc = UpdateCrc(crc, block.Source.Span);
        return block;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc = UpdateCrcByte(crc, value);
        }

        return crc;
    }

    private static uint UpdateCrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }

        return crc;
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        ~UpdateCrc(UpdateCrc(uint.MaxValue, first), second);

    private static int ReadLength(uint value, string what)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"A Compact Pro {what} exceeds the supported size.");
        }

        return (int)value;
    }

    // The whole archive, or the leading segments joined when a later one is missing (Complete false).
    private sealed record SegmentSet(byte[] Archive, bool Complete);
    private sealed record CompactProDirectory(uint StoredCrc, uint ComputedCrc, int TableEnd, MacString Comment,
        List<CompactProEntry> Entries);
    private sealed record CompactProEntry(MacString Name, int ChildEntryCount, CompactProFile? File, int Offset)
    {
        public bool IsDirectory => File is null;
    }

    // A file entry's 45 bytes of metadata.
    private sealed record CompactProFile(byte Volume, uint Offset, FourCC Type, FourCC Creator, uint Created, uint Modified,
        FinderFlags FinderFlags, uint Crc, ushort Flags, uint ResourceLength, uint DataLength, uint ResourceCompressedLength,
        uint DataCompressedLength);
    private readonly record struct FolderScope(int LastEntry, MacString Name);
}
