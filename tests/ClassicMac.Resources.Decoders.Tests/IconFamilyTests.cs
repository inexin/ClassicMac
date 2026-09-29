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
            if (count - n is 1 or 2) n = 127;                   // leave a run of at least 3
            yield return (byte)(n + 125);
            yield return value;
            count -= n;
        }
        if (count > 0)
        {
            yield return (byte)(count - 1);
            for (int i = 0; i < count; i++) yield return value;
        }
    }

    // A 32 x 32 1-bit icon list: a filled square (8..24) and its mask, a bigger square (4..28).
    private static byte[] IconList()
    {
        var list = new byte[256];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                if (x >= 8 && x < 24 && y >= 8 && y < 24) list[y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
                if (x >= 4 && x < 28 && y >= 4 && y < 28) list[128 + y * 4 + x / 8] |= (byte)(0x80 >> (x & 7));
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
}
