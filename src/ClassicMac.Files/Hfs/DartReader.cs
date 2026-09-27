using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Compression;
using ClassicMac.Resources;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// DART images (Apple's Disk Archive/Retrieval Tool, type <c>DMd1</c>–<c>DMd7</c>, creator <c>DART</c>), from the
    /// disassembly of Disk Copy 6.3.3, which reads them, confirmed on files DART 1.5.3 made. The data fork is a header
    /// — compression (0 RLE "fast", 1 LZH "best", 2 none), source type, size in KB, then one length per block (40, or 72
    /// for 1440K) — and the blocks: 40 sectors each, 20,480 data bytes then 480 tag bytes, compressed or stored (length
    /// −1). RLE lengths count words, LZH lengths bytes. The disk comes out as one file whose data fork is the volume, for
    /// the HFS or MFS reader to open next; tags are dropped, as Disk Copy drops them. <c>CKSM</c> 2 (data) and 1 (tags)
    /// are Disk Copy 4.2 sums, checked like a Disk Copy 4.2 image's.
    /// </summary>
    public sealed class DartReader : IContainerReader
    {
        private const int BlockSectors = 40, DataPerBlock = BlockSectors * 512, TagsPerBlock = BlockSectors * 12;
        private const int BlockLength = DataPerBlock + TagsPerBlock; // 20,960
        private const byte Rle = 0, Lzh = 1, Stored = 2;
        private static readonly FourCC Creator = FourCC.FromString("DART"), Cksm = FourCC.FromString("CKSM");

        /// <summary>The reader.</summary>
        public static DartReader Instance { get; } = new();

        private DartReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "DART";

        /// <inheritdoc/>
        public bool CanRead(ForkData input) => Header.TryRead(input, out var header) && header.Total == input.Length;

        // With Finder info the blocks may also stop short of the end of the data fork (Disk Copy only refuses more).
        /// <inheritdoc/>
        public bool CanRead(MacFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (!Header.TryRead(file.DataFork, out var header)) return false;
            var typed = file.FinderInfo.Creator == Creator || file.FinderInfo.Type.ToString().StartsWith("DMd", StringComparison.Ordinal);
            return header.Total == file.DataFork.Length || (typed && header.Total <= file.DataFork.Length);
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) =>
            Read(new MacFile { Name = context.HostName ?? MacString.FromMacRoman("DART"), DataFork = input }, context);

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(MacFile file, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(context);
            if (!Header.TryRead(file.DataFork, out var header)) throw new InvalidDataException("Not a DART image.");
            var raw = file.DataFork.ToArray(context.Options.MaxExpandedBytesPerInput);

            var blocks = header.Kilobytes * 2 / BlockSectors;
            var data = new byte[blocks * DataPerBlock];
            var tags = new byte[blocks * TagsPerBlock];
            var lzh = new DartLzh();
            var block = new byte[BlockLength];
            var at = header.Length;
            for (var i = 0; i < blocks; i++)
            {
                var length = header.Lengths[i];
                var stored = header.Compression == Stored || length == -1;
                var bytes = stored ? BlockLength : header.Compression == Rle ? length * 2 : length;
                var source = raw.AsSpan(at, bytes);
                at += bytes;
                Array.Clear(block);
                if (stored) source.CopyTo(block);
                else if (header.Compression == Rle)
                {
                    if (!DartRle.Decompress(source, block, out var written))
                        Report(context, $"RLE block {i} decodes to {written} of its {BlockLength} bytes; the rest reads as zeros.");
                }
                else
                {
                    // DART's own files can end a block a byte short (a tag byte): zeros, as Disk Copy leaves them.
                    var written = lzh.Decode(source, block);
                    if (written < DataPerBlock)
                        Report(context, $"LZH block {i} decodes to {written} of its {BlockLength} bytes; the rest reads as zeros.");
                }
                block.AsSpan(0, DataPerBlock).CopyTo(data.AsSpan(i * DataPerBlock));
                block.AsSpan(DataPerBlock, TagsPerBlock).CopyTo(tags.AsSpan(i * TagsPerBlock));
            }

            CheckSums(file, data, tags, context);
            var name = context.HostName ?? file.Name;
            return [new MacFile { Name = name, DataFork = ForkData.FromBytes(data) }];
        }

        private static void Report(ContainerContext context, string message) =>
            context.Report(DiagnosticSeverity.Error, "dart.bad-block", message);

        // CKSM 2 is the Disk Copy 4.2 sum of the data, CKSM 1 of all the tags (unlike a Disk Copy 4.2 header's tag sum,
        // which skips the first 12 bytes).
        private static void CheckSums(MacFile file, byte[] data, byte[] tags, ContainerContext context)
        {
            if (file.ResourceFork.Length == 0) return;
            ResourceFork fork;
            try
            {
                fork = ResourceFork.Read(file.ResourceFork.ToArray());
            }
            catch (InvalidDataException)
            {
                return;
            }
            foreach (var (id, bytes, what) in new[] { ((short)2, data, "data"), ((short)1, tags, "tag") })
            {
                if (fork.Find(Cksm, id)?.GetData() is not { Length: >= 4 } stored) continue;
                var expected = BinaryPrimitives.ReadUInt32BigEndian(stored.Span);
                var sum = DiskCopy42Reader.Sum(bytes);
                if (sum != expected)
                {
                    context.Report(DiagnosticSeverity.Warning, "dart.checksum",
                        $"The {what} checksum is ${expected:X8} but the image gives ${sum:X8}.");
                }
            }
        }

        private sealed record Header(byte Compression, byte SourceType, int Kilobytes, short[] Lengths, int Length, long Total)
        {
            // Compression 0–2, a floppy size DART knows, and block lengths that fit a block; Total is where the blocks end.
            public static bool TryRead(ForkData input, out Header header)
            {
                header = null!;
                var start = input.ReadPrefix(4 + 72 * 2);
                if (start.Length < 4) return false;
                var compression = start[0];
                int kilobytes = BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(2));
                if (compression > Stored || kilobytes is not (400 or 720 or 800 or 1440)) return false;
                var count = kilobytes == 1440 ? 72 : 40;
                var length = 4 + count * 2;
                if (start.Length < length) return false;
                var blocks = kilobytes * 2 / BlockSectors;
                var lengths = new short[count];
                long total = length;
                for (var i = 0; i < count; i++)
                {
                    lengths[i] = BinaryPrimitives.ReadInt16BigEndian(start.AsSpan(4 + i * 2));
                    if (i >= blocks) continue;
                    var l = lengths[i];
                    if (compression == Stored || l == -1) total += BlockLength;
                    else if (l <= 0 || l > BlockLength) return false; // Disk Copy's limit, on the value as stored
                    else total += compression == Rle ? l * 2 : l;
                }
                if (total > input.Length) return false;
                header = new Header(compression, start[1], kilobytes, lengths, length, total);
                return true;
            }
        }
    }
}
