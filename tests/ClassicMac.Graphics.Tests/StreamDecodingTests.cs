using System.Text;
using ClassicMac.Graphics;
using ClassicMac.Graphics.ImageSharp;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickTime;
using ClassicMac.Graphics.SkiaSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace ClassicMac.Graphics.Tests;

// PICT, QuickTime image files and MacPaint read from streams: the same results as from bytes, from
// non-seekable streams that hand out a byte at a time, from the stream's current position, leaving it open.
public class StreamDecodingTests
{
    private static readonly Configuration Config = CreateConfiguration();
    private static readonly DecoderOptions Options = new() { Configuration = Config };

    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.Configure(new PictConfigurationModule());
        return configuration;
    }

    // Non-seekable; at most `chunk` bytes a read; records disposal.
    private sealed class TrickleStream(byte[] data, int chunk = 1) : Stream
    {
        private int position;

        public bool Disposed { get; private set; }

        public bool AtEnd => position == data.Length;

        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            int n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - position);
            data.AsSpan(position, n).CopyTo(buffer);
            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static RgbaBitmap TestCard(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap[x, y] = new RgbaColor((byte)(x * 40), (byte)(y * 60), (byte)((x + y) * 20));
            }
        }

        return bitmap;
    }

    private static byte[] Written(PictWriteOptions options)
    {
        using var stream = new MemoryStream();
        PictWriter.Write(stream, TestCard(5, 3), options);
        return stream.ToArray();
    }

    public static TheoryData<string> Pictures() => new() { "v1", "v2", "v2 without HeaderOp", "file rgb888 144 dpi icc", "bare indexed", "bare rgb555" };

    private static byte[] Picture(string name) => name switch
    {
        "v1" => PictBuilder.V1(0, 0, 4, 6).U8(0x31).Rect(1, 1, 3, 5).U8(0xFF).ToArray(),          // paintRect
        "v2" => PictBuilder.V2(0, 0, 4, 6).U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(1, 1, 3, 5)
            .U16(0x00A0).U16(130).U16(0x00FF).ToArray(),                                            // with a ShortComment
        "v2 without HeaderOp" => new PictBuilder().U16(0).Rect(0, 0, 4, 6).U16(0x0011).U16(0x02FF)
            .U16(0x0031).Rect(1, 1, 3, 5).U16(0x00FF).ToArray(),
        "file rgb888 144 dpi icc" => Written(new PictWriteOptions
        { HorizontalResolution = 144, VerticalResolution = 144, IccProfile = [1, 2, 3, 4, 5] }),
        "bare indexed" => Written(new PictWriteOptions { FileHeader = false, Format = PictPixelFormat.Indexed8 }),
        "bare rgb555" => Written(new PictWriteOptions { FileHeader = false, Format = PictPixelFormat.Rgb555 }),
        _ => throw new ArgumentException(name),
    };

    private static void AssertSameInfo(PictInfo expected, PictInfo actual)
    {
        Assert.Equal((expected.Version, expected.IsExtendedVersion2, expected.PictureFrame, expected.Bounds),
            (actual.Version, actual.IsExtendedVersion2, actual.PictureFrame, actual.Bounds));
        Assert.Equal((expected.HorizontalResolution, expected.VerticalResolution), (actual.HorizontalResolution, actual.VerticalResolution));
        Assert.Equal(expected.IccProfile, actual.IccProfile);
        Assert.Equal(expected.Comments.Select(c => (c.Kind, c.Data.ToArray())), actual.Comments.Select(c => (c.Kind, c.Data.ToArray())));
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void Pictures_read_from_a_stream_match_the_bytes(string name)
    {
        var data = Picture(name);
        var expected = PictReader.Read(data);
        var stream = new TrickleStream(data);
        var actual = PictReader.Read(stream);
        Assert.Equal(expected.Bitmap.Pixels, actual.Bitmap.Pixels);
        AssertSameInfo(expected.Info, actual.Info);
        Assert.True(stream.AtEnd);
        Assert.False(stream.Disposed);

        var info = PictHeader.ReadInfo(new TrickleStream(data));
        Assert.Equal((expected.Info.Version, expected.Info.Bounds, expected.Info.HorizontalResolution),
            (info.Version, info.Bounds, info.HorizontalResolution));
    }

    [Fact]
    public void A_picture_is_read_from_the_streams_position_to_its_end()
    {
        var picture = Picture("file rgb888 144 dpi icc");
        var stream = new MemoryStream([.. "junk"u8, .. picture]) { Position = 4 };
        var info = PictHeader.ReadInfo(stream);
        Assert.Equal(144, info.HorizontalResolution);
        Assert.Equal(stream.Length, stream.Position);

        stream.Position = 4;
        Assert.Equal(PictReader.Decode(picture).Pixels, PictReader.Decode(stream).Pixels);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Truncated_pictures_fail_alike_from_bytes_and_streams()
    {
        // Cut anywhere, a picture gives the same pixels or the same exception either way.
        static string Outcome(Func<RgbaBitmap> decode)
        {
            try
            { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(decode().Pixels)); }
            catch (Exception ex) { return ex.GetType().Name; }
        }
        var picture = Picture("bare indexed");
        var outcomes = new HashSet<string>();
        for (int length = 0; length < picture.Length; length += 7)
        {
            var cut = picture[..length];
            string fromBytes = Outcome(() => PictReader.Decode(cut));
            Assert.Equal(fromBytes, Outcome(() => PictReader.Decode(new TrickleStream(cut))));
            outcomes.Add(fromBytes);
        }
        Assert.Contains(nameof(EndOfStreamException), outcomes);
        Assert.Throws<EndOfStreamException>(() => PictHeader.ReadInfo(new TrickleStream(picture[..11])));
        Assert.Throws<NotSupportedException>(() => PictReader.Decode(new TrickleStream(new byte[64])));
    }

    // ---- QuickTime image files ----

    private static byte[] Atom(string type, byte[] content, bool extended = false, int? declaredSize = null)
    {
        var b = new PictBuilder();
        if (extended)
        {
            long size = declaredSize ?? content.Length + 16;
            b.U16(0).U16(1).Bytes(Encoding.ASCII.GetBytes(type)).U16((int)(size >> 48)).U16((int)(size >> 32)).U16((int)(size >> 16)).U16((int)size);
        }
        else
        {
            int size = declaredSize ?? content.Length + 8;
            b.U16(size >> 16).U16(size).Bytes(Encoding.ASCII.GetBytes(type));
        }
        return b.Bytes(content).ToArray();
    }

    private static byte[] Description(int width, int height) =>
        new PictBuilder().U16(0).U16(86).Bytes(Encoding.ASCII.GetBytes("raw ")).Zeros(8).U16(0).U16(0).Zeros(4)
            .Zeros(8).U16(width).U16(height).U16(144).U16(0).U16(72).U16(0).U16(0).U16(8).U16(1).U8(0).Zeros(31).U16(32).U16(0xFFFF).ToArray();

    private static readonly byte[] Pixels = [0, 255, 0, 0, 0, 0, 0, 255];        // 2 x 1 ARGB: red, blue

    public static TheoryData<string> QuickTimeFiles() =>
        new() { "plain", "unknown atoms and idat first", "extended sizes", "profile cut at the end", "size 0 runs to the end" };

    private static byte[] QuickTimeFile(string name) => name switch
    {
        "plain" => [.. Atom("idsc", Description(2, 1)), .. Atom("idat", Pixels), .. Atom("iicc", [1, 2, 3])],
        "unknown atoms and idat first" => [.. Atom("free", new byte[5]), .. Atom("idat", Pixels), .. Atom("skip", [9]),
            .. Atom("idsc", Description(2, 1)), .. Atom("iicc", [4, 5]), .. Atom("iicc", [6])],
        "extended sizes" => [.. Atom("idsc", Description(2, 1), extended: true), .. Atom("idat", Pixels, extended: true)],
        "profile cut at the end" => [.. Atom("idsc", Description(2, 1)), .. Atom("idat", Pixels), .. Atom("iicc", [7, 8], declaredSize: 100)],
        "size 0 runs to the end" => [.. Atom("idsc", Description(2, 1)), .. Atom("idat", Pixels, declaredSize: 0)],
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(QuickTimeFiles))]
    public void QuickTime_files_read_from_a_stream_match_the_bytes(string name)
    {
        var data = QuickTimeFile(name);
        var stream = new TrickleStream(data, chunk: 3);
        var result = QuickTimeImageFile.Read(stream);
        Assert.Equal(QuickTimeImageFile.Decode(data).Pixels, result.Bitmap.Pixels);
        var description = QuickTimeImageFile.ReadDescription(data);
        Assert.Equal((description.CodecType, description.Width, description.Height, description.Depth, description.HorizontalResolution),
            (result.Description.CodecType, result.Description.Width, result.Description.Height, result.Description.Depth,
                result.Description.HorizontalResolution));
        Assert.Equal(QuickTimeImageFile.ReadIccProfile(data), result.IccProfile);
        Assert.Equal(new RgbaColor(255, 0, 0), result.Bitmap[0, 0]);
        Assert.True(stream.AtEnd);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public void QuickTime_files_without_a_description_or_image_fail_alike()
    {
        byte[] noImage = Atom("idsc", Description(2, 1)), noDescription = Atom("idat", Pixels);
        Assert.Equal(Assert.Throws<NotSupportedException>(() => QuickTimeImageFile.Decode(noImage)).Message,
            Assert.Throws<NotSupportedException>(() => QuickTimeImageFile.Decode(new TrickleStream(noImage))).Message);
        Assert.Equal(Assert.Throws<NotSupportedException>(() => QuickTimeImageFile.Decode(noDescription)).Message,
            Assert.Throws<NotSupportedException>(() => QuickTimeImageFile.Decode(new TrickleStream(noDescription))).Message);
        Assert.Throws<OperationCanceledException>(() =>
            QuickTimeImageFile.Decode(new TrickleStream(QuickTimeFile("plain")), cancellationToken: new CancellationToken(true)));
    }

    // ---- MacPaint ----

    // Version 2 header; row 0 = 8 black bytes + 64 white; the other rows white, or `fill` for every row after `rows`.
    private static byte[] MacPaint(int rows = 720)
    {
        var b = new List<byte>(new byte[512]);
        b[3] = 2;
        b.AddRange(new byte[] { 0xF9, 0xFF, 0xC1, 0x00 });
        for (int row = 1; row < rows; row++)
        {
            b.AddRange(new byte[] { 0xB9, 0x00 });
        }

        return b.ToArray();
    }

    private static byte[] MacBinary(byte[] fork, byte[] after)
    {
        var header = new byte[128];
        header[1] = 5;
        Encoding.ASCII.GetBytes("Paint").CopyTo(header, 2);
        Encoding.ASCII.GetBytes("PNTGMPNT").CopyTo(header, 65);
        header[83] = (byte)(fork.Length >> 24);
        header[84] = (byte)(fork.Length >> 16);
        header[85] = (byte)(fork.Length >> 8);
        header[86] = (byte)fork.Length;
        return [.. header, .. fork, .. after];
    }

    public static TheoryData<string> MacPaintFiles() => new() { "bare", "MacBinary", "MacBinary with data after a short fork", "truncated" };

    private static byte[] MacPaintFile(string name) => name switch
    {
        "bare" => MacPaint(),
        "MacBinary" => MacBinary(MacPaint(), []),
        // The fork holds two rows; the black rows after it belong to the next fork and are never read.
        "MacBinary with data after a short fork" => MacBinary(MacPaint(rows: 2), [.. Enumerable.Repeat(new byte[] { 0xB9, 0xFF }, 10).SelectMany(r => r)]),
        "truncated" => MacPaint(rows: 300),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(MacPaintFiles))]
    public void MacPaint_documents_read_from_a_stream_match_the_bytes(string name)
    {
        var data = MacPaintFile(name);
        var stream = new TrickleStream(data, chunk: 7);
        var bitmap = ClassicMac.Graphics.MacPaintFile.Decode(stream);
        Assert.Equal(ClassicMac.Graphics.MacPaintFile.Decode(data).Pixels, bitmap.Pixels);
        Assert.Equal(new RgbaColor(0, 0, 0), bitmap[0, 0]);
        Assert.Equal(new RgbaColor(255, 255, 255), bitmap[0, 2]);
        Assert.True(stream.AtEnd);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public void Short_MacPaint_documents_fail_alike()
    {
        var header = new byte[512];
        Assert.Throws<NotSupportedException>(() => ClassicMac.Graphics.MacPaintFile.Decode(header));
        Assert.Throws<NotSupportedException>(() => ClassicMac.Graphics.MacPaintFile.Decode(new TrickleStream(header)));
        Assert.Throws<OperationCanceledException>(() =>
            ClassicMac.Graphics.MacPaintFile.Decode(new TrickleStream(MacPaint()), new CancellationToken(true)));
    }

    // ---- ImageSharp ----

    public static TheoryData<string> AllFormats() => new() { "PICT", "QTIF", "MacPaint" };

    private static byte[] File(string format) => format switch
    {
        "PICT" => Picture("file rgb888 144 dpi icc"),
        "QTIF" => QuickTimeFile("plain"),
        _ => MacPaint(),
    };

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void ImageSharp_decodes_from_the_streams_position_and_leaves_it_open(string format)
    {
        var data = File(format);
        using var expected = Image.Load<Rgba32>(Options, data);
        var stream = new MemoryStream([.. "prefix"u8, .. data]) { Position = 6 };
        using var image = Image.Load<Rgba32>(Options, stream);
        Assert.Equal(expected.Metadata.DecodedImageFormat, image.Metadata.DecodedImageFormat);
        Assert.True(image.DangerousTryGetSinglePixelMemory(out var actualPixels));
        Assert.True(expected.DangerousTryGetSinglePixelMemory(out var expectedPixels));
        Assert.True(expectedPixels.Span.SequenceEqual(actualPixels.Span));
        Assert.True(stream.CanRead);

        using var trickled = Image.Load<Rgba32>(Options, new TrickleStream(data, chunk: 5));
        Assert.Equal((expected.Width, expected.Height), (trickled.Width, trickled.Height));
    }

    [Fact]
    public void ImageSharp_identifies_a_QuickTime_file_from_its_atoms()
    {
        byte[] file = [.. Atom("idsc", Description(2, 1)), .. Atom("free", new byte[3]), .. Atom("idat", Pixels), .. Atom("iicc", [1, 2])];
        var info = Image.Identify(Options, new MemoryStream(file));
        Assert.Equal((2, 1), (info.Width, info.Height));
        Assert.Equal(144, info.Metadata.HorizontalResolution);
        Assert.Equal(72, info.Metadata.VerticalResolution);
        Assert.NotNull(info.Metadata.IccProfile);
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public async Task ImageSharp_decoding_can_be_cancelled(string format)
    {
        var decoder = format switch
        {
            "PICT" => (IImageDecoder)PictDecoder.Instance,
            "QTIF" => QuickTimeImageDecoder.Instance,
            _ => MacPaintDecoder.Instance,
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            decoder.DecodeAsync<Rgba32>(Options, new MemoryStream(File(format)), new CancellationToken(true)));
    }

    // ---- SkiaSharp ----

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Skia_DecodeAny_reads_a_non_seekable_stream(string format)
    {
        var data = File(format);
        using var expected = PictSkia.DecodeAny(data);
        var stream = new TrickleStream(data, chunk: 2);
        using var bitmap = PictSkia.DecodeAny(stream);
        Assert.NotNull(bitmap);
        Assert.Equal(expected!.Bytes, bitmap.Bytes);
        Assert.True(stream.AtEnd);
        Assert.False(stream.Disposed);
    }

    [Fact]
    public void Skia_DecodeAny_starts_at_the_streams_position_and_returns_null_for_other_data()
    {
        var picture = Picture("v2");
        var stream = new MemoryStream([.. "xx"u8, .. picture]) { Position = 2 };
        using var bitmap = PictSkia.DecodeAny(stream);
        using var expected = PictSkia.Decode(picture);
        Assert.Equal(expected.Bytes, bitmap!.Bytes);
        Assert.Null(PictSkia.DecodeAny(new MemoryStream("not an image at all"u8.ToArray())));
        Assert.Null(PictSkia.DecodeAny(new MemoryStream([])));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Skia_stream_decoding_can_be_cancelled(string format)
    {
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PictSkia.DecodeAny(new MemoryStream(File(format)), cancellationToken: new CancellationToken(true)));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PictSkia.Decode(new MemoryStream(Picture("v2")), cancellationToken: new CancellationToken(true)));
    }
}
