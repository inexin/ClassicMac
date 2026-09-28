using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Fonts
{
    /// <summary>Finds a font resource by type and ID (in a fork, a suitcase, the System file…); null when there is none.</summary>
    public delegate ReadOnlyMemory<byte>? FontLookup(FourCC type, short id);

    /// <summary>One font of a family: a strike of a size and style, or an outline font (size 0).</summary>
    /// <param name="Size">The point size; 0 for an outline (<c>'sfnt'</c>) font.</param>
    /// <param name="Style">The QuickDraw style it is drawn for (0 plain); a non-zero high byte marks a colour or depth variant.</param>
    /// <param name="FontId">The resource ID of the <c>'NFNT'</c>/<c>'FONT'</c> (or <c>'sfnt'</c>).</param>
    public readonly record struct FontAssociation(int Size, int Style, short FontId);

    /// <summary>A family width table: advances for one style, in ems (4.12 fixed-point per point).</summary>
    /// <param name="Style">The style.</param>
    /// <param name="Widths">Per character from <see cref="FontFamily.FirstChar"/>, then the missing symbol and one more.</param>
    public sealed record FamilyWidthTable(int Style, IReadOnlyList<double> Widths);

    /// <summary>A kerning pair: how much closer (negative) or further apart two characters are set, in ems.</summary>
    public readonly record struct KerningPair(byte First, byte Second, double Kern);

    /// <summary>A family's kerning pairs for one style.</summary>
    public sealed record KerningTable(int Style, IReadOnlyList<KerningPair> Pairs);

    /// <summary>
    /// The style-mapping table: the font class, the offset of the glyph-encoding subtable, the 48 style indexes, and the
    /// style-name strings they point into (the first is the base font name).
    /// </summary>
    public sealed record StyleMapping(int FontClass, int EncodingOffset, IReadOnlyList<byte> Indexes, IReadOnlyList<string> Names);

    /// <summary>
    /// A font family (<c>'FOND'</c>, <i>Inside Macintosh: Text</i>, Font Manager): the family's metrics, the fonts that
    /// make it up, and optional width, kerning and style-mapping tables. Its resource name is the family's name.
    /// </summary>
    public sealed class FontFamily
    {
        /// <summary>The family's name (the <c>'FOND'</c>'s resource name).</summary>
        public string Name { get; private init; } = "";

        /// <summary>ffFlags: bit 1 the family has width tables, bit 12 use the style extra widths even without fractional widths, bit 13 never use them, bit 14 ignore the family width tables, bit 15 a fixed-width family.</summary>
        public ushort Flags { get; private init; }

        /// <summary>The family ID (ffFamID).</summary>
        public int FamilyId { get; private init; }

        /// <summary>The first character in the family's tables.</summary>
        public int FirstChar { get; private init; }

        /// <summary>The last character in the family's tables.</summary>
        public int LastChar { get; private init; }

        /// <summary>The ascent for one point, in ems (4.12).</summary>
        public double Ascent { get; private init; }

        /// <summary>The descent for one point, in ems (4.12; stored negative).</summary>
        public double Descent { get; private init; }

        /// <summary>The leading for one point, in ems.</summary>
        public double Leading { get; private init; }

        /// <summary>The widest advance for one point, in ems.</summary>
        public double MaxWidth { get; private init; }

        /// <summary>
        /// The style extra widths (ffProperty) in ems per point: plain, then bold, italic, underline, outline, shadow,
        /// condense, extend. Words $8000–$8FFF are sign-magnitude negatives (the ROM's Font Manager).
        /// </summary>
        public IReadOnlyList<double> StyleExtras { get; private init; } = [];

        /// <summary>ffIntl: the two words reserved for international use.</summary>
        public IReadOnlyList<ushort> International { get; private init; } = [];

        /// <summary>The family record's version (ffVersion).</summary>
        public int Version { get; private init; }

        /// <summary>The fonts, in the order stored (by size, then style).</summary>
        public IReadOnlyList<FontAssociation> Fonts { get; private init; } = [];

        /// <summary>The family width tables.</summary>
        public IReadOnlyList<FamilyWidthTable> WidthTables { get; private init; } = [];

        /// <summary>The kerning tables.</summary>
        public IReadOnlyList<KerningTable> KerningTables { get; private init; } = [];

        /// <summary>The style-mapping table, or null.</summary>
        public StyleMapping? StyleMapping { get; private init; }

        /// <summary>
        /// Reads a family record. Throws <see cref="InvalidDataException"/> under 54 bytes (the header and association
        /// count); tables that run past the data are read as far as they go and reported (<c>font.short</c>).
        /// </summary>
        public static FontFamily Read(ReadOnlySpan<byte> input, string name, ICollection<Diagnostic>? diagnostics = null)
        {
            var data = input.ToArray();
            if (input.Length < 54) throw new InvalidDataException($"A font family record needs 54 bytes; this is {data.Length}.");
            var shortData = false;
            short Word(int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
            static double Fixed412(int value) => value / 4096.0;
            var firstChar = (int)Word(4);
            var lastChar = (int)Word(6);

            var extras = new double[9];
            for (var i = 0; i < 9; i++)
            {
                var w = (ushort)Word(28 + 2 * i);
                extras[i] = Fixed412(w is >= 0x8000 and <= 0x8FFF ? -(w & 0x0FFF) : (short)w);
            }

            var count = Word(52) + 1;
            var fonts = new List<FontAssociation>();
            for (var i = 0; i < count; i++)
            {
                var at = 54 + 6 * i;
                if (at + 6 > data.Length)
                {
                    shortData = true;
                    break;
                }
                fonts.Add(new FontAssociation(Word(at), (ushort)Word(at + 2), Word(at + 4)));
            }

            // Width tables: a count less one, then per table a style and a 4.12 width per character, the missing symbol
            // and one more, stepped by the family's own range.
            var widthTables = new List<FamilyWidthTable>();
            var widthOffset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
            var entries = lastChar - firstChar + 3;
            if (widthOffset > 0 && lastChar != 0 && entries > 0)
            {
                if (widthOffset + 2 > data.Length) shortData = true;
                else
                {
                    var tables = Word(widthOffset) + 1;
                    var at = widthOffset + 2;
                    for (var t = 0; t < tables; t++)
                    {
                        if (at + 2 + 2 * entries > data.Length)
                        {
                            shortData = true;
                            break;
                        }
                        var widths = new double[entries];
                        for (var c = 0; c < entries; c++) widths[c] = Fixed412((ushort)Word(at + 2 + 2 * c));
                        widthTables.Add(new FamilyWidthTable((ushort)Word(at), widths));
                        at += 2 + 2 * entries;
                    }
                }
            }

            // Kerning tables: a count less one, then per table a style, a number of pairs, and 4-byte pairs (first
            // character, second character, 4.12 kern).
            var kerningTables = new List<KerningTable>();
            var kernOffset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
            if (kernOffset > 0)
            {
                if (kernOffset + 2 > data.Length) shortData = true;
                else
                {
                    var tables = Word(kernOffset) + 1;
                    var at = kernOffset + 2;
                    for (var t = 0; t < tables; t++)
                    {
                        if (at + 4 > data.Length)
                        {
                            shortData = true;
                            break;
                        }
                        var style = (ushort)Word(at);
                        var pairs = Word(at + 2);
                        at += 4;
                        var list = new List<KerningPair>();
                        for (var p = 0; p < pairs; p++, at += 4)
                        {
                            if (at + 4 > data.Length)
                            {
                                shortData = true;
                                break;
                            }
                            list.Add(new KerningPair(data[at], data[at + 1], Fixed412(Word(at + 2))));
                        }
                        kerningTables.Add(new KerningTable(style, list));
                        if (shortData) break;
                    }
                }
            }

            // Style mapping: font class, encoding offset, reserved long, 48 indexes, then a count and the style names.
            StyleMapping? mapping = null;
            var styleOffset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(24));
            if (styleOffset > 0)
            {
                if (styleOffset + 58 > data.Length) shortData = true;
                else
                {
                    var names = new List<string>();
                    var at = styleOffset + 58;
                    var nameCount = at + 2 <= data.Length ? Word(at) : 0;
                    at += 2;
                    for (var n = 0; n < nameCount; n++)
                    {
                        if (at >= data.Length || at + 1 + data[at] > data.Length)
                        {
                            shortData = true;
                            break;
                        }
                        names.Add(MacRoman.Decode(data.AsSpan(at + 1, data[at])));
                        at += 1 + data[at];
                    }
                    mapping = new StyleMapping(Word(styleOffset), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(styleOffset + 2)),
                        data.AsSpan(styleOffset + 10, 48).ToArray(), names);
                }
            }

            if (shortData)
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "font.short", "The family record's tables run past its data; read as far as they go."));
            return new FontFamily
            {
                Name = name,
                Flags = (ushort)Word(0),
                FamilyId = (ushort)Word(2),
                FirstChar = firstChar,
                LastChar = lastChar,
                Ascent = Fixed412(Word(8)),
                Descent = Fixed412(Word(10)),
                Leading = Fixed412(Word(12)),
                MaxWidth = Fixed412(Word(14)),
                StyleExtras = extras[..8],
                International = [(ushort)Word(46), (ushort)Word(48)],
                Version = (ushort)Word(50),
                Fonts = fonts,
                WidthTables = widthTables,
                KerningTables = kerningTables,
                StyleMapping = mapping,
            };
        }

        /// <summary>
        /// A bitmap font of the family, found by <paramref name="lookup"/>: the <c>'NFNT'</c> of its ID, else the
        /// <c>'FONT'</c>; null for an outline font or when neither exists (or is empty).
        /// </summary>
        public static BitmapFont? Strike(FontAssociation font, FontLookup lookup, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(lookup);
            if (font.Size == 0) return null;
            // An empty result counts as none (a null array converts to empty memory, not null).
            var data = lookup(FourCC.FromString("NFNT"), font.FontId) is { Length: > 0 } nfnt ? nfnt : lookup(FourCC.FromString("FONT"), font.FontId);
            return data is { Length: > 0 } bytes ? BitmapFont.Read(bytes.Span, diagnostics) : null;
        }

        /// <summary>The family's sizes with a bitmap font, in order.</summary>
        public IEnumerable<int> BitmapSizes => Fonts.Where(f => f.Size > 0).Select(f => f.Size).Distinct().Order();
    }
}
