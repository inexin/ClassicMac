using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Core.Tests;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 6 and 95 documents ('W6BN') built byte by byte as docs/formats/documents/word-binary.md §4.1 lays them out: one
// WordDocument stream holding the FIB, the 8-bit text, the CHPX and PAPX FKP pages, and the bin tables, font table,
// style sheet and (for a fast-saved document) piece table, in a compound file. Sprms are one-byte codes. No Microsoft
// file is used.
internal sealed class WordSixBuilder
{
    private const int Page = 512, TextStart = 1024;

    private readonly List<(int Start, int End, byte[] Grpprl)> characters = [];
    private readonly List<(int Start, int End, ushort Istd, byte[] Grpprl)> paragraphs = [];
    private readonly List<string> fonts = [];
    private readonly List<(ushort Base, byte[] Papx, byte[] Chpx)> styles = [];
    private string text = "";

    public ushort NFib { get; set; } = 0x0065;

    public ushort Flags { get; set; }

    /// <summary>The FIB's character set at +$14: 256 for the Macintosh's, 0 for Windows'.</summary>
    public ushort Chse { get; set; } = 256;

    /// <summary>Writes a piece table (a fast-saved document's) instead of the FIB's text range alone.</summary>
    public bool PieceTable { get; set; }

    /// <summary>The piece's Prm, for a fast save's property changes.</summary>
    public ushort Prm { get; set; }

    public WordSixBuilder Text(string value)
    {
        text = value;
        return this;
    }

    public WordSixBuilder Chp(int start, int end, params byte[] grpprl)
    {
        characters.Add((start, end, grpprl));
        return this;
    }

    public WordSixBuilder Pap(int start, int end, ushort istd = 0, params byte[] grpprl)
    {
        paragraphs.Add((start, end, istd, grpprl));
        return this;
    }

    public WordSixBuilder Font(string name)
    {
        fonts.Add(name);
        return this;
    }

    public WordSixBuilder Style(ushort istdBase, byte[] papx, byte[] chpx)
    {
        styles.Add((istdBase, papx, chpx));
        return this;
    }

