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
    /// NDIF disk images, as Disk Copy 6 writes them (Read-Only, Read-Only Compressed, self-mounting <c>.smi</c>, and
    /// segmented images). The data fork holds the disk in chunks; the <c>bcem</c> 128 resource maps them: a 128-byte
    /// header (version, volume name, sector count, checksum, …) and 12-byte entries (start sector and type, data-fork
    /// offset, stored length) ending with type $FF. Chunks are raw ($02), ADC-compressed ($83) or zero-filled ($00). A
    /// segmented image's parts are files named <c>… 1of4</c>, <c>… 2of4</c>, … in the same folder; only part 1 has the
    /// map, whose offsets run across all parts' data forks, and every part has a <c>bcm#</c> 128. The disk comes out as
    /// one file whose data fork is the volume, for the HFS or MFS reader to open next. Layout fitted to images Disk Copy
    /// 6.3.3 made in SheepShaver (each decodes to its source sectors); the checksum is not verified yet.
    /// </summary>
    public sealed class NdifReader : IContainerReader
    {
        private const int SectorSize = 512;
        private const int HeaderLength = 0x80, EntryLength = 12;
        private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkAdc = 0x83, ChunkEnd = 0xFF;
        private static readonly FourCC Bcem = FourCC.FromString("bcem"), Bcm = FourCC.FromString("bcm#");

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

        /// <inheritdoc/>
        public bool CanRead(MacFile file)
        {
            ArgumentNullException.ThrowIfNull(file);
            return Map(file) is { } map && Header.TryRead(map, out _);
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(MacFile file, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(context);
            if (Map(file) is not { } map || !Header.TryRead(map, out var header))
                throw new InvalidDataException("Not an NDIF image.");

            var data = Segments(file, context) ?? file.DataFork;
            var diskLength = (long)header.Sectors * SectorSize;
            if (diskLength > context.Options.MaxExpandedBytesPerInput)
            {
                throw new InvalidDataException(
                    $"The disk is {diskLength} bytes, over the {context.Options.MaxExpandedBytesPerInput}-byte limit.");
            }

            var chunks = new List<Chunk>();
            for (var k = 0; k < header.Entries; k++)
            {
                var entry = map.AsSpan(HeaderLength + k * EntryLength, EntryLength);
                var word = BinaryPrimitives.ReadUInt32BigEndian(entry);
                var start = (long)(word >> 8);
                var type = (byte)word;
                if (type == ChunkEnd) break;
                if (k + 1 >= header.Entries)
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.no-end", "The chunk map has no end entry; its last chunk is ignored.");
                    break;
                }
                var next = (long)(BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(HeaderLength + (k + 1) * EntryLength)) >> 8);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                long stored = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                if (next <= start || next > header.Sectors || (chunks.Count > 0 && start != chunks[^1].End / SectorSize))
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.bad-map",
                        $"Chunk {k} covers sectors {start}–{next}, out of order or past the disk's {header.Sectors}; the map is read up to it.");
                    break;
                }
                if (type is not (ChunkZero or ChunkRaw or ChunkAdc))
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.unsupported-chunk",
                        $"Chunk {k} (sectors {start}–{next}) has type ${type:X2}, which is not read; it reads as zeros.");
                    type = ChunkZero;
                }
                if (type != ChunkZero && (offset + stored > data.Length || stored < 0))
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.short",
                        $"Chunk {k} lies past the end of the image's data; it reads as zeros.");
                    type = ChunkZero;
                }
                chunks.Add(new Chunk(start * SectorSize, next * SectorSize, type, offset, stored));
            }

            var disk = new ChunkedForkData(data, chunks, diskLength, context);
            return [new MacFile { Name = header.Name.Bytes.Length > 0 ? header.Name : file.Name, DataFork = disk }];
        }

        private static byte[]? Map(MacFile file)
        {
            if (file.ResourceFork.Length is < 256 or > 16 * 1024 * 1024) return null;
            try
            {
                var fork = ResourceFork.Read(file.ResourceFork.ToArray());
                return fork.Resources.FirstOrDefault(r => r.Type == Bcem && r.Id == 128)?.GetData().ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        // A segmented image's data: part 1's data fork followed by the other parts', found by name beside it (" 1of4"
        // → " 2of4" …). Null for an image in one piece.
        private static ForkData? Segments(MacFile file, ContainerContext context)
        {
            byte[]? part;
            try
            {
                part = ResourceFork.Read(file.ResourceFork.ToArray()).Resources.FirstOrDefault(r => r.Type == Bcm && r.Id == 128)?.GetData().ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
            if (part is null || part.Length < 4) return null;
            int number = BinaryPrimitives.ReadUInt16BigEndian(part), count = BinaryPrimitives.ReadUInt16BigEndian(part.AsSpan(2));
            if (count <= 1) return null;

            var name = file.Name.ToMacRoman();
            var suffix = $"{number}of{count}";
            if (number != 1 || !name.EndsWith(suffix, StringComparison.Ordinal))
            {
                context.Report(DiagnosticSeverity.Error, "ndif.segment-name",
                    $"\"{name}\" is part {number} of {count} but is not named \"… 1of{count}\"; its other parts cannot be found.");
                return null;
            }
            var stem = name[..^suffix.Length];
            var parts = new List<ForkData> { file.DataFork };
            for (var n = 2; n <= count; n++)
            {
                var sibling = context.Siblings?.Invoke(MacString.FromMacRoman($"{stem}{n}of{count}"));
                if (sibling is null)
                {
                    context.Report(DiagnosticSeverity.Error, "ndif.missing-segment",
                        $"Part {n} of {count} (\"{stem}{n}of{count}\") is not beside part 1; the disk is read without it.");
                    break;
                }
                parts.Add(sibling.DataFork);
            }
            return new ConcatForkData(parts);
        }

        private sealed record Header(MacString Name, uint Sectors, int Entries)
        {
            // Version 10 (Read-Only) or 11 (Read-Only Compressed) seen; a sector count and an entry count that fits.
            public static bool TryRead(byte[] map, out Header header)
            {
                header = null!;
                if (map.Length < HeaderLength + EntryLength) return false;
                var version = BinaryPrimitives.ReadUInt16BigEndian(map);
                var nameLength = Math.Min(map[4], (byte)63);
                var sectors = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x44));
                var entries = BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(0x7C));
                if (version is < 1 or > 0xFF || sectors == 0 || sectors > 0xFFFFFF) return false;
                if (entries == 0 || entries > (map.Length - HeaderLength) / EntryLength) return false;
                header = new Header(new MacString(map.AsSpan(5, nameLength)), sectors, (int)entries);
                return true;
            }
        }

        private sealed record Chunk(long Start, long End, byte Type, long Offset, long Stored);

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

        // The disk: each chunk decoded when first read (the last one kept), zeros where the map says so.
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
                    if (chunk.Type != ChunkZero)
                    {
                        var stored = data.Slice(chunk.Offset, chunk.Stored).ToArray();
                        if (chunk.Type == ChunkRaw)
                            stored.AsSpan(0, (int)Math.Min(stored.Length, bytes.Length)).CopyTo(bytes);
                        else if (Adc.Decompress(stored, bytes, out var written) is var result && (result != Adc.Result.Done || written < bytes.Length)
                            && reported.Add(index))
                        {
                            context.Report(DiagnosticSeverity.Error, "ndif.bad-adc",
                                $"The compressed chunk at sector {chunk.Start / SectorSize} decodes to {written} of its {bytes.Length} bytes ({result}); the rest reads as zeros.");
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
                            // Sectors no chunk covers read as zeros.
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
