using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// Apple partition maps, from <i>Inside Macintosh: Devices</i> (SCSI Manager, "Partition Map"), read as the Mac OS 9
    /// CD driver reads them (disassembly): block 0 holds a driver descriptor ('ER') or starts with a zero word, and the
    /// partition entries ('PM') follow at a stride found by probing: 512 bytes, else 2048 (CDs mastered with 2048-byte
    /// blocks). Every block number in an entry is in units of that stride; the driver descriptor's block size is never
    /// used. Each HFS or MFS partition comes out as one file whose data fork is the volume, for the volume readers to
    /// open next; drivers and free space are skipped.
    /// </summary>
    public sealed class PartitionMapReader : IContainerReader
    {
        private const int Block = 512, CdBlock = 2048;
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
        public bool CanRead(ForkData input) => Stride(input) > 0;

        // The entry stride: 'PM' at byte 512, else at byte 2048; 0 when there is no map. Block 0 must start with 'ER' or
        // a zero word; nothing else in it is read.
        private static int Stride(ForkData input)
        {
            var start = input.ReadPrefix(CdBlock + 2);
            if (start.Length < Block + 2)
            {
                return 0;
            }

            var reader = new BigEndianReader(start);
            if (reader.ReadUInt16At(0) is not (DriverSignature or 0))
            {
                return 0;
            }

            if (reader.ReadUInt16At(Block) == EntrySignature)
            {
                return Block;
            }

            if (start.Length == CdBlock + 2 && reader.ReadUInt16At(CdBlock) == EntrySignature)
            {
                return CdBlock;
            }

            return 0;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var stride = Stride(input);
            if (stride == 0)
            {
                throw new InvalidDataException("Not an Apple partition map.");
            }

            var first = input.Slice(stride, Block).ToArray();
            long entries = new BigEndianReader(first).ReadUInt32At(4);
            var files = new List<MacFile>();
            for (long i = 0; i < entries; i++)
            {
                var at = (1 + i) * stride;
                if (at + Block > input.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "partition.map-truncated",
                        $"The map lists {entries} partitions but the image ends after {i}.", at);
                    break;
                }
                var entry = input.Slice(at, Block).ToArray();
                var reader = new BigEndianReader(entry);
                if (reader.ReadUInt16At(0) != EntrySignature)
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

                // Physical start and size, and where the data starts in the partition, in units of the stride. The
                // size comes from the partition's block count: no Mac mounting code reads pmDataCnt.
                long start = reader.ReadUInt32At(8);
                long count = reader.ReadUInt32At(12);
                long dataStart = reader.ReadUInt32At(80);
                var offset = (start + dataStart) * stride;
                var length = Math.Max(0, count - dataStart) * stride;
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
