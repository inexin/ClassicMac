using System.Buffers.Binary;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Tests;

// One fork with a resource for every built-in decoder and its variants, made in code (no Apple data), for the golden
// tests. Colour structures follow Inside Macintosh: Imaging With QuickDraw (PixMap, ColorTable, CIcon, CCrsr, PixPat).
internal static class GoldenFixtures
{
    // A fixture: the resource, and the options it is decoded with (a key suffix names non-default ones).
    public sealed record Fixture(string Key, Resource Resource, DecodeOptions Options);

    public static ResourceFork Fork()
    {
        var fork = new ResourceFork();
        foreach (var r in Resources()) fork.Add(r);
        return fork;
    }

    public static IReadOnlyList<Fixture> All()
    {
        var fixtures = new List<Fixture>();
        foreach (var r in Resources())
        {
            var key = $"{r.Type.ToString().Replace(' ', '_')}-{r.Id}";
            fixtures.Add(new Fixture(key, r, DecodeOptions.Default));
            if (r.Type.ToString() == "PICT" && r.Id == 129)
            {
                foreach (var depth in new[] { 1, 8 })
                    fixtures.Add(new Fixture($"{key}-d{depth}", r, DecodeOptions.Default with { ScreenDepth = depth }));
            }
        }
        return fixtures;
    }

    private static Resource Res(string type, short id, byte[] data, string? name = null)
    {
        var r = new Resource(FourCC.FromString(type), id, data);
        if (name is not null) r.Name = MacString.FromMacRoman(name);
        return r;
    }

