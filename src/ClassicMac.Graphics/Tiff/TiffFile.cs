using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ClassicMac.Core;

namespace ClassicMac.Graphics;

/// <summary>
/// TIFF images (file type <c>TIFF</c>; <c>.tif</c>, <c>.tiff</c>; docs/formats/graphics/tiff.md): the first image of
/// the file, in either byte order, in strips or tiles, chunky or planar; uncompressed, PackBits, LZW (TIFF 6.0's and
/// TIFF 5.0's), Deflate, ThunderScan and CCITT fax (modified Huffman, Group 3, Group 4), with the horizontal
/// predictor; bilevel, grey, palette, RGB (with an alpha extra sample) and CMYK pixels of 1 to 32 bits per sample.
/// </summary>
public static class TiffFile
{
    private const ushort ImageWidth = 256, ImageLength = 257, BitsPerSample = 258, CompressionTag = 259, PhotometricTag = 262,
        StripOffsets = 273, SamplesPerPixel = 277, RowsPerStrip = 278, StripByteCounts = 279, PlanarConfiguration = 284,
        PredictorTag = 317, ColorMap = 320, TileWidth = 322, TileLength = 323, TileOffsets = 324, TileByteCounts = 325,
        InkSet = 332, ExtraSamples = 338, SampleFormat = 339, FillOrder = 266, T4Options = 292,
        YCbCrCoefficients = 529, YCbCrSubSampling = 530, ReferenceBlackWhite = 532, JpegTables = 347,
        JpegInterchangeFormat = 513, JpegInterchangeFormatLength = 514, JpegRestartInterval = 515, JpegQTables = 519,
        JpegDcTables = 520, JpegAcTables = 521;

    private const ushort None = 1, Lzw = 5, Deflate = 8, AdobeDeflate = 32946, PackBitsCompression = 32773, ThunderScan = 32809,
        CcittRle = 2, CcittGroup3 = 3, CcittGroup4 = 4, CcittRlew = 32771, OldJpeg = 6, Jpeg = 7;

    private const ushort WhiteIsZero = 0, BlackIsZero = 1, Rgb = 2, Palette = 3, Separated = 5, YCbCr = 6, CieLab = 8,
        IccLab = 9;

    /// <summary>The most pixels an image may have (width × height). Default 64 Mi.</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Whether <paramref name="start"/> begins a TIFF file: <c>II</c> and 42 little-endian, or <c>MM</c> and 42 big-endian.</summary>
    public static bool IsTiffFile(ReadOnlySpan<byte> start) =>
        start.Length >= 4 && (start[0] == 'I' && start[1] == 'I' && start[2] == 42 && start[3] == 0
            || start[0] == 'M' && start[1] == 'M' && start[2] == 0 && start[3] == 42);

    /// <summary>
    /// Decodes the file's first image to RGBA. Damage that leaves the image readable goes to
    /// <paramref name="diagnostics"/>; JPEG-compressed images need <see cref="TiffDecodeOptions.JpegDecoder"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a TIFF file, or its structures are damaged.</exception>
    /// <exception cref="NotSupportedException">The image is compressed or laid out in a way not read here (the message says which).</exception>
    public static RgbaBitmap Decode(ReadOnlyMemory<byte> data, ICollection<Diagnostic>? diagnostics = null, TiffDecodeOptions? options = null)
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

