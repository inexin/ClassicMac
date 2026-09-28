using System.Buffers.Binary;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Tests;

// Small DOCMaker and SimpleText documents made in code, laid out as DOCMaker 4.8.4 and SimpleText 1.4 read them.
internal static class DocumentFixtures
{
    // A 4×4 version 1 picture: frame (0,0)–(4,4), a black 2×2 square. And a 40×20 one.
    public static byte[] Picture(short width = 4, short height = 4) =>
        [0, 0, 0, 0, 0, 0, .. BE16(height), .. BE16(width), 0x11, 0x01, 0x31, 0, 0, 0, 0, 0, 2, 0, 2, 0xFF];

    public static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];

    public static byte[] Pascal(string text) => [(byte)MacRoman.Encode(text).Length, .. MacRoman.Encode(text)];

    // styl: runs of (start, line height, ascent, font, face, size, red).
    public static byte[] Styl(params (int Start, short Height, short Ascent, short Font, byte Face, short Size, ushort Red)[] runs)
    {
        var data = new byte[2 + runs.Length * 20];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)runs.Length);
        for (var i = 0; i < runs.Length; i++)
        {
            var e = data.AsSpan(2 + i * 20);
            var (start, height, ascent, font, face, size, red) = runs[i];
            BinaryPrimitives.WriteInt32BigEndian(e, start);
            BinaryPrimitives.WriteInt16BigEndian(e[4..], height);
            BinaryPrimitives.WriteInt16BigEndian(e[6..], ascent);
            BinaryPrimitives.WriteInt16BigEndian(e[8..], font);
            e[10] = face;
            BinaryPrimitives.WriteInt16BigEndian(e[12..], size);
            BinaryPrimitives.WriteUInt16BigEndian(e[14..], red);
        }
        return data;
    }

    // Wndo: margins (top, left, bottom, right), print margins, no-footer byte, justification.
    public static byte[] Wndo(short left, short right, short justification) =>
        [0, 10, .. BE16(left), 0, 10, .. BE16(right), 0, 36, 0, 36, 0, 36, 0, 36, 0, 0, .. BE16(justification)];

    // pInf: PICT, alignment (1 centre, 2 left, 3 right), no-scale, action, action data, print flag.
    public static byte[] PInf(short pict, short alignment, short noScale, short action, byte[] data) =>
        [.. BE16(pict), .. BE16(alignment), .. BE16(noScale), .. BE16(action), .. data, 0, 1];

    // Two chapters in a 300-pixel window:
    //  1 "Welcome": a Palatino 18 bold heading, then body text with two pictures on one line (left, right; the left one a
    //    link to chapter 2, the right one to a chapter that does not exist), and a centred wide picture scaled to the
    //    column; an extra option-space with no pInf.
    //  2 (no STR 2002: "Chapter 2"): centred text, a blue background, a "next" button.
    public static ResourceFork DocMaker()
    {
        var fork = new ResourceFork();
        void Add(string type, short id, byte[] data) => fork.Add(new Resource(FourCC.FromString(type), id, data));
        var text1 = MacRoman.Encode("Welcome\rThe game begins here.\r") is var head
            ? [.. head, 0xCA, 0xCA, .. MacRoman.Encode("\rA wide map:\r"), 0xCA, .. MacRoman.Encode("\rThe end."), 0xCA]
            : Array.Empty<byte>();
        Add("TEXT", 128, text1);
        Add("styl", 128, Styl((0, 22, 17, 16, 1, 18, 0), (8, 16, 12, 16, 0, 12, 0)));
        Add("Wndo", 128, Wndo(20, 5, 0));
        Add("STR ", 2001, Pascal("Welcome"));
        Add("pInf", 201, PInf(1001, 2, 0, 1, [0, 2, 0xFF, 0xFF]));
        Add("pInf", 202, PInf(1002, 3, 0, -1, [0, 9, 0, 0]));
        Add("pInf", 203, PInf(1003, 1, 0, 0, []));
        Add("TEXT", 129, MacRoman.Encode("Second chapter.\r") is var t2 ? [.. t2, 0xCA] : []);
        Add("styl", 129, Styl((0, 16, 12, 3, 0, 12, 0)));
        Add("Wndo", 129, Wndo(20, 5, 1));
        Add("pInf", 301, PInf(1001, 3, 0, 14, []));
        Add("PICT", 1001, Picture());
        Add("PICT", 1002, Picture(40, 20));
        Add("PICT", 1003, Picture(400, 100));
        Add("sTwD", 128, [0, 2, 0, 0, 0, 200, .. BE16(300), 0, 0]);
        Add("clut", 128, [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 1, 0xCC, 0xCC, 0xDD, 0xDD, 0xFF, 0xFF]);
        Add("cnt#", 128, [0, 1, 0, 1, 0, 8, 0, 20, .. Pascal("The game")]);
        return fork;
    }
}
