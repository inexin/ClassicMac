using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// MFS volumes (the 1984 Macintosh File System, 400K floppies), from <i>Inside Macintosh II</i>, the File Manager:
    /// the volume information at byte 1024 followed by a 12-bit allocation block map (blocks numbered from 2; 1 marks
    /// a fork's last block), and a flat file directory. MFS has no real folders (the Finder kept them), so the files
    /// have no folder path.
    /// </summary>
    public sealed class MfsReader : IContainerReader
    {
        private const int InfoOffset = 1024;
        private const int MapOffset = InfoOffset + 64;
        private const ushort Signature = 0xD2D7;
        private const int BlockSize = 512;

        /// <summary>The reader.</summary>
        public static MfsReader Instance { get; } = new();

        private MfsReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "MFS volume";

        /// <inheritdoc/>
        public bool CanRead(ForkData input) =>
            input.Length >= MapOffset && BinaryPrimitives.ReadUInt16BigEndian(input.Slice(InfoOffset, 2).ToArray()) == Signature;

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException("Not an MFS volume.");
            var info = input.Slice(InfoOffset, 64).ToArray();
            int directoryStart = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0x0E));
            int directoryBlocks = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0x10));
            int blocks = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0x12));
            long blockSize = BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(0x14));
            long firstBlock = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0x1C)) * (long)BlockSize;
            int fileCount = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(0x0C));
            if (blockSize == 0 || blockSize % 512 != 0)
                throw new InvalidDataException($"The allocation block size {blockSize} is not a multiple of 512.");

            var mapLength = (blocks * 3 + 1) / 2;
            if (MapOffset + mapLength > input.Length) throw new InvalidDataException("The allocation block map lies outside the volume.");
            var map = input.Slice(MapOffset, mapLength).ToArray();

            var directoryEnd = (long)(directoryStart + directoryBlocks) * BlockSize;
            if (directoryEnd > input.Length)
            {
                context.Report(DiagnosticSeverity.Error, "mfs.directory-truncated", "The file directory runs past the end of the volume.");
                directoryEnd = input.Length / BlockSize * BlockSize;
            }
            var directory = input.Slice((long)directoryStart * BlockSize, Math.Max(0, directoryEnd - (long)directoryStart * BlockSize)).ToArray();

            var files = new List<MacFile>();
            for (var block = 0; block * BlockSize < directory.Length; block++)
            {
                var at = block * BlockSize;
                var end = at + BlockSize;
                // Entries never cross a block. As the File Manager scans (disassembly of the ROM's MFS code): an entry is
                // any nonzero flags byte, a zero one ends the block, and no entry starts at block offset 460 or later.
                while (at - block * BlockSize < 460 && at + 51 <= end && directory[at] != 0)
                {
                    var e = directory.AsSpan(at);
                    int nameLength = e[50];
                    if (at + 51 + nameLength > end)
                    {
                        context.Report(DiagnosticSeverity.Error, "mfs.bad-entry", $"A directory entry in block {directoryStart + block} runs past it.");
                        break;
                    }
                    var name = new MacString(e.Slice(51, nameLength));
                    var label = $"\"{name}\"";
                    files.Add(new MacFile
                    {
                        Name = name,
                        FinderInfo = FinderInfo.Read(e.Slice(2, 16)),
                        Created = Date(BinaryPrimitives.ReadUInt32BigEndian(e[42..])),
                        Modified = Date(BinaryPrimitives.ReadUInt32BigEndian(e[46..])),
                        DataFork = Fork(BinaryPrimitives.ReadUInt16BigEndian(e[22..]), BinaryPrimitives.ReadUInt32BigEndian(e[24..]), $"{label}'s data fork"),
                        ResourceFork = Fork(BinaryPrimitives.ReadUInt16BigEndian(e[32..]), BinaryPrimitives.ReadUInt32BigEndian(e[34..]), $"{label}'s resource fork"),
                    });
                    if (files.Count > context.Options.MaxVolumeEntries)
                    {
                        context.Report(DiagnosticSeverity.Error, "mfs.too-many-entries",
                            $"The directory holds more than {context.Options.MaxVolumeEntries} files; reading stopped.");
                        return files;
                    }
                    at += 51 + nameLength + ((51 + nameLength) & 1);
                }
            }
            if (files.Count != fileCount)
            {
                context.Report(DiagnosticSeverity.Info, "mfs.counts",
                    $"The directory holds {files.Count} files; the volume information says {fileCount}.");
            }
            return files;

            // A fork: its chain of allocation blocks through the map, merged into ranges, cut to its logical length.
            ForkData Fork(int start, long length, string what)
            {
                if (length == 0) return ForkData.Empty;
                var ranges = new List<(long Offset, long Length)>();
                var seen = new HashSet<int>();
                long covered = 0;
                for (var b = start; covered < length;)
                {
                    if (b < 2 || b >= blocks + 2 || !seen.Add(b))
                    {
                        context.Report(DiagnosticSeverity.Error, "mfs.bad-chain",
                            $"The block chain of {what} reaches block {b}, outside the volume or already used; the rest is missing.");
                        break;
                    }
                    var offset = firstBlock + (b - 2) * blockSize;
                    if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == offset)
                        ranges[^1] = (ranges[^1].Offset, ranges[^1].Length + blockSize);
                    else
                        ranges.Add((offset, blockSize));
                    covered += blockSize;
                    var next = Entry(b);
                    if (next == 1) break;
                    if (next == 0)
                    {
                        context.Report(DiagnosticSeverity.Error, "mfs.bad-chain", $"The block chain of {what} runs into a free block.");
                        break;
                    }
                    b = next;
                }
                var inImage = new List<(long, long)>();
                long available = 0;
                foreach (var (offset, count) in ranges)
                {
                    var take = Math.Clamp(input.Length - offset, 0, count);
                    if (take > 0) inImage.Add((offset, take));
                    available += take;
                    if (take < count) break;
                }
                if (available < length)
                {
                    context.Report(DiagnosticSeverity.Error, "mfs.fork-short",
                        $"{what} has {available} of its {length} bytes on the volume; the rest is missing.");
                    length = available;
                }
                return new ExtentForkData(input, inImage, length);
            }

            // The 12-bit map entry for allocation block b (numbered from 2).
            int Entry(int b)
            {
                var i = b - 2;
                var at = i * 3 / 2;
                return (i & 1) == 0 ? map[at] << 4 | map[at + 1] >> 4 : (map[at] & 0x0F) << 8 | map[at + 1];
            }
        }

        private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
    }
}
