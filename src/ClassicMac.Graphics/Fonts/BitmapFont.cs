using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Graphics.Fonts
{
    /// <summary>
    /// One character of a bitmap font: where its image is in the strike, and how it is placed.
    /// </summary>
    /// <param name="Character">The character code, or −1 for the missing symbol (drawn for characters the font lacks).</param>
    /// <param name="Advance">The advance width in pixels: how far the pen moves.</param>
    /// <param name="Offset">The image's offset: its left edge is at pen + <see cref="BitmapFont.MaxKern"/> + this.</param>
    /// <param name="StrikeLeft">The image's first column in the strike.</param>
    /// <param name="ImageWidth">The image's width in pixels (0 for a blank character such as the space).</param>
    /// <param name="FractionalAdvance">The advance in pixels from the glyph-width table, when the font has one.</param>
    /// <param name="Top">From the image-height table (when the font has one): the first row with ink.</param>
    /// <param name="Rows">From the image-height table: the number of rows with ink; otherwise the font rectangle's height.</param>
    public sealed record BitmapGlyph(int Character, int Advance, int Offset, int StrikeLeft, int ImageWidth, double? FractionalAdvance, int Top, int Rows);

    /// <summary>
    /// A bitmap font strike: a <c>'NFNT'</c> or <c>'FONT'</c> resource (<i>Inside Macintosh: Text</i>, Font Manager): a
    /// header, the strike (every glyph's image side by side in one bitmap), the location table (each glyph's columns),
    /// the offset/width table, and optionally a glyph-width table and an image-height table.
    /// </summary>
    public sealed class BitmapFont
    {
        private readonly byte[] strike;
        private readonly byte[] data;
        private readonly Dictionary<int, BitmapGlyph> glyphs = [];

        private BitmapFont(byte[] strike, byte[] data)
        {
            this.strike = strike;
            this.data = data;
        }

        /// <summary>The font type word: bit 0 image-height table, bit 1 glyph-width table, bits 2–3 depth, bit 7 colour table, bit 8 synthetic, bit 9 colours other than black, bit 13 fixed width, bit 14 not to be expanded to the screen depth.</summary>
        public ushort FontType { get; private init; }

        /// <summary>The first character code in the font.</summary>
        public int FirstChar { get; private init; }

        /// <summary>The last character code in the font.</summary>
        public int LastChar { get; private init; }

        /// <summary>The widest advance.</summary>
        public int MaxWidth { get; private init; }

        /// <summary>The largest leftward kern (0 or negative): added to every glyph's offset.</summary>
        public int MaxKern { get; private init; }

        /// <summary>The font rectangle's width: the widest glyph image's extent.</summary>
        public int RectWidth { get; private init; }

        /// <summary>The font rectangle's height, ascent plus descent: the strike's height.</summary>
        public int RectHeight { get; private init; }

        /// <summary>Pixels above the baseline.</summary>
        public int Ascent { get; private init; }

        /// <summary>Pixels below the baseline.</summary>
        public int Descent { get; private init; }

        /// <summary>Pixels between lines.</summary>
        public int Leading { get; private init; }

        /// <summary>The strike's row length in 16-bit words (of 1-bit pixels; a deeper font's rows are that many times longer).</summary>
        public int RowWords { get; private init; }

        /// <summary>Bits per pixel: 1, or 2, 4 or 8 for a colour font.</summary>
        public int Depth { get; private init; }

        /// <summary>Whether the font has an image-height table.</summary>
        public bool HasHeightTable => (FontType & 0x0001) != 0;

        /// <summary>Whether the font has a glyph-width table (fractional advances).</summary>
        public bool HasWidthTable => (FontType & 0x0002) != 0;

        /// <summary>Whether the font has a font colour table (<c>'fctb'</c>) of its ID.</summary>
        public bool HasColorTable => (FontType & 0x0080) != 0;

        /// <summary>Whether every character has the same advance.</summary>
        public bool IsFixedWidth => (FontType & 0x2000) != 0;

        /// <summary>The strike's width in pixels.</summary>
        public int StrikeWidth => RowWords * 16;

        /// <summary>The glyphs in the font, by character code, the missing symbol last (character −1).</summary>
        public IReadOnlyCollection<BitmapGlyph> Glyphs => glyphs.Values;

        /// <summary>
        /// Reads a strike. Throws <see cref="InvalidDataException"/> under 26 bytes (the header). Tables that run past the
        /// data are read as far as they go and reported (<c>font.short</c>).
        /// </summary>
        public static BitmapFont Read(ReadOnlySpan<byte> input, ICollection<Diagnostic>? diagnostics = null) => Read(input, diagnostics, rom: false);

        // Reads a strike the way Mac OS 9 does, or with rom the way the 68k ROM's Font Manager and text drawing do: the
        // depth code in fontType bits 2-4 (Mac OS 9: 2-3), rowWords' top bit not masked (a strike with it set is
        // rejected), and the location table right after the strike (Mac OS 9: just before the offset/width table).
        internal static BitmapFont Read(ReadOnlySpan<byte> input, ICollection<Diagnostic>? diagnostics, bool rom)
        {
            var data = input.ToArray();
            if (input.Length < 26) throw new InvalidDataException($"A font strike needs a 26-byte header; this is {data.Length} bytes.");
            short Word(int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
            var fontType = (ushort)Word(0);
            int firstChar = Word(2), lastChar = Word(4);
            if (lastChar < firstChar || firstChar < 0 || lastChar > 255)
                throw new InvalidDataException($"The font's characters run from {firstChar} to {lastChar}.");
            var rowWords = rom ? Word(24) : (ushort)Word(24) & 0x7FFF; // Mac OS 9 masks the top bit
            if (rowWords < 0) throw new InvalidDataException($"The strike's row length is {rowWords} words.");
            var rectHeight = Math.Max(0, (int)Word(14));
            var depth = 1 << ((fontType >> 2) & (rom ? 7 : 3));
            var strikeLong = (long)rowWords * 2 * depth * rectHeight;
            if (strikeLong > int.MaxValue / 2) throw new InvalidDataException($"The strike would be {strikeLong} bytes.");
            var strikeLength = (int)strikeLong;
            var shortData = false;
            var strike = new byte[strikeLength];
            data.AsSpan(26, Math.Min(strikeLength, data.Length - 26)).CopyTo(strike);
            if (26 + strikeLength > data.Length) shortData = true;

            // owTLoc is the offset in words from itself (+16) to the offset/width table; a positive nDescent (+10) is its
            // high word (Mac OS 9; the ROM takes 0 or more).
            long owTLoc = (ushort)Word(16);
            var nDescent = Word(10);
            if (nDescent > 0) owTLoc |= (long)nDescent << 16;
            var font = new BitmapFont(strike, data)
            {
                FontType = fontType,
                Depth = depth,
                FirstChar = firstChar,
                LastChar = lastChar,
                MaxWidth = Word(6),
                MaxKern = Word(8),
                RectWidth = Word(12),
                RectHeight = rectHeight,
                Ascent = Word(18),
                Descent = Word(20),
                Leading = Word(22),
                RowWords = rowWords,
            };

            // Every table has one entry per character, then the missing symbol, then one more (the location table's end).
            var entries = lastChar - firstChar + 3;
            int? Entry(long offset, int index)
            {
                var at = offset + 2L * index;
                if (at < 0 || at + 2 > data.Length)
                {
                    shortData = true;
                    return null;
                }
                return BinaryPrimitives.ReadInt16BigEndian(data.AsSpan((int)at));
            }
            // Mac OS 9 finds the location table just before the offset/width table (the ROM, right after the strike;
            // the same place in a well-formed font).
            var offsetWidths = 16 + owTLoc * 2;
            var locations = rom ? 26L + strikeLength : offsetWidths - 2L * entries;
            if (locations != 26L + strikeLength)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Info, "font.location-table",
                    "The location table is not right after the strike; it is read just before the offset/width table, as Mac OS 9 reads it."));
            }
            var after = offsetWidths + 2L * entries;
            var widths = font.HasWidthTable ? after : -1;
            if (font.HasWidthTable) after += 2L * entries;
            var heights = font.HasHeightTable ? after : -1;

            for (var i = 0; i < entries - 1; i++)
            {
                var ow = Entry(offsetWidths, i);
                if (ow is null or -1) continue; // missing: drawn as the missing symbol
                var left = (ushort)(Entry(locations, i) ?? 0);
                var right = (ushort)(Entry(locations, i + 1) ?? left);
                var character = i == entries - 2 ? -1 : firstChar + i;
                double? fractional = widths >= 0 && Entry(widths, i) is { } w ? (ushort)w / 256.0 : null;
                int top = 0, rows = rectHeight;
                if (heights >= 0 && Entry(heights, i) is { } h)
                {
                    top = (ushort)h >> 8;
                    rows = h & 0xFF;
                }
                font.glyphs[character] = new BitmapGlyph(character, ow.Value & 0xFF, (ow.Value >> 8) & 0xFF, left, Math.Max(0, right - left), fractional, top, rows);
            }
            if (shortData)
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "font.short", "The font's tables run past its data; the glyphs there are left out or blank."));

            // The raw tables for the renderer, words past the data read as the text code finds them (0; −1 for a
            // missing offset/width entry).
            font.OffsetWidthTable = offsetWidths;
            font.Locations = Words(data, locations, entries, unsigned: true);
            font.OffsetWidths = Words(data, offsetWidths, entries, unsigned: false);
            if (widths >= 0) font.FractionalWidths = Words(data, widths, entries, unsigned: true);
            if (heights >= 0) font.Heights = Words(data, heights, entries, unsigned: true);
            return font;
        }

        private static int[] Words(byte[] data, long offset, int count, bool unsigned)
        {
            var result = new int[Math.Max(0, count)];
            for (var i = 0; i < result.Length; i++)
            {
                var at = offset + 2L * i;
                if (at < 0 || at + 2 > data.Length)
                {
                    result[i] = unsigned ? 0 : -1;
                    continue;
                }
                var w = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan((int)at));
                result[i] = unsigned ? (ushort)w : w;
            }
            return result;
        }

        // The tables as the renderer reads them, one entry per character, then the missing symbol and one more.
        internal int[] Locations { get; private set; } = [];

        // Offset << 8 | width, −1 for a missing character.
        internal int[] OffsetWidths { get; private set; } = [];

        // 8.8 advances (fontType bit 1), or null.
        internal int[]? FractionalWidths { get; private set; }

        // Top << 8 | rows (fontType bit 0), or null.
        internal int[]? Heights { get; private set; }

        private long OffsetWidthTable { get; set; }

        // The missing symbol's slot.
        internal int MissingIndex => LastChar - FirstChar + 1;

        internal int RowBytes => RowWords * 2 * Depth;

        // An offset/width word by raw index, read wherever it lands in the resource (0 past its end), as DrText's
        // first-character kerning reads it.
        internal int RawOffsetWidth(int index)
        {
            var at = OffsetWidthTable + 2L * index;
            return at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadInt16BigEndian(data.AsSpan((int)at)) : 0;
        }

        // A 1-bit strike's pixel.
        internal bool StrikeBit(int row, int column)
        {
            if (row < 0 || row >= RectHeight || column < 0 || column >= RowBytes * 8) return false;
            var i = row * RowBytes + (column >> 3);
            return i < strike.Length && ((strike[i] >> (7 - (column & 7))) & 1) != 0;
        }

        // The strike as a pixel map of its depth (a colour font), with the given palette.
        internal PixMap StrikeMap(RgbaColor[] palette) => new()
        {
            Bounds = new PictRect(0, 0, RectHeight, RowWords * 16), RowBytes = RowBytes, PixelSize = Depth,
            IsPixMap = true, Palette = palette, Data = strike,
        };

        /// <summary>The glyph for a character, or the missing symbol when the font lacks it (null when it has neither).</summary>
        public BitmapGlyph? Glyph(int character) =>
            glyphs.TryGetValue(character, out var glyph) ? glyph : glyphs.GetValueOrDefault(-1);

        /// <summary>A pixel of the strike: 0 or 1 for a 1-bit font, else the pixel value (an index into the font's colours).</summary>
        public int Pixel(int x, int y)
        {
            if (x < 0 || y < 0 || y >= RectHeight || x >= StrikeWidth || Depth > 8) return 0;
            var rowBytes = RowWords * 2 * Depth;
            var bit = x * Depth;
            var at = y * rowBytes + (bit >> 3);
            if (at >= strike.Length) return 0;
            return (strike[at] >> (8 - Depth - (bit & 7))) & ((1 << Depth) - 1);
        }

        /// <summary>A glyph's image: <see cref="BitmapGlyph.ImageWidth"/> × <see cref="RectHeight"/> pixel values, rows top down.</summary>
        public byte[] Image(BitmapGlyph glyph)
        {
            ArgumentNullException.ThrowIfNull(glyph);
            var pixels = new byte[glyph.ImageWidth * RectHeight];
            for (var y = 0; y < RectHeight; y++)
            {
                for (var x = 0; x < glyph.ImageWidth; x++) pixels[y * glyph.ImageWidth + x] = (byte)Pixel(glyph.StrikeLeft + x, y);
            }
            return pixels;
        }
    }
}
