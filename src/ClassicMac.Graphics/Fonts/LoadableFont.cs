using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Graphics.Fonts;

/// <summary>
/// A Mac TrueType font made loadable by Windows and other modern loaders (docs/formats/resources/outline-fonts.md §3):
/// the tables they require and Mac fonts often lack are added from what the font has (a Windows Unicode <c>cmap</c>
/// subtable, Windows <c>name</c> records, <c>OS/2</c>, <c>post</c>), as the OpenType specification describes them; every
/// other table is kept as it is.
/// </summary>
public static class LoadableFont
{
    private const uint ChecksumMagic = 0xB1B0AFBA;

    /// <summary>
    /// <paramref name="sfnt"/> with what it lacks added, and its directory, checksums and <c>head</c>'s
    /// checkSumAdjustment made again; the data unchanged when it lacks nothing, or is not TrueType. What was added goes
    /// to <paramref name="added"/> (<c>cmap (3,1)</c>, <c>name (Windows)</c>, <c>OS/2</c>, <c>post</c>).
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not an sfnt.</exception>
    public static byte[] Make(ReadOnlyMemory<byte> sfnt, ICollection<string>? added = null)
    {
        var font = OutlineFont.Read(sfnt);
        if (!font.IsTrueType)
        {
            return sfnt.ToArray();
        }

        var tables = new SortedDictionary<uint, byte[]>();
        foreach (var table in font.Tables)
        {
            if ((long)table.Offset + table.Length <= sfnt.Length)
            {
                tables[table.Tag.Value] = sfnt.Slice((int)table.Offset, (int)table.Length).ToArray();
            }
        }

        // Windows takes only version $00010000 for TrueType ('true' is the Mac's).
        var made = new List<string>();
        if (font.Version.Value != 0x00010000)
        {
            made.Add("version");
        }

        var characters = new SortedDictionary<int, int>();
        if (Tag("cmap") is var cmapTag && tables.GetValueOrDefault(cmapTag) is { } cmap)
        {
            if (Cmaps.WithWindows(cmap, characters) is { } windows)
            {
                tables[cmapTag] = windows;
                made.Add("cmap (3,1)");
            }
        }

        // head's fontRevision (Fixed) as OpenType's version string, "Version 1.000".
        var revision = tables.GetValueOrDefault(Tag("head")) is { Length: >= 8 } headTable ? new BigEndianReader(headTable).ReadUInt32At(4) : 0x00010000u;
        var version = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Version {revision / 65536.0:0.000}");
        if (Tag("name") is var nameTag && tables.GetValueOrDefault(nameTag) is { } name && Names.WithWindows(name, version) is { } windowsNames)
        {
            tables[nameTag] = windowsNames;
            made.Add("name (Windows)");
        }

        var head = tables.GetValueOrDefault(Tag("head"));
        var hhea = tables.GetValueOrDefault(Tag("hhea"));
        var hmtx = tables.GetValueOrDefault(Tag("hmtx"));
        if (!tables.ContainsKey(Tag("OS/2")) && head is { Length: >= 54 } && hhea is { Length: >= 36 } && hmtx is not null)
        {
            tables[Tag("OS/2")] = Os2(head, hhea, hmtx, characters);
            made.Add("OS/2");
        }

        if (!tables.ContainsKey(Tag("post")) && head is { Length: >= 54 } && hhea is { Length: >= 36 } && hmtx is not null)
        {
            tables[Tag("post")] = Post(head, hhea, hmtx);
            made.Add("post");
        }

        if (made.Count == 0)
        {
            return sfnt.ToArray();
        }

        foreach (var item in made)
        {
            added?.Add(item);
        }

        return Assemble(tables);
    }

    private static uint Tag(string tag) => FourCC.FromString(tag).Value;

