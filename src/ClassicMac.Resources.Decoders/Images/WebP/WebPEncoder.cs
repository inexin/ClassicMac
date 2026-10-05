using System;
using System.Buffers.Binary;
using ClassicMac.Resources.Decoders.Images.WebP;

namespace ClassicMac.Resources.Decoders.Images;

/// <summary>
/// Lossless WebP (RFC 9649; docs/formats/output/webp.md): a RIFF <c>WEBP</c> file of one <c>VP8L</c> chunk. The pixels
/// are exact, as PNG's, and the files usually smaller.
/// </summary>
public sealed class WebPEncoder : IImageEncoder
{
    private const int MaxSide = 16384;

    /// <summary>The encoder.</summary>
    public static WebPEncoder Instance { get; } = new();

    private WebPEncoder()
    {
    }

    /// <inheritdoc/>
    public string Name => "webp";

    /// <inheritdoc/>
    public string Extension => ".webp";

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">A side under 1 or over 16,384 pixels, which VP8L cannot hold.</exception>
    public byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxSide);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxSide);
        if (rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException("Fewer pixels than the size says.", nameof(rgba));
        }

        var argb = new uint[width * height];
        for (var i = 0; i < argb.Length; i++)
        {
            argb[i] = (uint)rgba[4 * i + 3] << 24 | (uint)rgba[4 * i] << 16 | (uint)rgba[4 * i + 1] << 8 | rgba[4 * i + 2];
        }

        var data = Vp8lEncoder.Encode(width, height, argb);

        // RIFF (little-endian sizes): "RIFF", the size after it, "WEBP", then the "VP8L" chunk, padded to an even length.
        var padded = data.Length + (data.Length & 1);
        var file = new byte[20 + padded];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 12 + padded);
        "WEBPVP8L"u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), data.Length);
        data.CopyTo(file, 20);
        return file;
    }
}
