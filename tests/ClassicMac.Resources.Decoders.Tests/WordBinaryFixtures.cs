using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core.Tests;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 97 binary documents ('W8BN') built byte by byte as Microsoft's [MS-DOC] lays them out
// (docs/formats/documents/word-binary.md): the FIB, the text in one or two pieces, the CHPX and PAPX FKP pages, and in
// the table stream the Clx, the bin tables, the font table and the style sheet; then the compound file around them.
// No Microsoft file is used.
internal sealed class WordBinaryBuilder
{
    private const int Page = 512, TextStart = 1024;

    private readonly List<(int Start, int End, byte[] Grpprl)> characters = [];
    private readonly List<(int Start, int End, ushort Istd, byte[] Grpprl)> paragraphs = [];
    private readonly List<string> fonts = [];
    private readonly List<(ushort Base, byte[] Papx, byte[] Chpx)> styles = [];
    private string text = "";

    /// <summary>Where the text's second piece, stored as UTF-16, starts (no second piece when negative).</summary>
    public int UnicodeFrom { get; set; } = -1;

    /// <summary>FibBase's flags at +$0A ($0200: the table stream is 1Table).</summary>
    public ushort Flags { get; set; } = 0x0200;

    public ushort NFib { get; set; } = 0x00C1;

    /// <summary>A piece's Prm, for a fast save's property changes.</summary>
    public ushort Prm { get; set; }

    /// <summary>The Clx's Prcs: the property lists a Prm1 picks by index.</summary>
    public List<byte[]> Prcs { get; } = [];

    /// <summary>The default font's index (rgftcStandardChp).</summary>
    public ushort DefaultFont { get; set; }

    public WordBinaryBuilder Text(string value)
    {
        text = value;
        return this;
    }

    public WordBinaryBuilder Chp(int start, int end, params byte[] grpprl)
    {
        characters.Add((start, end, grpprl));
        return this;
    }

    public WordBinaryBuilder Pap(int start, int end, ushort istd = 0, params byte[] grpprl)
    {
        paragraphs.Add((start, end, istd, grpprl));
        return this;
    }

    public WordBinaryBuilder Font(string name)
    {
        fonts.Add(name);
        return this;
    }

    // A paragraph style based on istdBase ($0FFF for none), with its paragraph and character sprms.
    public WordBinaryBuilder Style(ushort istdBase, byte[] papx, byte[] chpx)
    {
        styles.Add((istdBase, papx, chpx));
        return this;
    }

    // Sprm helpers.
    public static byte[] Sprm(ushort sprm, params byte[] operand) => [(byte)sprm, (byte)(sprm >> 8), .. operand];

    public static byte[] Word(short value) => [(byte)value, (byte)(value >> 8)];

    // The CP's FC: one byte each in the first (compressed) piece, two in the second.
    private long FcOf(int cp, long second) => UnicodeFrom < 0 || cp < UnicodeFrom ? TextStart + cp : second + 2L * (cp - UnicodeFrom);

