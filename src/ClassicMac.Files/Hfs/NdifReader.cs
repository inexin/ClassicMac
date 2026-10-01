using System;
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
    /// images made by Disk Copy 6.1.2, 6.3.3 and 6.5b13 in SheepShaver): Read-Only, Read/Write, Read-Only Compressed
    /// (KenCode or ADC), self-mounting <c>.smi</c>, and segmented images. The data fork holds the disk in chunks, mapped
    /// by the <c>bcem</c> 128 resource: a 128-byte header (version 10, 11 for ADC, or 12 from Disk Copy 6.5; volume name,
    /// disk blocks, buffer size in blocks, data start, CRC-32 (hdiutil's "CRC28"), segmented flag, chunk count) and
    /// 12-byte entries (start block and type, offset from the data start, stored length) ending with type $FF. Chunks
    /// are zero-filled ($00), raw ($02), KenCode ($80, "Smaller (KC)"), DART RLE ($81), DART LZH ($82) or ADC ($83); a
    /// compressed image stores any chunk that does not shrink raw, so types mix. Version 2 (Disk Image Mounter 1.0 and
    /// Disk Copy 6.0.1) is read as Disk Copy 6.1.2's driver reads it (confirmed by mounting test images): the same header up
    /// to +$54, where the count (end entry included) stands, then 8-byte entries (start block and type, offset), each
    /// chunk's length running to the next offset. A segmented image (version 12) has parts in the same folder, each with a <c>bcm#</c> 128 (part number, count, image ID); Disk Copy finds them by that, typed
    /// <c>dseg</c>, never by name, and the map's offsets run across the parts' data forks back to back. The disk comes
    /// out as one file whose data fork is the volume, for the HFS or MFS reader to open next.
    /// </summary>
    public sealed class NdifReader : IContainerReader
    {
        private const int SectorSize = 512;
        private const int HeaderLength = 0x80, EntryLength = 12, OldHeaderLength = 0x58, OldEntryLength = 8;
        private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkKenCode = 0x80, ChunkDartRle = 0x81, ChunkDartLzh = 0x82,
            ChunkAdc = 0x83, ChunkShrinkWrap = 0xF0, ChunkEnd = 0xFF;
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
            var header = Header.Read(map, file, context);

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
            var disk = new ChunkedForkData(data, chunks, diskLength, Decode, (chunk, problem) => context.Report(DiagnosticSeverity.Error,
                "ndif.bad-chunk", $"The compressed chunk at block {chunk.Start / SectorSize} {problem}; Disk Copy calls it damaged. The rest reads as zeros."));
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

        // MaxChunk is the buffer size in blocks: the chunk size plus room for the largest compression overrun (0 when
        // nothing is compressed). The chunk size itself is the user's choice, recorded only by the entries.
        private sealed record Header(int Version, MacString Name, uint Blocks, uint MaxChunk, uint DataStart, uint Crc,
            bool Segmented, int Count, int EntriesAt, int EntrySize)
        {
            // The checks Disk Copy's validator makes that stop it from mounting throw; the rest are reported.
            public static Header Read(byte[] map, MacFile file, ContainerContext context)
            {
                // A map too short for its version word throws as a span read past its end does.
                if (map.Length < 2) throw new ArgumentOutOfRangeException(nameof(map));
                var reader = new BigEndianReader(map);
                int version = reader.ReadUInt16At(0);
                if (version > 12)
                    throw new InvalidDataException($"The image is NDIF version {version}, newer than Disk Copy 6.5 writes.");
                if (version is not (2 or 10 or 11 or 12))
                    throw new InvalidDataException($"The image's map has version {version}; Disk Copy calls it damaged.");
                var old = version == 2;
                var (entriesAt, entrySize) = old ? (OldHeaderLength, OldEntryLength) : (HeaderLength, EntryLength);
                if (map.Length < entriesAt) throw new InvalidDataException("The map is shorter than its header.");
                if (old)
                {
                    // No real image has been seen: the fields that would show another layout are reported with a request.
                    context.Report(DiagnosticSeverity.Info, "ndif.version-2",
                        $"A version 2 map (Disk Image Mounter 1.0 or Disk Copy 6.0.1), read as Disk Copy 6.1.2 reads it: type "
                        + $"'{file.FinderInfo.Type}', creator '{file.FinderInfo.Creator}', +$54 = ${reader.ReadUInt32At(0x54):X8}, "
                        + $"map {map.Length} bytes. No real image of this kind has been seen yet: please send it to the ClassicMac project.");
                }
                var count = (int)reader.ReadUInt32At(old ? 0x54 : 0x7C);
                if (count < 2) throw new InvalidDataException($"The map lists {count} chunks; Disk Copy calls it damaged.");
                var room = (map.Length - entriesAt) / entrySize;
                if (map.Length != entriesAt + count * entrySize)
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
                var blocks = reader.ReadUInt32At(0x44);
                if (blocks == 0 || blocks >= MaxBlocks)
                    throw new InvalidDataException($"The disk is {blocks} blocks; Disk Copy calls it damaged.");
                var segmented = !old && reader.ReadUInt32At(0x54) != 0;
                if (segmented && version < 12)
                    throw new InvalidDataException("The map says segmented but its version is below 12; Disk Copy calls it damaged.");
                var crc = reader.ReadUInt32At(0x50);
                if (crc == 0xFFFFFFFF)
                    context.Report(DiagnosticSeverity.Warning, "ndif.crc-uninitialized", "The image's checksum was never set.");
                return new Header(version, new MacString(map.AsSpan(5, Math.Min(nameLength, (byte)63))), blocks,
                    reader.ReadUInt32At(0x48), reader.ReadUInt32At(0x4C),
                    crc, segmented, count, entriesAt, entrySize);
            }
        }


        // The entries, checked as Disk Copy's validator checks them. What it refuses is reported as an error and read
        // as zeros, so the rest of the disk stays readable.
        private static List<DiskChunk> Chunks(byte[] map, Header header, long dataLength, ContainerContext context)
        {
            var entries = new List<(long Start, byte Type, long Offset, long Stored)>();
            for (var k = 0; k < header.Count; k++)
            {
                var e = new BigEndianReader(map.AsSpan(header.EntriesAt + k * header.EntrySize, header.EntrySize));
                var word = e.ReadUInt32();
                long offset = e.ReadUInt32();
                long stored = header.EntrySize == EntryLength ? e.ReadUInt32() : 0;
                entries.Add((word >> 8, (byte)word, offset, stored));
            }
            if (header.EntrySize == OldEntryLength)
            {
                // Version 2: a chunk's length runs to the next entry's offset (the end entry's offset is the data's end).
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

            var chunks = new List<DiskChunk>();
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
                    case ChunkAdc or ChunkDartRle or ChunkDartLzh or ChunkKenCode:
                        if (type == ChunkAdc && header.Version < 11)
                            context.Report(DiagnosticSeverity.Error, "ndif.bad-map", $"{where} is ADC-compressed, which needs map version 11.");
                        // Disk Copy 6.1.2 refuses a chunk over +$48 blocks; 6.3.3 and 6.5b13 clear that validator error at
                        // the next entry and fail only when the chunk overflows the decode buffer, +$48 blocks (doubled
                        // for version 10) (disassembly; checked in SheepShaver). The chunk still decodes here.
                        var buffer = header.MaxChunk * (header.Version == 10 ? 2L : 1L);
                        if (next - start > buffer)
                        {
                            context.Report(DiagnosticSeverity.Error, "ndif.chunk-size",
                                $"{where} is larger than Disk Copy's decode buffer ({buffer} blocks); it cannot read the image.");
                        }
                        else if (next - start > header.MaxChunk)
                        {
                            context.Report(DiagnosticSeverity.Warning, "ndif.chunk-size",
                                $"{where} covers more blocks than the map's buffer size ({header.MaxChunk}); Disk Copy 6.1.2 calls the image damaged, later versions read it.");
                        }
                        break;
                    case ChunkShrinkWrap:
                        // Named by Aaru as ShrinkWrap's (StuffIt) codec; not seen in any image yet.
                        context.Report(DiagnosticSeverity.Error, "ndif.unknown-chunk",
                            $"{where} has type $F0 (ShrinkWrap 3 compression), which ClassicMac does not decode; it reads as zeros.");
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
                var storage = type switch { ChunkZero => ChunkStorage.Zeros, ChunkRaw => ChunkStorage.Raw, _ => ChunkStorage.Compressed };
                chunks.Add(new DiskChunk(start * SectorSize, next * SectorSize, type, storage, absolute, stored));
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
            var reader = new BigEndianReader(master);
            int count = reader.ReadUInt16At(2);
            var id = master.AsSpan(4, 16).ToArray();
            var parts = new ForkData?[count + 1];
            parts[reader.ReadUInt16At(0)] = file.DataFork;
            foreach (var sibling in context.Siblings?.Invoke() ?? [])
            {
                if (sibling.FinderInfo.Type != Dseg || Resources(sibling)?.Find(Bcm, 128)?.GetData().ToArray() is not { Length: >= 20 } part) continue;
                var partReader = new BigEndianReader(part);
                int number = partReader.ReadUInt16At(0);
                if (partReader.ReadUInt16At(2) == count && part.AsSpan(4, 16).SequenceEqual(id)
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

        // A chunk's stored bytes decoded into its disk bytes: the problem to report, or null.
        private static string? Decode(DiskChunk chunk, byte[] stored, byte[] bytes)
        {
            switch (chunk.Type)
            {
                case ChunkAdc:
                    var result = Adc.Decompress(stored, bytes, out var adcWritten);
                    return result == Adc.Result.Done ? null : $"decodes to {adcWritten} of its {bytes.Length} bytes ({result})";
                case ChunkDartRle:
                    return DartRle.Decompress(stored, bytes, out var rleWritten) ? null : $"decodes to {rleWritten} of its {bytes.Length} bytes";
                case ChunkKenCode:
                    var kcResult = KenCode.Decompress(stored, bytes, out var kcWritten);
                    return kcResult == KenCode.Result.Done ? null : $"decodes to {kcWritten} of its {bytes.Length} bytes ({kcResult})";
                case ChunkDartLzh:
                    // Each chunk starts with a clear window. Disk Copy leaves the ring's last 60 bytes from the chunk
                    // decoded before, which changes nothing on real data; no Disk Copy writes $82 chunks.
                    var lzhWritten = new DartLzh().Decode(stored, bytes);
                    return lzhWritten < bytes.Length - 1 ? $"decodes to {lzhWritten} of its {bytes.Length} bytes" : null;
                default:
                    return null;
            }
        }
    }
}
