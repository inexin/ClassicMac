using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClassicMac.App.ViewModels;
using ClassicMac.Graphics;

namespace ClassicMac.App.Views;

// Resource ▸ Import's images: any file Avalonia decodes (PNG, JPEG, BMP, GIF…), converted to unpremultiplied RGBA.
internal sealed class AvaloniaImageReader : IImageReader
{
    /// <inheritdoc/>
    public RgbaBitmap Read(string path)
    {
        using var source = new Bitmap(path);
        var size = source.PixelSize;
        var format = source.Format ?? PixelFormat.Bgra8888;
        bool bgra = format == PixelFormat.Bgra8888;
        if (!bgra && format != PixelFormat.Rgba8888)
        {
            throw new NotSupportedException($"The image's pixel format {format} is not read.");
        }

        var result = new RgbaBitmap(size.Width, size.Height);
        var pixels = result.Pixels;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            source.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        bool premultiplied = source.AlphaFormat == AlphaFormat.Premul;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (bgra)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            if (source.AlphaFormat == AlphaFormat.Opaque)
            {
                pixels[i + 3] = 255;
            }
            else if (premultiplied && pixels[i + 3] is > 0 and < 255 and var a)
            {
                for (int c = 0; c < 3; c++)
                {
                    pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + a / 2) / a);
                }
            }
        }
        return result;
    }
}
