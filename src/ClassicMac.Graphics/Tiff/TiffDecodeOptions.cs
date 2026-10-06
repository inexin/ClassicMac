using System;

namespace ClassicMac.Graphics;

/// <summary>How <see cref="TiffFile.Decode(ReadOnlyMemory{byte}, System.Collections.Generic.ICollection{ClassicMac.Core.Diagnostic}?, TiffDecodeOptions?)"/> reads an image.</summary>
public sealed record TiffDecodeOptions
{
    /// <summary>The options' defaults: no JPEG decoder.</summary>
    public static TiffDecodeOptions Default { get; } = new();

    /// <summary>
    /// Decodes a whole JPEG stream (a JFIF file's bytes) to RGBA, or returns null when it cannot; JPEG-compressed
    /// TIFF images (compression 6 and 7) are read only with one. ClassicMac.Graphics has no JPEG decoder of its own:
    /// the app passes the platform's.
    /// </summary>
    public Func<byte[], RgbaBitmap?>? JpegDecoder { get; init; }
}