    // The font file: the offset table (searchRange and its kin for the count), the directory in tag order with each
    // table's checksum, the tables 4-byte aligned, then head's checkSumAdjustment so the file sums to $B1B0AFBA.
    private static byte[] Assemble(SortedDictionary<uint, byte[]> tables)
    {
        var head = Tag("head");
        if (tables.TryGetValue(head, out var headData) && headData.Length >= 12)
        {
            headData = headData.ToArray();
            new BigEndianWriter(headData).WriteUInt32At(8, 0u);
            tables[head] = headData;
        }

        int count = tables.Count, power = 1, log = 0;
        while (power * 2 <= count)
        {
            power *= 2;
            log++;
        }

        var w = new BigEndianWriter();
        w.WriteUInt32(0x00010000u);
        w.WriteUInt16((ushort)count);
        w.WriteUInt16((ushort)(power * 16));
        w.WriteUInt16((ushort)log);
        w.WriteUInt16((ushort)(count * 16 - power * 16));
        long offset = 12 + 16 * count;
        foreach (var (tag, data) in tables)
        {
            w.WriteUInt32(tag);
            w.WriteUInt32(Checksum(data));
            w.WriteUInt32(offset);
            w.WriteUInt32(data.Length);
            offset += (data.Length + 3) & ~3;
        }

        var headAt = -1;
        foreach (var (tag, data) in tables)
        {
            if (tag == head)
            {
                headAt = w.Length;
            }

            w.WriteBytes(data);
            w.WriteZeros(((data.Length + 3) & ~3) - data.Length);
        }

        var file = w.ToArray();
        if (headAt >= 0)
        {
            new BigEndianWriter(file).WriteUInt32At(headAt + 8, unchecked(ChecksumMagic - Checksum(file)));
        }

        return file;
    }

