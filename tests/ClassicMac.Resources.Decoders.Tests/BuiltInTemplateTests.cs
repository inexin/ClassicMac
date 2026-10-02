using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.Resources.Decoders.Tests;

// ClassicMac's own templates for common types with no form of their own: each is usable, reads a fixture built from
// the layout its source documents, and writes it back unchanged.
public class BuiltInTemplateTests
{
    private static byte[] BE16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] BE32(long value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Type(string fourCC) => MacRoman.Encode(fourCC);

    private static byte[] Pascal(string text) => [(byte)text.Length, .. MacRoman.Encode(text)];

    private static byte[] Rgb(int r, int g, int b) => [.. BE16(r), .. BE16(g), .. BE16(b)];

    // One fixture per built-in type, with the value texts its first fields read as.
    public static TheoryData<string, byte[], string[]> Fixtures => new()
    {
        // Menu bar: two menus.
        { "MBAR", [.. BE16(2), .. BE16(128), .. BE16(129)], ["2", "128", "129"] },
        // Bundle: owner 'RLMZ' 0; two types (count − 1 = 1): ICN# with local 0 → 128 and local 1 → 129, FREF with local 0 → 128.
        { "BNDL", [.. Type("RLMZ"), .. BE16(0), .. BE16(1), .. Type("ICN#"), .. BE16(1), .. BE16(0), .. BE16(128), .. BE16(1), .. BE16(129),
            .. Type("FREF"), .. BE16(0), .. BE16(0), .. BE16(128)], ["RLMZ", "0", "1", "ICN#", "1", "0", "128", "1", "129", "FREF", "0", "0", "128"] },
        // File reference: APPL, local 0, no name.
        { "FREF", [.. Type("APPL"), .. BE16(0), 0], ["APPL", "0", ""] },
        // SIZE: 32-bit clean and high-level-event aware ($0080 | $0040 = $00C0... with canBackground $1000), 512 K preferred, 384 K minimum.
        { "SIZE", [.. BE16(0x10C0), .. BE32(512 * 1024), .. BE32(384 * 1024)],
            ["0", "0", "0", "1", "0", "0", "0", "0", "1", "1", "0", "0", "0", "0", "0", "0", "524288", "393216"] },
        // A template: two fields.
        { "TMPL", [.. Pascal("ID"), .. Type("DWRD"), .. Pascal("Name"), .. Type("PSTR")], ["ID", "DWRD", "Name", "PSTR"] },
        // Cursor: an arrow's 32 bytes, its mask, hot spot (1, 1).
        { "CURS", [.. Enumerable.Repeat((byte)0x80, 32), .. Enumerable.Repeat((byte)0xC0, 32), .. BE16(1), .. BE16(1)],
            [Convert.ToHexString(Enumerable.Repeat((byte)0x80, 32).ToArray()), Convert.ToHexString(Enumerable.Repeat((byte)0xC0, 32).ToArray()), "1", "1"] },
        // Pattern: 50% grey.
        { "PAT ", [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55], ["AA55AA55AA55AA55"] },
        // Pattern list: two patterns.
        { "PAT#", [.. BE16(2), .. new byte[8], .. Enumerable.Repeat((byte)0xFF, 8)], ["2", "0000000000000000", "FFFFFFFFFFFFFFFF"] },
        // Colour tables: seed, flags, entries − 1, then value and RGB per entry.
        { "clut", [.. BE32(0), .. BE16(0), .. BE16(1), .. BE16(0), .. Rgb(0xFFFF, 0xFFFF, 0xFFFF), .. BE16(1), .. Rgb(0, 0, 0)],
            ["0", "$0000", "1", "0", "$FFFF", "$FFFF", "$FFFF", "1", "$0000", "$0000", "$0000"] },
        { "wctb", [.. BE32(0), .. BE16(0), .. BE16(0), .. BE16(0), .. Rgb(0xDDDD, 0xDDDD, 0xDDDD)], ["0", "$0000", "0", "0", "$DDDD", "$DDDD", "$DDDD"] },
        { "actb", [.. BE32(0), .. BE16(0), .. BE16(0), .. BE16(0), .. Rgb(0xEEEE, 0xEEEE, 0xEEEE)], ["0", "$0000", "0", "0", "$EEEE", "$EEEE", "$EEEE"] },
        { "dctb", [.. BE32(0), .. BE16(0), .. BE16(0), .. BE16(0), .. Rgb(0xCCCC, 0xCCCC, 0xCCCC)], ["0", "$0000", "0", "0", "$CCCC", "$CCCC", "$CCCC"] },
        { "cctb", [.. BE32(0), .. BE16(0), .. BE16(0), .. BE16(1), .. Rgb(0, 0, 0)], ["0", "$0000", "0", "1", "$0000", "$0000", "$0000"] },
        // Menu colour table: one entry for menu 128, item 0, four colours, reserved.
        { "mctb", [.. BE16(1), .. BE16(128), .. BE16(0), .. Rgb(1, 2, 3), .. Rgb(4, 5, 6), .. Rgb(7, 8, 9), .. Rgb(10, 11, 12), .. BE16(0)],
            ["1", "128", "0", "$0001", "$0002", "$0003", "$0004", "$0005", "$0006", "$0007", "$0008", "$0009", "$000A", "$000B", "$000C", "0"] },
    };

    private static IEnumerable<string> Texts(IReadOnlyList<TemplateValue> values) => values.SelectMany(v => v switch
    {
        TemplateScalar s => [s.Text],
        TemplateList l => l.Items.SelectMany(Texts),
        _ => [],
    });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Each_built_in_template_reads_its_type_and_writes_it_back(string type, byte[] data, string[] texts)
    {
        var builtIn = BuiltInTemplates.For(FourCC.FromString(type));
        Assert.NotNull(builtIn);
        Assert.Empty(builtIn.Template.Problems);
        Assert.False(string.IsNullOrEmpty(builtIn.Source));
        var read = builtIn.Template.Read(data);
        Assert.Equal(0, read.MissingBytes);
        Assert.Equal(0, read.Extra.Length);
        Assert.Equal(texts, Texts(read.Values).Take(texts.Length));
        Assert.Equal(data, builtIn.Template.Write(read.Values));
    }

    [Fact]
    public void The_list_is_the_types_with_fixtures_and_others_have_none()
    {
        Assert.Equal(Fixtures.Select(f => f.Data.Item1).Order(), BuiltInTemplates.Types.Select(t => t.ToString()).Order());
        Assert.Null(BuiltInTemplates.For(FourCC.FromString("STR#")));         // it has a form of its own
        Assert.Null(BuiltInTemplates.For(FourCC.FromString("Xyzw")));
        Assert.Same(BuiltInTemplates.For(FourCC.FromString("MBAR")), BuiltInTemplates.For(FourCC.FromString("MBAR")));
    }

    [Fact]
    public void Templates_are_made_from_fields_as_from_a_TMPL()
    {
        var made = ResourceTemplate.FromFields([new TemplateField("ID", "DWRD"), new TemplateField("Name", "PSTR")]);
        var parsed = ResourceTemplate.Parse(TemplateTests.Tmpl(("ID", "DWRD"), ("Name", "PSTR")));
        Assert.Equal(parsed.Fields, made.Fields);
        Assert.Empty(made.Problems);
        Assert.NotEmpty(ResourceTemplate.FromFields([new TemplateField("X", "ABCD")]).Problems);
    }
}
