using ClassicMac.Core;

namespace Fuzz;

/// <summary>
/// Seeds for the tiff and wav targets: small files of each kind the readers know, built byte by byte (the repository
/// holds no TIFF or WAV files; libtiff's and Mac-made samples may be added to a local corpus).
/// </summary>
internal static class MediaSeeds
{
    private const ushort Byte = 1, Short = 3, Long = 4, Undefined = 7;

    /// <summary>The TIFF seeds: both byte orders, strips and tiles, the compressions and colour models read.</summary>
    public static IEnumerable<byte[]> Tiffs()
    {
        // Uncompressed RGB, big-endian.
        yield return Tiff(true, 2, 2, 2, [8, 8, 8], 1, [[255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]]);
        // Grey with PackBits, little-endian: a run of four, then a literal of four.
        yield return Tiff(false, 4, 2, 1, [8], 32773, [[0xFD, 0x80, 0x03, 1, 2, 3, 4]]);
        // LZW with the predictor: each byte its own 9-bit code between Clear and EndOfInformation.
        yield return Tiff(true, 2, 1, 2, [8, 8, 8], 5, [Lzw([10, 20, 30, 5, 5, 5])], (317, Short, [2]));
        // Group 4: horizontal and vertical modes over three rows of 8.
        yield return Tiff(true, 8, 3, 0, [1], 4, [Bits("001 0111 011 1  1 1 1  011 010 1")]);
        // Group 3, two-dimensional, with EOLs.
        yield return Tiff(false, 8, 2, 0, [1], 3, [Bits("000000000001 1 10011  000000000001 0 001 0111 011 1")], (292, Long, [1]));
        // A 4-bit palette.
        var map = new uint[48];
        map[1] = 0xFFFF;
        map[16 + 2] = 0xFFFF;
        yield return Tiff(true, 2, 1, 3, [4], 1, [[0x12]], (320, Short, map));
        // Subsampled YCbCr: one 2 × 2 unit.
        yield return Tiff(true, 2, 2, 6, [8, 8, 8], 1, [[10, 20, 30, 40, 100, 200]]);
        // Tiles of 16 × 16 for a 20 × 10 grey image.
        yield return Tiff(true, 20, 10, 1, [8], 1, [new byte[256], Enumerable.Repeat((byte)0x80, 256).ToArray()], (322, Short, [16]), (323, Short, [16]));
        // 32-bit floats, little-endian.
        yield return Tiff(false, 2, 1, 1, [32], 1, [[0, 0, 0, 0, 0, 0, 0x80, 0x3F]], (339, Short, [3]));
        // ThunderScan: raw, run, deltas.
        yield return Tiff(true, 8, 1, 0, [4], 32809, [[0xCF, 0x02, 0x7A, 0xAA, 0xC0]]);
        // JPEG (7) with JPEGTables: the stream is put together for the decoder.
        yield return Tiff(true, 2, 1, 6, [8, 8, 8], 7, [[0xFF, 0xD8, 0xFF, 0xDA, 1, 2, 0xFF, 0xD9]],
            (347, Undefined, [0xFF, 0xD8, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xD9]));
        // LogL: a run in the high plane, literals in the low.
        yield return Tiff(true, 2, 1, 32844, [16], 34676, [[130, 0x40, 2, 0, 0]], (339, Short, [2]));
        // LogLuv24: neutral white.
        yield return Tiff(true, 1, 1, 32845, [16, 16, 16], 34677, [[0xC0, 0x2F, 0xEA]]);
    }

    /// <summary>The WAV seeds: 8-bit mono, 16-bit stereo with a chunk before the data, 32-bit float.</summary>
    public static IEnumerable<byte[]> Wavs()
    {
        yield return Wav(1, 1, 11025, 8, [0x80, 0xFF, 0x00, 0x40]);
        yield return Wav(1, 2, 22050, 16, [0, 0x40, 0, 0xC0, 0xFF, 0x7F, 0, 0x80], ("LIST", [1, 2, 3]));
        yield return Wav(3, 1, 8000, 32, [0, 0, 0x80, 0x3F, 0, 0, 0, 0xBF]);
    }

