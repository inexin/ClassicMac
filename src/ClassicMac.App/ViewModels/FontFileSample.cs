using System;
using System.Collections.Generic;
using ClassicMac.Files;
using SkiaSharp;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// TrueType and OpenType font files (a data fork holding an <c>sfnt</c>: <c>.ttf</c>, <c>.otf</c>, <c>.ttc</c> files
/// from other systems, Mac OS X data-fork fonts) previewed as a sample drawn in the font by the platform (SkiaSharp).
/// Fonts in a suitcase's resources preview as their family instead.
/// </summary>
internal static class FontFileSample
{
    private const long MaxLength = 32L * 1024 * 1024;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "tfil", "sfnt", "OTTO" };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { "ttf", "otf", "ttc", "dfont" };

    private const string Pangram = "The quick brown fox jumps over the lazy dog";

    /// <summary>A drawn sample: its PNG, size and what the font is ("TrueType · Geneva").</summary>
    internal sealed record Sample(byte[] Png, int Width, int Height, string Detail);

    /// <summary>The sample of a font file, or null when the file is no font file or the platform cannot load it.</summary>
    public static Sample? Draw(MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = file.Name.ToString();
        var dot = name.LastIndexOf('.');
        var byName = dot > 0 && Extensions.Contains(name[(dot + 1)..]);
        if (file.DataFork.Length is < 12 or > MaxLength || !(byName || Types.Contains(file.FinderInfo.Type.ToString())))
        {
            return null;
        }

        if (Kind(file.DataFork.ReadPrefix(4)) is not { } kind)
        {
            return null;
        }

        using var data = SKData.CreateCopy(file.DataFork.ToArray());
        using var typeface = SKTypeface.FromData(data);
        if (typeface is null || typeface.GlyphCount == 0)
        {
            return null;
        }

        return Render(typeface, $"{kind} · {typeface.FamilyName}");
    }

    // What the first four bytes say the file is (the sfnt version, or a collection's tag).
    private static string? Kind(ReadOnlySpan<byte> start) => start switch
    {
        [0x00, 0x01, 0x00, 0x00] or [(byte)'t', (byte)'r', (byte)'u', (byte)'e'] => "TrueType",
        [(byte)'O', (byte)'T', (byte)'T', (byte)'O'] => "OpenType",
        [(byte)'t', (byte)'t', (byte)'c', (byte)'f'] => "TrueType collection",
        _ => null,
    };

    // The family's name, the pangram at four sizes, and the digits, black on white.
    private static Sample Render(SKTypeface typeface, string detail)
    {
        float[] sizes = [12, 18, 24, 36];
        const float margin = 12;
        var lines = new List<(string Text, float Size)> { (typeface.FamilyName, 24) };
        foreach (var size in sizes)
        {
            lines.Add((Pangram, size));
        }

        lines.Add(("0123456789 &?!.,;:()", 18));
        var fonts = new List<SKFont>();
        try
        {
            float width = 0, height = margin;
            foreach (var (text, size) in lines)
            {
                var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias };
                fonts.Add(font);
                width = Math.Max(width, font.MeasureText(text));
                height += font.Spacing;
            }

            var info = new SKImageInfo((int)Math.Ceiling(width + 2 * margin), (int)Math.Ceiling(height + margin));
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            var y = margin;
            for (var i = 0; i < lines.Count; i++)
            {
                y += -fonts[i].Metrics.Ascent;
                canvas.DrawText(lines[i].Text, margin, y, fonts[i], paint);
                y += fonts[i].Metrics.Descent + fonts[i].Metrics.Leading;
            }

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new Sample(png.ToArray(), info.Width, info.Height, detail);
        }
        finally
        {
            foreach (var font in fonts)
            {
                font.Dispose();
            }
        }
    }
}
