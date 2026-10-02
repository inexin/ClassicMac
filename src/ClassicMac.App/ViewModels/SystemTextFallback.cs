using System;
using ClassicMac.Graphics.QuickDraw;
using SkiaSharp;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// Text for the previews when no bitmap font of the user's files draws it: an installed font close to the Mac's
    /// (Charcoal or Chicago when installed, else a bold sans serif), rasterized with Skia into QuickDraw's 1-bit mask
    /// [ClassicMac: an approximation of the Mac's fonts].
    /// </summary>
    internal sealed class SystemTextFallback : ITextFallback
    {
        public static SystemTextFallback Instance { get; } = new();

        private static readonly string[] Families = ["Charcoal", "Chicago", "Charcoal CY", "Verdana", "Tahoma", "Arial"];

        private readonly SKTypeface?[] typefaces = new SKTypeface?[4];

        private SKTypeface? Typeface(bool bold, bool italic)
        {
            int key = (bold ? 1 : 0) | (italic ? 2 : 0);
            lock (typefaces)
            {
                if (typefaces[key] is { } cached) return cached;
                var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                    italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                foreach (var family in Families)
                {
                    var typeface = SKTypeface.FromFamilyName(family, style);
                    if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                        return typefaces[key] = typeface;
                }
                return typefaces[key] = SKTypeface.FromFamilyName(null, style);
            }
        }

        public TextFallbackMask? Render(string text, TextFallbackStyle style)
        {
            ArgumentNullException.ThrowIfNull(text);
            // The system font (0) is Charcoal, a heavy face: drawn bold. Its 12 points are narrower than most fonts'.
            bool bold = (style.Face & 1) != 0 || style.FontId == 0;
            if (Typeface(bold, (style.Face & 2) != 0) is not { } typeface) return null;
            float size = style.Size <= 0 ? 12 : style.Size;
            using var font = new SKFont(typeface, style.FontId == 0 ? size * 0.92f : size) { Edging = SKFontEdging.Alias, Subpixel = false };
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
            float advance = font.MeasureText(text, paint);
            var metrics = font.Metrics;
            int ascent = (int)Math.Ceiling(-metrics.Ascent), descent = (int)Math.Ceiling(metrics.Descent);
            int pad = (int)Math.Ceiling(font.Size / 2);
            int width = (int)Math.Ceiling(advance) + 2 * pad, height = ascent + descent + 2;
            if (width <= 0 || height <= 0) return null;
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawText(text, pad, ascent, SKTextAlign.Left, font, paint);
            }
            var pixels = bitmap.GetPixelSpan();
            var bits = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    bits[y * width + x] = pixels[y * bitmap.RowBytes + x] >= 128 ? (byte)1 : (byte)0;
            return new TextFallbackMask(width, height, pad, ascent, bits, (float)Math.Round(advance));
        }
    }
}
