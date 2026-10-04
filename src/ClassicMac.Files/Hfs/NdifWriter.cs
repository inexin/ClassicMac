using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Compression;
using ClassicMac.Resources;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// Makes an NDIF (Disk Copy 6) image again around a changed disk (docs/formats/disk-images/ndif.md §3): the chunk
/// boundaries stay, a chunk whose sectors are unchanged keeps its stored bytes (compressed or not), and a changed one
/// is stored raw, as Disk Copy stores a chunk that would not shrink, its runs of zero sectors as zero chunks (not in a
/// read/write image, whose chunks are all raw). The map's offsets, types and lengths, its CRC
/// (when the image has one) and the CRC in the <c>'vers'</c> text are made again.
/// </summary>
public static class NdifWriter
{
    private const int SectorSize = 512, HeaderLength = 0x80, EntryLength = 12;
    private const byte ChunkZero = 0x00, ChunkRaw = 0x02, ChunkKenCode = 0x80, ChunkAdc = 0x83, ChunkEnd = 0xFF;
    private static readonly FourCC Bcem = FourCC.FromString("bcem"), Vers = FourCC.FromString("vers");

    /// <summary>Whether <see cref="Rewrite"/> takes the image: an NDIF image with map version 10 to 12, not segmented.</summary>
    public static bool CanRewrite(MacFile image)
    {
        ArgumentNullException.ThrowIfNull(image);
        try
        {
            var map = ResourceFork.Read(image.ResourceFork.ToArray()).Find(Bcem, 128)?.GetData().ToArray();
            return map is { Length: >= HeaderLength } && new BigEndianReader(map) is var reader &&
                reader.ReadUInt16At(0) is >= 10 and <= 12 && reader.ReadUInt32At(0x54) == 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The image with its disk replaced by <paramref name="disk"/>; its name, Finder info and other resources stay. With
    /// <paramref name="changedSectors"/> (the 512-byte sectors that may differ from the image's disk), a chunk holding
    /// none of them keeps its stored bytes without the old disk being decoded to compare it.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The image is not one ClassicMac rewrites (map version 10–12, not segmented), or the disk is not the image's size.
    /// </exception>
    public static MacFile Rewrite(MacFile image, ReadOnlyMemory<byte> disk, IReadOnlySet<long>? changedSectors = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var fork = ResourceFork.Read(image.ResourceFork.ToArray());
        var bcem = fork.Find(Bcem, 128) ?? throw new InvalidDataException("Not an NDIF image: it has no 'bcem' 128.");
        var map = bcem.GetData().ToArray();
        var reader = new BigEndianReader(map);
        int version = reader.ReadUInt16At(0);
        if (version is < 10 or > 12)
        {
            throw new InvalidDataException($"NDIF map version {version} is not rewritten (versions 10 to 12 are).");
        }

        if (reader.ReadUInt32At(0x54) != 0)
        {
            throw new InvalidDataException("A segmented NDIF image is not rewritten.");
        }

        long blocks = reader.ReadUInt32At(0x44);
        if (blocks * SectorSize != disk.Length)
        {
            throw new InvalidDataException($"The disk is {disk.Length} bytes; the image holds {blocks * SectorSize}.");
        }

        long dataStart = reader.ReadUInt32At(0x4C);
        int count = checked((int)reader.ReadUInt32At(0x7C));
        if (map.Length < HeaderLength + count * EntryLength)
        {
            throw new InvalidDataException("The NDIF map is shorter than its entries.");
        }

        // A read/write image (raw chunks only) stays so: Disk Copy refuses a read/write mount with zero chunks (§2.1).
        bool zeroRuns = Enumerable.Range(0, count).Select(k => (byte)reader.ReadUInt32At(HeaderLength + k * EntryLength))
            .Any(type => type != ChunkRaw && type != ChunkEnd);
        // A compressed image has changed runs compressed again with its own codec, as Disk Copy stores them: ADC (map
        // version 11 or later) or KenCode, unless that is longer than the sectors (§1.4, adc.md §3, kencode.md §3);
        // other images keep them raw.
        var types = Enumerable.Range(0, count).Select(k => (byte)reader.ReadUInt32At(HeaderLength + k * EntryLength)).ToHashSet();
        byte codec = version >= 11 && types.Contains(ChunkAdc) ? ChunkAdc : types.Contains(ChunkKenCode) ? ChunkKenCode : ChunkRaw;
        long buffer = reader.ReadUInt32At(0x48);
        var old = changedSectors is null ? NdifReader.Instance.Read(image, new ContainerContext()).Single().DataFork.ToArray() : null;
        var data = image.DataFork.ToArray();
        var output = new MemoryStream();
        output.Write(data.AsSpan(0, (int)Math.Min(dataStart, data.Length)));
        var entries = new BigEndianWriter();
        int written = 0;
        for (var k = 0; k < count; k++)
        {
            int at = HeaderLength + k * EntryLength;
            uint word = reader.ReadUInt32At(at);
            long start = word >> 8;
            byte type = (byte)word;
            if (type == ChunkEnd)
            {
                Entry(entries, word, output.Length - dataStart, 0);
                written++;
                continue;
            }

            long next = k + 1 < count ? reader.ReadUInt32At(at + EntryLength) >> 8 : blocks;
            int from = checked((int)(start * SectorSize)), size = checked((int)((next - start) * SectorSize));
            var sectors = disk.Span.Slice(from, size);
            bool same = old is not null
                ? sectors.SequenceEqual(old.AsSpan(from, size))
                : !changedSectors!.Any(sector => sector >= start && sector < next);
            if (same)
            {
                long offset = reader.ReadUInt32At(at + 4), stored = reader.ReadUInt32At(at + 8);
                if (type == ChunkZero)
                {
                    Entry(entries, word, offset, stored);
                }
                else
                {
                    Entry(entries, word, output.Length - dataStart, stored);
                    output.Write(data.AsSpan((int)(dataStart + offset), (int)stored));
                }

                written++;
                continue;
            }

            // A changed chunk: stored raw, its runs of zero sectors as zero chunks (offset and length 0, as Disk Copy
            // writes them, §1.3) where the image may hold them.
            for (long run = start; run < next;)
            {
                bool zero = zeroRuns && IsZero(disk.Span, run);
                long end = run + 1;
                while (end < next && (!zeroRuns || IsZero(disk.Span, end) == zero))
                {
                    end++;
                }

                if (zero)
                {
                    Entry(entries, (uint)(run << 8) | ChunkZero, 0, 0);
                }
                else
                {
                    var bytes = disk.Span.Slice(checked((int)(run * SectorSize)), checked((int)((end - run) * SectorSize)));
                    int margin = 0;
                    var packed = codec switch
                    {
                        ChunkAdc => Adc.Compress(bytes, out margin),
                        ChunkKenCode => KenCode.Compress(bytes, out margin),
                        _ => null,
                    };
                    if (packed is not null && packed.Length <= bytes.Length)
                    {
                        // +$48 covers the chunk and its decoder's overrun, as Disk Copy sets it (§4.3).
                        buffer = Math.Max(buffer, end - run + (margin + SectorSize - 1) / SectorSize);
                        Entry(entries, (uint)(run << 8) | codec, output.Length - dataStart, packed.Length);
                        output.Write(packed);
                    }
                    else
                    {
                        Entry(entries, (uint)(run << 8) | ChunkRaw, output.Length - dataStart, bytes.Length);
                        output.Write(bytes);
                    }
                }

                written++;
                run = end;
            }
        }

        // The header, the entries, and anything the map held past its count (a Disk Copy 6.0 map's extra entry).
        var writer = new BigEndianWriter();
        writer.WriteBytes(map.AsSpan(0, HeaderLength));
        writer.WriteBytes(entries.WrittenSpan);
        writer.WriteBytes(map.AsSpan(HeaderLength + count * EntryLength));
        writer.WriteUInt32At(0x7C, written);
        writer.WriteUInt32At(0x48, buffer);
        map = writer.ToArray();
        writer = new BigEndianWriter(map);

        uint crc = 0;
        if (reader.ReadUInt32At(0x50) != 0)
        {
            crc = NdifReader.Crc(ForkData.FromBytes(disk.ToArray()));
            writer.WriteUInt32At(0x50, crc);
        }

        var resources = new ResourceFork();
        foreach (var resource in fork.Resources)
        {
            var bytes = resource.Type == Bcem && resource.Id == 128 ? map
                : resource.Type == Vers && resource.Id == 1 && crc != 0 ? WithCrc(resource.GetData().ToArray(), crc)
                : resource.GetData().ToArray();
            resources.Add(new Resource(resource.Type, resource.Id, bytes) { Name = resource.Name, Attributes = resource.Attributes });
        }

        var result = image with { DataFork = ForkData.FromBytes(output.ToArray()), ResourceFork = ForkData.FromBytes(resources.ToArray()) };
        var check = new List<Diagnostic>();
        var decoded = NdifReader.Instance.Read(result, new ContainerContext(diagnostics: check)).Single().DataFork.ToArray();
        if (!decoded.AsSpan().SequenceEqual(disk.Span) || check.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidDataException("The rewritten NDIF image does not read back as the disk.");
        }

        return result;
    }

    private static void Entry(BigEndianWriter entries, uint word, long offset, long stored)
    {
        entries.WriteUInt32(word);
        entries.WriteUInt32(offset);
        entries.WriteUInt32(stored);
    }

    private static bool IsZero(ReadOnlySpan<byte> disk, long sector) =>
        !disk.Slice(checked((int)(sector * SectorSize)), SectorSize).ContainsAnyExcept((byte)0);

    // The 'vers' text's "CRC: $xxxxxxxx" (or "CRC28: $…") with the new value; the same length, so the strings' lengths stay.
    private static byte[] WithCrc(byte[] vers, uint crc)
    {
        var text = Encoding.Latin1.GetString(vers);
        var replaced = Regex.Replace(text, @"(CRC(?:28)?: ?\$)[0-9A-Fa-f]{8}", m => m.Groups[1].Value + crc.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        return Encoding.Latin1.GetBytes(replaced);
    }
}
