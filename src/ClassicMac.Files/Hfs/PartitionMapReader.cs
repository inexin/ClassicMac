using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// Apple partition maps, from <i>Inside Macintosh: Devices</i> (SCSI Manager, "Partition Map"): a driver descriptor
    /// ('ER') in block 0 and one partition entry ('PM') per 512-byte block from block 1. Each HFS or MFS partition comes
    /// out as one file whose data fork is the volume, for the volume readers to open next; drivers and free space are
    /// skipped.
    /// </summary>
    public sealed class PartitionMapReader : IContainerReader
    {
        private const int Block = 512;
        private const ushort DriverSignature = 0x4552; // 'ER'
        private const ushort EntrySignature = 0x504D; // 'PM'

        /// <summary>The reader.</summary>
        public static PartitionMapReader Instance { get; } = new();

        private PartitionMapReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "Apple partition map";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            var start = input.ReadPrefix(2 * Block);
            return start.Length == 2 * Block
                && BinaryPrimitives.ReadUInt16BigEndian(start) == DriverSignature
                && BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(Block)) == EntrySignature;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException("Not an Apple partition map.");
            var first = input.Slice(Block, Block).ToArray();
            long entries = BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(4));
            var files = new List<MacFile>();
            for (long i = 0; i < entries; i++)
            {
                var at = (1 + i) * Block;
                if (at + Block > input.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "partition.map-truncated",
                        $"The map lists {entries} partitions but the image ends after {i}.", at);
                    break;
                }
                var entry = input.Slice(at, Block).ToArray();
                if (BinaryPrimitives.ReadUInt16BigEndian(entry) != EntrySignature)
                {
                    context.Report(DiagnosticSeverity.Error, "partition.bad-entry", $"Partition entry {i + 1} has no 'PM' signature.", at);
                    continue;
                }
                var name = CString(entry.AsSpan(16, 32));
                var type = Encoding.ASCII.GetString(CString(entry.AsSpan(48, 32)));
                if (type is not ("Apple_HFS" or "Apple_MFS"))
                {
                    context.Report(DiagnosticSeverity.Info, "partition.skipped", $"Partition {i + 1} ({type}) is not a Mac volume; skipped.", at);
                    continue;
                }

                // Physical start and size in 512-byte blocks; the data area may start later and be shorter.
                long start = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
                long count = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(12));
                long dataStart = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(80));
                long dataCount = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(84));
                var offset = (start + dataStart) * Block;
                var length = (dataCount > 0 ? dataCount : count - dataStart) * Block;
                if (offset >= input.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "partition.outside", $"Partition {i + 1} starts past the end of the image.", at);
                    continue;
                }
                if (offset + length > input.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "partition.truncated", $"Partition {i + 1} runs past the end of the image.", at);
                    length = input.Length - offset;
                }
                files.Add(new MacFile { Name = new MacString(name), DataFork = input.Slice(offset, length) });
            }
            return files;
        }

        private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> field)
        {
            var end = field.IndexOf((byte)0);
            return end < 0 ? field : field[..end];
        }
    }
}