    public byte[] Build()
    {
        var bytes = Chse == 256 ? MacRoman.Encode(text) : text.Select(c => (byte)c).ToArray();
        var textEnd = TextStart + bytes.Length;
        var chpPage = (textEnd + Page - 1) / Page;
        var papPage = chpPage + 1;
        var document = new List<byte>(new byte[(papPage + 1) * Page]);
        for (var i = 0; i < bytes.Length; i++)
        {
            document[TextStart + i] = bytes[i];
        }

        List<(long From, long To, byte[]? Block)> Runs(IEnumerable<(int Start, int End, byte[] Block)> given)
        {
            var runs = new List<(long, long, byte[]?)>();
            var at = 0;
            foreach (var (start, end, block) in given.OrderBy(g => g.Start))
            {
                if (start > at)
                {
                    runs.Add((TextStart + at, TextStart + start, null));
                }

                runs.Add((TextStart + start, TextStart + end, block));
                at = end;
            }

            if (at < text.Length)
            {
                runs.Add((TextStart + at, TextStart + text.Length, null));
            }

            return runs;
        }

        var chpRuns = Runs(characters.Select(c => (c.Start, c.End, c.Grpprl)));
        var papRuns = Runs(paragraphs.Select(p => (p.Start, p.End, (byte[])[(byte)p.Istd, (byte)(p.Istd >> 8), .. p.Grpprl])));
        var pages = document.ToArray();
        Fkp(pages.AsSpan(chpPage * Page, Page), chpRuns, block => [(byte)block.Length, .. block], 1);
        // A PAPX: a count of words, then the istd and the grpprl, padded to a whole word.
        Fkp(pages.AsSpan(papPage * Page, Page), papRuns, block => [(byte)((block.Length + 1) / 2), .. block, .. block.Length % 2 == 1 ? new byte[1] : []], 7);

        // The tables, after the pages, in the same stream.
        var all = new List<byte>(pages);
        (int Fc, int Lcb) Add(byte[] data)
        {
            var at = all.Count;
            all.AddRange(data);
            return (at, data.Length);
        }

        var stsh = Add(Stylesheet());
        var chpBte = Add(Bte(chpRuns[0].From, chpRuns[^1].To, chpPage));
        var papBte = Add(Bte(papRuns[0].From, papRuns[^1].To, papPage));
        var ffn = Add(FontTable());
        var clx = PieceTable ? Add(Clx()) : (0, 0);

        var fib = all.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(fib, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(2), NFib);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(0x0A), Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(0x14), Chse);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x18), TextStart);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x1C), textEnd);
        BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x34), text.Length);
        void Pair(int index, (int Fc, int Lcb) value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x58 + 8 * index), value.Fc);
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x5C + 8 * index), value.Lcb);
        }

        Pair(1, stsh);
        Pair(12, chpBte);
        Pair(13, papBte);
        Pair(15, ffn);
        Pair(33, clx);
        return new CompoundFileBuilder().Stream("WordDocument", fib).Build();
    }

    private static void Fkp(Span<byte> page, List<(long From, long To, byte[]? Block)> runs, Func<byte[], byte[]> encode, int entry)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page[(4 * i)..], (uint)runs[i].From);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(page[(4 * runs.Count)..], (uint)runs[^1].To);
        var top = Page - 1;
        for (var i = 0; i < runs.Count; i++)
        {
            var block = runs[i].Block ?? (entry == 7 ? [0, 0] : null);
            if (block is null)
            {
                continue;
            }

            var encoded = encode(block);
            top = (top - encoded.Length) & ~1;
            encoded.CopyTo(page[top..]);
            page[4 * (runs.Count + 1) + entry * i] = (byte)(top / 2);
        }

        page[Page - 1] = (byte)runs.Count;
    }

    // A bin table with one page: two FCs, a 2-byte page number.
    private static byte[] Bte(long from, long to, int pn)
    {
        var bytes = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)from);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)to);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)pn);
        return bytes;
    }

    // One piece: the whole text, its FC a plain byte offset.
    private byte[] Clx()
    {
        var plc = new List<byte>();
        plc.AddRange(BitConverter.GetBytes(0));
        plc.AddRange(BitConverter.GetBytes(text.Length));
        plc.AddRange(BitConverter.GetBytes((ushort)0));
        plc.AddRange(BitConverter.GetBytes(TextStart));
        plc.AddRange(BitConverter.GetBytes(Prm));
        return [0x02, .. BitConverter.GetBytes(plc.Count), .. plc];
    }

    // The font table: its size in bytes, then per font a size byte (less one), ffid, weight (2), charset, the alternate
    // name's index, and the name, null-terminated.
    private byte[] FontTable()
    {
        var body = new List<byte>();
        foreach (var name in fonts)
        {
            var entry = new List<byte> { 0, 0, 0, 0, 0 };
            entry.AddRange(MacRoman.Encode(name));
            entry.Add(0);
            body.Add((byte)entry.Count);
            body.AddRange(entry);
        }

        return [.. BitConverter.GetBytes((ushort)(body.Count + 2)), .. body];
    }

    // The style sheet: the STSHI, then per style an STD with its base, its name as a length byte, the characters and a
    // null, and its UPXs.
    private byte[] Stylesheet()
    {
        var bytes = new List<byte>();
        var stshi = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(stshi, (ushort)styles.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(stshi.AsSpan(2), 10);
        bytes.AddRange(BitConverter.GetBytes((ushort)stshi.Length));
        bytes.AddRange(stshi);
        for (var i = 0; i < styles.Count; i++)
        {
            var (istdBase, papx, chpx) = styles[i];
            var std = new List<byte>();
            std.AddRange(BitConverter.GetBytes((ushort)i));
            std.AddRange(BitConverter.GetBytes((ushort)(1 | (istdBase << 4))));
            std.AddRange(BitConverter.GetBytes((ushort)(2 | (i << 4))));
            std.AddRange(new byte[4]);
            var name = MacRoman.Encode($"Style {i}");
            std.Add((byte)name.Length);
            std.AddRange(name);
            std.Add(0);
            if (std.Count % 2 == 1)
            {
                std.Add(0);
            }

            std.AddRange(BitConverter.GetBytes((ushort)(2 + papx.Length)));
            std.AddRange(BitConverter.GetBytes((ushort)i));
            std.AddRange(papx);
            if ((2 + papx.Length) % 2 == 1)
            {
                std.Add(0);
            }

            std.AddRange(BitConverter.GetBytes((ushort)chpx.Length));
            std.AddRange(chpx);
            if (chpx.Length % 2 == 1)
            {
                std.Add(0);
            }

            bytes.AddRange(BitConverter.GetBytes((ushort)std.Count));
            bytes.AddRange(std);
        }

        return [.. bytes];
    }
}
