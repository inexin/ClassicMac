using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Wraps a disk in an NDIF image the way Disk Copy 6.3.3's images are laid out (see NdifReader): the data fork holds
// the chunks, the 'bcem' 128 resource maps them. Each chunk is given as (sectors, kind); ADC chunks are encoded here
// with literal runs and one long match per run of repeated bytes, enough to exercise the decoder.
internal static class NdifBuilder
{
    public enum Kind : byte { Zero = 0x00, Raw = 0x02, Adc = 0x83, Unknown = 0x84 }

    public static (byte[] Data, byte[] Resource) Build(byte[] disk, string name, params (int Sectors, Kind Kind)[] chunks)
    {
        var data = new List<byte>();
        var entries = new List<(uint Start, byte Type, uint Offset, uint Length)>();
        var sector = 0;
        foreach (var (count, kind) in chunks)
        {
            var bytes = disk.AsSpan(sector * 512, count * 512).ToArray();
            var stored = kind switch { Kind.Raw or Kind.Unknown => bytes, Kind.Adc => Adc(bytes), _ => [] };
            entries.Add(((uint)sector, (byte)kind, (uint)data.Count, (uint)stored.Length));
            data.AddRange(stored);
            sector += count;
        }
        entries.Add(((uint)sector, 0xFF, (uint)data.Count, 0));

        var map = new byte[0x80 + entries.Count * 12];
        BinaryPrimitives.WriteUInt16BigEndian(map, chunks.Any(c => c.Kind == Kind.Adc) ? (ushort)11 : (ushort)10);
        map[4] = (byte)name.Length;
        MacRoman.Encode(name).CopyTo(map, 5);
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x44), (uint)(disk.Length / 512));
        // Buffer size, as Disk Copy writes it: the largest compressed chunk plus a sector of room (0 with none).
        var compressed = chunks.Where(c => c.Kind == Kind.Adc).Select(c => c.Sectors).DefaultIfEmpty(-1).Max();
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x48), (uint)(compressed + 1));
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x7C), (uint)entries.Count);
        for (var k = 0; k < entries.Count; k++)
        {
            var e = map.AsSpan(0x80 + k * 12);
            BinaryPrimitives.WriteUInt32BigEndian(e, entries[k].Start << 8 | entries[k].Type);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], entries[k].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], entries[k].Length);
        }
        return ([.. data], Fork(("bcem", 128, map)));
    }

    public static byte[] Fork(params (string Type, short Id, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, bytes) in resources) fork.Add(new Resource(FourCC.FromString(type), id, bytes));
        return fork.ToArray();
    }

    // 'bcm#' 128: part number, part count, a 16-byte image ID (here filled with one byte), the part's CRC.
    public static byte[] PartResource(int number, int count, int image = 1)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)number);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)count);
        bytes.AsSpan(4, 16).Fill((byte)image);
        return bytes;
    }

    public static byte[] Adc(byte[] input)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < input.Length)
        {
            // A run of one repeated byte: the byte as a literal, then a long match one back.
            var run = 1;
            while (i + run < input.Length && input[i + run] == input[i] && run < 68) run++;
            if (run >= 5)
            {
                output.AddRange([0x80, input[i], (byte)(0x40 | (run - 1 - 4)), 0, 0]);
                i += run;
                continue;
            }
            var literal = Math.Min(128, input.Length - i);
            output.Add((byte)(0x80 | (literal - 1)));
            output.AddRange(input.AsSpan(i, literal).ToArray());
            i += literal;
        }
        return [.. output];
    }
}