    private static IEnumerable<Resource> Resources()
    {
        // Text.
        yield return Res("STR ", 128, Pascal("Café ƒ™\rline 2"), "Greeting");
        yield return Res("STR#", 128, [0, 3, .. Pascal("Human"), .. Pascal("Élf"), .. Pascal("")]);
        yield return Res("TEXT", 128, MacRoman.Encode("Title\rBody text, “quoted”."));
        yield return Res("styl", 128, Styl((0, 20, 1, 18, 0, 0, 0), (6, 4, 2, 10, 0xFFFF, 0, 0), (11, 3, 4, 12, 0, 0x8000, 0)));
        yield return Res("TEXT", 129, MacRoman.Encode("Plain text with no styles.\rSecond line."));
        yield return Res("styl", 130, Styl((0, 3, 0, 12, 0, 0, 0xFFFF)));
        yield return Res("vers", 1, [1, 0x20, 0x80, 0, 0, 0, .. Pascal("1.2"), .. Pascal("1.2, © 1996")]);

        // Pictures: version 1 (black square) and version 2 (a colour square, also drawn at 1 and 8 bits).
        yield return Res("PICT", 128, [0, 0, 0, 0, 0, 0, 0, 4, 0, 4, 0x11, 0x01, 0x31, 0, 0, 0, 0, 0, 2, 0, 2, 0xFF]);
        yield return Res("PICT", 129, ColourPicture());

        // Icons, black-and-white with masks, and colour ones that take those masks.
        yield return Res("ICON", 128, Checker(32, 32));
        yield return Res("ICN#", 128, [.. Checker(32, 32), .. Circle(32, 32)]);
        yield return Res("ics#", 128, [.. Checker(16, 16), .. Circle(16, 16)]);
        yield return Res("icm#", 128, [.. Checker(16, 12), .. Circle(16, 12)]);
        yield return Res("icl4", 128, Ramp(32 * 32 / 2));
        yield return Res("icl8", 128, Ramp(32 * 32));
        yield return Res("ics4", 128, Ramp(16 * 16 / 2));
        yield return Res("ics8", 128, Ramp(16 * 16));
        yield return Res("icm4", 128, Ramp(16 * 12 / 2));
        yield return Res("icm8", 128, Ramp(16 * 12));
        yield return Res("cicn", 128, ColourIcon());
        yield return Res("SICN", 128, [.. Checker(16, 16), .. Circle(16, 16)]);

        // Cursors and patterns.
        yield return Res("CURS", 128, [.. Checker(16, 16), .. Circle(16, 16), 0, 7, 0, 8]);
        yield return Res("crsr", 128, ColourCursor());
        yield return Res("PAT ", 128, [0xAA, 0x55, 0xAA, 0x55, 0x88, 0x22, 0x88, 0x22]);
        yield return Res("PAT#", 128, [0, 2, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81]);
        yield return Res("ppat", 128, ColourPattern());
        var pattern = ColourPattern();
        yield return Res("ppt#", 128, [0, 2, .. BE32(10), .. BE32((uint)(10 + pattern.Length)), .. pattern, .. pattern]);

        // Sounds: each header kind and sample format, and a sound with commands only.
        yield return Res("snd ", 128, SoundFormat1(Standard(Wave(64, 8), loopStart: 8, loopEnd: 56, note: 72)), "Beep");
        yield return Res("snd ", 129, SoundFormat2(Standard(Wave(32, 8))));
        yield return Res("snd ", 130, SoundFormat1(Long(0xFF, 2, 16, 16, "", 0, Wave(64, 16))));
        yield return Res("snd ", 131, SoundFormat1(Long(0xFE, 1, 32, 16, "sowt", -1, Swap16(Wave(32, 16)))));
        yield return Res("snd ", 132, SoundFormat1(Long(0xFE, 1, 8, 32, "fl32", -1, Floats(8))));
        yield return Res("snd ", 133, SoundFormat1(Long(0xFE, 1, 4, 8, "MAC3", 3, Bytes(8, 0x3C))));
        yield return Res("snd ", 134, SoundFormat1(Long(0xFE, 1, 8, 8, "MAC6", 4, Bytes(8, 0x5A))));
        yield return Res("snd ", 135, SoundFormat1(Long(0xFE, 1, 2, 16, "ima4", -1, [.. Ima4Packet(0x0000), .. Ima4Packet(0x0F02)])));
        yield return Res("snd ", 136, SoundFormat1(Long(0xFE, 1, 16, 16, "ulaw", -1, Bytes(16, 0x17))));
        yield return Res("snd ", 137, [0, 1, 0, 0, 0, 2, 0, 40, 0, 60, 0, 0, 3, 0xE8, 0, 46, 0, 0, 1, 0, 1, 0]);

        // Interface resources (Inside Macintosh: Macintosh Toolbox Essentials): a menu with a divider, a Command key, a
        // check mark, a submenu and a disabled item (Quit); a menu bar; window, dialog and alert templates with and without the
        // positioning word; an item list of every type; controls.
        yield return Res("MENU", 128, [0, 128, 0, 0, 0, 0, 0, 0, 0, 0, .. BE32(0b1011_1011), .. Pascal("File"),
            .. Pascal("Open…"), 0, (byte)'O', 0, 0, .. Pascal("-"), 0, 0, 0, 0, .. Pascal("Checked"), 0, 0, 0x12, 1,
            .. Pascal("Recent"), 0, 0x1B, 130, 0, .. Pascal("Italic"), 0, 0, 0, 2, .. Pascal("Quit"), 0, (byte)'Q', 0, 0, 0]);
        yield return Res("MBAR", 128, [0, 3, 0, 1, 0, 128, 0, 129]);
        yield return Res("WIND", 128, [0, 40, 0, 40, 1, 44, 1, 184, 0, 8, 1, 0, 1, 0, 0, 0, 0, 7, .. Pascal("Maps"), 0, 0x28, 0x0A]);
        yield return Res("DLOG", 128, [0, 50, 0, 50, 0, 150, 1, 94, 0, 5, 1, 0, 0, 0, 0, 0, 0, 0, 0, 128, .. Pascal("Save")]);
        yield return Res("ALRT", 128, [0, 40, 0, 40, 0, 140, 1, 144, 0, 128, 0x76, 0x54, 0x30, 0x0A]);
        yield return Res("DITL", 128, [0, 9,
            .. DitlItem(4, [60, 10, 80, 70], Pascal("OK")[1..]), .. DitlItem(5, [30, 10, 48, 150], Pascal("Sound")[1..]),
            .. DitlItem(6, [30, 160, 48, 250], Pascal("Music")[1..]), .. DitlItem(7, [90, 10, 110, 150], [0, 128]),
            .. DitlItem(8 | 0x80, [10, 50, 26, 250], MacRoman.Encode("Save changes to ^0?")), .. DitlItem(16, [120, 10, 136, 150], []),
            .. DitlItem(32 | 0x80, [10, 10, 42, 42], [0, 1]), .. DitlItem(64, [140, 10, 172, 42], [0, 128]), .. DitlItem(0, [180, 10, 200, 200], []),
            .. DitlItem(1, [0, 0, 0, 0], [0, 8, 0, 128, 0, 0])]);
        yield return Res("DITL", 129, [0, 1, .. DitlItem(4, [0, 0, 20, 60], Pascal("OK")[1..])]); // says 2 items, holds 1
        yield return Res("CNTL", 128, [0, 10, 0, 10, 0, 26, 0, 150, 0, 1, 1, 0, 0, 5, 0, 1, 3, 0xF0, 0, 0, 0, 0, .. Pascal("Level:")]);
        // A pop-up menu control: menu 128 (min), title width 60 (max), centred bold title (value), AddResMenu of 'FONT'.
        yield return Res("CNTL", 129, [0, 40, 0, 10, 0, 60, 0, 200, 0x01, 0x01, 1, 0, 0, 60, 0, 128, 0x03, 0xF4, .. "FONT"u8, .. Pascal("Font:")]);

        // Colour and extension resources: window, dialog (the 8-byte "default colours" form), alert and control colour
        // tables; menu colours (the bar, a title, an item, the end); item colours for DITL 128 (the button's colour
        // table; the static text's font by name, size and colour); Appearance extensions (dlgx, alrx of both versions,
        // xmnu with a skipped entry).
        yield return Res("wctb", 128, [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xCC, 0xCC, 0, 1, 0, 0, 0, 0, 0x80, 0]);
        yield return Res("dctb", 128, [0, 0, 0, 0, 0, 0, 0xFF, 0xFF]);
        yield return Res("actb", 128, [0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0xFF, 0xFF, 0, 0, 0, 0]);
        yield return Res("cctb", 128, [0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xFF, 0xFF]);
        yield return Res("mctb", 128, [0, 4, .. MenuColor(0, 0), .. MenuColor(128, 0), .. MenuColor(128, 3), .. MenuColor(-99, 0)]);
        yield return Res("ictb", 128, [0, 16, 0, 40, .. new byte[12], 0x80, 0x0C, 0, 56, .. new byte[20],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0xFF, 0, 0, 0, 0, 0, 76, 0, 0, 0, 12, 0xFF, 0xFF, 0, 0, 0, 0, .. new byte[8], .. Pascal("Geneva")]);
        yield return Res("dlgx", 128, [0, 0, 0, 0, 0, 0x0B]);
        yield return Res("alrx", 128, [0, 1, 0, 0, 0, 0x0F, 0, 0, 0, 7, 1, 0, .. Pascal("Warning")]);
        yield return Res("alrx", 129, [0, 0, 0, 0, 0, 0x0B, 0, 0, 0, 0, 0, 0, .. new byte[16], .. Pascal("Old")]);
        yield return Res("xmnu", 128, [0, 0, 0, 2, 0, 0, 0, 1, .. "quit"u8, 3, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 1, 44, 0, 3, 0, 0]);

        // Palettes: a pixel map's colour table (values as indices; two stray bytes after it), a device table (in order),
        // and a palette with usages and tolerances.
        yield return Res("clut", 128, [0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 5, 0x80, 0x00, 0, 0, 0, 0,
            0, 255, 0, 0, 0, 0, 0, 0, 0, 9]);
        yield return Res("clut", 129, [0, 0, 0, 0, 0x80, 0, 0, 1, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0]);
        yield return Res("pltt", 128, [0, 2, .. new byte[14], 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0x02, 0x10, 0x00, .. new byte[6],
            0x33, 0x33, 0x66, 0x66, 0x99, 0x99, 0, 0x24, 0, 0, .. new byte[6]]);

        // Finder resources: a bundle with icon and file reference maps (local icon 1 → ICN# 128, a TEXT FREF with no icon
        // mapped), its FREFs, and a size resource.
        yield return Res("BNDL", 128, [.. "RLMZ"u8, 0, 0, 0, 1, .. "ICN#"u8, 0, 1, 0, 0, 0, 128, 0, 1, 0, 129, .. "FREF"u8, 0, 1, 0, 0, 0, 128, 0, 1, 0, 129]);
        yield return Res("FREF", 128, [.. "APPL"u8, 0, 0, 0]);
        yield return Res("FREF", 129, [.. "TEXT"u8, 0, 7, 0]);
        yield return Res("SIZE", -1, [0x58, 0x80, 0, 0x20, 0, 0, 0, 0x10, 0, 0]);

        // Fonts: the family "Example" (FOND 1024) with a 9-point FONT (1033), a 12-point NFNT (1036, with width and height
        // tables), and its outline font (sfnt 1024); the old-style family-name FONT (family 8 × 128, empty); a 2-bit colour
        // NFNT's colour table.
        yield return Res("FOND", 1024, ClassicMac.Fonts.Tests.FontBuilder.Family(), "Example");
        yield return Res("FONT", 1033, ClassicMac.Fonts.Tests.FontBuilder.Sample());
        yield return Res("NFNT", 1036, ClassicMac.Fonts.Tests.FontBuilder.Sample(tables: true));
        yield return Res("sfnt", 1024, ClassicMac.Fonts.Tests.FontBuilder.Sfnt());
        yield return Res("FONT", 1024, [], "Example");
        yield return Res("fctb", 1036, [0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0xFF, 0xFF, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0, 0xFF, 0xFF]);

        // A type no decoder handles: exported raw.
        yield return Res("CODE", 1, [0x4E, 0x75]);
    }

