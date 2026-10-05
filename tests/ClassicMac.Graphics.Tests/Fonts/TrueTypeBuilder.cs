using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Graphics.Tests.Fonts;

// A small TrueType font as a Mac 'sfnt' holds one: head, hhea, maxp, hmtx, loca, glyf (.notdef and two squares, for 'A'
// and Mac OS Roman $8E 'é'), a Macintosh Roman cmap (format 0) and Macintosh names only; no OS/2 and no post. Tables
// can be added or left out.
internal sealed class TrueTypeBuilder
{
    public Dictionary<string, byte[]> Tables { get; } = new(StringComparer.Ordinal);

    public static TrueTypeBuilder Mac()
    {
        var b = new TrueTypeBuilder();
        var head = new BigEndianWriter();
        head.WriteUInt32(0x00010000u);
        head.WriteUInt32(0x00010000u);
        head.WriteUInt32(0u);                                             // checkSumAdjustment
        head.WriteUInt32(0x5F0F3CF5u);
        head.WriteUInt16(0x000B);
        head.WriteUInt16(1000);                                           // unitsPerEm
        head.WriteZeros(16);                                              // created, modified
        head.WriteInt16(0);
        head.WriteInt16(-200);
        head.WriteInt16(600);
        head.WriteInt16(800);
        head.WriteUInt16(0);                                              // macStyle
        head.WriteUInt16(8);
        head.WriteInt16(2);
        head.WriteInt16(0);                                               // short loca
        head.WriteInt16(0);
        b.Tables["head"] = head.ToArray();

        var hhea = new BigEndianWriter();
        hhea.WriteUInt32(0x00010000u);
        hhea.WriteInt16(800);
        hhea.WriteInt16(-200);
        hhea.WriteInt16(0);
        hhea.WriteUInt16(600);
        hhea.WriteInt16(0);
        hhea.WriteInt16(0);
        hhea.WriteInt16(600);
        hhea.WriteInt16(1);
        hhea.WriteInt16(0);
        hhea.WriteInt16(0);
        hhea.WriteZeros(8);
        hhea.WriteInt16(0);
        hhea.WriteUInt16(3);                                              // numberOfHMetrics
        b.Tables["hhea"] = hhea.ToArray();

        var maxp = new BigEndianWriter();
        maxp.WriteUInt32(0x00010000u);
        maxp.WriteUInt16(3);                                              // numGlyphs
        maxp.WriteUInt16(4);
        maxp.WriteUInt16(1);
        maxp.WriteZeros(4);
        maxp.WriteUInt16(2);                                              // maxZones
        maxp.WriteZeros(18);
        b.Tables["maxp"] = maxp.ToArray();

        var hmtx = new BigEndianWriter();
        foreach (var advance in new ushort[] { 500, 600, 600 })
        {
            hmtx.WriteUInt16(advance);
            hmtx.WriteInt16(0);
        }

        b.Tables["hmtx"] = hmtx.ToArray();

        var glyf = new BigEndianWriter();
        for (var g = 0; g < 2; g++)
        {
            glyf.WriteInt16(1);                                           // one contour
            glyf.WriteInt16(0);
            glyf.WriteInt16(0);
            glyf.WriteInt16(500);
            glyf.WriteInt16(700);
            glyf.WriteUInt16(3);                                          // endPtsOfContours
            glyf.WriteUInt16(0);                                          // no instructions
            glyf.WriteBytes([1, 1, 1, 1]);                                // on curve, x and y as words
            foreach (short x in new short[] { 0, 0, 500, 0 })
            {
                glyf.WriteInt16(x);
            }

            foreach (short y in new short[] { 0, 700, 0, -700 })
            {
                glyf.WriteInt16(y);
            }

            glyf.WriteZeros(2);                                           // to an even length
        }

        b.Tables["glyf"] = glyf.ToArray();
        var loca = new BigEndianWriter();
        foreach (ushort offset in new ushort[] { 0, 0, 18, 36 })
        {
            loca.WriteUInt16(offset);
        }

        b.Tables["loca"] = loca.ToArray();

        var cmap = new BigEndianWriter();
        cmap.WriteUInt16(0);
        cmap.WriteUInt16(1);
        cmap.WriteUInt16(1);                                              // Macintosh
        cmap.WriteUInt16(0);                                              // Roman
        cmap.WriteUInt32(12u);
        cmap.WriteUInt16(0);                                              // format 0
        cmap.WriteUInt16(262);
        cmap.WriteUInt16(0);
        var glyphs = new byte[256];
        glyphs[0x41] = 1;
        glyphs[0x8E] = 2;
        cmap.WriteBytes(glyphs);
        b.Tables["cmap"] = cmap.ToArray();

        b.Tables["name"] = MacNames(("Test Sans", 1), ("Regular", 2), ("Test Sans", 4), ("TestSans", 6));
        return b;
    }

    // A name table with Macintosh Roman English records.
    public static byte[] MacNames(params (string Text, ushort Id)[] names)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(0);
        w.WriteUInt16(names.Length);
        w.WriteUInt16(6 + 12 * names.Length);
        var storage = new List<byte>();
        foreach (var (text, id) in names)
        {
            var bytes = MacRoman.Encode(text);
            w.WriteUInt16(1);
            w.WriteUInt16(0);
            w.WriteUInt16(0);
            w.WriteUInt16(id);
            w.WriteUInt16(bytes.Length);
            w.WriteUInt16(storage.Count);
            storage.AddRange(bytes);
        }

        w.WriteBytes(storage.ToArray());
        return w.ToArray();
    }

    // The font file: the offset table, the directory in tag order (checksums 0), the tables 4-byte aligned.
    public byte[] Build()
    {
        var tags = Tables.Keys.Order(StringComparer.Ordinal).ToList();
        var w = new BigEndianWriter();
        w.WriteUInt32(0x00010000u);
        w.WriteUInt16(tags.Count);
        w.WriteZeros(6);
        var offset = 12 + 16 * tags.Count;
        foreach (var tag in tags)
        {
            w.WriteBytes(Encoding.ASCII.GetBytes(tag.PadRight(4)));
            w.WriteUInt32(0u);
            w.WriteUInt32(offset);
            w.WriteUInt32(Tables[tag].Length);
            offset += (Tables[tag].Length + 3) & ~3;
        }

        foreach (var tag in tags)
        {
            w.WriteBytes(Tables[tag]);
            w.WriteZeros(((Tables[tag].Length + 3) & ~3) - Tables[tag].Length);
        }

        return w.ToArray();
    }
}
