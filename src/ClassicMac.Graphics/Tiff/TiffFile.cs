using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Graphics;

/// <summary>
/// TIFF images (file type <c>TIFF</c>; <c>.tif</c>, <c>.tiff</c>; docs/formats/graphics/tiff.md): baseline TIFF 6.0,
/// the first image of the file. Either byte order; uncompressed, PackBits and LZW strips, with the horizontal predictor;
/// bilevel, grey, palette, RGB (with an alpha extra sample) and CMYK pixels of 1 to 16 bits per sample.
/// </summary>
public static class TiffFile
{
    private const ushort ImageWidth = 256, ImageLength = 257, BitsPerSample = 258, Compression = 259, Photometric = 262,
        StripOffsets = 273, SamplesPerPixel = 277, RowsPerStrip = 278, StripByteCounts = 279, PlanarConfiguration = 284,
        Predictor = 317, ColorMap = 320, TileWidth = 322, ExtraSamples = 338, InkSet = 332;

    private const ushort None = 1, Lzw = 5, PackBitsCompression = 32773;

    private const ushort WhiteIsZero = 0, BlackIsZero = 1, Rgb = 2, Palette = 3, Separated = 5;

    /// <summary>The most pixels an image may have (width × height). Default 64 Mi.</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Whether <paramref name="start"/> begins a TIFF file: <c>II</c> and 42 little-endian, or <c>MM</c> and 42 big-endian.</summary>
    public static bool IsTiffFile(ReadOnlySpan<byte> start) =>
        start.Length >= 4 && (start[0] == 'I' && start[1] == 'I' && start[2] == 42 && start[3] == 0
            || start[0] == 'M' && start[1] == 'M' && start[2] == 0 && start[3] == 42);

    /// <summary>Decodes the file's first image to RGBA. Damage that leaves the image readable goes to <paramref name="diagnostics"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a TIFF file, or its structures are damaged.</exception>
    /// <exception cref="NotSupportedException">The image is compressed or laid out in a way not read here (the message says which).</exception>
    public static RgbaBitmap Decode(ReadOnlyMemory<byte> data, ICollection<Diagnostic>? diagnostics = null)
    {
        if (!IsTiffFile(data.Span))
        {
            throw new InvalidDataException("Not a TIFF file.");
        }

        var file = new TiffData(data);
        var ifd = file.U32(4);
        if (ifd < 8 || ifd > data.Length - 2)
        {
            throw new InvalidDataException($"The first IFD at {ifd} lies outside the {data.Length}-byte file.");
        }

        var tags = ReadIfd(file, ifd);
        return DecodeImage(file, tags, diagnostics ?? []);
    }

    // The IFD's entries: tag → its values (each widened to uint; RATIONALs are not needed and are skipped).
    private static Dictionary<ushort, uint[]> ReadIfd(TiffData file, long ifd)
    {
        var count = file.U16(ifd);
        if (count == 0 || ifd + 2 + count * 12L > file.Length)
        {
            throw new InvalidDataException($"The IFD at {ifd} has {count} entries, more than the file holds.");
        }

        var tags = new Dictionary<ushort, uint[]>();
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + i * 12L;
            var tag = file.U16(entry);
            var type = file.U16(entry + 2);
            long n = file.U32(entry + 4);
            var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, _ => 0 };
            if (size == 0 || n == 0)
            {
                continue;
            }

            long at = n * size <= 4 ? entry + 8 : file.U32(entry + 8);
            if (n > file.Length || at + n * size > file.Length)
            {
                throw new InvalidDataException($"Tag {tag}'s {n} values lie outside the file.");
            }

            var values = new uint[n];
            for (var k = 0; k < n; k++)
            {
                values[k] = size switch { 1 => file.Byte(at + k), 2 => file.U16(at + k * 2L), _ => file.U32(at + k * 4L) };
            }

