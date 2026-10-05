using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ClassicMac.Core;
using ClassicMac.Files.Compression;
using ClassicMac.Resources;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// New NDIF images (docs/formats/disk-images/ndif.md §3.2, §3.3): a disk laid out as Disk Copy 6.3.3 lays it out, and an
/// image cut into the parts of a segmented image. <see cref="NdifWriter"/> is the public face.
/// </summary>
internal static class NdifCreation
{
    private const int SectorSize = 512, HeaderLength = 0x80, MaxParts = 128, MaxPartName = 31;
    private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkKenCode = 0x80, ChunkAdc = 0x83, ChunkEnd = 0xFF;
    private static readonly FourCC Bcem = FourCC.FromString("bcem"), BcmCount = FourCC.FromString("bcm#"), Vers = FourCC.FromString("vers"),
        Dimg = FourCC.FromString("dimg"), Rohd = FourCC.FromString("rohd"), Ddsk = FourCC.FromString("ddsk"), Dseg = FourCC.FromString("dseg");

    private enum Store
    {
        Raw,
        Zero,
        Compressed,
    }

    // The layout of a plain HFS disk: the sectors before the first allocation block, the end of the used blocks, and
    // the volume name (ndif.md §4.3).
    private sealed record HfsLayout(long First, long UsedEnd, MacString Name);

