using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Fonts;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Fonts
{
    /// <summary>
    /// The Font Manager's resources through <c>ClassicMac.Fonts</c>: bitmap strikes (<c>NFNT</c>, <c>FONT</c>) as a glyph
    /// sheet image, a BDF font and metrics JSON; families (<c>FOND</c>) and font colour tables (<c>fctb</c>) as JSON;
    /// outline fonts (<c>sfnt</c>) as a <c>.ttf</c> file and JSON.
    /// </summary>
    internal sealed class FontDecoder(DecodeOptions options, string name, params string[] types) : IResourceDecoder, IBuiltInDecoder
    {
        private static readonly FourCC Fond = FourCC.FromString("FOND");
        private readonly HashSet<FourCC> handled = [.. types.Select(FourCC.FromString)];

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC type) => handled.Contains(type);

        public IReadOnlyCollection<FourCC> Types => handled;

        public static IEnumerable<IResourceDecoder> All(DecodeOptions options) =>
        [
            new FontDecoder(options, "font.bitmap", "NFNT", "FONT"),
            new FontDecoder(options, "font.family", "FOND"),
            new FontDecoder(options, "font.outline", "sfnt"),
            new FontDecoder(options, "font.colors", "fctb"),
        ];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            try
            {
                return input.Resource.Type.ToString() switch
                {
                    "NFNT" or "FONT" => Bitmap(input),
                    "FOND" => Family(input),
                    "sfnt" => Outline(input),
                    _ => Colors(input),
                };
            }
            catch (InvalidDataException e)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "font.undecodable", $"{input.Resource}: {e.Message}"));
                return [];
            }
        }

        // The family, size and style a strike is for: from a FOND that lists its ID, else (a FONT) its ID's family × 128 + size.
        private static (string? Family, int Size, int Style) Owner(DecodeInput input)
        {
            var id = input.Resource.Id;
            foreach (var (fondId, fondName) in input.Ids(Fond))
            {
                if (input.Find(Fond, fondId) is not { } data) continue;
                FontFamily family;
                try
                {
                    family = FontFamily.Read(data.Span, fondName ?? "");
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                if (family.Fonts.FirstOrDefault(f => f.FontId == id && f.Size > 0) is { Size: > 0 } font) return (fondName, font.Size, font.Style);
            }
            if (input.Resource.Type.ToString() == "FONT")
            {
                var familyName = input.Ids(input.Resource.Type).FirstOrDefault(r => r.Id == (id & ~127)).Name;
                return (familyName, id & 127, 0);
            }
            return (null, 0, 0);
        }

        private IReadOnlyList<DecodedFile> Bitmap(DecodeInput input)
        {
            // A FONT of size 0 (family × 128) holds only the family's name, as its resource name.
            if (input.Data.Length == 0)
            {
                return [new DecodedFile(".json", MacText.Json(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("familyName", input.Resource.Name?.ToMacRoman());
                    w.WriteNumber("family", input.Resource.Id >> 7);
                    w.WriteEndObject();
                }))];
            }
            var font = BitmapFont.Read(input.Data.Span, input.Diagnostics);
            var (family, size, style) = Owner(input);
            var glyphs = font.Glyphs.OrderBy(g => g.Character < 0 ? int.MaxValue : g.Character).ToList();
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("family", family);
                w.WriteNumber("size", size);
                w.WriteNumber("style", style);
                w.WriteNumber("fontType", font.FontType);
                w.WriteNumber("depth", font.Depth);
                w.WriteBoolean("fixedWidth", font.IsFixedWidth);
                w.WriteNumber("firstChar", font.FirstChar);
                w.WriteNumber("lastChar", font.LastChar);
                w.WriteNumber("ascent", font.Ascent);
                w.WriteNumber("descent", font.Descent);
                w.WriteNumber("leading", font.Leading);
                w.WriteNumber("maxWidth", font.MaxWidth);
                w.WriteNumber("maxKern", font.MaxKern);
                w.WriteNumber("rectWidth", font.RectWidth);
                w.WriteNumber("rectHeight", font.RectHeight);
                w.WriteStartArray("glyphs");
                foreach (var g in glyphs)
                {
                    w.WriteStartObject();
                    w.WriteNumber("character", g.Character);
                    if (g.Character >= 0) w.WriteString("text", MacRoman.Decode([(byte)g.Character]));
                    w.WriteNumber("advance", g.Advance);
                    w.WriteNumber("left", font.MaxKern + g.Offset);
                    w.WriteNumber("width", g.ImageWidth);
                    if (g.FractionalAdvance is { } f) w.WriteNumber("fractionalAdvance", f);
                    if (font.HasHeightTable)
                    {
                        w.WriteNumber("top", g.Top);
                        w.WriteNumber("rows", g.Rows);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            return
            [
                new DecodedFile(options.ImageEncoder.Extension, Sheet(font, glyphs, Palette(input, font))),
                new DecodedFile(".bdf", Encoding.ASCII.GetBytes(Bdf(font, glyphs, family, size, input.Resource.Id))),
                new DecodedFile(".json", json),
            ];
        }

        // Pixel values to colours: a 1-bit font black; a deeper one through its fctb, else greys from white to black.
        private static Func<int, (byte R, byte G, byte B)> Palette(DecodeInput input, BitmapFont font)
        {
            if (font.Depth == 1) return _ => (0, 0, 0);
            var table = input.Find(FourCC.FromString("fctb"), input.Resource.Id) is { } fctb ? FontColors.Read(fctb.Span) : [];
            var colours = table.GroupBy(e => e.Value).ToDictionary(g => g.Key, g => ((byte)(g.First().Red >> 8), (byte)(g.First().Green >> 8), (byte)(g.First().Blue >> 8)));
            var max = (1 << font.Depth) - 1;
            return value =>
            {
                if (colours.TryGetValue(value, out var colour)) return colour;
                var grey = (byte)(255 - value * 255 / max);
                return (grey, grey, grey);
            };
        }

        // Every glyph in a grid of 16 per row, in character order then the missing symbol, each drawn at its pen position
        // in a cell as wide as the widest glyph, on white [ClassicMac].
        private byte[] Sheet(BitmapFont font, List<BitmapGlyph> glyphs, Func<int, (byte R, byte G, byte B)> colour)
        {
            const int Columns = 16, Gap = 2;
            var left = Math.Min(0, glyphs.Select(g => font.MaxKern + g.Offset).DefaultIfEmpty(0).Min());
            var right = glyphs.Select(g => Math.Max(g.Advance, font.MaxKern + g.Offset + g.ImageWidth)).DefaultIfEmpty(1).Max();
            var cellWidth = right - left + Gap;
            var cellHeight = Math.Max(1, font.RectHeight) + Gap;
            var rows = Math.Max(1, (glyphs.Count + Columns - 1) / Columns);
            int width = Columns * cellWidth, height = rows * cellHeight;
            var rgba = new byte[width * height * 4];
            rgba.AsSpan().Fill(255);
            for (var i = 0; i < glyphs.Count; i++)
            {
                var g = glyphs[i];
                var x0 = i % Columns * cellWidth + Gap / 2 - left + font.MaxKern + g.Offset;
                var y0 = i / Columns * cellHeight + Gap / 2;
                var pixels = font.Image(g);
                for (var y = 0; y < font.RectHeight; y++)
                {
                    for (var x = 0; x < g.ImageWidth; x++)
                    {
                        var value = pixels[y * g.ImageWidth + x];
                        if (value == 0) continue;
                        var (r, gr, b) = colour(value);
                        var at = ((y0 + y) * width + x0 + x) * 4;
                        rgba[at] = r;
                        rgba[at + 1] = gr;
                        rgba[at + 2] = b;
                    }
                }
            }
            return options.ImageEncoder.Encode(width, height, rgba);
        }

        // BDF 2.1 (Adobe's Glyph Bitmap Distribution Format): one character per glyph at its Mac OS Roman code (the missing
        // symbol at −1), its image as the bitmap, placed by the kern offset and the descent; 72 dpi. A colour font's
        // pixels count as ink where non-zero [ClassicMac].
        private static string Bdf(BitmapFont font, List<BitmapGlyph> glyphs, string? family, int size, short id)
        {
            var name = string.IsNullOrEmpty(family) ? $"NFNT{id}" : family.Replace(' ', '_');
            var pixelSize = size > 0 ? size : font.RectHeight;
            var text = new StringBuilder();
            void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Line($"STARTFONT 2.1");
            Line($"FONT -ClassicMac-{name}-Medium-R-Normal--{pixelSize}-{pixelSize * 10}-72-72-{(font.IsFixedWidth ? "M" : "P")}-{font.MaxWidth * 10}-Apple-Roman");
            Line($"SIZE {pixelSize} 72 72");
            Line($"FONTBOUNDINGBOX {Math.Max(1, font.RectWidth)} {font.RectHeight} {font.MaxKern} {-font.Descent}");
            Line($"STARTPROPERTIES 5");
            Line($"FONT_ASCENT {font.Ascent}");
            Line($"FONT_DESCENT {font.Descent}");
            Line($"CHARSET_REGISTRY \"Apple\"");
            Line($"CHARSET_ENCODING \"Roman\"");
            Line($"FAMILY_NAME \"{(family ?? name).Replace("\"", "", StringComparison.Ordinal)}\"");
            Line($"ENDPROPERTIES");
            Line($"CHARS {glyphs.Count}");
            foreach (var g in glyphs)
            {
                Line($"STARTCHAR {(g.Character < 0 ? "missing" : $"c{g.Character:X2}")}");
                Line($"ENCODING {g.Character}");
                Line($"SWIDTH {g.Advance * 1000 / Math.Max(1, pixelSize)} 0");
                Line($"DWIDTH {g.Advance} 0");
                if (g.ImageWidth == 0)
                {
                    // A blank glyph (the space): an empty box and no rows.
                    Line($"BBX 0 0 0 0");
                    Line($"BITMAP");
                    Line($"ENDCHAR");
                    continue;
                }
                Line($"BBX {g.ImageWidth} {font.RectHeight} {font.MaxKern + g.Offset} {-font.Descent}");
                Line($"BITMAP");
                var pixels = font.Image(g);
                var rowBytes = (g.ImageWidth + 7) / 8;
                for (var y = 0; y < font.RectHeight; y++)
                {
                    var row = new byte[rowBytes];
                    for (var x = 0; x < g.ImageWidth; x++)
                    {
                        if (pixels[y * g.ImageWidth + x] != 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                    text.Append(Convert.ToHexString(row)).Append('\n');
                }
                Line($"ENDCHAR");
            }
            Line($"ENDFONT");
            return text.ToString();
        }

        private IReadOnlyList<DecodedFile> Family(DecodeInput input)
        {
            var family = FontFamily.Read(input.Data.Span, input.Resource.Name?.ToMacRoman() ?? "", input.Diagnostics);
            return [new DecodedFile(".json", MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("name", family.Name);
                w.WriteNumber("familyId", family.FamilyId);
                w.WriteNumber("flags", family.Flags);
                w.WriteNumber("version", family.Version);
                w.WriteNumber("firstChar", family.FirstChar);
                w.WriteNumber("lastChar", family.LastChar);
                w.WriteNumber("ascent", family.Ascent);
                w.WriteNumber("descent", family.Descent);
                w.WriteNumber("leading", family.Leading);
                w.WriteNumber("maxWidth", family.MaxWidth);
                w.WriteStartArray("styleExtras");
                foreach (var extra in family.StyleExtras) w.WriteNumberValue(extra);
                w.WriteEndArray();
                w.WriteStartArray("fonts");
                foreach (var font in family.Fonts)
                {
                    w.WriteStartObject();
                    w.WriteNumber("size", font.Size);
                    w.WriteNumber("style", font.Style);
                    w.WriteNumber("id", font.FontId);
                    var type = font.Size == 0 ? "sfnt" : input.Find(FourCC.FromString("NFNT"), font.FontId) is not null ? "NFNT"
                        : input.Find(FourCC.FromString("FONT"), font.FontId) is not null ? "FONT" : null;
                    if (type is null) w.WriteNull("resource");
                    else w.WriteString("resource", type);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("widthTables");
                foreach (var table in family.WidthTables)
                {
                    w.WriteStartObject();
                    w.WriteNumber("style", table.Style);
                    w.WriteStartArray("widths");
                    foreach (var width in table.Widths) w.WriteNumberValue(width);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("kerningTables");
                foreach (var table in family.KerningTables)
                {
                    w.WriteStartObject();
                    w.WriteNumber("style", table.Style);
                    w.WriteStartArray("pairs");
                    foreach (var pair in table.Pairs)
                    {
                        w.WriteStartObject();
                        w.WriteString("first", MacRoman.Decode([pair.First]));
                        w.WriteString("second", MacRoman.Decode([pair.Second]));
                        w.WriteNumber("kern", pair.Kern);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (family.StyleMapping is { } mapping)
                {
                    w.WriteStartObject("styleMapping");
                    w.WriteNumber("fontClass", mapping.FontClass);
                    w.WriteNumber("encodingOffset", mapping.EncodingOffset);
                    w.WriteStartArray("indexes");
                    foreach (var index in mapping.Indexes) w.WriteNumberValue(index);
                    w.WriteEndArray();
                    w.WriteStartArray("names");
                    foreach (var n in mapping.Names) w.WriteStringValue(n);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteNull("styleMapping");
                }
                w.WriteEndObject();
            }), MacText.EncodingName(options.TextEncoding))];
        }

        private static IReadOnlyList<DecodedFile> Outline(DecodeInput input)
        {
            var font = OutlineFont.Read(input.Data.Span, input.Diagnostics);
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("version", font.Version.ToString());
                w.WriteBoolean("trueType", font.IsTrueType);
                w.WriteString("familyName", font.FamilyName);
                w.WriteString("subfamilyName", font.SubfamilyName);
                w.WriteString("fullName", font.FullName);
                w.WriteStartArray("tables");
                foreach (var table in font.Tables)
                {
                    w.WriteStartObject();
                    w.WriteString("tag", table.Tag.ToString());
                    w.WriteNumber("offset", table.Offset);
                    w.WriteNumber("length", table.Length);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            // The data is the font file itself: TrueType as .ttf; anything else (a PostScript 'typ1' sfnt) as .sfnt.
            return [new DecodedFile(font.IsTrueType ? ".ttf" : ".sfnt", input.Data), new DecodedFile(".json", json)];
        }

        private static IReadOnlyList<DecodedFile> Colors(DecodeInput input)
        {
            var entries = FontColors.Read(input.Data.Span);
            return [new DecodedFile(".json", MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("entries");
                foreach (var (value, r, g, b) in entries)
                {
                    w.WriteStartObject();
                    w.WriteNumber("value", value);
                    w.WriteNumber("red", r);
                    w.WriteNumber("green", g);
                    w.WriteNumber("blue", b);
                    w.WriteString("hex", $"#{r >> 8:x2}{g >> 8:x2}{b >> 8:x2}");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }))];
        }
    }
}
