using System.Runtime.InteropServices;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics.Fonts;
using Xunit;

namespace ClassicMac.Graphics.Tests.Fonts;

// Making a Mac TrueType font loadable (docs/formats/resources/outline-fonts.md §3): what Windows and other modern loaders
// require and Mac fonts often lack is added (a Windows Unicode cmap, Windows names, OS/2, post); the rest is kept.
public sealed class LoadableFontTests
{
    private sealed record Table(string Tag, uint Checksum, byte[] Data);

    private static List<Table> Read(byte[] font)
    {
        var r = new BigEndianReader(font);
        int count = r.ReadUInt16At(4);
        var tables = new List<Table>();
        for (var i = 0; i < count; i++)
        {
            var at = 12 + 16 * i;
            var tag = Encoding.ASCII.GetString(font, at, 4);
            tables.Add(new Table(tag, r.ReadUInt32At(at + 4), font.AsSpan((int)r.ReadUInt32At(at + 8), (int)r.ReadUInt32At(at + 12)).ToArray()));
        }

        return tables;
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : 0u);
            }

            sum += word;
        }

        return sum;
    }

    // The (platform, encoding) subtables of a cmap, and a format 4 subtable's character to glyph mapping.
    private static Dictionary<(int, int), int> Subtables(byte[] cmap)
    {
        var r = new BigEndianReader(cmap);
        return Enumerable.Range(0, r.ReadUInt16At(2)).ToDictionary(i => ((int)r.ReadUInt16At(4 + 8 * i), (int)r.ReadUInt16At(6 + 8 * i)), i => (int)r.ReadUInt32At(8 + 8 * i));
    }

    private static int GlyphOf(byte[] cmap, int offset, int c)
    {
        var r = new BigEndianReader(cmap.AsMemory(offset));
        Assert.Equal(4, r.ReadUInt16At(0));
        int segments = r.ReadUInt16At(6) / 2;
        for (var s = 0; s < segments; s++)
        {
            int end = r.ReadUInt16At(14 + 2 * s), start = r.ReadUInt16At(16 + 2 * segments + 2 * s);
            short delta = r.ReadInt16At(16 + 4 * segments + 2 * s);
            if (c >= start && c <= end)
            {
                Assert.Equal(0, r.ReadUInt16At(16 + 6 * segments + 2 * s));               // no range offset
                return (ushort)(c + delta);
            }
        }

        return 0;
    }

    private static Dictionary<(int Platform, int Name), string> Names(byte[] name)
    {
        var r = new BigEndianReader(name);
        int count = r.ReadUInt16At(2), storage = r.ReadUInt16At(4);
        var names = new Dictionary<(int, int), string>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 12 * i;
            int platform = r.ReadUInt16At(at), id = r.ReadUInt16At(at + 6), length = r.ReadUInt16At(at + 8), offset = r.ReadUInt16At(at + 10);
            var bytes = name.AsSpan(storage + offset, length);
            names[(platform, id)] = platform == 1 ? MacRoman.Decode(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
        }

        return names;
    }

    [Fact]
    public void A_Mac_TrueType_font_gets_what_Windows_needs()
    {
        var original = TrueTypeBuilder.Mac().Build();
        var added = new List<string>();

        var font = LoadableFont.Make(original, added);

        Assert.Equal(["cmap (3,1)", "name (Windows)", "OS/2", "post"], added);
        var tables = Read(font);
        Assert.Equal(tables.Select(t => t.Tag).Order(StringComparer.Ordinal), tables.Select(t => t.Tag));
        Assert.Contains("OS/2", tables.Select(t => t.Tag));
        Assert.Contains("post", tables.Select(t => t.Tag));
        var cmap = tables.Single(t => t.Tag == "cmap").Data;
        var windows = Subtables(cmap)[(3, 1)];
        Assert.Equal(1, GlyphOf(cmap, windows, 'A'));
        Assert.Equal(2, GlyphOf(cmap, windows, 'é'));                                        // Mac OS Roman $8E
        Assert.Equal(0, GlyphOf(cmap, windows, 'B'));
        var mac = Subtables(cmap)[(1, 0)];                                                   // the Mac one kept
        Assert.Equal((0, 1), (cmap[mac] << 8 | cmap[mac + 1], (int)cmap[mac + 6 + 0x41]));
        var names = Names(tables.Single(t => t.Tag == "name").Data);
        Assert.Equal(("Test Sans", "Regular", "Test Sans", "TestSans"), (names[(3, 1)], names[(3, 2)], names[(3, 4)], names[(3, 6)]));
        Assert.Equal(("Test Sans: Version 1.000", "Version 1.000"), (names[(3, 3)], names[(3, 5)]));   // GDI needs 3
        Assert.Equal("Test Sans", names[(1, 1)]);
        var os2 = new BigEndianReader(tables.Single(t => t.Tag == "OS/2").Data);
        Assert.Equal((1, 567, 400, 5, (ushort)0x40), (os2.ReadUInt16At(0), (int)os2.ReadInt16At(2), (int)os2.ReadUInt16At(4), (int)os2.ReadUInt16At(6), os2.ReadUInt16At(62)));
        Assert.Equal(('A', 'é'), ((char)os2.ReadUInt16At(64), (char)os2.ReadUInt16At(66)));      // first and last character
        Assert.Equal((800, 200), ((int)os2.ReadUInt16At(74), (int)os2.ReadUInt16At(76)));       // usWinAscent, usWinDescent

        // Checksums: each table's, and the whole file's through head.checkSumAdjustment.
        Assert.All(tables.Where(t => t.Tag != "head"), t => Assert.Equal(Sum(t.Data), t.Checksum));
        Assert.Equal(0xB1B0AFBAu, Sum(font));
        Assert.Equal(tables.Single(t => t.Tag == "glyf").Data, Read(original).Single(t => t.Tag == "glyf").Data);
    }

    // A font that has everything is returned as it is; so is one that is not TrueType.
    [Fact]
    public void A_font_with_what_Windows_needs_or_not_TrueType_is_kept()
    {
        var once = LoadableFont.Make(TrueTypeBuilder.Mac().Build());
        var added = new List<string>();

        Assert.Equal(once, LoadableFont.Make(once, added));
        Assert.Empty(added);
        byte[] type1 = [.. "typ1"u8, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(type1, LoadableFont.Make(type1));
    }

    // Names Windows requires that the Mac font lacks are made: the style "Regular", the full name from the family and
    // style, the PostScript name from the full name without spaces, the unique name and the version from head.
    [Fact]
    public void Missing_names_are_made_from_the_family()
    {
        var builder = TrueTypeBuilder.Mac();
        builder.Tables["name"] = TrueTypeBuilder.MacNames(("Test Serif", 1), ("Bold", 2));

        var font = LoadableFont.Make(builder.Build());

        var names = Names(Read(font).Single(t => t.Tag == "name").Data);
        Assert.Equal(("Test Serif", "Bold", "Test Serif Bold", "TestSerif-Bold"), (names[(3, 1)], names[(3, 2)], names[(3, 4)], names[(3, 6)]));
    }

    // Apple's own fonts often have some Windows names but not the unique name (3) GDI needs, nor the PostScript name
    // (6), and the 'true' version Windows does not take: the missing names are added beside the others, and the version
    // made $00010000.
    [Fact]
    public void Missing_Windows_names_and_the_true_version_are_made_right()
    {
        var builder = TrueTypeBuilder.Mac();
        var names = new BigEndianWriter();
        byte[] mac = MacRoman.Encode("Test Sans"), windows = Encoding.BigEndianUnicode.GetBytes("Test Sans");
        names.WriteUInt16(0);
        names.WriteUInt16(2);
        names.WriteUInt16(6 + 24);
        foreach (var (platform, encoding, language, bytes, at) in new[] { (1, 0, 0, mac, 0), (3, 1, 0x409, windows, mac.Length) })
        {
            names.WriteUInt16((ushort)platform);
            names.WriteUInt16((ushort)encoding);
            names.WriteUInt16((ushort)language);
            names.WriteUInt16(1);
            names.WriteUInt16((ushort)bytes.Length);
            names.WriteUInt16((ushort)at);
        }

        names.WriteBytes(mac);
        names.WriteBytes(windows);
        builder.Tables["name"] = names.ToArray();
        builder.Tables["OS/2"] = new byte[86];
        builder.Tables["post"] = new byte[32];
        var font = builder.Build();
        new BigEndianWriter(font).WriteFourCCAt(0, FourCC.FromString("true"));
        var added = new List<string>();

        var made = LoadableFont.Make(font, added);

        Assert.Equal(["version", "cmap (3,1)", "name (Windows)"], added);
        Assert.Equal(0x00010000u, new BigEndianReader(made).ReadUInt32At(0));
        var windowsNames = Names(Read(made).Single(t => t.Tag == "name").Data);
        Assert.Equal(("Test Sans", "Regular", "Test Sans: Version 1.000", "Test Sans", "TestSans"),
            (windowsNames[(3, 1)], windowsNames[(3, 2)], windowsNames[(3, 3)], windowsNames[(3, 4)], windowsNames[(3, 6)]));
    }

    // Windows' own loader (GDI) refuses the Mac font and takes it made loadable.
    [Fact]
    public void Windows_loads_the_font_made_loadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("GDI is Windows'.");
        }

        Assert.Equal(0, Gdi.Load(TrueTypeBuilder.Mac().Build()));
        Assert.Equal(1, Gdi.Load(LoadableFont.Make(TrueTypeBuilder.Mac().Build())));
    }

    private static class Gdi
    {
        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint AddFontMemResourceEx(byte[] font, uint size, nint reserved, ref uint fonts);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontMemResourceEx(nint handle);

        // How many fonts GDI installs from the file (0 when it refuses it).
        public static int Load(byte[] font)
        {
            uint fonts = 0;
            var handle = AddFontMemResourceEx(font, (uint)font.Length, 0, ref fonts);
            if (handle == 0)
            {
                return 0;
            }

            RemoveFontMemResourceEx(handle);
            return (int)fonts;
        }
    }
}