    // A TIFF of one image: the tags given and the strips' (or tiles') offsets and counts, the strips after the IFD,
    // values that do not fit in an entry after them.
    private static byte[] Tiff(bool bigEndian, int width, int height, ushort photometric, ushort[] bits, ushort compression,
        byte[][] strips, params (ushort Tag, ushort Type, uint[] Values)[] extra)
    {
        var tiled = extra.Any(e => e.Tag == 322);
        var tags = new SortedDictionary<ushort, (ushort Type, uint[] Values)>
        {
            [256] = (Long, [(uint)width]), [257] = (Long, [(uint)height]), [258] = (Short, [.. bits.Select(b => (uint)b)]),
            [259] = (Short, [compression]), [262] = (Short, [photometric]), [277] = (Short, [(uint)bits.Length]),
            [(ushort)(tiled ? 324 : 273)] = (Long, new uint[strips.Length]),
            [(ushort)(tiled ? 325 : 279)] = (Long, [.. strips.Select(s => (uint)s.Length)]),
        };
        if (!tiled)
        {
            tags[278] = (Long, [(uint)height]);
        }

        foreach (var (tag, type, values) in extra)
        {
            tags[tag] = (type, values);
        }

        var stripStart = 8 + 2 + tags.Count * 12 + 4;
        var at = (uint)stripStart;
        var offsets = tags[(ushort)(tiled ? 324 : 273)].Values;
        for (var i = 0; i < strips.Length; i++)
        {
            offsets[i] = at;
            at += (uint)strips[i].Length;
        }

        var w = new List<byte>();
        var outside = new List<byte>();
        void U16(List<byte> to, uint v) => to.AddRange(bigEndian ? [(byte)(v >> 8), (byte)v] : [(byte)v, (byte)(v >> 8)]);
        void U32(List<byte> to, uint v) => to.AddRange(bigEndian
            ? [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]
            : [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
        w.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(w, 42);
        U32(w, 8);
        U16(w, (uint)tags.Count);
        foreach (var (tag, (type, values)) in tags)
        {
            U16(w, tag);
            U16(w, type);
            U32(w, (uint)values.Length);
            var body = new List<byte>();
            foreach (var v in values)
            {
                switch (type)
                {
                    case Byte or Undefined:
                        body.Add((byte)v);
                        break;
                    case Short:
                        U16(body, v);
                        break;
                    default:
                        U32(body, v);
                        break;
                }
            }

            if (body.Count <= 4)
            {
                w.AddRange(body);
                w.AddRange(new byte[4 - body.Count]);
            }
            else
            {
                U32(w, at + (uint)outside.Count);
                outside.AddRange(body);
            }
        }

        U32(w, 0);
        foreach (var strip in strips)
        {
            w.AddRange(strip);
        }

        w.AddRange(outside);
        return [.. w];
    }

    // Each byte as its own 9-bit LZW code, between Clear (256) and EndOfInformation (257): a valid strip.
    private static byte[] Lzw(byte[] data)
    {
        var bits = new System.Text.StringBuilder();
        foreach (var code in new[] { 256 }.Concat(data.Select(b => (int)b)).Append(257))
        {
            bits.Append(Convert.ToString(code, 2).PadLeft(9, '0'));
        }

        return Bits(bits.ToString());
    }

    // '0' and '1' characters, most significant bit first, padded to a byte.
    private static byte[] Bits(string text)
    {
        text = text.Replace(" ", "", StringComparison.Ordinal);
        var bytes = new byte[(text.Length + 7) / 8];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '1')
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    // A WAV file: RIFF, WAVE, fmt, any chunks given, data; little-endian.
    private static byte[] Wav(ushort tag, ushort channels, uint rate, ushort bitsPerSample, byte[] samples, params (string Id, byte[] Body)[] before)
    {
        var block = (ushort)(channels * ((bitsPerSample + 7) / 8));
        var body = new List<byte>("WAVE"u8.ToArray());
        void Chunk(string id, byte[] data)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes((uint)data.Length));
            body.AddRange(data);
            if ((data.Length & 1) == 1)
            {
                body.Add(0);
            }
        }

        Chunk("fmt ", [.. BitConverter.GetBytes(tag), .. BitConverter.GetBytes(channels), .. BitConverter.GetBytes(rate),
            .. BitConverter.GetBytes(rate * block), .. BitConverter.GetBytes(block), .. BitConverter.GetBytes(bitsPerSample)]);
        foreach (var (id, data) in before)
        {
            Chunk(id, data);
        }

        Chunk("data", samples);
        return [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes((uint)body.Count), .. body];
    }
}