    // An mctb entry: menu, item, four colours (grey levels 1–4), reserved.
    private static byte[] MenuColor(short menu, short item) =>
        [(byte)(menu >> 8), (byte)menu, (byte)(item >> 8), (byte)item,
            .. Enumerable.Range(1, 4).SelectMany(level => Enumerable.Repeat((byte)(level * 0x30), 6)), 0, 0];

    // A dialog item: placeholder, rectangle (top, left, bottom, right), type, length, data padded to even.
    private static byte[] DitlItem(int type, short[] rect, byte[] data) =>
        [0, 0, 0, 0, .. rect.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }), (byte)type, (byte)data.Length, .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    private static byte[] Pascal(string text) => [(byte)MacRoman.Encode(text).Length, .. MacRoman.Encode(text)];

    // styl: runs of (start, font, face, size, r, g, b).
    private static byte[] Styl(params (int Start, short Font, byte Face, short Size, ushort R, ushort G, ushort B)[] runs)
    {
        var data = new byte[2 + runs.Length * 20];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)runs.Length);
        for (var i = 0; i < runs.Length; i++)
        {
            var e = data.AsSpan(2 + i * 20);
            var (start, font, face, size, r, g, b) = runs[i];
            BinaryPrimitives.WriteInt32BigEndian(e, start);
            BinaryPrimitives.WriteInt16BigEndian(e[4..], (short)(size + size / 4)); // line height
            BinaryPrimitives.WriteInt16BigEndian(e[6..], (short)size); // ascent
            BinaryPrimitives.WriteInt16BigEndian(e[8..], font);
            e[10] = face;
            BinaryPrimitives.WriteInt16BigEndian(e[12..], size);
            BinaryPrimitives.WriteUInt16BigEndian(e[14..], r);
            BinaryPrimitives.WriteUInt16BigEndian(e[16..], g);
            BinaryPrimitives.WriteUInt16BigEndian(e[18..], b);
        }
        return data;
    }

    // 1-bit images, rows padded to 16 bits.
    private static byte[] Checker(int width, int height) => Bits(width, height, (x, y) => ((x / 4) + (y / 4)) % 2 == 0);

    private static byte[] Circle(int width, int height) =>
        Bits(width, height, (x, y) => Math.Pow(x - (width - 1) / 2.0, 2) + Math.Pow(y - (height - 1) / 2.0, 2) <= Math.Pow(Math.Min(width, height) / 2.0, 2));

    private static byte[] Bits(int width, int height, Func<int, int, bool> set)
    {
        var rowBytes = (width + 15) / 16 * 2;
        var data = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (set(x, y)) data[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
        return data;
    }

    private static byte[] Ramp(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    private static byte[] Bytes(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(seed * (i + 1) + i)).ToArray();

    private static byte[] BE16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] BE32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Rect(int top, int left, int bottom, int right) => [.. BE16(top), .. BE16(left), .. BE16(bottom), .. BE16(right)];

    // A PixMap record for an indexed image of the given size and depth; pmTable as given (an offset in resources).
    private static byte[] PixMap(int width, int height, int depth, uint table) =>
    [
        0, 0, 0, 0, .. BE16(0x8000 | (width * depth + 15) / 16 * 2), .. Rect(0, 0, height, width),
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0x48, 0, 0, 0, 0x48, 0, 0, 0, 0, .. BE16(depth), 0, 1, .. BE16(depth),
        0, 0, 0, 0, .. BE32(table), 0, 0, 0, 0,
    ];

    private static byte[] ColorTable(params (ushort R, ushort G, ushort B)[] colours)
    {
        var table = new List<byte>([0, 0, 0, 0, 0, 0, .. BE16(colours.Length - 1)]);
        for (var i = 0; i < colours.Length; i++) table.AddRange([.. BE16(i), .. BE16(colours[i].R), .. BE16(colours[i].G), .. BE16(colours[i].B)]);
        return [.. table];
    }

    private static readonly (ushort, ushort, ushort)[] FourColours = [(0xFFFF, 0xFFFF, 0xFFFF), (0xFFFF, 0, 0), (0, 0x8000, 0), (0, 0, 0)];

    // 2 bits per pixel, a diagonal ramp.
    private static byte[] Pixels2(int width, int height)
    {
        var rowBytes = (width * 2 + 15) / 16 * 2;
        var data = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            data[y * rowBytes + x / 4] |= (byte)(((x + y) / 4 % 4) << (6 - x % 4 * 2));
        return data;
    }

    // CIcon: PixMap, mask BitMap, icon BitMap, iconData, then mask, icon bits, colour table and pixels.
    private static byte[] ColourIcon()
    {
        byte[] bitmap = [0, 0, 0, 0, 0, 2, .. Rect(0, 0, 16, 16)];
        return [.. PixMap(16, 16, 2, 0), .. bitmap, .. bitmap, 0, 0, 0, 0, .. Circle(16, 16), .. Checker(16, 16),
            .. ColorTable(FourColours), .. Pixels2(16, 16)];
    }

    // CCrsr (96 bytes), then its PixMap, pixels and colour table.
    private static byte[] ColourCursor()
    {
        var pixels = Pixels2(16, 16);
        const int map = 96, data = map + 50;
        var table = (uint)(data + pixels.Length);
        return [0x80, 0x01, .. BE32(map), .. BE32(data), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. Checker(16, 16), .. Circle(16, 16),
            0, 7, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, .. PixMap(16, 16, 2, table), .. pixels, .. ColorTable(FourColours)];
    }

    // PixPat (28 bytes), then its PixMap, pixels and colour table.
    private static byte[] ColourPattern()
    {
        var pixels = Pixels2(8, 8);
        const int map = 28, data = map + 50;
        var table = (uint)(data + pixels.Length);
        return [0, 1, .. BE32(map), .. BE32(data), 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55,
            .. PixMap(8, 8, 2, table), .. pixels, .. ColorTable(FourColours)];
    }

    // A version 2 picture, 8×8: header, RGBForeColor, PaintRect over (1,1)–(7,7), end.
    private static byte[] ColourPicture() =>
    [
        0, 0, .. Rect(0, 0, 8, 8), 0x00, 0x11, 0x02, 0xFF, 0x0C, 0x00, 0xFF, 0xFE, 0, 0, 0, 0x48, 0, 0, 0, 0x48, 0, 0,
        .. Rect(0, 0, 8, 8), 0, 0, 0, 0, 0x00, 0x1A, 0x80, 0x00, 0x40, 0x00, 0xC0, 0x00, 0x00, 0x31, .. Rect(1, 1, 7, 7), 0x00, 0xFF,
    ];

    // Sound resources: format 1 with a sampled-sound synth and a bufferCmd to offset 20; format 2 with one at 14.
    private static byte[] SoundFormat1(byte[] header) => [0, 1, 0, 1, 0, 5, 0, 0, 0, 0x80, 0, 1, 0x80, 0x51, 0, 0, 0, 0, 0, 20, .. header];

    private static byte[] SoundFormat2(byte[] header) => [0, 2, 0, 0, 0, 1, 0x80, 0x51, 0, 0, 0, 0, 0, 14, .. header];

    private const uint Rate11k = 0x2B7745D1; // 11127.27 Hz

    private static byte[] Standard(byte[] samples, uint loopStart = 0, uint loopEnd = 0, byte note = 60) =>
        [0, 0, 0, 0, .. BE32((uint)samples.Length), .. BE32(Rate11k), .. BE32(loopStart), .. BE32(loopEnd), 0, note, .. samples];

    private static byte[] Long(byte encode, int channels, int frames, int sampleSize, string format, short compressionId, byte[] samples)
    {
        var header = new byte[64];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)channels);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), Rate11k);
        header[20] = encode;
        header[21] = 60;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(22), (uint)frames);
        if (encode == 0xFF)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(48), (ushort)sampleSize);
        }
        else
        {
            FourCC.FromString(format).CopyTo(header.AsSpan(40));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(56), compressionId);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(62), (ushort)sampleSize);
        }
        return [.. header, .. samples];
    }

    // A sine wave: 8-bit offset binary, or 16-bit big-endian.
    private static byte[] Wave(int samples, int bits)
    {
        var data = new List<byte>();
        for (var i = 0; i < samples; i++)
        {
            var v = Math.Sin(i * Math.PI / 8);
            if (bits == 8) data.Add((byte)(128 + (int)Math.Round(100 * v)));
            else data.AddRange(BE16((short)Math.Round(20000 * v)));
        }
        return [.. data];
    }

    private static byte[] Swap16(byte[] data)
    {
        var swapped = (byte[])data.Clone();
        for (var i = 0; i + 1 < swapped.Length; i += 2) (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        return swapped;
    }

    private static byte[] Floats(int count) =>
        Enumerable.Range(0, count).SelectMany(i => BE32(BitConverter.SingleToUInt32Bits((float)Math.Sin(i * Math.PI / 4) / 2))).ToArray();

    private static byte[] Ima4Packet(ushort preamble) => [.. BE16(preamble), .. Bytes(32, 0x4B)];
}