    // The sum of the data's big-endian 32-bit words, the last padded with zeros.
    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }

    // OS/2 version 1 (86 bytes) from head, hhea, hmtx and the characters mapped (OpenType's OS/2): the average advance of
    // the glyphs that have one; weight and selection from head's macStyle; the sub-, superscript and strikeout metrics
    // at the usual fractions of the em; the Unicode ranges and code pages the characters fall in; typo metrics from
    // hhea; the Windows ascent and descent covering head's bounds and hhea's.
    private static byte[] Os2(byte[] head, byte[] hhea, byte[] hmtx, SortedDictionary<int, int> characters)
    {
        var headReader = new BigEndianReader(head);
        var hheaReader = new BigEndianReader(hhea);
        int unitsPerEm = headReader.ReadUInt16At(18), macStyle = headReader.ReadUInt16At(44);
        int yMin = headReader.ReadInt16At(38), yMax = headReader.ReadInt16At(42);
        int ascender = hheaReader.ReadInt16At(4), descender = hheaReader.ReadInt16At(6), lineGap = hheaReader.ReadInt16At(8);
        var advances = Advances(hmtx, hheaReader.ReadUInt16At(34)).Where(a => a > 0).ToList();
        int Em(double fraction) => (int)Math.Round(unitsPerEm * fraction, MidpointRounding.AwayFromZero);
        bool bold = (macStyle & 1) != 0, italic = (macStyle & 2) != 0;

        var w = new BigEndianWriter();
        w.WriteUInt16(1);                                                         // version
        w.WriteInt16((short)(advances.Count == 0 ? 0 : (int)Math.Round(advances.Average(), MidpointRounding.AwayFromZero)));
        w.WriteUInt16((ushort)(bold ? 700 : 400));                                // usWeightClass
        w.WriteUInt16(5);                                                         // usWidthClass: medium
        w.WriteUInt16(0);                                                         // fsType: installable
        w.WriteInt16((short)Em(0.65));                                            // ySubscriptXSize
        w.WriteInt16((short)Em(0.6));
        w.WriteInt16(0);
        w.WriteInt16((short)Em(0.075));
        w.WriteInt16((short)Em(0.65));                                            // ySuperscriptXSize
        w.WriteInt16((short)Em(0.6));
        w.WriteInt16(0);
        w.WriteInt16((short)Em(0.35));
        w.WriteInt16((short)Em(0.05));                                            // yStrikeoutSize
        w.WriteInt16((short)Em(0.26));
        w.WriteInt16(0);                                                          // sFamilyClass
        w.WriteZeros(10);                                                         // panose: any
        var ranges = new uint[4];
        foreach (var c in characters.Keys)
        {
            foreach (var (from, to, bit) in UnicodeRanges)
            {
                if (c >= from && c <= to)
                {
                    ranges[bit / 32] |= 1u << (bit % 32);
                }
            }
        }

        foreach (var range in ranges)
        {
            w.WriteUInt32(range);
        }

        w.WriteBytes("    "u8);                                                   // achVendID: none
        w.WriteUInt16((ushort)((italic ? 1 : 0) | (bold ? 0x20 : 0) | (!bold && !italic ? 0x40 : 0)));
        w.WriteUInt16((ushort)(characters.Count == 0 ? 0 : Math.Min(characters.Keys.First(), 0xFFFF)));
        w.WriteUInt16((ushort)(characters.Count == 0 ? 0 : Math.Min(characters.Keys.Last(), 0xFFFF)));
        w.WriteInt16((short)ascender);
        w.WriteInt16((short)descender);
        w.WriteInt16((short)lineGap);
        w.WriteUInt16((ushort)Math.Max(0, Math.Max(yMax, ascender)));            // usWinAscent
        w.WriteUInt16((ushort)Math.Max(0, Math.Max(-yMin, -descender)));         // usWinDescent
        w.WriteUInt32(((ranges[0] & 1) != 0 ? 1u : 0u) | 1u << 29);             // Latin 1 with Basic Latin; Macintosh
        w.WriteUInt32(0u);
        return w.ToArray();
    }

    // The bits of OS/2's ulUnicodeRange for the blocks a Mac font's characters come from.
    private static readonly (int From, int To, int Bit)[] UnicodeRanges =
    [
        (0x0020, 0x007E, 0), (0x00A0, 0x00FF, 1), (0x0100, 0x017F, 2), (0x0180, 0x024F, 3), (0x02B0, 0x02FF, 5),
        (0x0300, 0x036F, 6), (0x0370, 0x03FF, 7), (0x0400, 0x04FF, 9), (0x0590, 0x05FF, 11), (0x0600, 0x06FF, 13),
        (0x0E00, 0x0E7F, 24), (0x2000, 0x206F, 31), (0x20A0, 0x20CF, 33), (0x2100, 0x214F, 35), (0x2190, 0x21FF, 37),
        (0x2200, 0x22FF, 38), (0x25A0, 0x25FF, 45), (0xE000, 0xF8FF, 60), (0xFB00, 0xFB4F, 62),
    ];

    // post version 3 (no glyph names) from head, hhea and hmtx: upright, the underline at the usual place, fixed pitch
    // when every glyph's advance is the same.
    private static byte[] Post(byte[] head, byte[] hhea, byte[] hmtx)
    {
        int unitsPerEm = new BigEndianReader(head).ReadUInt16At(18);
        var advances = Advances(hmtx, new BigEndianReader(hhea).ReadUInt16At(34)).Distinct().Count();
        var w = new BigEndianWriter();
        w.WriteUInt32(0x00030000u);
        w.WriteUInt32(0u);                                                        // italicAngle
        w.WriteInt16((short)-Math.Round(unitsPerEm * 0.1, MidpointRounding.AwayFromZero));
        w.WriteInt16((short)Math.Round(unitsPerEm * 0.05, MidpointRounding.AwayFromZero));
        w.WriteUInt32(advances == 1 ? 1u : 0u);                                   // isFixedPitch
        w.WriteZeros(16);                                                         // memory hints
        return w.ToArray();
    }

    private static IEnumerable<int> Advances(byte[] hmtx, int metrics)
    {
        var r = new BigEndianReader(hmtx);
        for (var i = 0; i < metrics && 4 * i + 2 <= hmtx.Length; i++)
        {
            yield return r.ReadUInt16At(4 * i);
        }
    }

    // The character map: a Windows Unicode (3,1) subtable added when the font has none, from a Unicode one (platform 0)
    // or else a Macintosh one (platform 1, its encoding a Mac script), each code read as its character.
    private static class Cmaps
    {
        public static byte[]? WithWindows(byte[] cmap, SortedDictionary<int, int> characters)
        {
            var r = new BigEndianReader(cmap);
            if (cmap.Length < 4)
            {
                return null;
            }

            int count = r.ReadUInt16At(2);
            var records = new List<(int Platform, int Encoding, uint Offset)>();
            for (var i = 0; i < count && 12 + 8 * i <= cmap.Length; i++)
            {
                records.Add((r.ReadUInt16At(4 + 8 * i), r.ReadUInt16At(6 + 8 * i), r.ReadUInt32At(8 + 8 * i)));
            }

            if (records.FirstOrDefault(x => x.Platform == 3 && x.Encoding is 1 or 10) is { Offset: > 0 } windows)
            {
                Read(cmap, windows.Offset, null, characters);
                return null;
            }

            // A Unicode subtable is shared; else one is made from the Macintosh one.
            byte[]? subtable = null;
            if (records.FirstOrDefault(x => x.Platform == 0) is { Offset: > 0 } unicode && Read(cmap, unicode.Offset, null, characters))
            {
                subtable = cmap.AsSpan((int)unicode.Offset).ToArray();
            }
            else if (records.FirstOrDefault(x => x.Platform == 1) is { Offset: > 0 } mac
                && Enum.IsDefined((MacTextEncoding)mac.Encoding) && Read(cmap, mac.Offset, (MacTextEncoding)mac.Encoding, characters))
            {
                subtable = Format4(characters);
            }

            if (subtable is null)
            {
                return null;
            }

            // The table again: the old records and (3,1) on the new subtable, after the old subtables.
            var w = new BigEndianWriter();
            w.WriteUInt16(0);
            w.WriteUInt16(records.Count + 1);
            var all = records.Append((3, 1, 0u)).OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
            var shift = 8u;
            foreach (var (platform, encoding, offset) in all)
            {
                w.WriteUInt16((ushort)platform);
                w.WriteUInt16((ushort)encoding);
                w.WriteUInt32(platform == 3 && offset == 0 ? 0u : offset + shift);
            }

            var newAt = w.Length + cmap.Length - 4 - 8 * count;
            w.WriteBytes(cmap.AsSpan(4 + 8 * count));
            var bytes = w.ToArray();
            var indexOfNew = all.FindIndex(x => x.Item1 == 3 && x.Item3 == 0);
            var result = new BigEndianWriter();
            result.WriteBytes(bytes);
            result.WriteBytes(subtable);
            var output = result.ToArray();
            new BigEndianWriter(output).WriteUInt32At(4 + 8 * indexOfNew + 4, newAt);
            return output;
        }

        // Reads a subtable's character to glyph mapping (formats 0, 4 and 6) into characters, each code made a
        // character through the Mac encoding when there is one; false for a format not read.
        private static bool Read(byte[] cmap, uint offset, MacTextEncoding? encoding, SortedDictionary<int, int> characters)
        {
            if (offset + 6 > cmap.Length)
            {
                return false;
            }

            var r = new BigEndianReader(cmap.AsMemory((int)offset));
            void Add(int code, int glyph)
            {
                if (glyph == 0)
                {
                    return;
                }

                int c = code;
                if (encoding is { } e)
                {
                    if (code > 0xFF)
                    {
                        return;
                    }

                    var text = MacEncodings.Decode([(byte)code], e);
                    if (text.Length != 1 || text[0] == '�' || char.IsControl(text[0]))
                    {
                        return;
                    }

                    c = text[0];
                }

                characters.TryAdd(c, glyph);
            }

            switch (r.ReadUInt16At(0))
            {
                case 0 when r.Length >= 262:
                    for (var code = 0; code < 256; code++)
                    {
                        Add(code, r.ReadByteAt(6 + code));
                    }

                    return true;
                case 6 when r.Length >= 10:
                    int first = r.ReadUInt16At(6), entries = r.ReadUInt16At(8);
                    for (var i = 0; i < entries && 12 + 2 * i <= r.Length; i++)
                    {
                        Add(first + i, r.ReadUInt16At(10 + 2 * i));
                    }

                    return true;
                case 4 when r.Length >= 14:
                    int segments = r.ReadUInt16At(6) / 2;
                    if (16 + 8 * segments > r.Length)
                    {
                        return false;
                    }

                    for (var s = 0; s < segments; s++)
                    {
                        int end = r.ReadUInt16At(14 + 2 * s), start = r.ReadUInt16At(16 + 2 * segments + 2 * s);
                        int delta = r.ReadInt16At(16 + 4 * segments + 2 * s);
                        int rangeAt = 16 + 6 * segments + 2 * s, range = r.ReadUInt16At(rangeAt);
                        for (var code = start; code <= end && code != 0xFFFF; code++)
                        {
                            int glyph;
                            if (range == 0)
                            {
                                glyph = (code + delta) & 0xFFFF;
                            }
                            else
                            {
                                var at = rangeAt + range + 2 * (code - start);
                                glyph = at + 2 <= r.Length && r.ReadUInt16At(at) is var g and > 0 ? (g + delta) & 0xFFFF : 0;
                            }

                            Add(code, glyph);
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }

        // A format 4 subtable for the Basic Multilingual Plane's characters: a segment for each run of consecutive
        // characters whose glyphs are consecutive too (idDelta, no range offsets), then the closing $FFFF.
        private static byte[] Format4(SortedDictionary<int, int> characters)
        {
            var segments = new List<(int Start, int End, int Delta)>();
            foreach (var (c, glyph) in characters.Where(x => x.Key < 0xFFFF))
            {
                if (segments.Count > 0 && segments[^1] is var last && c == last.End + 1 && glyph == ((c + last.Delta) & 0xFFFF))
                {
                    segments[^1] = (last.Start, c, last.Delta);
                }
                else
                {
                    segments.Add((c, c, (glyph - c) & 0xFFFF));
                }
            }

            segments.Add((0xFFFF, 0xFFFF, 1));
            int count = segments.Count, power = 1, log = 0;
            while (power * 2 <= count)
            {
                power *= 2;
                log++;
            }

            var w = new BigEndianWriter();
            w.WriteUInt16(4);
            w.WriteUInt16((ushort)(16 + 8 * count));
            w.WriteUInt16(0);                                                     // language
            w.WriteUInt16((ushort)(2 * count));
            w.WriteUInt16((ushort)(2 * power));
            w.WriteUInt16((ushort)log);
            w.WriteUInt16((ushort)(2 * count - 2 * power));
            foreach (var s in segments)
            {
                w.WriteUInt16((ushort)s.End);
            }

            w.WriteUInt16(0);                                                     // reservedPad
            foreach (var s in segments)
            {
                w.WriteUInt16((ushort)s.Start);
            }

            foreach (var s in segments)
            {
                w.WriteUInt16((ushort)s.Delta);
            }

            w.WriteZeros(2 * count);                                              // idRangeOffset
            return w.ToArray();
        }
    }

    // The naming table: Windows records for each name the font has that its Windows records lack, as UTF-16, beside
    // the Windows ones it has (their encoding and language; Unicode English, (3, 1, $0409), when there are none); the
    // family (1), style (2), unique name (3, which GDI will not do without), full name (4), version (5) and PostScript
    // name (6) made from the others when missing. Null when nothing is missing.
    private static class Names
    {
        public static byte[]? WithWindows(byte[] name, string version)
        {
            var r = new BigEndianReader(name);
            if (name.Length < 6)
            {
                return null;
            }

            int count = r.ReadUInt16At(2), storage = r.ReadUInt16At(4);
            var records = new List<(int Platform, int Encoding, int Language, int Id, byte[] Text)>();
            for (var i = 0; i < count && 6 + 12 * (i + 1) <= name.Length; i++)
            {
                var at = 6 + 12 * i;
                int length = r.ReadUInt16At(at + 8), offset = r.ReadUInt16At(at + 10);
                if (storage + offset + length <= name.Length)
                {
                    records.Add((r.ReadUInt16At(at), r.ReadUInt16At(at + 2), r.ReadUInt16At(at + 4), r.ReadUInt16At(at + 6),
                        name.AsSpan(storage + offset, length).ToArray()));
                }
            }

            var windows = records.Where(x => x.Platform == 3).ToList();
            var (windowsEncoding, windowsLanguage) = windows.Count > 0 ? (windows[0].Encoding, windows[0].Language) : (1, 0x0409);
            var texts = new SortedDictionary<int, string>();
            foreach (var (platform, encoding, _, id, text) in records.OrderBy(x => x.Platform == 1 ? 0 : 1))
            {
                var decoded = platform switch
                {
                    0 or 3 => Encoding.BigEndianUnicode.GetString(text),
                    1 when Enum.IsDefined((MacTextEncoding)encoding) => MacEncodings.Decode(text, (MacTextEncoding)encoding),
                    _ => null,
                };
                if (decoded is not null && (platform != 1 || !texts.ContainsKey(id)))
                {
                    texts[id] = decoded;
                }
            }

            if (!texts.TryGetValue(1, out var family))
            {
                return null;
            }

            var style = texts.GetValueOrDefault(2) ?? "Regular";
            texts[2] = style;
            texts.TryAdd(4, style == "Regular" ? family : family + " " + style);
            texts.TryAdd(5, version);
            texts.TryAdd(3, texts[4] + ": " + texts[5]);
            texts.TryAdd(6, PostScriptName(style == "Regular" ? family : family + "-" + style));

            var have = windows.Where(x => x.Encoding == windowsEncoding && x.Language == windowsLanguage).Select(x => x.Id).ToHashSet();
            var missing = texts.Where(t => !have.Contains(t.Key)).ToList();
            if (missing.Count == 0)
            {
                return null;
            }

            var all = records.Select(x => (x.Platform, x.Encoding, x.Language, x.Id, x.Text))
                .Concat(missing.Select(t => (Platform: 3, Encoding: windowsEncoding, Language: windowsLanguage, Id: t.Key, Text: Encoding.BigEndianUnicode.GetBytes(t.Value))))
                .OrderBy(x => x.Platform).ThenBy(x => x.Encoding).ThenBy(x => x.Language).ThenBy(x => x.Id).ToList();
            var w = new BigEndianWriter();
            w.WriteUInt16(0);
            w.WriteUInt16((ushort)all.Count);
            w.WriteUInt16((ushort)(6 + 12 * all.Count));
            var strings = new BigEndianWriter();
            foreach (var (platform, encoding, language, id, text) in all)
            {
                w.WriteUInt16((ushort)platform);
                w.WriteUInt16((ushort)encoding);
                w.WriteUInt16((ushort)language);
                w.WriteUInt16((ushort)id);
                w.WriteUInt16((ushort)text.Length);
                w.WriteUInt16((ushort)strings.Length);
                strings.WriteBytes(text);
            }

            w.WriteBytes(strings.WrittenSpan);
            return w.ToArray();
        }

        // A PostScript name: printable ASCII without spaces or the characters PostScript reserves, at most 63.
        private static string PostScriptName(string name) =>
            new([.. name.Where(c => c is > ' ' and < '\u007F' && "[](){}<>/%".IndexOf(c, StringComparison.Ordinal) < 0).Take(63)]);
    }
}