    public static MacFile Create(ReadOnlyMemory<byte> disk, string fileName, NdifCreateOptions? options)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        options ??= NdifCreateOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChunkSectors, 1, nameof(options));
        if (disk.Length == 0 || disk.Length % SectorSize != 0)
        {
            throw new ArgumentException($"A disk is whole 512-byte sectors; this one is {disk.Length} bytes.", nameof(disk));
        }

        long sectors = disk.Length / SectorSize;
        if (sectors >= 0x400000)
        {
            throw new ArgumentException($"An NDIF image holds fewer than $400000 sectors; this disk has {sectors}.", nameof(disk));
        }

        var hfs = Hfs(disk, sectors);
        var chunks = Chunks(options, sectors, hfs);
        var stored = disk.ToArray();
        foreach (var (start, count, store) in chunks)
        {
            if (store == Store.Zero)
            {
                stored.AsSpan(checked((int)(start * SectorSize)), checked((int)(count * SectorSize))).Clear();
            }
        }

        byte codec = options.Format switch
        {
            NdifFormat.Adc => ChunkAdc,
            NdifFormat.KenCode => ChunkKenCode,
            _ => ChunkRaw,
        };
        var data = new BigEndianWriter();
        var entries = new BigEndianWriter();
        long buffer = 0;
        foreach (var (start, count, store) in chunks)
        {
            var bytes = stored.AsSpan(checked((int)(start * SectorSize)), checked((int)(count * SectorSize)));
            if (store == Store.Zero)
            {
                Entry(entries, start, ChunkZero, 0, 0);
                continue;
            }

            // A chunk that would not shrink is stored raw (§1.4); +$48 covers the largest compressed chunk and its
            // decoder's overrun (§4.3).
            int margin = 0;
            var packed = store == Store.Compressed ? codec == ChunkAdc ? Adc.Compress(bytes, out margin) : KenCode.Compress(bytes, out margin) : null;
            if (packed is not null && packed.Length <= bytes.Length)
            {
                buffer = Math.Max(buffer, count + (margin + SectorSize - 1) / SectorSize);
                Entry(entries, start, codec, data.Length, packed.Length);
                data.WriteBytes(packed);
            }
            else
            {
                Entry(entries, start, ChunkRaw, data.Length, bytes.Length);
                data.WriteBytes(bytes);
            }
        }

        Entry(entries, sectors, ChunkEnd, data.Length, 0);
        uint crc = options.Format == NdifFormat.ReadWrite ? 0 : NdifReader.Crc(ForkData.FromBytes(stored));
        var name = hfs?.Name ?? Name(Path.GetFileNameWithoutExtension(fileName));

        var map = new BigEndianWriter();
        map.WriteUInt16((ushort)(options.Format == NdifFormat.Adc ? 11 : 10));
        map.WriteUInt16(0);                                                       // file-system ID
        map.WriteByte((byte)name.Bytes.Length);
        map.WriteBytes(name.Bytes);
        map.WriteZeros(63 - name.Bytes.Length);
        map.WriteUInt32(sectors);
        map.WriteUInt32(buffer);
        map.WriteUInt32(0u);                                                      // data start
        map.WriteUInt32(crc);
        map.WriteZeros(HeaderLength - 0x54 - 4);                                  // segmented flag, performance, reserved
        map.WriteUInt32(entries.Length / 12);
        map.WriteBytes(entries.WrittenSpan);

        var fork = new ResourceFork();
        fork.Add(new Resource(Bcem, 128, map.ToArray()) { Name = name });
        var size = (sectors * SectorSize / 1024).ToString(CultureInfo.InvariantCulture);
        var text = (hfs is null ? $"{size}K disk image" : $"Mac™ OS HFS {size}K image")
            + (crc == 0 ? "" : "\r" + string.Create(CultureInfo.InvariantCulture, $"CRC: ${crc:X8}"));
        fork.Add(new Resource(Vers, 1, VersData(text)));

        var image = new MacFile
        {
            Name = Name(fileName),
            FinderInfo = FinderInfo.Empty with { Type = options.Format == NdifFormat.ReadWrite ? Dimg : Rohd, Creator = Ddsk },
            DataFork = ForkData.FromBytes(data.ToArray()),
            ResourceFork = ForkData.FromBytes(fork.ToArray()),
        };
        Check(image, stored, null);
        return image;
    }

    // The chunks, as Disk Copy 6.3.3 lays them out (§4.3): read/write, the whole disk raw; otherwise, on a plain HFS
    // disk, the sectors before the first allocation block raw, the used area (to the last block in use) raw or in
    // compressed chunks of the chunk size, the free space after it as zeros, the alternate MDB raw and the last sector
    // as zeros. Another disk has no used area to find: all of it is the used area [ClassicMac].
    private static List<(long Start, long Count, Store Store)> Chunks(NdifCreateOptions options, long sectors, HfsLayout? hfs)
    {
        var chunks = new List<(long, long, Store)>();
        if (options.Format == NdifFormat.ReadWrite)
        {
            chunks.Add((0, sectors, Store.Raw));
            return chunks;
        }

        long usedStart = 0, usedEnd = sectors;
        if (hfs is not null)
        {
            chunks.Add((0, hfs.First, Store.Raw));
            (usedStart, usedEnd) = (hfs.First, hfs.UsedEnd);
        }

        if (options.Format == NdifFormat.ReadOnly)
        {
            if (usedEnd > usedStart)
            {
                chunks.Add((usedStart, usedEnd - usedStart, Store.Raw));
            }
        }
        else
        {
            for (var start = usedStart; start < usedEnd; start += options.ChunkSectors)
            {
                chunks.Add((start, Math.Min(options.ChunkSectors, usedEnd - start), Store.Compressed));
            }
        }

        if (hfs is not null)
        {
            if (sectors - 2 > usedEnd)
            {
                chunks.Add((usedEnd, sectors - 2 - usedEnd, Store.Zero));
            }

            chunks.Add((sectors - 2, 1, Store.Raw));
            chunks.Add((sectors - 1, 1, Store.Zero));
        }

        return chunks;
    }

    // A plain HFS volume's layout from its MDB and bitmap, or null when the disk is not one (or its MDB does not fit it).
    private static HfsLayout? Hfs(ReadOnlyMemory<byte> disk, long sectors)
    {
        if (sectors < 6)
        {
            return null;
        }

        var mdb = new BigEndianReader(disk.Slice(1024, SectorSize));
        if (mdb.ReadUInt16At(0) != 0x4244)
        {
            return null;
        }

        long bitmapStart = mdb.ReadUInt16At(0x0E), blocks = mdb.ReadUInt16At(0x12), blockSize = mdb.ReadUInt32At(0x14), first = mdb.ReadUInt16At(0x1C);
        int nameLength = mdb.ReadByteAt(0x24);
        if (blockSize == 0 || blockSize % SectorSize != 0 || nameLength > 27 || first < 3
            || first + blocks * (blockSize / SectorSize) > sectors - 2 || (bitmapStart + (blocks + 4095) / 4096) * SectorSize > disk.Length)
        {
            return null;
        }

        var bitmap = disk.Span.Slice(checked((int)(bitmapStart * SectorSize)), checked((int)((blocks + 7) / 8)));
        long last = -1;
        for (var block = blocks - 1; block >= 0; block--)
        {
            if ((bitmap[(int)(block / 8)] & (0x80 >> (int)(block % 8))) != 0)
            {
                last = block;
                break;
            }
        }

        return new HfsLayout(first, first + (last + 1) * (blockSize / SectorSize), new MacString(disk.Span.Slice(1024 + 0x25, nameLength)));
    }

    public static IReadOnlyList<MacFile> Split(MacFile image, int parts, string baseName, DateTime? created)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parts, MaxParts);
        var (fork, map) = Whole(image);
        if (new BigEndianReader(map).ReadUInt32At(0x54) != 0)
        {
            throw new InvalidDataException("The image is already segmented.");
        }

        if (parts > (image.DataFork.Length + SectorSize - 1) / SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), $"The image's {image.DataFork.Length} bytes do not make {parts} parts of a sector or more.");
        }

        // One ID for every part: the date, then random bytes (Disk Copy mixes the date, the tick count, a random number
        // and a CRC-32; readers only compare it) [ClassicMac].
        var id = new BigEndianWriter();
        id.WriteUInt32(MacDate.FromDateTime(created ?? DateTime.Now).Seconds);
        id.WriteBytes(RandomNumberGenerator.GetBytes(12));
        var width = parts.ToString(CultureInfo.InvariantCulture).Length;
        var stem = Name(baseName).Bytes.ToArray();
        var versions = fork.Find(Vers, 1)?.GetData().ToArray();
        var result = Cut(image, fork, map, parts, id.ToArray(), n =>
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $" {n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}of{parts}");
            return new MacString([.. stem.AsSpan(0, Math.Min(stem.Length, MaxPartName - suffix.Length)), .. MacRoman.Encode(suffix)]);
        }, n => n == 1 ? image.FinderInfo : image.FinderInfo with { Type = Dseg },
            n => [new Resource(Vers, 1, VersData(string.Create(CultureInfo.InvariantCulture, $"Part {n}/{parts} of a disk image"), versions))]);
        Check(result[0], NdifReader.Instance.Read(image, new ContainerContext()).Single().DataFork.ToArray(), result.Skip(1).ToList());
        return result;
    }

    // A segmented image edited (§3.4): its parts joined into one image, made again around the disk (§3.1), and cut again
    // into as many parts, each keeping its name, Finder info, other resources and the image ID.
    public static IReadOnlyList<MacFile> RewriteSegmented(IReadOnlyList<MacFile> parts, ReadOnlyMemory<byte> disk, IReadOnlySet<long>? changedSectors)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var ordered = Ordered(parts) ?? throw new InvalidDataException("The files are not every part of one segmented NDIF image.");
        var first = ordered[0];
        var (fork, map) = Whole(first);
        if (new BigEndianReader(map).ReadUInt32At(0x54) == 0)
        {
            throw new InvalidDataException("Part 1's map is not marked segmented.");
        }

        // The whole image as it was before it was cut: the parts' data back to back, part 1's resources without its
        // 'bcm#', the map not flagged (version 12 stays).
        var unflagged = map.ToArray();
        new BigEndianWriter(unflagged).WriteUInt32At(0x54, 0u);
        var resources = new ResourceFork();
        foreach (var resource in fork.Resources.Where(r => r.Type != BcmCount))
        {
            resources.Add(new Resource(resource.Type, resource.Id, resource.Type == Bcem && resource.Id == 128 ? unflagged : resource.GetData().ToArray())
            {
                Name = resource.Name,
                Attributes = resource.Attributes,
            });
        }

        var whole = first with
        {
            DataFork = ForkData.FromBytes(ordered.SelectMany(p => p.DataFork.ToArray()).ToArray()),
            ResourceFork = ForkData.FromBytes(resources.ToArray()),
        };
        var rewritten = NdifWriter.Rewrite(whole, disk, changedSectors);
        var (newFork, newMap) = Whole(rewritten);
        var id = PartRecord(first)!.AsSpan(4, 16).ToArray();
        var result = Cut(rewritten, newFork, newMap, ordered.Count, id, n => ordered[n - 1].Name, n => ordered[n - 1].FinderInfo,
            n => ResourceFork.Read(ordered[n - 1].ResourceFork.ToArray()).Resources.Where(r => r.Type != BcmCount));
        Check(result[0], disk.ToArray(), result.Skip(1).ToList());
        return result;
    }

    // The parts in part order by their 'bcm#' (§2.6): one ID and count, every number once, part 1 holding the map; null
    // when the files are not that.
    internal static IReadOnlyList<MacFile>? Ordered(IReadOnlyList<MacFile> parts)
    {
        var records = parts.Select(p => (Part: p, Record: PartRecord(p))).ToList();
        if (records.Any(r => r.Record is null) || records.Count < 2)
        {
            return null;
        }

        var id = records[0].Record!.AsSpan(4, 16).ToArray();
        var count = parts.Count;
        var byNumber = new MacFile?[count + 1];
        foreach (var (part, record) in records)
        {
            var reader = new BigEndianReader(record!);
            int number = reader.ReadUInt16At(0);
            if (reader.ReadUInt16At(2) != count || !record!.AsSpan(4, 16).SequenceEqual(id) || number < 1 || number > count || byNumber[number] is not null)
            {
                return null;
            }

            byNumber[number] = part;
        }

        return ResourceFork.Read(byNumber[1]!.ResourceFork.ToArray()).Find(Bcem, 128) is null ? null : [.. byNumber.Skip(1).Select(p => p!)];
    }

    // A part's 'bcm#' 128 (at least 20 bytes), or null.
    internal static byte[]? PartRecord(MacFile part)
    {
        try
        {
            return ResourceFork.Read(part.ResourceFork.ToArray()).Find(BcmCount, 128)?.GetData().ToArray() is { Length: >= 20 } record ? record : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // An image's resources and its map, version 10 to 12.
    private static (ResourceFork Fork, byte[] Map) Whole(MacFile image)
    {
        var fork = ResourceFork.Read(image.ResourceFork.ToArray());
        var bcem = fork.Find(Bcem, 128) ?? throw new InvalidDataException("Not an NDIF image: it has no 'bcem' 128.");
        var map = bcem.GetData().ToArray();
        if (map.Length < HeaderLength || new BigEndianReader(map).ReadUInt16At(0) is < 10 or > 12)
        {
            throw new InvalidDataException("Only NDIF map versions 10 to 12 are cut into parts.");
        }

        return (fork, map);
    }

    // The image's data fork cut raw into parts of ceil(sectors / parts) sectors, the last taking the rest (§1.6). When
    // that would leave the last part nothing (an edited image grown smaller than its parts), the data is padded with
    // zeros to equal parts; readers address chunks by offset, so bytes past the last are not read [ClassicMac]. Every
    // part has a 'bcm#' (number, count, ID, its own CRC28 when the image has a CRC); part 1 also the map, version 12
    // and flagged, its resource unnamed, and the image's other resources; the others what `others` gives them.
    private static List<MacFile> Cut(MacFile image, ResourceFork fork, byte[] map, int parts, byte[] id, Func<int, MacString> name,
        Func<int, FinderInfo> finder, Func<int, IEnumerable<Resource>> others)
    {
        var data = image.DataFork.ToArray();
        long partSize = Math.Max(1, ((data.Length + SectorSize - 1) / SectorSize + parts - 1) / parts) * SectorSize;
        if (partSize * (parts - 1) >= data.Length)
        {
            Array.Resize(ref data, checked((int)(partSize * parts)));
        }

        bool hasCrc = new BigEndianReader(map).ReadUInt32At(0x50) != 0;
        var result = new List<MacFile>();
        for (var n = 1; n <= parts; n++)
        {
            var slice = data.AsMemory((int)((n - 1) * partSize), (int)Math.Min(partSize, data.Length - (n - 1) * partSize));
            var record = new BigEndianWriter();
            record.WriteUInt16((ushort)n);
            record.WriteUInt16((ushort)parts);
            record.WriteBytes(id);
            record.WriteUInt32(hasCrc ? NdifReader.Crc(ForkData.FromBytes(slice)) : 0u);

            var resources = new ResourceFork();
            resources.Add(new Resource(BcmCount, 128, record.ToArray()));
            if (n == 1)
            {
                var segmented = map.ToArray();
                var writer = new BigEndianWriter(segmented);
                writer.WriteUInt16At(0, (ushort)12);
                writer.WriteUInt32At(0x54, 1u);
                foreach (var resource in fork.Resources)
                {
                    resources.Add(resource.Type == Bcem && resource.Id == 128
                        ? new Resource(Bcem, 128, segmented) { Attributes = resource.Attributes }
                        : new Resource(resource.Type, resource.Id, resource.GetData().ToArray()) { Name = resource.Name, Attributes = resource.Attributes });
                }
            }
            else
            {
                foreach (var resource in others(n))
                {
                    resources.Add(new Resource(resource.Type, resource.Id, resource.GetData().ToArray()) { Name = resource.Name, Attributes = resource.Attributes });
                }
            }

            result.Add(new MacFile
            {
                Name = name(n),
                FinderInfo = finder(n),
                DataFork = ForkData.FromBytes(slice),
                ResourceFork = ForkData.FromBytes(resources.ToArray()),
            });
        }

        return result;
    }

    private static void Entry(BigEndianWriter entries, long start, byte type, long offset, long stored)
    {
        entries.WriteUInt32((uint)(start << 8) | type);
        entries.WriteUInt32(offset);
        entries.WriteUInt32(stored);
    }

    // 'vers' 1 [ClassicMac]: version 1.0 final, "ClassicMac" as the short string (the image is not Disk Copy's), the
    // text Disk Copy gives as the long one. A part's takes the image's numbers and short string when it has them.
    private static byte[] VersData(string text, byte[]? like = null)
    {
        var writer = new BigEndianWriter();
        if (like is { Length: > 7 } && like.Length >= 7 + like[6])
        {
            writer.WriteBytes(like.AsSpan(0, 7 + like[6]));
        }
        else
        {
            writer.WriteBytes([1, 0, 0x80, 0, 0, 0]);
            var program = MacRoman.Encode("ClassicMac");
            writer.WriteByte((byte)program.Length);
            writer.WriteBytes(program);
        }

        var bytes = Name(text, 255).Bytes;
        writer.WriteByte((byte)bytes.Length);
        writer.WriteBytes(bytes);
        return writer.ToArray();
    }

    // Text as Mac OS Roman, a character it lacks as '?', cut to fit.
    private static MacString Name(string text, int max = 63)
    {
        var bytes = text.Select(c => MacRoman.TryGetByte(c, out var b) ? b : (byte)'?').Take(max).ToArray();
        return new MacString(bytes);
    }

    // The image reads back as the disk it stores, with its checksum; anything else is a bug here.
    private static void Check(MacFile image, byte[] disk, IReadOnlyList<MacFile>? siblings)
    {
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = true }, diagnostics,
            siblings: siblings is null ? null : () => siblings);
        var decoded = NdifReader.Instance.Read(image, context).Single().DataFork.ToArray();
        if (!decoded.AsSpan().SequenceEqual(disk) || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidDataException("The new NDIF image does not read back as the disk.");
        }
    }
}