    public byte[] Build()
    {
        var split = UnicodeFrom < 0 ? text.Length : UnicodeFrom;
        var first = Compress(text[..split]);
        var secondAt = (TextStart + first.Length + 1) & ~1;
        var second = Encoding.Unicode.GetBytes(text[split..]);
        var textEnd = secondAt + second.Length;
        var chpPage = (textEnd + Page - 1) / Page;
        var papPage = chpPage + 1;

        var document = new byte[(papPage + 1) * Page];
        first.CopyTo(document, TextStart);
        second.CopyTo(document, secondAt);

        // FC runs covering the text: the given CP runs (cut at the pieces' boundary), the gaps with no properties.
        List<(long From, long To, byte[]? Block)> Runs(IEnumerable<(int Start, int End, byte[] Block)> given)
        {
            var runs = new List<(long, long, byte[]?)>();
            var at = 0;
            void Add(int from, int to, byte[]? block)
            {
                if (from >= to)
                {
                    return;
                }

                if (UnicodeFrom >= 0 && from < UnicodeFrom && to > UnicodeFrom)
                {
                    Add(from, UnicodeFrom, block);
                    Add(UnicodeFrom, to, block);
                    return;
                }

                runs.Add((FcOf(from, secondAt), FcOf(to - 1, secondAt) + (UnicodeFrom >= 0 && to - 1 >= UnicodeFrom ? 2 : 1), block));
            }

            foreach (var (start, end, block) in given.OrderBy(g => g.Start))
            {
                Add(at, start, null);
                Add(start, end, block);
                at = end;
            }

            Add(at, text.Length, null);
            return runs;
        }

        var chpRuns = Runs(characters.Select(c => (c.Start, c.End, c.Grpprl)));
        var papRuns = Runs(paragraphs.Select(p => (p.Start, p.End, (byte[])[(byte)p.Istd, (byte)(p.Istd >> 8), .. p.Grpprl])));
        Fkp(document.AsSpan(chpPage * Page, Page), chpRuns, block => [(byte)block.Length, .. block], 1);
        Fkp(document.AsSpan(papPage * Page, Page), papRuns, Papx, 13);

        // The table stream.
        var table = new List<byte>();
        (int Fc, int Lcb) Add(byte[] bytes)
        {
            var at = table.Count;
            table.AddRange(bytes);
            return (at, bytes.Length);
        }

        var stsh = Add(Stylesheet());
        var chpBte = Add(Bte(chpRuns[0].From, chpRuns[^1].To, chpPage));
        var papBte = Add(Bte(papRuns[0].From, papRuns[^1].To, papPage));
        var ffn = Add(FontTable());
        var clx = Add(Clx(split, first.Length, secondAt));

        // The FIB.
        var fib = document.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(fib, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(fib[2..], NFib);
        BinaryPrimitives.WriteUInt16LittleEndian(fib[0x0A..], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(fib[0x20..], 14);
        BinaryPrimitives.WriteUInt16LittleEndian(fib[0x3E..], 22);
        BinaryPrimitives.WriteInt32LittleEndian(fib[0x4C..], text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(fib[0x98..], 0x5D);
        void Pair(int index, (int Fc, int Lcb) value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(document.AsSpan(154 + 8 * index), value.Fc);
            BinaryPrimitives.WriteInt32LittleEndian(document.AsSpan(158 + 8 * index), value.Lcb);
        }

        Pair(1, stsh);
        Pair(12, chpBte);
        Pair(13, papBte);
        Pair(15, ffn);
        Pair(33, clx);

        return new CompoundFileBuilder().Stream("WordDocument", document).Stream((Flags & 0x0200) != 0 ? "1Table" : "0Table", [.. table]).Build();
    }

    // Windows-1252 for the compressed piece.
    private static byte[] Compress(string value) => value.Select(c => c switch
    {
        '’' => (byte)0x92,
        '—' => (byte)0x97,
        _ => (byte)c,
    }).ToArray();

    // PapxInFkp: an odd-length GrpPrlAndIstd after a count of words cb (2 × cb − 1 bytes), an even one after 0 and its words.
    private static byte[] Papx(byte[] block) => block.Length % 2 == 1 ? [(byte)((block.Length + 1) / 2), .. block] : [0, (byte)(block.Length / 2), .. block];

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
            var block = runs[i].Block ?? (entry == 13 ? [0, 0] : null);
            if (block is null)
            {
                continue;
            }

            var bytes = encode(block);
            top = (top - bytes.Length) & ~1;
            bytes.CopyTo(page[top..]);
            page[4 * (runs.Count + 1) + entry * i] = (byte)(top / 2);
        }

        page[Page - 1] = (byte)runs.Count;
    }

    private static byte[] Bte(long from, long to, int pn)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)from);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)to);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)pn);
        return bytes;
    }

    private byte[] Clx(int split, int firstLength, int secondAt)
    {
        var pieces = new List<(int Cp, int End, uint Fc)> { (0, split, (uint)(TextStart * 2) | 0x40000000) };
        if (UnicodeFrom >= 0)
        {
            pieces.Add((split, text.Length, (uint)secondAt));
        }

        _ = firstLength;
        var plc = new List<byte>();
        foreach (var piece in pieces)
        {
            plc.AddRange(BitConverter.GetBytes(piece.Cp));
        }

        plc.AddRange(BitConverter.GetBytes(pieces[^1].End));
        foreach (var piece in pieces)
        {
            plc.AddRange(BitConverter.GetBytes((ushort)0));
            plc.AddRange(BitConverter.GetBytes(piece.Fc));
            plc.AddRange(BitConverter.GetBytes(Prm));
        }

        var prcs = Prcs.SelectMany(g => (byte[])[0x01, .. BitConverter.GetBytes((short)g.Length), .. g]);
        return [.. prcs, 0x02, .. BitConverter.GetBytes(plc.Count), .. plc];
    }

    private byte[] FontTable()
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)fonts.Count));
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        foreach (var name in fonts)
        {
            var ffn = new List<byte>(new byte[39]);
            ffn.AddRange(Encoding.Unicode.GetBytes(name + "\0"));
            bytes.Add((byte)ffn.Count);
            bytes.AddRange(ffn);
        }

        return [.. bytes];
    }

    private byte[] Stylesheet()
    {
        var bytes = new List<byte>();
        var stshi = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(stshi, (ushort)styles.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(stshi.AsSpan(2), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(stshi.AsSpan(12), DefaultFont);
        bytes.AddRange(BitConverter.GetBytes((ushort)stshi.Length));
        bytes.AddRange(stshi);
        for (var i = 0; i < styles.Count; i++)
        {
            var (istdBase, papx, chpx) = styles[i];
            var std = new List<byte>();
            std.AddRange(BitConverter.GetBytes((ushort)i));                                   // sti
            std.AddRange(BitConverter.GetBytes((ushort)(1 | (istdBase << 4))));             // stk 1 (paragraph), istdBase
            std.AddRange(BitConverter.GetBytes((ushort)(2 | (i << 4))));                    // cupx 2, istdNext
            std.AddRange(new byte[4]);                                                      // bchUpe, grfstd
            var name = $"Style {i}";
            std.AddRange(BitConverter.GetBytes((ushort)name.Length));
            std.AddRange(Encoding.Unicode.GetBytes(name + "\0"));
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
