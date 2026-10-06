using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ClassicMac.Files;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickTime;
using ClassicMac.Resources.Decoders.Images;
using SkiaSharp;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// Picture files previewed as their image: MacPaint documents, TIFF images and QuickTime image files by ClassicMac's
/// decoders, JPEG, PNG, GIF, BMP and WebP files by the platform's (SkiaSharp). A file is taken by its type or its
/// name's extension, or, with no type of its own, by its signature; the bytes must then decode.
/// </summary>
internal static class FilePictures
{
    // Larger files stay in Hex.
    private const long MaxLength = 64L * 1024 * 1024;

    private static readonly HashSet<string> CommonTypes = new(StringComparer.Ordinal)
    {
        "JPEG", "GIFf", "GIF ", "PNGf", "PNG ", "BMP ", "BMPp",
    };

    private static readonly HashSet<string> CommonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "gif", "png", "bmp", "webp",
    };

    /// <summary>A decoded picture file: its PNG, size and format's name.</summary>
    internal sealed record Picture(byte[] Png, int Width, int Height, string Format);

    /// <summary>The picture a file holds, or null when it is no picture file or does not decode.</summary>
    public static Picture? Read(MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.DataFork.Length is 0 or > MaxLength)
        {
            return null;
        }

        var type = file.FinderInfo.Type.ToString();
        var extension = Extension(file.Name.ToString());
        try
        {
            if (type == "PNTG" || extension is "mac" or "pntg" or "pnt")
            {
                var data = file.DataFork.ToArray();
                return type == "PNTG" || MacPaintFile.IsMacPaintFile(data) ? Encode(MacPaintFile.Decode(data), "MacPaint") : null;
            }

            if (type == "qtif" || extension is "qtif" or "qti" or "qif")
            {
                var data = file.DataFork.ToArray();
                return QuickTimeImageFile.IsQuickTimeImageFile(data) ? Encode(QuickTimeImageFile.Decode(data, SkiaImageCodec.Instance), "QuickTime image") : null;
            }

            var untyped = file.FinderInfo.Type == default || type == "????";
            if ((type == "TIFF" || extension is "tif" or "tiff" || untyped) && TiffFile.IsTiffFile(file.DataFork.ReadPrefix(4)))
            {
                return Encode(TiffFile.Decode(file.DataFork.ToArray()), "TIFF");
            }

            if (!CommonTypes.Contains(type) && !(extension is not null && CommonExtensions.Contains(extension)) && !untyped)
            {
                return null;
            }

            if (Signature(file.DataFork.ReadPrefix(16)) is not { } format)
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(file.DataFork.ToArray());
            if (bitmap is null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new Picture(png.ToArray(), bitmap.Width, bitmap.Height, format);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException or IOException)
        {
            return null;
        }
    }

    // The format a file's first bytes say it is.
    private static string? Signature(ReadOnlySpan<byte> start) => start switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "JPEG",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "PNG",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "GIF",
        [(byte)'B', (byte)'M', ..] => "BMP",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "WebP",
        _ => null,
    };

    private static string? Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : null;
    }

    private static Picture Encode(RgbaBitmap bitmap, string format) =>
        new(PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels), bitmap.Width, bitmap.Height, format);

    // JPEG, PNG, GIF and BMP images inside QuickTime image files, by the platform's decoders; others are ClassicMac's
    // own codecs or not decoded.
    private sealed class SkiaImageCodec : IPictImageCodec
    {
        public static readonly SkiaImageCodec Instance = new();

        public RgbaBitmap? Decode(PictImageDescription description, byte[] data)
        {
            if (description.CodecType is not ("jpeg" or "png " or "gif " or "WRLE"))
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(data);
            return bitmap is null ? null : ToRgba(bitmap);
        }

        private static RgbaBitmap ToRgba(SKBitmap bitmap)
        {
            var result = new RgbaBitmap(bitmap.Width, bitmap.Height);
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var handle = GCHandle.Alloc(result.Pixels, GCHandleType.Pinned);
            try
            {
                using var image = SKImage.FromBitmap(bitmap);
                image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0);
            }
            finally
            {
                handle.Free();
            }

            return result;
        }
    }
}
