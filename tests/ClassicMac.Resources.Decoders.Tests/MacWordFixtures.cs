using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 4 and 5 for the Macintosh ('WDBN') documents built byte by byte as docs/formats/documents/word-mac.md §1 lays them
// out: the header, the text, character and paragraph FKP pages and their bin tables, the style sheet, and the font
// tables. No Microsoft file is used.
internal sealed class MacWordBuilder
{
    private const int Page = 512;

    private readonly List<(int Start, int End, byte[] Chpx)> characters = [];
    private readonly List<(int Start, int End, byte Style, byte[] Sprms)> paragraphs = [];
    private readonly List<(short Id, string Name)> fonts = [];
    private readonly List<(byte[]? Chp, byte[]? Pap)> styles = [];
    private string text = "";

    /// <summary>Word 5 (version $23) by default; Word 4 is $1C.</summary>
    public ushort Version { get; set; } = 0x23;

    /// <summary>The header's flag byte at +$0A ($04: fast saved).</summary>
    public byte Flags { get; set; }

    /// <summary>The piece table zone (18), for a fast-saved file.</summary>
    public byte[] PieceTable { get; set; } = [];

    public MacWordBuilder Text(string value)
    {
        text = value;
        return this;
    }

    // A run of character properties: a CHPX (the CHP's bytes up to the last that differs from the style's).
    public MacWordBuilder Chp(int start, int end, params byte[] chpx)
    {
        characters.Add((start, end, chpx));
        return this;
    }

    // A paragraph (up to and including its end mark) with its style and paragraph sprms.
    public MacWordBuilder Pap(int start, int end, byte style = 0, params byte[] sprms)
    {
        paragraphs.Add((start, end, style, sprms));
        return this;
    }

    public MacWordBuilder Font(short id, string name)
    {
        fonts.Add((id, name));
        return this;
    }

    // A style: its character block (flags, what, font, size …) and its paragraph sprms; null for none.
    public MacWordBuilder Style(byte[]? chp, byte[]? pap)
    {
        styles.Add((chp, pap));
        return this;
    }

    public byte[] Build()
    {
        var bytes = MacRoman.Encode(text);
        var fcMin = 0x100;
        var fcMac = fcMin + bytes.Length;
        var chpPages = new List<(int First, byte[] Page)>();
        var papPages = new List<(int First, byte[] Page)>();
        // One FKP page each (enough for the fixtures), placed after the text.
        var firstPage = (fcMac + Page - 1) / Page;
        var chpRuns = Cover(characters.Select(c => (c.Start, c.End, (object?)c.Chpx)).ToList(), bytes.Length);
        var papRuns = Cover(paragraphs.Select(p => (p.Start, p.End, (object?)p)).ToList(), bytes.Length);
        chpPages.Add((fcMin, Fkp(chpRuns, fcMin, run => run is byte[] chpx ? [(byte)chpx.Length, .. chpx] : null, bytesAreWords: false)));
        papPages.Add((fcMin, Fkp(papRuns, fcMin, run => run is ValueTuple<int, int, byte, byte[]> p ? Papx(p.Item3, p.Item4) : Papx(0, []), bytesAreWords: true)));

        var file = new BigEndianWriter();
        file.WriteZeros(fcMin);
        file.WriteBytes(bytes);
        file.WriteZeros(firstPage * Page - fcMac);
        var chpPn = firstPage;
        foreach (var (_, page) in chpPages)
        {
            file.WriteBytes(page);
        }

        var papPn = chpPn + chpPages.Count;
        foreach (var (_, page) in papPages)
        {
            file.WriteBytes(page);
        }

        // The bin tables: (n + 1) FCs, then n page numbers.
        int Zone(byte[] data)
        {
            var at = file.WrittenSpan.Length;
            file.WriteBytes(data);
            return at;
        }

        var chpBte = Zone(BinTable(chpPages.Select(p => p.First).Append(fcMac).ToList(), chpPn));
        var papBte = Zone(BinTable(papPages.Select(p => p.First).Append(fcMac).ToList(), papPn));
        var styleSheet = StyleSheet();
        var styleAt = Zone(styleSheet);
        var fontNames = FontNames();
        var fontNamesAt = Zone(fontNames);
        var piecesAt = Zone(PieceTable);
        var end = file.WrittenSpan.Length;

        var header = new BigEndianWriter(file.ToArray());
        header.WriteUInt16At(0x00, 0xFE37);
        header.WriteUInt16At(0x02, Version);
        header.WriteByteAt(0x0A, Flags);
        header.WriteUInt32At(0x14, fcMin);
        header.WriteUInt32At(0x18, fcMac);
        header.WriteUInt32At(0x1C, end);
        header.WriteUInt32At(0x24, bytes.Length);
        void ZoneEntry(int index, int fc, int length)
        {
            var at = index < 20 ? 0x40 + 6 * index : 0xBC + 6 * (index - 20);
            header.WriteUInt32At(at, fc);
            header.WriteUInt16At(at + 4, length);
        }

        ZoneEntry(0, styleAt, styleSheet.Length);
        ZoneEntry(1, styleAt, styleSheet.Length);
        ZoneEntry(9, chpBte, 4 * (chpPages.Count + 1) + 2 * chpPages.Count);
        ZoneEntry(10, papBte, 4 * (papPages.Count + 1) + 2 * papPages.Count);
        ZoneEntry(18, piecesAt, PieceTable.Length);
        ZoneEntry(21, fontNamesAt, fontNames.Length);
        header.WriteUInt16At(0xB8, chpPages.Count);
        header.WriteUInt16At(0xBA, papPages.Count);
        return header.ToArray();
    }

