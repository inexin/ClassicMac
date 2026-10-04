using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Checksums;
using ClassicMac.Files.Compression;
using ClassicMac.Resources;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// UDIF disk images (Disk Copy 6.4 and 6.5, and Mac OS X's <c>.dmg</c>): a 512-byte <c>koly</c> trailer at the end of
/// the data fork, and <c>mish</c> block tables, one per partition, mapping the device's sectors onto runs stored in
/// the data fork: zeros, raw, ADC, zlib or bzip2. Early images (Disk Copy 6.5b13, whose read-only, compressed and
/// "entire device" images decode to their source devices exactly) keep the tables as <c>blkx</c> resources in a
/// resource fork embedded in the data fork; later ones base64-encoded in an XML property list. The device comes out
/// as one file whose data fork is the disk (usually an Apple partition map), for the next readers. Checksums (CRC-32
/// or MD5, as each field says) are verified only on request. Encrypted and segmented images are recognised and
/// refused; LZFSE runs read as zeros with an error.
/// </summary>
public sealed class UdifReader : IContainerReader
{
    private const int Sector = 512, TrailerLength = 512, TableHeader = 0xCC, RunLength = 0x28;
    private const uint RunZero = 0, RunRaw = 1, RunFree = 2, RunAdc = 0x80000004, RunZlib = 0x80000005, RunBzip2 = 0x80000006,
        RunLzfse = 0x80000007, RunComment = 0x7FFFFFFE, RunEnd = 0xFFFFFFFF;
    private const uint ChecksumCrc32 = 2, ChecksumMd5 = 4;
    private const long MaxXml = 64L * 1024 * 1024;
    private static readonly FourCC Blkx = FourCC.FromString("blkx");

    /// <summary>The reader.</summary>
    public static UdifReader Instance { get; } = new();

