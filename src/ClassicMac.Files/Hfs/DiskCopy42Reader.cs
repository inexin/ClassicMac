using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// Disk Copy 4.2 images, from Apple's File Type Note for $E0/$0005: an 84-byte header (disk name, data and tag
    /// sizes, their checksums, disk format, format byte, $0100), the disk's blocks, then 12 tag bytes per block. The
    /// disk comes out as one file whose data fork is the volume, for the HFS or MFS reader to open next.
    /// </summary>
    public sealed class DiskCopy42Reader : IContainerReader
    {
        private const int HeaderLength = 84;
        private const ushort Private = 0x0100;

        /// <summary>The reader.</summary>
        public static DiskCopy42Reader Instance { get; } = new();

        private DiskCopy42Reader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "DiskCopy 4.2";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            var header = input.ReadPrefix(HeaderLength);
            if (header.Length < HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(82)) != Private) return false;
            if (header[0] > 63) return false;
            long dataSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(64));
            long tagSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(68));
            // Fitted checks on top of the note's $0100: whole blocks, 12 tag bytes per block or none.
            return dataSize > 0 && dataSize % 512 == 0 && (tagSize == 0 || tagSize == dataSize / 512 * 12);
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException("Not a Disk Copy 4.2 image.");
            var header = input.ReadPrefix(HeaderLength);
            long dataSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(64));
            long tagSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(68));
            var available = input.Length - HeaderLength;
            if (dataSize > available)
            {
                context.Report(DiagnosticSeverity.Error, "diskcopy.truncated",
                    $"The image holds {available} of its {dataSize} data bytes; the rest of the disk is missing.");
                dataSize = available / 512 * 512;
            }
            else if (tagSize > available - dataSize)
            {
                context.Report(DiagnosticSeverity.Warning, "diskcopy.tags-truncated", "The tag bytes after the disk are cut short.");
                tagSize = 0;
            }

            var disk = input.Slice(HeaderLength, dataSize);
            if (dataSize == BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(64)))
            {
                CheckSum(disk, 0, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(72)), "data", context);
                if (tagSize > 0)
                    CheckSum(input.Slice(HeaderLength + dataSize, tagSize), 12, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(76)), "tag", context);
            }
            return [new MacFile { Name = new MacString(header.AsSpan(1, header[0])), DataFork = disk }];
        }

        // The note's checksum: add each big-endian 16-bit word, then rotate the 32-bit sum right by one. The tag
        // checksum skips the first 12 bytes (the first block's tags), as Disk Copy does.
        private static void CheckSum(ForkData data, int skip, uint stored, string what, ContainerContext context)
        {
            using var stream = data.Open();
            var buffer = new byte[65536];
            uint sum = 0;
            long position = 0;
            int read;
            while ((read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                for (var i = 0; i + 1 < read; i += 2)
                {
                    if (position + i >= skip)
                    {
                        sum += BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(i));
                        sum = sum >> 1 | sum << 31;
                    }
                }
                position += read;
            }
            if (sum != stored)
            {
                context.Report(DiagnosticSeverity.Warning, "diskcopy.checksum",
                    $"The {what} checksum is ${stored:X8} but the image gives ${sum:X8}.");
            }
        }
    }
}