    // Runs covering the whole text: the given ones, and gaps as runs with no properties.
    private static List<(int Start, int End, object? Run)> Cover(List<(int Start, int End, object? Run)> given, int length)
    {
        var runs = new List<(int, int, object?)>();
        var at = 0;
        foreach (var (start, end, run) in given.OrderBy(g => g.Start))
        {
            if (start > at)
            {
                runs.Add((at, start, null));
            }

            runs.Add((start, end, run));
            at = end;
        }

        if (at < length)
        {
            runs.Add((at, length, null));
        }

        return runs;
    }

    private static byte[] Papx(byte style, byte[] sprms)
    {
        // A count of words, the style, six bytes of line-height data (PHE), the sprms; padded to whole words.
        var body = new List<byte> { style, 0, 0, 0, 0, 0, 0 };
        body.AddRange(sprms);
        if ((body.Count + 1) % 2 != 0)
        {
            body.Add(0);
        }

        return [(byte)((body.Count + 1) / 2), .. body];
    }

    // An FKP: the FCs, an offset byte per run (in words from the page start, 0 for none), the property blocks from the
    // end of the page back, the run count in the last byte.
    private static byte[] Fkp(List<(int Start, int End, object? Run)> runs, int fcMin, Func<object?, byte[]?> block, bool bytesAreWords)
    {
        _ = bytesAreWords;
        var page = new byte[Page];
        var writer = new BigEndianWriter(page);
        for (var i = 0; i < runs.Count; i++)
        {
            writer.WriteUInt32At(4 * i, fcMin + runs[i].Start);
        }

        writer.WriteUInt32At(4 * runs.Count, fcMin + runs[^1].End);
        var top = Page - 1;
        for (var i = 0; i < runs.Count; i++)
        {
            if (block(runs[i].Run) is not { } data)
            {
                continue;
            }

            top -= data.Length;
            top &= ~1;
            data.CopyTo(page, top);
            page[4 * (runs.Count + 1) + i] = (byte)(top / 2);
        }

        page[Page - 1] = (byte)runs.Count;
        return page;
    }

    private static byte[] BinTable(List<int> fcs, int firstPn)
    {
        var writer = new BigEndianWriter();
        foreach (var fc in fcs)
        {
            writer.WriteUInt32(fc);
        }

        for (var i = 0; i < fcs.Count - 1; i++)
        {
            writer.WriteUInt16(firstPn + i);
        }

        return writer.ToArray();
    }

    // The style sheet: a word, the names, the character blocks, the paragraph blocks, the next/based-on pairs.
    private byte[] StyleSheet()
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(0);
        writer.WriteUInt16(2 + styles.Count);
        foreach (var _ in styles)
        {
            writer.WriteByte(0);                                     // unnamed: a standard style
        }

        var chp = new BigEndianWriter();
        foreach (var (block, _) in styles)
        {
            if (block is null)
            {
                chp.WriteByte(0xFF);
            }
            else
            {
                chp.WriteByte(block.Length);
                chp.WriteBytes(block);
            }
        }

        writer.WriteUInt16(2 + chp.WrittenSpan.Length);
        writer.WriteBytes(chp.ToArray());
        var pap = new BigEndianWriter();
        for (var i = 0; i < styles.Count; i++)
        {
            if (styles[i].Pap is not { } sprms)
            {
                pap.WriteByte(0xFF);
                continue;
            }

            pap.WriteByte(7 + sprms.Length);
            pap.WriteByte(i);
            pap.WriteZeros(6);
            pap.WriteBytes(sprms);
        }

        writer.WriteUInt16(2 + pap.WrittenSpan.Length);
        writer.WriteBytes(pap.ToArray());
        writer.WriteUInt16(styles.Count);
        foreach (var _ in styles)
        {
            writer.WriteByte(0);
            writer.WriteByte(0);
        }

        return writer.ToArray();
    }

    // The font names: a count, then for each a reserved word, the family ID and its name.
    private byte[] FontNames()
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(fonts.Count);
        foreach (var (id, name) in fonts)
        {
            writer.WriteUInt16(0);
            writer.WriteInt16(id);
            var bytes = MacRoman.Encode(name);
            writer.WriteByte(bytes.Length);
            writer.WriteBytes(bytes);
        }

        return writer.ToArray();
    }
}