        var rationals = new Dictionary<ushort, double[]>();
        var image = Image.Read(file, ReadIfd(file, ifd, rationals), rationals, options ?? TiffDecodeOptions.Default);
        return image.Decode(file, diagnostics ?? []);
    }

    // The IFD's entries: tag → its values, each widened to uint; RATIONAL and SRATIONAL ones go to rationals as
    // numbers. The floating types are not needed.
    private static Dictionary<ushort, uint[]> ReadIfd(TiffData file, long ifd, Dictionary<ushort, double[]> rationals)
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
            var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, 5 or 10 => 8, _ => 0 };
            if (size == 0 || n == 0)
            {
                continue;
            }

            long at = n * size <= 4 ? entry + 8 : file.U32(entry + 8);
            if (n > file.Length || at + n * size > file.Length)
            {
                throw new InvalidDataException($"Tag {tag}'s {n} values lie outside the file.");
            }

            if (size == 8)
            {
                var numbers = new double[n];
                for (var k = 0; k < n; k++)
                {
                    uint numerator = file.U32(at + k * 8L), denominator = file.U32(at + k * 8L + 4);
                    numbers[k] = type == 10
                        ? (denominator == 0 ? 0 : (double)(int)numerator / (int)denominator)
                        : (denominator == 0 ? 0 : (double)numerator / denominator);
                }

                rationals[tag] = numbers;
                continue;
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

    // One image's layout, read from its tags: what its pixels are and where its strips or tiles lie.
    private sealed class Image
    {
        public int Width, Height, Samples, Depth, ColorSamples;
        public uint Photometric, Compression, Predictor, Alpha, FaxOptions, Format;
        public bool Planar, Reversed;
        public RgbaColor[]? Map;
        public int SubH = 1, SubV = 1;
        public double[] Coefficients = [0.299, 0.587, 0.114];
        public double[] Reference = [0, 255, 128, 255, 128, 255];
        public double FloatLow, FloatHigh = 1;

        public bool Float => Format == 3;

        public Func<byte[], RgbaBitmap?>? JpegDecoder;
        public byte[]? Tables;                              // JPEGTables, for compression 7
        public (long Offset, long Length)? Interchange;     // an old-style JPEG's whole stream
        public byte[]? OldTables;                           // an old-style JPEG's rebuilt tables (DQT, DHT, and DRI)

        // The units the pixels are stored in (strips or tiles): where each lies in the file and in the image.
        public readonly List<(long Offset, long Count, int X, int Y, int Width, int Height, int Plane)> Units = [];

        public static Image Read(TiffData file, Dictionary<ushort, uint[]> tags, Dictionary<ushort, double[]> rationals, TiffDecodeOptions options)
        {
            uint One(ushort tag, uint fallback) => tags.TryGetValue(tag, out var v) ? v[0] : fallback;
            uint Required(ushort tag, string name) =>
                tags.TryGetValue(tag, out var v) ? v[0] : throw new InvalidDataException($"The image has no {name} (tag {tag}).");

            var image = new Image();
            var width = Required(ImageWidth, "ImageWidth");
            var height = Required(ImageLength, "ImageLength");
            if (width == 0 || height == 0 || (long)width * height > MaxPixels)
            {
                throw new InvalidDataException($"The image is {width} × {height} pixels: none, or more than {MaxPixels}.");
            }

            (image.Width, image.Height) = ((int)width, (int)height);
            image.Compression = One(CompressionTag, None);
            if (image.Compression is not (None or Lzw or Deflate or AdobeDeflate or PackBitsCompression or ThunderScan
                or CcittRle or CcittGroup3 or CcittGroup4 or CcittRlew or OldJpeg or Jpeg))
            {
                throw new NotSupportedException($"The image is compressed with {CompressionName(image.Compression)}, which is not read.");
            }

            image.Samples = (int)One(SamplesPerPixel, 1);
            var bits = tags.TryGetValue(BitsPerSample, out var b) ? b : [1];
            image.Depth = (int)bits[0];
            image.Format = One(SampleFormat, 1);
            if (image.Format is not (1 or 2 or 3))
            {
                throw new NotSupportedException($"SampleFormat {image.Format} is not read.");
            }

            var depthRead = image.Float ? image.Depth is 16 or 32 or 64 : image.Depth is >= 1 and <= 32;
            if (image.Samples is < 1 or > 8 || Array.Exists(bits, x => x != image.Depth) || !depthRead)
            {
                throw new NotSupportedException($"Samples of {string.Join("/", bits)} bits ({image.Samples} per pixel{(image.Float ? ", floating point" : "")}) are not read.");
            }

            image.Photometric = Required(PhotometricTag, "PhotometricInterpretation");
            image.ColorSamples = image.Photometric switch
            {
                WhiteIsZero or BlackIsZero or Palette => 1,
                Rgb => 3,
                Separated when One(InkSet, 1) == 1 => 4,
                YCbCr or CieLab or IccLab => 3,
                _ => throw new NotSupportedException($"Photometric interpretation {image.Photometric} ({PhotometricName(image.Photometric)}) is not read."),
            };
            if (image.Samples < image.ColorSamples || image.Photometric == Palette && (image.Depth > 16 || image.Float)
                || image.Photometric is YCbCr or CieLab or IccLab && image.Float)
            {
                throw new NotSupportedException($"{image.Samples} samples of {image.Depth} bits for photometric interpretation {image.Photometric} are not read.");
            }

            if (image.Compression == ThunderScan && (image.Depth != 4 || image.Samples != 1))
            {
                throw new NotSupportedException($"ThunderScan compression of {image.Samples} samples of {image.Depth} bits is not read (only 4-bit grey).");
            }

            if (image.Compression is CcittRle or CcittGroup3 or CcittGroup4 or CcittRlew && (image.Depth != 1 || image.Samples != 1))
            {
                throw new NotSupportedException($"CCITT compression of {image.Samples} samples of {image.Depth} bits is not read (only bilevel).");
            }

            if (image.Compression is OldJpeg or Jpeg)
            {
                image.JpegDecoder = options.JpegDecoder ?? throw new NotSupportedException(
                    "The image is compressed with JPEG, which needs a JPEG decoder (TiffDecodeOptions.JpegDecoder).");
                if (tags.TryGetValue(JpegTables, out var tables))
                {
                    image.Tables = [.. Array.ConvertAll(tables, v => (byte)v)];
                }

                if (image.Compression == OldJpeg)
                {
                    if (tags.TryGetValue(JpegInterchangeFormat, out var at) && tags.TryGetValue(JpegInterchangeFormatLength, out var length)
                        && at[0] < file.Length && length[0] > 0)
                    {
                        image.Interchange = (at[0], Math.Min(length[0], file.Length - at[0]));
                    }
                    else
                    {
                        image.OldTables = OldJpegTables(file, tags, image.Samples);
                    }
                }
            }

            image.FaxOptions = One(T4Options, 0);
            image.Reversed = One(FillOrder, 1) == 2;
            image.Predictor = One(PredictorTag, 1);
            var predictorRead = image.Predictor switch
            {
                1 => true,
                2 => !image.Float && image.Depth is 8 or 16 or 32,
                3 => image.Float,
                _ => false,
            };
            if (!predictorRead)
            {
                throw new NotSupportedException($"Predictor {image.Predictor} with {image.Depth}-bit {(image.Float ? "floating-point" : "integer")} samples is not read.");
            }

            if (image.Photometric == YCbCr)
            {
                if (tags.TryGetValue(YCbCrSubSampling, out var sub) && sub.Length >= 2)
                {
                    (image.SubH, image.SubV) = ((int)sub[0], (int)sub[1]);
                }
                else
                {
                    (image.SubH, image.SubV) = (2, 2);
                }

                if (image.SubH is not (1 or 2 or 4) || image.SubV is not (1 or 2 or 4))
                {
                    throw new InvalidDataException($"YCbCr subsampling {image.SubH} × {image.SubV}.");
                }

                // JPEG streams carry their own subsampling; others come in data units of 8-bit samples (§2.6).
                if ((image.SubH, image.SubV) != (1, 1) && image.Compression is not (OldJpeg or Jpeg)
                    && (image.Depth != 8 || One(PlanarConfiguration, 1) == 2 || image.Predictor != 1 || image.Samples != 3))
                {
                    throw new NotSupportedException("Subsampled YCbCr other than 8-bit, chunky and without a predictor is not read.");
                }

                if (rationals.TryGetValue(YCbCrCoefficients, out var luma) && luma.Length >= 3 && luma[1] > 0)
                {
                    image.Coefficients = luma[..3];
                }
            }

            if (rationals.TryGetValue(ReferenceBlackWhite, out var reference) && reference.Length >= 6
                && reference[1] > reference[0] && reference[3] > reference[2] && reference[5] > reference[4])
            {
                image.Reference = reference[..6];
            }

            image.Alpha = image.Samples > image.ColorSamples && tags.TryGetValue(ExtraSamples, out var extra) ? extra[0] : 0u;
            image.Map = image.Photometric == Palette ? ReadMap(tags, image.Depth) : null;
            image.Planar = One(PlanarConfiguration, 1) == 2 && image.Samples > 1;
            var planes = image.Planar ? image.Samples : 1;

            if (tags.ContainsKey(TileWidth))
            {
                var tileWidth = (int)Required(TileWidth, "TileWidth");
                var tileLength = (int)Required(TileLength, "TileLength");
                if (tileWidth is <= 0 or > 65536 || tileLength is <= 0 or > 65536)
                {
                    throw new InvalidDataException($"Tiles of {tileWidth} × {tileLength} pixels.");
                }

                var across = (image.Width + tileWidth - 1) / tileWidth;
                var down = (image.Height + tileLength - 1) / tileLength;
                // A tiled image's offsets may stand under the strip tags, which libtiff takes as the same fields.
                var offsets = Values(tags, tags.ContainsKey(TileOffsets) ? TileOffsets : StripOffsets, "TileOffsets", (long)across * down * planes);
                var counts = Values(tags, tags.ContainsKey(TileByteCounts) ? TileByteCounts : StripByteCounts, "TileByteCounts", (long)across * down * planes);
                for (var p = 0; p < planes; p++)
                {
                    for (var t = 0; t < across * down; t++)
                    {
                        var i = p * across * down + t;
                        image.Units.Add((offsets[i], counts[i], t % across * tileWidth, t / across * tileLength, tileWidth, tileLength, p));
                    }
                }
            }
            else
            {
                var rowsPerStrip = (int)Math.Min(One(RowsPerStrip, uint.MaxValue), (uint)image.Height);
                var strips = (image.Height + rowsPerStrip - 1) / rowsPerStrip;
                var offsets = Values(tags, StripOffsets, "StripOffsets", (long)strips * planes);
                var counts = Values(tags, StripByteCounts, "StripByteCounts", (long)strips * planes);
                for (var p = 0; p < planes; p++)
                {
                    for (var s = 0; s < strips; s++)
                    {
                        var first = s * rowsPerStrip;
                        image.Units.Add((offsets[p * strips + s], counts[p * strips + s], 0, first, image.Width,
                            Math.Min(rowsPerStrip, image.Height - first), p));
                    }
                }
            }

            return image;
        }

        private static uint[] Values(Dictionary<ushort, uint[]> tags, ushort tag, string name, long needed)
        {
            var values = tags.TryGetValue(tag, out var v) ? v : throw new InvalidDataException($"The image has no {name}.");
            return values.Length >= needed ? values : throw new InvalidDataException($"The image needs {needed} {name}; it has {values.Length}.");
        }

        public RgbaBitmap Decode(TiffData file, ICollection<Diagnostic> diagnostics)
        {
            var bitmap = new RgbaBitmap(Width, Height);
            Array.Fill(bitmap.Pixels, (byte)255);
            var unitSamples = Planar ? 1 : Samples;
            // Planar and floating-point images gather their samples first (a plane each; floats for their range);
            // others go straight to the pixels.
            var gathered = Planar || Float ? new double[Samples][] : null;
            if (gathered is not null)
            {
                for (var p = 0; p < Samples; p++)
                {
                    gathered[p] = new double[(long)Width * Height];
                }
            }

            int shortUnits = 0;
            if (Interchange is { } whole)
            {
                if (JpegDecoder!(file.Slice(whole.Offset, whole.Length).ToArray()) is { } decoded)
                {
                    Place(bitmap, decoded, 0, 0, Width, Height);
                }
                else
                {
                    shortUnits++;
                }

                Report(diagnostics, shortUnits);
                return bitmap;
            }

            var pixel = new double[Samples];
            foreach (var unit in Units)
            {
                var subsampled = Photometric == YCbCr && !Planar && (SubH, SubV) != (1, 1);
                var rowBytes = ((long)unit.Width * unitSamples * Depth + 7) / 8;
                var blockBytes = SubH * SubV + 2;
                var expected = subsampled
                    ? (long)((unit.Width + SubH - 1) / SubH) * ((unit.Height + SubV - 1) / SubV) * blockBytes
                    : rowBytes * unit.Height;
                var start = unit.Offset;
                var length = Math.Min(unit.Count, Math.Max(0, file.Length - start));
                var packed = file.Slice(start, length);
                if (Reversed)
                {
                    // FillOrder 2: each byte's bits stored least significant first.
                    var reversed = packed.ToArray();
                    for (var i = 0; i < reversed.Length; i++)
                    {
                        reversed[i] = (byte)(((reversed[i] * 0x0202020202UL) & 0x010884422010UL) % 1023);
                    }

                    packed = reversed;
                }

                if (Compression is Jpeg or OldJpeg)
                {
                    if (JpegDecoder!(JpegStream(packed, unit.Width, unit.Height)) is { } decoded)
                    {
                        Place(bitmap, decoded, unit.X, unit.Y, unit.Width, unit.Height);
                    }
                    else
                    {
                        shortUnits++;
                    }

                    continue;
                }

                var buffer = new byte[expected];
                var written = Compression switch
                {
                    Lzw => LzwDecode(packed, buffer),
                    Deflate or AdobeDeflate => Inflate(packed, buffer),
                    PackBitsCompression => PackBits.Unpack(packed, buffer).Written,
                    ThunderScan => ThunderDecode(packed, buffer, unit.Width, unit.Height),
                    CcittRle or CcittGroup3 or CcittGroup4 or CcittRlew => CcittFax.Decode(packed, buffer, unit.Width, unit.Height, Compression, FaxOptions),
                    _ => Copy(packed, buffer),
                };
                if (written < expected)
                {
                    shortUnits++;
                }

                if (subsampled)
                {
                    WriteBlocks(bitmap, unit.X, unit.Y, unit.Width, unit.Height, buffer, written);
                    continue;
                }

                // One reader over the unit's bytes for its big-endian samples.
                var reader = file.BigEndian ? new BigEndianReader(buffer) : null;
                var rows = (int)Math.Min(unit.Height, written / rowBytes);
                for (var r = 0; r < rows; r++)
                {
                    var y = unit.Y + r;
                    if (y >= Height)
                    {
                        break;
                    }

                    var row = new Row(buffer, (int)(r * rowBytes), (int)rowBytes, reader);
                    if (Predictor == 2)
                    {
                        Undifference(row, unitSamples, Depth);
                    }
                    else if (Predictor == 3)
                    {
                        FloatUndifference(row, unitSamples, Depth);
                    }

                    var columns = Math.Min(unit.Width, Width - unit.X);
                    for (var c = 0; c < columns; c++)
                    {
                        var x = unit.X + c;
                        if (gathered is not null)
                        {
                            if (Planar)
                            {
                                gathered[unit.Plane][(long)y * Width + x] = Value(row.Sample(c, Depth));
                                continue;
                            }

                            for (var s = 0; s < Samples; s++)
                            {
                                gathered[s][(long)y * Width + x] = Value(row.Sample(c * Samples + s, Depth));
                            }

                            continue;
                        }

                        for (var s = 0; s < Samples; s++)
                        {
                            pixel[s] = Value(row.Sample(c * Samples + s, Depth));
                        }

                        WritePixel(bitmap, x, y, pixel);
                    }
                }
            }

            if (gathered is not null)
            {
                if (Float)
                {
                    // Floats in 0–1 are levels; others are stretched over the range the colour samples span.
                    (FloatLow, FloatHigh) = (0, 1);
                    double low = double.MaxValue, high = double.MinValue;
                    for (var s = 0; s < ColorSamples; s++)
                    {
                        foreach (var v in gathered[s])
                        {
                            if (double.IsFinite(v))
                            {
                                low = Math.Min(low, v);
                                high = Math.Max(high, v);
                            }
                        }
                    }

                    if (low < 0 || high > 1)
                    {
                        (FloatLow, FloatHigh) = (low, high > low ? high : low + 1);
                    }
                }

                for (long i = 0; i < (long)Width * Height; i++)
                {
                    for (var s = 0; s < Samples; s++)
                    {
                        pixel[s] = gathered[s][i];
                    }

                    WritePixel(bitmap, (int)(i % Width), (int)(i / Width), pixel);
                }
            }

            Report(diagnostics, shortUnits);
            return bitmap;
        }

        private void Report(ICollection<Diagnostic> diagnostics, int shortUnits)
        {
            if (shortUnits > 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "tiff.short-strip",
                    $"{shortUnits} of the image's {Units.Count} strips or tiles give fewer bytes than their rows, or do not decode; what they lack is left white."));
            }
        }

        // A JPEG stream for a strip or tile (tiff.md §2.9): new style, the JPEGTables without their EOI and then the
        // unit without its SOI; old style, the tables rebuilt from the table tags, a frame and a scan header, the
        // unit's entropy-coded data and EOI.
        private byte[] JpegStream(ReadOnlySpan<byte> unit, int unitWidth, int unitHeight)
        {
            if (Compression == Jpeg)
            {
                if (Tables is not { Length: >= 4 } tables || unit.Length < 2)
                {
                    return unit.ToArray();
                }

                return [.. tables.AsSpan(0, tables.Length - 2), .. unit[2..]];
            }

            var stream = new List<byte>(OldTables!.Length + unit.Length + 64) { 0xFF, 0xD8 };
            stream.AddRange(OldTables);
            var components = Samples;
            // SOF0: baseline, 8-bit, the unit's size; Y takes the subsampling, the others 1 × 1; component i uses table i.
            stream.AddRange([0xFF, 0xC0, 0, (byte)(8 + 3 * components), 8, (byte)(unitHeight >> 8), (byte)unitHeight,
                (byte)(unitWidth >> 8), (byte)unitWidth, (byte)components]);
            for (var c = 0; c < components; c++)
            {
                var sampling = c == 0 && Photometric == YCbCr ? (byte)(SubH << 4 | SubV) : (byte)0x11;
                stream.AddRange([(byte)(c + 1), sampling, (byte)c]);
            }

            // SOS: every component, its DC and AC tables, the whole spectrum.
            stream.AddRange([0xFF, 0xDA, 0, (byte)(6 + 2 * components), (byte)components]);
            for (var c = 0; c < components; c++)
            {
                stream.AddRange([(byte)(c + 1), (byte)(c << 4 | c)]);
            }

            stream.AddRange([0, 63, 0]);
            stream.AddRange(unit.ToArray());
            stream.AddRange([0xFF, 0xD9]);
            return [.. stream];
        }

        // A decoded strip or tile drawn at its place, cut to its unit and the image.
        private void Place(RgbaBitmap bitmap, RgbaBitmap decoded, int x0, int y0, int unitWidth, int unitHeight)
        {
            var width = Math.Min(Math.Min(decoded.Width, unitWidth), Width - x0);
            var height = Math.Min(Math.Min(decoded.Height, unitHeight), Height - y0);
            for (var y = 0; y < height; y++)
            {
                decoded.Pixels.AsSpan(y * decoded.Width * 4, width * 4).CopyTo(bitmap.Pixels.AsSpan(((y0 + y) * Width + x0) * 4));
            }
        }

        // An old-style JPEG's tables (TIFF 6.0 §22): JPEGQTables, JPEGDCTables and JPEGACTables give one offset per
        // component, to 64 quantisation values or to 16 code counts and their values; JPEGRestartInterval a DRI.
        private static byte[] OldJpegTables(TiffData file, Dictionary<ushort, uint[]> tags, int components)
        {
            uint[] Offsets(ushort tag, string name) =>
                tags.TryGetValue(tag, out var v) && v.Length >= components ? v
                    : throw new NotSupportedException($"An old-style JPEG image without JPEGInterchangeFormat needs {name} for each component.");

            var q = Offsets(JpegQTables, "JPEGQTables");
            var dc = Offsets(JpegDcTables, "JPEGDCTables");
            var ac = Offsets(JpegAcTables, "JPEGACTables");
            var tables = new List<byte>();
            for (var c = 0; c < components; c++)
            {
                if (q[c] + 64L > file.Length)
                {
                    throw new InvalidDataException($"JPEGQTables[{c}] lies outside the file.");
                }

                tables.AddRange([0xFF, 0xDB, 0, 67, (byte)c]);
                tables.AddRange(file.Slice(q[c], 64).ToArray());
            }

            void Huffman(uint[] offsets, int tableClass)
            {
                for (var c = 0; c < components; c++)
                {
                    if (offsets[c] + 16L > file.Length)
                    {
                        throw new InvalidDataException("A JPEG Huffman table lies outside the file.");
                    }

                    var counts = file.Slice(offsets[c], 16);
                    var values = 0;
                    foreach (var n in counts)
                    {
                        values += n;
                    }

                    if (offsets[c] + 16L + values > file.Length)
                    {
                        throw new InvalidDataException("A JPEG Huffman table lies outside the file.");
                    }

                    var length = 2 + 1 + 16 + values;
                    tables.AddRange([0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)(tableClass << 4 | c)]);
                    tables.AddRange(file.Slice(offsets[c], 16 + values).ToArray());
                }
            }

            Huffman(dc, 0);
            Huffman(ac, 1);
            if (tags.TryGetValue(JpegRestartInterval, out var restart) && restart[0] is > 0 and <= 65535)
            {
                tables.AddRange([0xFF, 0xDD, 0, 4, (byte)(restart[0] >> 8), (byte)restart[0]]);
            }

            return [.. tables];
        }

        // A sample's bits as a number: unsigned, signed (two's complement) or IEEE floating point.
        private double Value(ulong raw)
        {
            switch (Format)
            {
                case 2:
                    var shift = 64 - Depth;
                    return (long)(raw << shift) >> shift;
                case 3:
                    return Depth switch
                    {
                        16 => (double)BitConverter.UInt16BitsToHalf((ushort)raw),
                        32 => BitConverter.UInt32BitsToSingle((uint)raw),
                        64 => BitConverter.UInt64BitsToDouble(raw),
                        _ => 0,
                    };
                default:
                    return raw;
            }
        }

        // A colour sample's value as a level 0–255: an integer's high 8 bits (a signed one first moved up by half its
        // range), a shallower integer spread over 0–255, a float by its range.
        private int Level(double value)
        {
            switch (Format)
            {
                case 3:
                    var level = (value - FloatLow) / (FloatHigh - FloatLow) * 255;
                    return double.IsNaN(level) ? 0 : (int)Math.Round(Math.Clamp(level, 0, 255));
                case 2:
                    value += Math.Pow(2, Depth - 1);
                    break;
            }

            var raw = (ulong)value;
            return Depth >= 8 ? (int)(raw >> (Depth - 8)) : (int)(raw * 255 / ((1UL << Depth) - 1));
        }

        // Subsampled YCbCr data units (TIFF 6.0 §21): each SubH × SubV luma samples, row by row, then Cb and Cr.
        private void WriteBlocks(RgbaBitmap bitmap, int x0, int y0, int unitWidth, int unitHeight, byte[] buffer, long written)
        {
            var across = (unitWidth + SubH - 1) / SubH;
            var down = (unitHeight + SubV - 1) / SubV;
            var blockBytes = SubH * SubV + 2;
            for (var by = 0; by < down; by++)
            {
                for (var bx = 0; bx < across; bx++)
                {
                    var at = ((long)by * across + bx) * blockBytes;
                    if (at + blockBytes > written)
                    {
                        return;
                    }

                    var cb = buffer[at + SubH * SubV];
                    var cr = buffer[at + SubH * SubV + 1];
                    for (var j = 0; j < SubV; j++)
                    {
                        for (var i = 0; i < SubH; i++)
                        {
                            var x = x0 + bx * SubH + i;
                            var y = y0 + by * SubV + j;
                            if (x >= Width || y >= Height || x >= x0 + unitWidth || y >= y0 + unitHeight)
                            {
                                continue;
                            }

                            var (r, g, b) = FromYCbCr(buffer[at + j * SubH + i], cb, cr);
                            var p = (y * Width + x) * 4;
                            (bitmap.Pixels[p], bitmap.Pixels[p + 1], bitmap.Pixels[p + 2], bitmap.Pixels[p + 3]) = (r, g, b, 255);
                        }
                    }
                }
            }
        }

        // YCbCr to RGB (TIFF 6.0 §21): each component scaled by ReferenceBlackWhite (Y to 0–255, Cb and Cr to ±127),
        // then R = Y + Cr × (2 − 2 × LumaRed), B = Y + Cb × (2 − 2 × LumaBlue), G = (Y − LumaBlue × B − LumaRed × R) / LumaGreen.
        private (byte R, byte G, byte B) FromYCbCr(double y, double cb, double cr)
        {
            var luma = (y - Reference[0]) * 255 / (Reference[1] - Reference[0]);
            var blue = (cb - Reference[2]) * 127 / (Reference[3] - Reference[2]);
            var red = (cr - Reference[4]) * 127 / (Reference[5] - Reference[4]);
            var r = luma + red * (2 - 2 * Coefficients[0]);
            var b = luma + blue * (2 - 2 * Coefficients[2]);
            var g = (luma - Coefficients[2] * b - Coefficients[0] * r) / Coefficients[1];
            static byte Clamp(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
            return (Clamp(r), Clamp(g), Clamp(b));
        }

        // CIE L*a*b* to sRGB: L in 0–100, a and b about 0, through XYZ with the D65 white, the sRGB matrix and curve.
        private static (byte R, byte G, byte B) FromLab(double l, double a, double b)
        {
            const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
            var fy = (l + 16) / 116;
            var fx = fy + a / 500;
            var fz = fy - b / 200;
            static double Inverse(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - 4.0 / 29);
            var x = Xn * Inverse(fx);
            var y = Yn * Inverse(fy);
            var z = Zn * Inverse(fz);
            var rl = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
            var gl = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
            var bl = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
            static byte Gamma(double v)
            {
                v = Math.Clamp(v, 0, 1);
                var s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
                return (byte)Math.Round(s * 255);
            }

            return (Gamma(rl), Gamma(gl), Gamma(bl));
        }

        // One pixel's sample values into RGBA.
        private void WritePixel(RgbaBitmap bitmap, int x, int y, double[] samples)
        {
            var at = (y * bitmap.Width + x) * 4;
            int r, g, b, a = 255;
            switch (Photometric)
            {
                case Palette:
                    var index = (ulong)samples[0];
                    var color = index < (ulong)Map!.Length ? Map[index] : new RgbaColor(0, 0, 0);
                    (r, g, b) = (color.R, color.G, color.B);
                    break;
                case WhiteIsZero or BlackIsZero:
                    r = g = b = Photometric == WhiteIsZero ? 255 - Level(samples[0]) : Level(samples[0]);
                    break;
                case Rgb:
                    (r, g, b) = (Level(samples[0]), Level(samples[1]), Level(samples[2]));
                    break;
                case YCbCr:
                    (r, g, b) = FromYCbCr(Level(samples[0]), Level(samples[1]), Level(samples[2]));
                    break;
                case CieLab or IccLab:
                    // L over 0–100 from the sample's range; a and b signed (CIELab) or about 2^(bits−1) (ICCLab), in
                    // units of 1 at 8 bits and 1/256 at 16.
                    var full = Math.Pow(2, Depth) - 1;
                    var unit = Depth > 8 ? Math.Pow(2, Depth - 8) : 1;
                    var middle = Photometric == IccLab ? Math.Pow(2, Depth - 1) : 0;
                    var signedA = Photometric == CieLab && Format != 2 ? Signed(samples[1]) : samples[1];
                    var signedB = Photometric == CieLab && Format != 2 ? Signed(samples[2]) : samples[2];
                    (r, g, b) = FromLab(samples[0] * 100 / full, (signedA - middle) / unit, (signedB - middle) / unit);
                    break;
                default:
                    // CMYK without colour management, as libtiff's RGBA interface converts it: each ink and black
                    // multiply what is left.
                    var k = 255 - Level(samples[3]);
                    r = (255 - Level(samples[0])) * k / 255;
                    g = (255 - Level(samples[1])) * k / 255;
                    b = (255 - Level(samples[2])) * k / 255;
                    break;
            }

            if (Alpha is 1 or 2 && Photometric is Rgb or BlackIsZero or WhiteIsZero)
            {
                a = Level(samples[ColorSamples]);
                if (Alpha == 1 && a is > 0 and < 255)
                {
                    // Associated alpha: the colour was multiplied by it.
                    r = Math.Min(255, (r * 255 + a / 2) / a);
                    g = Math.Min(255, (g * 255 + a / 2) / a);
                    b = Math.Min(255, (b * 255 + a / 2) / a);
                }
            }

            bitmap.Pixels[at] = (byte)r;
            bitmap.Pixels[at + 1] = (byte)g;
            bitmap.Pixels[at + 2] = (byte)b;
            bitmap.Pixels[at + 3] = (byte)a;
        }

        // CIELab's a and b stored unsigned (SampleFormat 1) are still two's complement numbers.
        private double Signed(double raw) => raw >= Math.Pow(2, Depth - 1) ? raw - Math.Pow(2, Depth) : raw;
    }

    // A row of a decoded unit: its bytes, and the unit's reader when the file is big-endian.
    private readonly record struct Row(byte[] Buffer, int Start, int Length, BigEndianReader? Reader)
    {
        // The sample at an index: whole bytes (8, 16, 24, 32, 64 bits) in the file's byte order; other depths as a
        // bit stream, most significant bit first.
        public ulong Sample(int index, int depth)
        {
            switch (depth)
            {
                case 8:
                    return Buffer[Start + index];
                case 16:
                    var at16 = Start + index * 2;
                    return Reader is not null ? Reader.ReadUInt16At(at16) : BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(at16));
                case 24:
                    var at24 = Start + index * 3;
                    return Reader is not null
                        ? (ulong)(Reader.ReadUInt16At(at24) << 8 | Buffer[at24 + 2])
                        : (ulong)(Buffer[at24 + 2] << 16 | BinaryPrimitives.ReadUInt16LittleEndian(Buffer.AsSpan(at24)));
                case 32:
                    var at32 = Start + index * 4;
                    return Reader is not null ? Reader.ReadUInt32At(at32) : BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(at32));
                case 64:
                    var at64 = Start + index * 8;
                    return Reader is not null
                        ? (ulong)Reader.ReadUInt32At(at64) << 32 | Reader.ReadUInt32At(at64 + 4)
                        : BinaryPrimitives.ReadUInt64LittleEndian(Buffer.AsSpan(at64));
                default:
                    var bit = (long)Start * 8 + (long)index * depth;
                    ulong value = 0;
                    for (var k = 0; k < depth; k++, bit++)
                    {
                        value = (value << 1) | (uint)((Buffer[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
                    }

                    return value;
            }
        }

        // Writes a whole-byte sample back in the file's byte order.
        public void Put(int index, int size, ulong value)
        {
            var at = Start + index * size;
            for (var k = 0; k < size; k++)
            {
                Buffer[at + (Reader is not null ? size - 1 - k : k)] = (byte)(value >> (8 * k));
            }
        }
    }

    private static int Copy(ReadOnlySpan<byte> from, Span<byte> to)
    {
        var n = Math.Min(from.Length, to.Length);
        from[..n].CopyTo(to);
        return n;
    }

    // Deflate (zlib) data, as Adobe's and the TIFF 6.0 technical note's code 8 have it.
    private static int Inflate(ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var stream = new ZLibStream(new MemoryStream(input.ToArray()), CompressionMode.Decompress);
        var written = 0;
        try
        {
            while (written < output.Length && stream.Read(output[written..]) is var n and > 0)
            {
                written += n;
            }
        }
        catch (InvalidDataException)
        {
            // Damaged data: what was inflated stays.
        }

        return written;
    }

    // TIFF's LZW (TIFF 6.0 §13), or TIFF 5.0's: a strip starting with Clear written least significant bit first
    // (00 01) is the old kind, whose codes are bit-reversed and whose width grows at the usual place, not one code early.
    private static int LzwDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        const int Clear = 256, End = 257;
        var old = input.Length >= 2 && input[0] == 0 && (input[1] & 1) == 1;
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
        Span<byte> stack = stackalloc byte[4097];
        while (bitPos + width <= totalBits && written < output.Length)
        {
            var code = 0;
            for (var k = 0; k < width; k++, bitPos++)
            {
                var bit = old
                    ? (input[(int)(bitPos >> 3)] >> (int)(bitPos & 7)) & 1
                    : (input[(int)(bitPos >> 3)] >> (7 - (int)(bitPos & 7))) & 1;
                code = old ? code | bit << k : (code << 1) | bit;
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
                var grow = old ? 1 << width : (1 << width) - 1;
                if (next == grow && width < 12)
                {
                    width++;
                }
            }

            previous = code;
        }

        return written;
    }

    // ThunderScan's 4-bit compression (Thunderware's Macintosh scanner software; tiff.md §2.4), row by row: each row
    // starts from pixel 0. A byte's top two bits say what it is: 00 the last pixel again, as many times as its low six
    // bits; 01 three 2-bit deltas; 10 two 3-bit deltas; 11 a pixel in its low four bits.
    private static int ThunderDecode(ReadOnlySpan<byte> input, Span<byte> output, int width, int height)
    {
        ReadOnlySpan<int> twoBit = [0, 1, 0, -1];      // 2 is "skip"
        ReadOnlySpan<int> threeBit = [0, 1, 2, 3, 0, -3, -2, -1];  // 4 is "skip"
        var rowBytes = (width + 1) / 2;
        var at = 0;
        var rows = 0;
        for (; rows < height && at < input.Length; rows++)
        {
            var row = output.Slice(rows * rowBytes, rowBytes);
            row.Clear();
            int last = 0, count = 0;
            void Set(Span<byte> r, int value)
            {
                last = value & 0xF;
                if (count < width)
                {
                    r[count >> 1] |= (byte)((count & 1) == 0 ? last << 4 : last);
                }

                count++;
            }

            while (count < width && at < input.Length)
            {
                var n = input[at++];
                switch (n & 0xC0)
                {
                    case 0x00:
                        for (var k = 0; k < (n & 0x3F); k++)
                        {
                            Set(row, last);
                        }

                        break;
                    case 0x40:
                        foreach (var shift in (ReadOnlySpan<int>)[4, 2, 0])
                        {
                            var delta = (n >> shift) & 3;
                            if (delta != 2)
                            {
                                Set(row, last + twoBit[delta]);
                            }
                        }

                        break;
                    case 0x80:
                        foreach (var shift in (ReadOnlySpan<int>)[3, 0])
                        {
                            var delta = (n >> shift) & 7;
                            if (delta != 4)
                            {
                                Set(row, last + threeBit[delta]);
                            }
                        }

                        break;
                    default:
                        Set(row, n);
                        break;
                }
            }

            if (count < width)
            {
                break;
            }
        }

        return rows * rowBytes;
    }

    // The horizontal predictor (TIFF 6.0 §14): each sample but a row's first is the difference from the one a pixel
    // before it, modulo its size.
    private static void Undifference(Row row, int samples, int depth)
    {
        var size = depth / 8;
        var count = row.Length / size;
        for (var i = samples; i < count; i++)
        {
            row.Put(i, size, row.Sample(i, depth) + row.Sample(i - samples, depth));
        }
    }

    // The floating-point predictor (Adobe Photoshop TIFF Technical Note 3): a row's samples split into byte planes,
    // most significant first, then each byte the difference from the one a pixel (a sample count) before it.
    private static void FloatUndifference(Row row, int samples, int depth)
    {
        var size = depth / 8;
        var bytes = row.Buffer.AsSpan(row.Start, row.Length);
        for (var i = samples; i < bytes.Length; i++)
        {
            bytes[i] += bytes[i - samples];
        }

        var count = bytes.Length / size;
        var planes = bytes.ToArray();
        for (var c = 0; c < count; c++)
        {
            ulong value = 0;
            for (var k = 0; k < size; k++)
            {
                value = value << 8 | planes[k * count + c];
            }

            row.Put(c, size, value);
        }
    }

    // A palette image's ColorMap: all reds, then all greens, then all blues, 16 bits each.
    private static RgbaColor[] ReadMap(Dictionary<ushort, uint[]> tags, int depth)
    {
        var entries = 1 << depth;
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
        32766 => "NeXT",
        34676 or 34677 => "SGILog",
        _ => $"scheme {compression}",
    };

    private static string PhotometricName(uint photometric) => photometric switch
    {
        4 => "transparency mask",
        6 => "YCbCr",
        8 => "CIE L*a*b*",
        32844 or 32845 => "LogL/LogLuv",
        _ => "unknown",
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
