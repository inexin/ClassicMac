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
    /// NDIF disk images, as Disk Copy 6.3.3 reads them (disassembly of its <c>.HDI</c> driver and codecs, confirmed on
    /// images it made in SheepShaver): Read-Only, Read/Write, Read-Only Compressed (ADC), self-mounting <c>.smi</c>, and
    /// segmented images. The data fork holds the disk in chunks, mapped by the <c>bcem</c> 128 resource: a 128-byte
    /// header (version, volume name, disk blocks, max chunk size, data start, CRC-32, segmented flag, chunk count) and
    /// 12-byte entries (start block and type, offset from the data start, stored length) ending with type $FF; the old
    /// version 2 map has 8-byte entries. Chunks are zero-filled ($00), raw ($02), DART RLE ($81), DART LZH ($82) or ADC
    /// ($83); KenCode ($80) is recognised but not decoded yet. A segmented image (version 12) has parts in the same
    /// folder, each with a <c>bcm#</c> 128 (part number, count, image ID); Disk Copy finds them by that, typed
    /// <c>dseg</c>, never by name, and the map's offsets run across the parts' data forks back to back. The disk comes
    /// out as one file whose data fork is the volume, for the HFS or MFS reader to open next.
    /// </summary>
    public sealed class NdifReader : IContainerReader
    {
        private const int SectorSize = 512;
        private const int HeaderLength = 0x80, EntryLength = 12, OldEntryLength = 8;
        private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkKenCode = 0x80, ChunkDartRle = 0x81, ChunkDartLzh = 0x82,
            ChunkAdc = 0x83, ChunkEnd = 0xFF;
        private const uint MaxBlocks = 0x400000;
        private static readonly FourCC Bcem = FourCC.FromString("bcem"), Bcm = FourCC.FromString("bcm#"), Dseg = FourCC.FromString("dseg");

        /// <summary>The reader.</summary>
        public static NdifReader Instance { get; } = new();

        private NdifReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "NDIF (Disk Copy 6)";

        // The data fork alone never says it is NDIF.
        /// <inheritdoc/>
        public bool CanRead(ForkData input) => false;

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) =>
            throw new InvalidDataException("An NDIF image needs its resource fork.");

        // A bcem 128 of any version: those Disk Copy cannot use are reported by Read.
        /// <inheritdoc/>
        public bool CanRead(MacFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            return Resources(file) is { } fork && fork.Find(Bcem, 128) is { Length: >= 0x58 };
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(MacFile file, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(context);
            var fork = Resources(file) ?? throw new InvalidDataException("Not an NDIF image.");
            var map = fork.Find(Bcem, 128)?.GetData().ToArray() ?? throw new InvalidDataException("Not an NDIF image.");
            var header = Header.Read(map, context);

            var data = header.Segmented ? Segments(file, fork, context) : file.DataFork;
            var diskLength = (long)header.Blocks * SectorSize;
            if (diskLength > context.Options.MaxExpandedBytesPerInput)
            {
                throw new InvalidDataException(
                    $"The disk is {diskLength} bytes, over the {context.Options.MaxExpandedBytesPerInput}-byte limit.");
            }
            if (header.DataStart > data.Length)
                context.Report(DiagnosticSeverity.Error, "ndif.bad-map", $"The data starts at {header.DataStart}, past the {data.Length}-byte data fork.");

            var chunks = Chunks(map, header, data.Length, context);
            var disk = new ChunkedForkData(data, chunks, diskLength, context);
            if (context.Options.VerifyChecksums) VerifyChecksum(disk, header.Crc, context);
            return [new MacFile { Name = header.Name.Bytes.Length > 0 ? header.Name : file.Name, DataFork = disk }];
        }

        private static ResourceFork? Resources(MacFile file)
        {
            if (file.ResourceFork.Length is < 256 or > ResourceFork.MaxForkLength) return null;
            try
            {
                return ResourceFork.Read(file.ResourceFork.ToArray());
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private sealed record Header(int Version, MacString Name, uint Blocks, uint MaxChunk, uint DataStart, uint Crc,
            bool Segmented, int Count, int EntriesAt, int EntrySize)
        {
            // The checks Disk Copy's validator makes that stop it from mounting throw; the rest are reported.
            public static Header Read(byte[] map, ContainerContext context)
            {
                int version = BinaryPrimitives.ReadUInt16BigEndian(map);
                if (version > 12)
                    throw new InvalidDataException($"The image is NDIF version {version}, newer than Disk Copy 6.3.3 reads.");
                if (version is not (2 or 10 or 11 or 12))
                    throw new InvalidDataException($"The image's map has version {version}; Disk Copy calls it damaged.");
                var old = version == 2;
                var count = (int)BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(old ? 0x54 : 0x7C));
                var entriesAt = old ? 0x58 : HeaderLength;
                var entrySize = old ? OldEntryLength : EntryLength;
                if (count < 2) throw new InvalidDataException($"The map lists {count} chunks; Disk Copy calls it damaged.");
                var room = (map.Length - entriesAt) / entrySize;
                if (!old && map.Length != entriesAt + count * entrySize)
                {
                    // Disk Copy 6.0 wrote version 10 maps one entry longer or shorter than their count.
                    if (version == 10 && Math.Abs(room - count) == 1)
                        context.Report(DiagnosticSeverity.Warning, "ndif.map-size", "The map's length is one entry off its count (Disk Copy 6.0).");
                    else
                        context.Report(DiagnosticSeverity.Error, "ndif.map-size", $"The map holds {room} entries but counts {count}.");
                }
                count = Math.Min(count, room);
                if (count < 2) throw new InvalidDataException("The map holds fewer than two chunks.");

                var nameLength = map[4];
                if (nameLength > 63)
                    context.Report(DiagnosticSeverity.Error, "ndif.bad-name", "The volume name is longer than 63 bytes; cut.");
                var blocks = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x44));
                if (blocks == 0 || blocks >= MaxBlocks)
                    throw new InvalidDataException($"The disk is {blocks} blocks; Disk Copy calls it damaged.");
                var segmented = !old && BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x54)) != 0;
                if (segmented && version < 12)
                    throw new InvalidDataException("The map says segmented but its version is below 12; Disk Copy calls it damaged.");
                var crc = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x50));
                if (crc == 0xFFFFFFFF)
                    context.Report(DiagnosticSeverity.Warning, "ndif.crc-uninitialized", "The image's checksum was never set.");
                return new Header(version, new MacString(map.AsSpan(5, Math.Min(nameLength, (byte)63))), blocks,
                    old ? 0 : BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x48)),
                    old ? 0 : BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x4C)),
                    old ? 0 : crc, segmented, count, entriesAt, entrySize);
            }
        }

        private sealed record Chunk(long Start, long End, byte Type, long Offset, long Stored);

        // The entries, checked as Disk Copy's validator checks them. What it refuses is reported as an error and read
        // as zeros, so the rest of the disk stays readable.
        private static List<Chunk> Chunks(byte[] map, Header header, long dataLength, ContainerContext context)
        {
            var entries = new List<(long Start, byte Type, long Offset, long Stored)>();
            for (var k = 0; k < header.Count; k++)
            {
                var e = map.AsSpan(header.EntriesAt + k * header.EntrySize, header.EntrySize);
                var word = BinaryPrimitives.ReadUInt32BigEndian(e);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
                long stored = header.EntrySize == EntryLength ? BinaryPrimitives.ReadUInt32BigEndian(e[8..]) : 0;
                entries.Add((word >> 8, (byte)word, offset, stored));
            }
            if (header.EntrySize == OldEntryLength)
            {
                // Version 2: a chunk's length is the next one's offset less its own.
                for (var k = 0; k + 1 < entries.Count; k++) entries[k] = entries[k] with { Stored = entries[k + 1].Offset - entries[k].Offset };
            }

            var end = entries.FindIndex(e => e.Type == ChunkEnd);
            if (end < 0)
            {
                context.Report(DiagnosticSeverity.Warning, "ndif.no-end", "The chunk map has no end entry; the last chunk runs to the end of the disk.");
                end = entries.Count;
            }
            else if (entries[end].Start != header.Blocks)
            {
                context.Report(DiagnosticSeverity.Error, "ndif.bad-map",
                    $"The map ends at block {entries[end].Start}, not at the disk's {header.Blocks}.");
            }
            if (entries.Count > 0 && entries[0].Start != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "ndif.gap",
                    $"The first chunk starts at block {entries[0].Start}; the blocks before it read as zeros (Disk Copy reads garbage).");
            }

            var chunks = new List<Chunk>();
            for (var k = 0; k < end; k++)
            {
                var (start, type, offset, stored) = entries[k];
                var next = k + 1 < entries.Count ? Math.Min(entries[k + 1].Start, header.Blocks) : header.Blocks;
                if (next <= start || start >= header.Blocks)
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.bad-map",
                        $"Chunk {k} covers blocks {start}–{next}, out of order or past the disk's {header.Blocks}; the map is read up to it.");
                    break;
                }
                var size = (next - start) * SectorSize;
                var where = $"Chunk {k} (blocks {start}–{next})";
                switch (type)
                {
                    case ChunkZero:
                        if (stored != 0) context.Report(DiagnosticSeverity.Warning, "ndif.zero-length", $"{where} is zero-filled but stores {stored} bytes.");
                        break;
                    case ChunkRaw:
                        if (stored < size) context.Report(DiagnosticSeverity.Error, "ndif.short", $"{where} stores {stored} of its {size} bytes; the rest reads as zeros.");
                        break;
                    case ChunkAdc or ChunkDartRle or ChunkDartLzh:
                        if (type == ChunkAdc && header.Version < 11)
                            context.Report(DiagnosticSeverity.Error, "ndif.bad-map", $"{where} is ADC-compressed, which needs map version 11.");
                        if (header.MaxChunk != 0 && next - start > header.MaxChunk)
                            context.Report(DiagnosticSeverity.Warning, "ndif.chunk-size", $"{where} is larger than the map's largest chunk ({header.MaxChunk} blocks).");
                        break;
                    case ChunkKenCode:
                        context.Report(DiagnosticSeverity.Error, "ndif.unsupported-chunk",
                            $"{where} is KenCode-compressed, which is not read yet; it reads as zeros.");
                        type = ChunkZero;
                        break;
                    default:
                        context.Report(DiagnosticSeverity.Error, "ndif.unknown-chunk",
                            $"{where} has type ${type:X2}, which Disk Copy does not recognise; it reads as zeros.");
                        type = ChunkZero;
                        break;
                }
                var absolute = header.DataStart + offset;
                if (type != ChunkZero && absolute + stored > dataLength)
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.short", $"{where} lies past the end of the image's data; what is there is read.");
                    stored = Math.Max(0, dataLength - absolute);
                }
                chunks.Add(new Chunk(start * SectorSize, next * SectorSize, type, absolute, stored));
            }
            return chunks;
        }

        // A segmented image's data: the parts' data forks back to back, in part order. Disk Copy looks in the image's
        // folder for files typed 'dseg' whose bcm# 128 has the same image ID and part count; names do not matter.
        private static ForkData Segments(MacFile file, ResourceFork fork, ContainerContext context)
        {
            var master = fork.Find(Bcm, 128)?.GetData().ToArray();
            if (master is null || master.Length < 20)
            {
                context.Report(DiagnosticSeverity.Error, "ndif.missing-segment", "The image is segmented but has no bcm# 128; only its own data is read.");
                return file.DataFork;
            }
            int count = BinaryPrimitives.ReadUInt16BigEndian(master.AsSpan(2));
            var id = master.AsSpan(4, 16).ToArray();
            var parts = new ForkData?[count + 1];
            parts[BinaryPrimitives.ReadUInt16BigEndian(master)] = file.DataFork;
            foreach (var sibling in context.Siblings?.Invoke() ?? [])
            {
                if (sibling.FinderInfo.Type != Dseg || Resources(sibling)?.Find(Bcm, 128)?.GetData().ToArray() is not { Length: >= 20 } part) continue;
                int number = BinaryPrimitives.ReadUInt16BigEndian(part);
                if (BinaryPrimitives.ReadUInt16BigEndian(part.AsSpan(2)) == count && part.AsSpan(4, 16).SequenceEqual(id)
                    && number is >= 1 && number <= count && parts[number] is null)
                    parts[number] = sibling.DataFork;
                if (parts.Skip(1).All(p => p is not null)) break;
            }

            var found = new List<ForkData>();
            for (var n = 1; n <= count; n++)
            {
                if (parts[n] is not { } p)
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.missing-segment",
                        $"Part {n} of {count} is not in the image's folder (Disk Copy: \"not all parts could be found\"); the disk is read up to it.");
                    break;
                }
                if (n < count && p.Length % SectorSize != 0)
                    context.Report(DiagnosticSeverity.Error, "ndif.bad-segment", $"Part {n} of {count} is not a whole number of blocks.");
                found.Add(p);
            }
            return new ConcatForkData(found);
        }

        // Disk Copy's CRC-32 of the whole disk: the reflected table built with the normal polynomial $04C11DB7 in a
        // right-shifting loop, initial value $FFFFFFFF, no final xor (so not zlib's CRC-32). Zero means none stored.
        // Only Disk Copy's "Verify checksum" setting checks it; its driver never does.
        private static void VerifyChecksum(ForkData disk, uint stored, ContainerContext context)
        {
            if (stored == 0) return;
            var computed = Crc(disk);
            if (computed != stored)
                context.Report(DiagnosticSeverity.Error, "ndif.bad-checksum", $"The disk's checksum is ${computed:X8}, not the ${stored:X8} the image records.");
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
        {
            var c = (uint)i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0x04C11DB7 : c >> 1;
            return c;
        }).ToArray();

        internal static uint Crc(ForkData disk)
        {
            var crc = 0xFFFFFFFF;
            using var stream = disk.Open();
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                foreach (var b in buffer.AsSpan(0, read)) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        // Several forks read one after another (a segmented image's parts).
        private sealed class ConcatForkData(IReadOnlyList<ForkData> parts) : ForkData
        {
            public override long Length { get; } = parts.Sum(p => p.Length);

            public override Stream Open()
            {
                var memory = new MemoryStream();
                foreach (var part in parts)
                {
                    using var stream = part.Open();
                    stream.CopyTo(memory);
                }
                memory.Position = 0;
                return memory;
            }

            public override ForkData Slice(long offset, long length)
            {
                // A range inside one part is a slice of that part; otherwise the generic slice.
                long start = 0;
                foreach (var part in parts)
                {
                    if (offset >= start && offset + length <= start + part.Length) return part.Slice(offset - start, length);
                    start += part.Length;
                }
                return base.Slice(offset, length);
            }
        }

        // The disk: each chunk decoded when first read (the last one kept, as Disk Copy's driver does), zeros where the
        // map says so. A chunk that fails to decode is reported once and reads as far as it decoded, then zeros.
        private sealed class ChunkedForkData(ForkData data, List<Chunk> chunks, long length, ContainerContext context) : ForkData
        {
            private readonly List<Chunk> chunks = chunks;
            private readonly long length = length;
            private readonly HashSet<int> reported = [];
            private int cachedIndex = -1;
            private byte[] cached = [];

            public override long Length => length;

            public override Stream Open() => new ChunkStream(this);

            private ReadOnlySpan<byte> Decoded(int index)
            {
                lock (reported)
                {
                    if (index == cachedIndex) return cached;
                    var chunk = chunks[index];
                    var bytes = new byte[chunk.End - chunk.Start];
                    if (chunk.Type != ChunkZero && chunk.Stored > 0)
                    {
                        var stored = data.Slice(chunk.Offset, chunk.Stored).ToArray();
                        string? problem = null;
                        switch (chunk.Type)
                        {
                            case ChunkRaw:
                                stored.AsSpan(0, (int)Math.Min(stored.Length, bytes.Length)).CopyTo(bytes);
                                break;
                            case ChunkAdc:
                                var result = Adc.Decompress(stored, bytes, out var adcWritten);
                                if (result != Adc.Result.Done) problem = $"decodes to {adcWritten} of its {bytes.Length} bytes ({result})";
                                break;
                            case ChunkDartRle:
                                if (!DartRle.Decompress(stored, bytes, out var rleWritten))
                                    problem = $"decodes to {rleWritten} of its {bytes.Length} bytes";
                                break;
                            case ChunkDartLzh:
                                // Each chunk starts with a clear window tail; Disk Copy's driver carries it over from the
                                // chunk it read last, which only matters when chunks are read out of order.
                                var lzhWritten = new DartLzh().Decode(stored, bytes);
                                if (lzhWritten < bytes.Length - 1) problem = $"decodes to {lzhWritten} of its {bytes.Length} bytes";
                                break;
                        }
                        if (problem is not null && reported.Add(index))
                        {
                            context.Report(DiagnosticSeverity.Error, "ndif.bad-chunk",
                                $"The compressed chunk at block {chunk.Start / SectorSize} {problem}; Disk Copy calls it damaged. The rest reads as zeros.");
                        }
                    }
                    cachedIndex = index;
                    cached = bytes;
                    return cached;
                }
            }

            private sealed class ChunkStream(ChunkedForkData fork) : Stream
            {
                private long position;

                public override bool CanRead => true;
                public override bool CanSeek => true;
                public override bool CanWrite => false;
                public override long Length => fork.length;

                public override long Position
                {
                    get => position;
                    set => position = value >= 0 ? value : throw new IOException("Cannot seek before the start of the stream.");
                }

                public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

                public override int Read(Span<byte> buffer)
                {
                    var total = 0;
                    while (buffer.Length > 0 && position < fork.length)
                    {
                        var index = fork.chunks.FindLastIndex(c => c.Start <= position);
                        int take;
                        if (index < 0 || position >= fork.chunks[index].End)
                        {
                            // Blocks no chunk covers read as zeros.
                            var nextStart = index + 1 < fork.chunks.Count ? fork.chunks[index + 1].Start : fork.length;
                            take = (int)Math.Min(buffer.Length, nextStart - position);
                            buffer[..take].Clear();
                        }
                        else
                        {
                            var chunk = fork.chunks[index];
                            var bytes = fork.Decoded(index);
                            var within = (int)(position - chunk.Start);
                            take = Math.Min(buffer.Length, bytes.Length - within);
                            bytes.Slice(within, take).CopyTo(buffer);
                        }
                        buffer = buffer[take..];
                        position += take;
                        total += take;
                    }
                    return total;
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    Position = origin switch
                    {
                        SeekOrigin.Begin => offset,
                        SeekOrigin.Current => position + offset,
                        SeekOrigin.End => fork.length + offset,
                        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                    };
                    return position;
                }

                public override void Flush()
                {
                }

                public override void SetLength(long value) => throw new NotSupportedException();

                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }
        }
    }
}