    private UdifReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "UDIF (.dmg)";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < TrailerLength)
        {
            return false;
        }

        return Trailer(input).AsSpan(0, 4).SequenceEqual("koly"u8) || Encrypted(input);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (Encrypted(input))
        {
            throw new InvalidDataException("The image is an encrypted UDIF image, which ClassicMac does not read.");
        }

        var koly = Koly.Read(Trailer(input));
        if (koly.SegmentCount > 1)
        {
            throw new InvalidDataException($"The image is part {koly.SegmentNumber} of a {koly.SegmentCount}-part UDIF image, which ClassicMac does not read yet.");
        }

        var tables = Tables(input, koly, context);
        var chunks = new List<DiskChunk>();
        var runsOf = new List<(Table Table, List<DiskChunk> Chunks)>();
        long end = 0;
        foreach (var table in tables)
        {
            var own = Runs(table, koly, input.Length, context);
            chunks.AddRange(own);
            runsOf.Add((table, own));
            end = Math.Max(end, (long)(table.FirstSector + table.SectorCount) * Sector);
        }
        chunks.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < chunks.Count; i++)
        {
            if (chunks[i].Start < chunks[i - 1].End)
            {
                context.Report(DiagnosticSeverity.Error, "udif.bad-table",
                    $"The runs at sectors {chunks[i - 1].Start / Sector} and {chunks[i].Start / Sector} overlap; the later one is read.");
            }
        }

        var length = koly.SectorCount > 0 ? (long)koly.SectorCount * Sector : end;
        if (length > context.Options.MaxExpandedBytesPerInput)
        {
            throw new InvalidDataException($"The disk is {length} bytes, over the {context.Options.MaxExpandedBytesPerInput}-byte limit.");
        }

        var disk = new ChunkedForkData(input, chunks, length, Decode, (chunk, problem) => context.Report(DiagnosticSeverity.Error,
            "udif.bad-run", $"The run at sector {chunk.Start / Sector} {problem}; the rest of it reads as zeros."));
        if (context.Options.VerifyChecksums)
        {
            VerifyChecksums(input, koly, runsOf, disk, context);
        }

        var name = context.HostName?.ToMacRoman() is { } host ? Path.GetFileNameWithoutExtension(host) : "Disk image";
        return [new MacFile { Name = MacString.FromMacRoman(name.Length > 0 ? name : "Disk image"), DataFork = disk }];
    }

    private static byte[] Trailer(ForkData input) => input.Slice(input.Length - TrailerLength, TrailerLength).ToArray();

    // Encrypted images: 'encrcdsa' at the start (header version 2) or 'cdsaencr' at the end (version 1).
    private static bool Encrypted(ForkData input) =>
        input.ReadPrefix(8).AsSpan().SequenceEqual("encrcdsa"u8)
        || input.Slice(input.Length - 8, 8).ToArray().AsSpan().SequenceEqual("cdsaencr"u8);

    private sealed record Koly(uint Version, uint Flags, ulong DataForkOffset, ulong DataForkLength, ulong ResourceOffset,
        ulong ResourceLength, uint SegmentNumber, uint SegmentCount, uint DataChecksumType, byte[] DataChecksum,
        ulong XmlOffset, ulong XmlLength, uint MasterChecksumType, byte[] MasterChecksum, ulong SectorCount)
    {
        public static Koly Read(ReadOnlyMemory<byte> t)
        {
            var reader = new BigEndianReader(t);
            return new(
                reader.ReadUInt32At(4), reader.ReadUInt32At(12),
                reader.ReadUInt64At(0x18), reader.ReadUInt64At(0x20),
                reader.ReadUInt64At(0x28), reader.ReadUInt64At(0x30),
                reader.ReadUInt32At(0x38), reader.ReadUInt32At(0x3C),
                reader.ReadUInt32At(0x50), Checksum(t[0x54..]),
                reader.ReadUInt64At(0xD8), reader.ReadUInt64At(0xE0),
                reader.ReadUInt32At(0x160), Checksum(t[0x164..]),
                reader.ReadUInt64At(0x1EC));
        }
    }

    // A checksum field: a u32 size in bits, then the value, left-aligned in 128 bytes.
    private static byte[] Checksum(ReadOnlyMemory<byte> field)
    {
        var bits = new BigEndianReader(field).ReadUInt32At(0);
        return field.Slice(4, (int)Math.Min(128, (bits + 7) / 8)).ToArray();
    }

    private sealed record Table(int Id, ulong FirstSector, ulong SectorCount, ulong DataOffset, uint ChecksumType, byte[] Checksum,
        byte[] Data);

    // The block tables: from the XML property list when there is one, else from the embedded resource fork; in
    // blkx ID order (the order the master checksum takes them in).
    private static List<Table> Tables(ForkData input, Koly koly, ContainerContext context)
    {
        var blocks = new List<(int Id, byte[] Data)>();
        if (koly.XmlLength > 0)
        {
            if (koly.XmlLength > MaxXml || koly.XmlOffset + koly.XmlLength > (ulong)input.Length)
            {
                throw new InvalidDataException("The image's property list lies outside it.");
            }

            blocks = PropertyListTables(input.Slice((long)koly.XmlOffset, (long)koly.XmlLength).ToArray());
        }
        else if (koly.ResourceLength > 0)
        {
            if (koly.ResourceOffset + koly.ResourceLength > (ulong)input.Length || koly.ResourceLength > ResourceFork.MaxForkLength)
            {
                throw new InvalidDataException("The image's embedded resource fork lies outside it.");
            }

            var fork = ResourceFork.Read(input.Slice((long)koly.ResourceOffset, (long)koly.ResourceLength).ToArray());
            blocks = [.. fork.OfType(Blkx).Select(r => ((int)r.Id, r.GetData().ToArray()))];
        }
        if (blocks.Count == 0)
        {
            throw new InvalidDataException("The image has no block tables (blkx).");
        }

        var tables = new List<Table>();
        foreach (var (id, data) in blocks.OrderBy(b => b.Id))
        {
            if (data.Length < TableHeader || !data.AsSpan(0, 4).SequenceEqual("mish"u8))
            {
                context.Report(DiagnosticSeverity.Error, "udif.bad-table", $"Block table {id} is not a 'mish' table; skipped.");
                continue;
            }
            var reader = new BigEndianReader(data);
            tables.Add(new Table(id, reader.ReadUInt64At(8), reader.ReadUInt64At(0x10),
                reader.ReadUInt64At(0x18), reader.ReadUInt32At(0x40), Checksum(data.AsMemory(0x44)), data));
        }
        return tables;
    }

    // The property list's resource-fork → blkx array: each entry's ID and base64 Data.
    private static List<(int Id, byte[] Data)> PropertyListTables(byte[] xml)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"The image's property list is not readable XML: {e.Message}");
        }
        var root = document.Root?.Element("dict");
        var blkx = Value(Value(root, "resource-fork"), "blkx");
        if (blkx is null || blkx.Name != "array")
        {
            return [];
        }

        var tables = new List<(int, byte[])>();
        foreach (var entry in blkx.Elements("dict"))
        {
            var data = Value(entry, "Data")?.Value;
            var id = Value(entry, "ID")?.Value;
            if (data is null)
            {
                continue;
            }

            try
            {
                tables.Add((int.TryParse(id, out var n) ? n : tables.Count, Convert.FromBase64String(new string(data.Where(c => !char.IsWhiteSpace(c)).ToArray()))));
            }
            catch (FormatException)
            {
                throw new InvalidDataException("A block table in the image's property list is not valid base64.");
            }
        }
        return tables;

        // The element following <key>name</key> in a dict.
        static XElement? Value(XElement? dict, string name) =>
            dict?.Elements("key").FirstOrDefault(k => k.Value == name)?.ElementsAfterSelf().FirstOrDefault();
    }

    // A table's runs as disk chunks.
    private static List<DiskChunk> Runs(Table table, Koly koly, long inputLength, ContainerContext context)
    {
        var t = new BigEndianReader(table.Data);
        var count = t.ReadUInt32At(0xC8);
        var room = (t.Length - TableHeader) / RunLength;
        if (count > room)
        {
            context.Report(DiagnosticSeverity.Error, "udif.bad-table", $"Block table {table.Id} lists {count} runs but holds {room}.");
            count = (uint)room;
        }
        var chunks = new List<DiskChunk>();
        var ended = false;
        for (var i = 0; i < count; i++)
        {
            var at = TableHeader + i * RunLength;
            var type = t.ReadUInt32At(at);
            var first = t.ReadUInt64At(at + 8);
            var sectors = t.ReadUInt64At(at + 0x10);
            var offset = t.ReadUInt64At(at + 0x18);
            var stored = t.ReadUInt64At(at + 0x20);
            if (type == RunEnd)
            {
                ended = true;
                break;
            }
            if (type == RunComment || sectors == 0)
            {
                continue;
            }

            var start = (long)(table.FirstSector + first) * Sector;
            var end = start + (long)sectors * Sector;
            var where = (long)(koly.DataForkOffset + table.DataOffset + offset);
            ChunkStorage storage;
            switch (type)
            {
                case RunZero or RunFree:
                    storage = ChunkStorage.Zeros;
                    break;
                case RunRaw:
                    storage = ChunkStorage.Raw;
                    break;
                case RunAdc or RunZlib or RunBzip2:
                    storage = ChunkStorage.Compressed;
                    break;
                default:
                    context.Report(DiagnosticSeverity.Error, "udif.unsupported-run", type == RunLzfse
                        ? $"The run at sector {start / Sector} is LZFSE-compressed, which ClassicMac does not decode yet; it reads as zeros."
                        : $"The run at sector {start / Sector} has type ${type:X8}, which ClassicMac does not know; it reads as zeros.");
                    storage = ChunkStorage.Zeros;
                    break;
            }
            if (storage != ChunkStorage.Zeros && (where < 0 || where + (long)stored > inputLength))
            {
                context.Report(DiagnosticSeverity.Error, "udif.bad-run", $"The run at sector {start / Sector} lies past the end of the image; what is there is read.");
                stored = (ulong)Math.Max(0, inputLength - where);
            }
            if (storage == ChunkStorage.Compressed && (long)sectors * Sector > int.MaxValue)
            {
                context.Report(DiagnosticSeverity.Error, "udif.bad-run", $"The compressed run at sector {start / Sector} is too large to decode; it reads as zeros.");
                storage = ChunkStorage.Zeros;
            }
            chunks.Add(new DiskChunk(start, end, type, storage, where, (long)stored));
        }
        if (!ended)
        {
            context.Report(DiagnosticSeverity.Warning, "udif.bad-table", $"Block table {table.Id} has no end run.");
        }

        return chunks;
    }

    private static string? Decode(DiskChunk chunk, byte[] stored, byte[] bytes)
    {
        switch (chunk.Type)
        {
            case RunAdc:
                var result = Adc.Decompress(stored, bytes, out var adcWritten);
                return result == Adc.Result.Done ? null : $"decodes to {adcWritten} of its {bytes.Length} bytes ({result})";
            case RunZlib:
                try
                {
                    using var zlib = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
                    var read = zlib.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                    return read == bytes.Length ? null : $"decodes to {read} of its {bytes.Length} bytes";
                }
                catch (InvalidDataException e)
                {
                    return $"is not valid zlib data ({e.Message})";
                }
            case RunBzip2:
                var bz = BZip2.Decompress(stored, bytes, out var bzWritten);
                return bz == BZip2.Result.Done && bzWritten == bytes.Length ? null : $"decodes to {bzWritten} of its {bytes.Length} bytes ({bz})";
            default:
                return null;
        }
    }

    // Each table's checksum over the sectors its runs store, the master checksum over the tables' checksums, and
    // the data checksum over the data fork: CRC-32 (type 2) or MD5 (type 4), as each field says.
    private static void VerifyChecksums(ForkData input, Koly koly, List<(Table Table, List<DiskChunk> Chunks)> tables, ForkData disk,
        ContainerContext context)
    {
        var computed = new List<byte[]>();
        foreach (var (table, chunks) in tables)
        {
            var hash = Hasher.For(table.ChecksumType);
            if (hash is null)
            {
                return;
            }

            using var stream = disk.Open();
            var buffer = new byte[65536];
            foreach (var chunk in chunks.Where(c => c.Storage != ChunkStorage.Zeros))
            {
                stream.Position = chunk.Start;
                for (var left = chunk.End - chunk.Start; left > 0;)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read == 0)
                    {
                        break;
                    }

                    hash.Add(buffer.AsSpan(0, read));
                    left -= read;
                }
            }
            var value = hash.Finish();
            computed.Add(value);
            Compare(value, table.Checksum, $"Block table {table.Id}'s", context);
        }
        if (Hasher.For(koly.MasterChecksumType) is { } master)
        {
            foreach (var value in computed)
            {
                master.Add(value);
            }

            Compare(master.Finish(), koly.MasterChecksum, "The master", context);
        }
        if (Hasher.For(koly.DataChecksumType) is { } data && koly.DataForkLength > 0)
        {
            using var stream = input.Slice((long)koly.DataForkOffset, (long)Math.Min(koly.DataForkLength, (ulong)input.Length)).Open();
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                data.Add(buffer.AsSpan(0, read));
            }

            Compare(data.Finish(), koly.DataChecksum, "The data", context);
        }
    }

    private static void Compare(byte[] computed, byte[] stored, string what, ContainerContext context)
    {
        if (!computed.AsSpan().SequenceEqual(stored))
        {
            context.Report(DiagnosticSeverity.Error, "udif.bad-checksum",
                $"{what} checksum is {Convert.ToHexString(computed)}, not the {Convert.ToHexString(stored)} the image records.");
        }
    }

    // CRC-32 (zlib's) or MD5, fed incrementally; the result as the image stores it (CRC-32 big-endian).
    private sealed class Hasher
    {
        private readonly IncrementalHash? md5;
        private uint crc = 0xFFFFFFFF;

        private Hasher(IncrementalHash? md5) => this.md5 = md5;

        public static Hasher? For(uint type) => type switch
        {
            ChecksumCrc32 => new Hasher(null),
            ChecksumMd5 => new Hasher(IncrementalHash.CreateHash(HashAlgorithmName.MD5)),
            _ => null,
        };

        public void Add(ReadOnlySpan<byte> bytes)
        {
            if (md5 is not null)
            {
                md5.AppendData(bytes);
                return;
            }
            crc = Crc32.Update(crc, bytes);
        }

        public byte[] Finish()
        {
            if (md5 is not null)
            {
                return md5.GetHashAndReset();
            }

            var writer = new BigEndianWriter(4);
            writer.WriteUInt32(~crc);
            return writer.ToArray();
        }
    }
}
