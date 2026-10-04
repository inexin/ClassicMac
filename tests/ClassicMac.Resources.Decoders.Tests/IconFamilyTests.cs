using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Tests;

// 'icns' families and their members as Mac OS 9's Icon Services reads them.
public class IconFamilyTests
{
    private static byte[] Element(string type, byte[] data)
    {
        var e = new byte[8 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(e, 0);
        BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(4), (uint)e.Length);
        data.CopyTo(e, 8);
        return e;
    }

    private static byte[] Icns(params byte[][] elements)
    {
        var body = elements.SelectMany(e => e).ToArray();
        var data = new byte[8 + body.Length];
        Encoding.ASCII.GetBytes("icns").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)data.Length);
        body.CopyTo(data, 8);
        return data;
    }

    // A plane of `count` bytes of one value as maximal runs (130 each), then the rest.
    private static IEnumerable<byte> Run(byte value, int count)
    {
        while (count >= 3)
        {
            int n = Math.Min(count, 130);
            if (count - n is 1 or 2)
            {
                n = 127;                   // leave a run of at least 3
            }

            yield return (byte)(n + 125);
            yield return value;
            count -= n;
        }
        if (count > 0)
        {
            yield return (byte)(count - 1);
            for (int i = 0; i < count; i++)
            {
                yield return value;
            }
        }
    }

    // A 32 x 32 1-bit icon list: a filled square (8..24) and its mask, a bigger square (4..28).
    private static byte[] IconList()
    {
        var list = new byte[256];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                if (x >= 8 && x < 24 && y >= 8 && y < 24)
                {
                    list[y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
                }

                if (x >= 4 && x < 28 && y >= 4 && y < 28)
                {
                    list[128 + y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return list;
    }

    private static byte[] Il32Compressed()
    {
        var planes = new List<byte>();
        planes.AddRange(new byte[] { 2, 1, 2, 3 });              // red: three literals, then 1021 of $10
        planes.AddRange(Run(0x10, 1021));
        planes.AddRange(Run(0x20, 1024));                        // green
        planes.AddRange(Run(0x30, 1024));                        // blue
        return planes.ToArray();
    }

    [Fact]
    public void Families_are_read_as_Mac_OS_9_reads_them()
    {
        var mask = Enumerable.Range(0, 1024).Select(i => (byte)(i % 32 < 16 ? 0x80 : 0)).ToArray();
        var diagnostics = new List<Diagnostic>();
        var family = IconFamily.ReadIcns(Icns(
            Element("TOC ", new byte[8]),
            Element("ICN#", IconList()),
            Element("il32", Il32Compressed()),
            Element("l8mk", mask),
            Element("ics#", new byte[40])), diagnostics);           // wrong size: dropped

        Assert.Equal(["ICN#", "il32", "l8mk"], family.Members.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["icon.family-ignored", "icon.member-size"], diagnostics.Select(d => d.Code).Order());
        var il32 = family.Members["il32"];
        Assert.Equal(new byte[] { 0, 1, 0x20, 0x30, 0, 2, 0x20, 0x30, 0, 3, 0x20, 0x30, 0, 0x10, 0x20, 0x30 }, il32[..16]);
        Assert.Equal(new byte[] { 0, 0x10, 0x20, 0x30 }, il32[^4..]);

        // The 8-bit mask wins over ICN#'s, as alpha.
        Assert.Equal("l8mk", family.MaskFor(32));
        var image = family.Masked("il32")!;
        Assert.Equal(new RgbaColor(0x10, 0x20, 0x30, 0x80), image[5, 5]);
        Assert.Equal(0, image[20, 5].A);
        // The mini size never takes an 8-bit mask: ICN#'s mask, scaled to 16 x 12.
        Assert.Equal("ICN#", family.MaskFor(12));
    }

    [Fact]
    public void Runs_and_literals_stop_at_the_planes_end()
    {
        // A run of 130 into a 4-pixel plane fills it and nothing carries over; the literal's extra byte is skipped.
        var data = new byte[] { 0xFF, 7, 4, 1, 2, 3, 4, 9, 0xFF, 5 };
        var argb = IconFamily.Decompress(data, 4);
        Assert.Equal(new byte[] { 0, 7, 1, 5, 0, 7, 2, 5, 0, 7, 3, 5, 0, 7, 4, 5 }, argb);
    }

    [Fact]
    public void Bad_families_are_empty_or_fail()
    {
        var good = Icns(Element("ICN#", IconList()));
        var longer = good.Concat(new byte[4]).ToArray();
        Assert.Empty(IconFamily.ReadIcns(longer).Members);                         // length != data: empty
        var it32 = Icns(Element("it32", new byte[] { 0, 0, 0, 1, 0xFF, 0 }));
        Assert.Throws<InvalidDataException>(() => IconFamily.ReadIcns(it32));      // format word not 0
        var zero = Icns(Element("ICN#", IconList()));
        BinaryPrimitives.WriteUInt32BigEndian(zero.AsSpan(12), 0);
        Assert.Throws<InvalidDataException>(() => IconFamily.ReadIcns(zero));      // element size 0
    }

    [Fact]
    public void Without_an_icns_the_classic_resources_make_the_family()
    {
        var resources = new Dictionary<(string, short), byte[]> { [("ICN#", 128)] = IconList(), [("icl8", 128)] = new byte[1024] };
        ReadOnlyMemory<byte>? Lookup(FourCC type, short id) => resources.TryGetValue((type.ToString(), id), out var d) ? d : null;

        var family = IconFamily.FromResources(Lookup, 128);

        Assert.Equal(["ICN#", "icl8"], family.Members.Keys.Order(StringComparer.Ordinal));
        var icl8 = family.Masked("icl8")!;
        Assert.Equal(255, icl8[4, 4].A);
        Assert.Equal(0, icl8[3, 4].A);
    }

    // Writing (icon-families.md §3): MakeIconFamilyHandle and AppendCompressedData, as Mac OS 9.0's Icon Services do them.
    private static byte[] Argb(int pixels, Func<int, (byte R, byte G, byte B)> colour)
    {
        var argb = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            (argb[4 * i + 1], argb[4 * i + 2], argb[4 * i + 3]) = colour(i);
        }

        return argb;
    }

    [Fact]
    public void A_32_bit_member_is_written_compressed_plane_by_plane()
    {
        var family = new IconFamily();
        family.SetMember("il32", Argb(1024, _ => (0xFF, 0, 0)));

        var icns = family.ToIcns();

        // Each plane: seven runs of 130 and one of 114 (control $EF).
        byte[] Plane(byte value) => [.. Enumerable.Range(0, 7).SelectMany(_ => new byte[] { 0xFF, value }), 0xEF, value];
        Assert.Equal([.. "icns"u8, 0, 0, 0, 0x40, .. "il32"u8, 0, 0, 0, 0x38, .. Plane(0xFF), .. Plane(0), .. Plane(0)], icns);
    }

    [Fact]
    public void Runs_start_at_three_and_literals_stop_at_128()
    {
        // Two equal bytes stay literal; three make a run; 200 distinct bytes split into 128 and 72 literals.
        byte[] red = [5, 5, 7, 7, 7, .. Enumerable.Range(0, 200).Select(i => (byte)(i % 2 == 0 ? i / 2 : 255 - i / 2)), .. new byte[51]];
        var compressed = IconFamily.Compress(Argb(256, i => (red[i], 9, 9)), 256);

        Assert.Equal([1, 5, 5, 0x80, 7, 127], compressed[..6]);
        Assert.Equal(71, compressed[5 + 1 + 128]);
        Assert.Equal(Argb(256, i => (red[i], 9, 9)), IconFamily.Decompress(compressed, 256));
    }

    [Fact]
    public void Members_are_written_in_table_order_and_read_back()
    {
        var random = new Random(7);
        var family = new IconFamily();
        var it32 = Argb(128 * 128, _ => ((byte)random.Next(4), (byte)random.Next(256), 0));
        family.SetMember("it32", it32);
        family.SetMember("ICN#", new byte[256]);
        family.SetMember("s8mk", Enumerable.Repeat((byte)0xFF, 256).ToArray());
        family.SetMember("ics#", new byte[64]);

        var icns = family.ToIcns();

        var types = new List<string>();
        for (var at = 8; at < icns.Length; at += BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(at + 4)))
        {
            types.Add(Encoding.ASCII.GetString(icns, at, 4));
        }

        Assert.Equal(["ics#", "s8mk", "ICN#", "it32"], types);
        var it32At = icns.AsSpan().IndexOf("it32"u8);
        Assert.Equal([0, 0, 0, 0], icns[(it32At + 8)..(it32At + 12)]);                   // it32's compression format
        var read = IconFamily.ReadIcns(icns);
        Assert.Equal(it32, read.Members["it32"]);
        Assert.Equal(family.Members.Keys.Order(StringComparer.Ordinal), read.Members.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Setting_a_member_takes_raw_or_compressed_32_bit_data_and_exact_sizes()
    {
        var family = new IconFamily();
        var argb = Argb(256, i => ((byte)i, 0, 0));
        family.SetMember("is32", IconFamily.Compress(argb, 256));
        Assert.Equal(argb, family.Members["is32"]);

        Assert.Throws<ArgumentException>(() => family.SetMember("ICN#", new byte[128]));
        Assert.Throws<ArgumentException>(() => family.SetMember("icns", new byte[8]));
    }
}