            tags[tag] = values;
        }

        return tags;
    }

    private static RgbaBitmap DecodeImage(TiffData file, Dictionary<ushort, uint[]> tags, ICollection<Diagnostic> diagnostics)
    {
        uint One(ushort tag, uint fallback) => tags.TryGetValue(tag, out var v) ? v[0] : fallback;
        uint Required(ushort tag, string name) =>
            tags.TryGetValue(tag, out var v) ? v[0] : throw new InvalidDataException($"The image has no {name} (tag {tag}).");

        var width = Required(ImageWidth, "ImageWidth");
        var height = Required(ImageLength, "ImageLength");
        if (width == 0 || height == 0 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException($"The image is {width} × {height} pixels: none, or more than {MaxPixels}.");
        }

        if (tags.ContainsKey(TileWidth))
        {
            throw new NotSupportedException("The image is in tiles, which are not read (only strips).");
        }

        var compression = One(Compression, None);
        if (compression is not (None or Lzw or PackBitsCompression))
        {
            throw new NotSupportedException($"The image is compressed with {CompressionName(compression)}, which is not read.");
        }

        var samples = (int)One(SamplesPerPixel, 1);
        var bits = tags.TryGetValue(BitsPerSample, out var b) ? b : [1];
        var depth = (int)bits[0];
        if (samples is < 1 or > 8 || Array.Exists(bits, x => x != depth) || depth is not (1 or 2 or 4 or 8 or 16))
        {
            throw new NotSupportedException($"Samples of {string.Join("/", bits)} bits ({samples} per pixel) are not read.");
        }

        if (One(PlanarConfiguration, 1) == 2 && samples > 1)
        {
            throw new NotSupportedException("The samples are planar (PlanarConfiguration 2), which is not read.");
        }

        var photometric = Required(Photometric, "PhotometricInterpretation");
        var colorSamples = photometric switch
        {
            WhiteIsZero or BlackIsZero or Palette => 1,
            Rgb => 3,
            Separated when One(InkSet, 1) == 1 => 4,
            _ => throw new NotSupportedException($"Photometric interpretation {photometric} is not read."),
        };
        if (samples < colorSamples || photometric is Rgb or Separated && depth < 8)
        {
            throw new NotSupportedException($"{samples} samples of {depth} bits for photometric interpretation {photometric} are not read.");
        }

        var alpha = samples > colorSamples && tags.TryGetValue(ExtraSamples, out var extra) ? extra[0] : 0u;
        var map = photometric == Palette ? Map(tags, depth) : null;

        var rowBytes = (width * (long)samples * depth + 7) / 8;
        var rowsPerStrip = Math.Min(One(RowsPerStrip, uint.MaxValue), height);
        var offsets = tags.TryGetValue(StripOffsets, out var o) ? o : throw new InvalidDataException("The image has no StripOffsets.");
        var counts = tags.TryGetValue(StripByteCounts, out var c) ? c : throw new InvalidDataException("The image has no StripByteCounts.");
        var strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        if (offsets.Length < strips || counts.Length < strips)
        {
            throw new InvalidDataException($"The image needs {strips} strips; StripOffsets and StripByteCounts give {Math.Min(offsets.Length, counts.Length)}.");
        }

        var predictor = One(Predictor, 1);
        var bitmap = new RgbaBitmap((int)width, (int)height);
        Array.Fill(bitmap.Pixels, (byte)255);
        var row = new byte[rowBytes];
        for (var s = 0; s < strips; s++)
        {
            var first = s * rowsPerStrip;
            var rows = Math.Min(rowsPerStrip, height - first);
            var expected = rows * rowBytes;
            var start = (long)offsets[s];
            var length = Math.Min(counts[s], Math.Max(0, file.Length - start));
            var packed = file.Slice(start, length);
            var strip = new byte[expected];
            var written = compression switch
            {
                Lzw => LzwDecode(packed, strip),
                PackBitsCompression => PackBits.Unpack(packed, strip).Written,
                _ => Copy(packed, strip),
            };
            if (written < expected)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "tiff.short-strip",
                    $"Strip {s} gives {written} of its {expected} bytes; the rows it lacks are left white."));
            }

            var whole = (int)(written / rowBytes);
            for (var r = 0; r < whole; r++)
            {
                strip.AsSpan((int)(r * rowBytes), (int)rowBytes).CopyTo(row);
                if (predictor == 2)
                {
                    Undifference(row, samples, depth, file.BigEndian);
                }

                WriteRow(bitmap, (int)(first + r), row, photometric, samples, depth, alpha, map, file.BigEndian);
            }
        }

        return bitmap;
    }

    private static int Copy(ReadOnlySpan<byte> from, Span<byte> to)
    {
        var n = Math.Min(from.Length, to.Length);
        from[..n].CopyTo(to);
        return n;
    }

    // TIFF's LZW (TIFF 6.0 §13): codes most significant bit first, 9 to 12 bits, Clear 256, EndOfInformation 257; the
    // code width grows one code early, as every TIFF writer does (libtiff's "early change"). The 5.0 bit-reversed
    // variant is not read.
    private static int LzwDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        const int Clear = 256, End = 257;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var lengths = new short[4096];
        for (var i = 0; i < 256; i++)
        {
            suffix[i] = (byte)i;
            lengths[i] = 1;
        }

        int next = 258, width = 9, previous = -1, written = 0;
        long bitPos = 0, totalBits = input.Length * 8L;
        Span<byte> stack = stackalloc byte[4096];
        while (bitPos + width <= totalBits && written < output.Length)
        {
            var code = 0;
            for (var k = 0; k < width; k++, bitPos++)
            {
                code = (code << 1) | ((input[(int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7))) & 1);
            }

            if (code == End)
            {
                break;
            }

            if (code == Clear)
            {
                next = 258;
                width = 9;
                previous = -1;
                continue;
            }

            int entry;
            if (code < next)
            {
                entry = code;
            }
            else if (code == next && previous >= 0)
            {
                entry = previous;     // KwKwK: the previous string, then its own first byte
            }
            else
            {
                break;                // a code not yet defined: damage; stop here
            }

            // The string of the entry, written backwards from its last byte.
            var length = lengths[entry];
            var at = entry;
            for (var k = length - 1; k >= 0; k--)
            {
                stack[k] = suffix[at];
                at = prefix[at];
            }

            var firstByte = stack[0];
            if (code == next)
            {
                stack[length] = firstByte;
                length++;
            }

            var copy = Math.Min(length, output.Length - written);
            stack[..copy].CopyTo(output[written..]);
            written += copy;

            if (previous >= 0 && next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next] = firstByte;
                lengths[next] = (short)(lengths[previous] + 1);
                next++;
                if (next == (1 << width) - 1 && width < 12)
                {
                    width++;
                }
            }

            previous = code;
        }

        return written;
    }

    // The horizontal predictor (TIFF 6.0 §14): each sample but a row's first is the difference from the one a pixel
    // before it.
    private static void Undifference(byte[] row, int samples, int depth, bool bigEndian)
    {
        if (depth == 8)
        {
            for (var i = samples; i < row.Length; i++)
            {
                row[i] += row[i - samples];
            }
        }
        else if (depth == 16)
        {
            var stride = samples * 2;
            for (var i = stride; i + 1 < row.Length; i += 2)
            {
                var sum = (ushort)(Read16(row, i, bigEndian) + Read16(row, i - stride, bigEndian));
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(i), sum);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(i), sum);
                }
            }
        }
    }

    private static ushort Read16(byte[] row, int at, bool bigEndian) =>
        bigEndian ? new BigEndianReader(row).ReadUInt16At(at) : BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(at));

    // One row of samples into RGBA.
    private static void WriteRow(RgbaBitmap bitmap, int y, byte[] row, uint photometric, int samples, int depth, uint alpha,
        RgbaColor[]? map, bool bigEndian)
    {
        var width = bitmap.Width;
        var pixels = bitmap.Pixels;
        var reader = depth == 16 && bigEndian ? new BigEndianReader(row) : null;

        // A sample scaled to 8 bits (16-bit samples keep their high byte; fewer bits are spread over 0–255).
        int Sample(int index)
        {
            switch (depth)
            {
                case 8:
                    return row[index];
                case 16:
                    var v = reader is not null ? reader.ReadUInt16At(index * 2) : BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(index * 2));
                    return v >> 8;
                default:
                    var bit = (long)index * depth;
                    var raw = (row[(int)(bit >> 3)] >> (8 - depth - (int)(bit & 7))) & ((1 << depth) - 1);
                    return raw;
            }
        }

        var max = (1 << Math.Min(depth, 8)) - 1;
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            var first = x * samples;
            byte r, g, bl, a = 255;
            switch (photometric)
            {
                case Palette:
                    var index = Sample(first);
                    var color = index < map!.Length ? map[index] : new RgbaColor(0, 0, 0);
                    (r, g, bl) = (color.R, color.G, color.B);
                    break;
                case WhiteIsZero or BlackIsZero:
                    var level = (byte)(Sample(first) * 255 / max);
                    if (photometric == WhiteIsZero)
                    {
                        level = (byte)(255 - level);
                    }

                    r = g = bl = level;
                    break;
                case Rgb:
                    (r, g, bl) = ((byte)Sample(first), (byte)Sample(first + 1), (byte)Sample(first + 2));
                    break;
                default:
                    // CMYK by subtraction: each ink takes away its colour and black takes away all (no colour management).
                    var k = Sample(first + 3);
                    r = (byte)Math.Max(0, 255 - Sample(first) - k);
                    g = (byte)Math.Max(0, 255 - Sample(first + 1) - k);
                    bl = (byte)Math.Max(0, 255 - Sample(first + 2) - k);
                    break;
            }

            if (alpha is 1 or 2 && photometric is Rgb or BlackIsZero or WhiteIsZero)
            {
                a = (byte)Sample(first + (photometric == Rgb ? 3 : 1));
                if (alpha == 1 && a is > 0 and < 255)
                {
                    // Associated alpha: the colour was multiplied by it.
                    r = (byte)Math.Min(255, (r * 255 + a / 2) / a);
                    g = (byte)Math.Min(255, (g * 255 + a / 2) / a);
                    bl = (byte)Math.Min(255, (bl * 255 + a / 2) / a);
                }
            }

            pixels[at] = r;
            pixels[at + 1] = g;
            pixels[at + 2] = bl;
            pixels[at + 3] = a;
        }
    }

    // A palette image's ColorMap: all reds, then all greens, then all blues, 16 bits each.
    private static RgbaColor[] Map(Dictionary<ushort, uint[]> tags, int depth)
    {
        var entries = 1 << Math.Min(depth, 8);
        if (!tags.TryGetValue(ColorMap, out var values) || values.Length < 3 * entries)
        {
            throw new InvalidDataException($"A palette image needs a ColorMap of {3 * entries} values.");
        }

        var map = new RgbaColor[entries];
        for (var i = 0; i < entries; i++)
        {
            map[i] = new RgbaColor((byte)(values[i] >> 8), (byte)(values[entries + i] >> 8), (byte)(values[2 * entries + i] >> 8));
        }

        return map;
    }

    private static string CompressionName(uint compression) => compression switch
    {
        2 => "CCITT modified Huffman",
        3 => "CCITT Group 3",
        4 => "CCITT Group 4",
        6 or 7 => "JPEG",
        8 or 32946 => "Deflate",
        32771 => "NeXT",
        _ => $"scheme {compression}",
    };

    // The file's bytes in its byte order: big-endian through BigEndianReader, little-endian through BinaryPrimitives.
    private sealed class TiffData(ReadOnlyMemory<byte> data)
    {
        private readonly BigEndianReader reader = new(data);

        public bool BigEndian { get; } = data.Span[0] == 'M';

        public long Length => data.Length;

        public byte Byte(long at) => data.Span[(int)at];

        public ushort U16(long at) => BigEndian ? reader.ReadUInt16At((int)at) : BinaryPrimitives.ReadUInt16LittleEndian(data.Span[(int)at..]);

        public uint U32(long at) => BigEndian ? reader.ReadUInt32At((int)at) : BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(int)at..]);

        public ReadOnlySpan<byte> Slice(long at, long length) =>
            at >= data.Length ? [] : data.Span.Slice((int)at, (int)Math.Min(length, data.Length - at));
    }
}
