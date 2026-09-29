using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.Resources.Decoders.Tests;

// TMPL templates: checked, read and written as ResEdit 2.1.3's template editor does.
public class TemplateTests
{
    // A TMPL's data from (label, type) pairs.
    internal static byte[] Tmpl(params (string Label, string Type)[] fields) =>
        [.. fields.SelectMany(f => (byte[])[(byte)MacRoman.Encode(f.Label).Length, .. MacRoman.Encode(f.Label), .. MacRoman.Encode(f.Type)])];

    private static IEnumerable<(string, string)> Texts(IReadOnlyList<TemplateValue> values) =>
        values.OfType<TemplateScalar>().Select(v => (v.Node.Label, v.Text));

    [Fact]
    public void Every_scalar_type_reads_and_writes_back()
    {
        var template = ResourceTemplate.Parse(Tmpl(("b", "DBYT"), ("w", "DWRD"), ("l", "DLNG"), ("hb", "HBYT"), ("hw", "HWRD"), ("hl", "HLNG"),
            ("c", "CHAR"), ("t", "TNAM"), ("bool", "BOOL"), ("r", "RECT"), ("p", "PSTR"), ("e", "ESTR"), ("o", "OSTR"), ("ws", "WSTR"),
            ("ls", "LSTR"), ("cs", "CSTR"), ("ec", "ECST"), ("oc", "OCST"), ("fill", "FBYT"), ("a", "AWRD"), ("h3", "H003"), ("c4", "C004"),
            ("p3", "P003"), ("al", "ALNG"), ("rest", "HEXD")));
        Assert.Empty(template.Problems);
        byte[] data =
        [
            0xFF, 0x80, 0, 0, 0xFF, 0xFF, 0xFF, 0xFE, 0x12, 0xAB, 0xCD, 1, 2, 3, (byte)'x', .. "STR "u8, 1, 0, 0, 1, 0, 2, 0, 3, 0, 4,
            2, .. "hi"u8, 2, .. "ab"u8, 0, 1, .. "c"u8, 0, 0, 2, .. "wd"u8, 0, 0, 0, 1, .. "L"u8, .. "cs"u8, 0, .. "e"u8, 0, .. "o"u8, 0, 0,
            0, 0, 1, 2, 3, .. "abc"u8, 0, 2, .. "xy"u8, 0, 0, 0, 0, 0xDE, 0xAD,
        ];
        var read = template.Read(data);
        Assert.Equal((0, 0), (read.MissingBytes, read.Extra.Length));
        Assert.Equal([("b", "-1"), ("w", "-32768"), ("l", "16777215"), ("hb", "$FE"), ("hw", "$12AB"), ("hl", "$CD010203"), ("c", "x"),
            ("t", "STR "), ("bool", "1"), ("r", "1, 2, 3, 4"), ("p", "hi"), ("e", "ab")], Texts(read.Values).Take(12));
        Assert.Equal(data, template.Write(read.Values));
    }

    [Fact]
    public void Padding_follows_resedit()
    {
        // ESTR/OSTR pad the string's own length to even/odd; CSTR variants count the NUL; AWRD/ALNG from the data start.
        var template = ResourceTemplate.Parse(Tmpl(("e", "ESTR"), ("o", "OSTR"), ("b", "DBYT"), ("a", "ALNG"), ("ec", "ECST"), ("oc", "OCST"), ("w", "AWRD"), ("x", "DBYT")));
        var values = template.NewValues().Select(v => v is TemplateScalar s ? s with
        {
            Text = s.Node.Label switch { "e" => "ab", "o" => "a", "b" => "7", "ec" => "x", "oc" => "", "x" => "9", _ => s.Text },
        } : v).ToList();
        Assert.Equal(new byte[] { 2, 97, 98, 0, 1, 97, 0, 7, 120, 0, 0, 0, 9 }, template.Write(values));
        Assert.Equal(values.OfType<TemplateScalar>().Select(v => v.Text), template.Read(template.Write(values)).Values.OfType<TemplateScalar>().Select(v => v.Text));
    }

    [Fact]
    public void Lists_counted_to_the_end_and_zero_terminated()
    {
        var template = ResourceTemplate.Parse(Tmpl(("Count", "ZCNT"), ("*****", "LSTC"), ("id", "DWRD"), ("flags", "BBIT"), ("", "BBIT"), ("", "BBIT"),
            ("", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("*****", "LSTE"), ("*****", "LSTZ"), ("s", "PSTR"), ("*****", "LSTE"),
            ("*****", "LSTB"), ("n", "OCNT"), ("*****", "LSTC"), ("c", "CHAR"), ("*****", "LSTE"), ("*****", "LSTE")));
        Assert.Empty(template.Problems);
        byte[] data = [0, 1, 0, 5, 0x81, 0, 6, 0x40, 1, (byte)'a', 0, 0, 2, (byte)'x', (byte)'y', 0, 0];
        var read = template.Read(data);
        Assert.Equal(0, read.MissingBytes);
        var counted = (TemplateList)read.Values[1];
        Assert.Equal(2, counted.Items.Count);
        Assert.Equal(["6", "0", "1", "0", "0", "0", "0", "0", "0"], counted.Items[1].OfType<TemplateScalar>().Select(v => v.Text));
        Assert.Single(((TemplateList)read.Values[2]).Items);                                  // ended by the 0 byte
        var toEnd = (TemplateList)read.Values[3];
        Assert.Equal(2, toEnd.Items.Count);                                                     // "xy", then an empty count
        Assert.Equal(data, template.Write(read.Values));

        // Counts follow the items: one item added to the counted list, the terminated list emptied.
        var more = read.Values.ToList();
        more[1] = counted with { Items = [.. counted.Items, ResourceTemplate.NewItem(counted.Node)] };
        more[2] = new TemplateList(more[2].Node, []);
        var written = template.Write(more);
        Assert.Equal(new byte[] { 0, 2 }, written[..2]);
        Assert.Equal(3, ((TemplateList)template.Read(written).Values[1]).Items.Count);
        Assert.Equal(0, written[2 + 3 * 3]);                                                   // the LSTZ's 0 byte
    }

    [Fact]
    public void Short_data_reads_as_zeros_and_extra_data_is_kept()
    {
        var template = ResourceTemplate.Parse(Tmpl(("r", "RECT"), ("id", "DWRD"), ("pos", "HWRD")));
        var read = template.Read(new byte[] { 0, 1, 0, 2, 0, 3, 0, 4, 0, 5 });
        Assert.Equal(2, read.MissingBytes);
        Assert.Equal("$0000", ((TemplateScalar)read.Values[2]).Text);
        Assert.Equal(12, template.Write(read.Values).Length);                                  // zero-filled, as ResEdit offers

        var extra = template.Read(new byte[14]);
        Assert.Equal(2, extra.Extra.Length);
        Assert.Equal(14, template.Write(extra.Values, extra.Extra.Span).Length);
    }

    [Fact]
    public void Templates_resedit_refuses_are_reported()
    {
        static IReadOnlyList<string> Problems(params (string, string)[] fields) => ResourceTemplate.Parse(Tmpl(fields)).Problems;
        Assert.Contains("unknown field type 'UBYT'", Problems(("u", "UBYT")).Single());
        Assert.Contains("unknown field type 'H00a'", Problems(("h", "H00a")).Single());           // lowercase hex refused
        Assert.Contains("not the last", Problems(("h", "HEXD"), ("x", "DBYT")).Single());
        Assert.Contains("multiple of 8", Problems(("a", "BBIT"), ("b", "DBYT")).Single());
        Assert.Contains("not followed by LSTC", Problems(("n", "OCNT"), ("x", "DBYT")).Single());
        Assert.Contains("does not follow", Problems(("l", "LSTC"), ("e", "LSTE")).Single());
        Assert.Contains("no LSTE", Problems(("l", "LSTB")).Single());
        Assert.Contains("no list begin", Problems(("e", "LSTE")).Single());
        // ResEdit's quirk: two top-level LSTBs in a row are refused.
        Assert.Contains("follows another LSTB", Problems(("a", "LSTB"), ("x", "DBYT"), ("e", "LSTE"), ("b", "LSTB"), ("y", "DBYT"), ("e", "LSTE")).Single());
        Assert.Throws<InvalidOperationException>(() => ResourceTemplate.Parse(Tmpl(("u", "UBYT"))).Read([]));
        Assert.Throws<InvalidDataException>(() => ResourceTemplate.Parse([3, (byte)'a', (byte)'b']));
    }

    [Fact]
    public void Values_that_do_not_fit_are_refused()
    {
        var template = ResourceTemplate.Parse(Tmpl(("b", "DBYT"), ("c", "C004"), ("t", "TNAM")));
        var values = template.NewValues();
        TemplateScalar With(int i, string text) => (TemplateScalar)values[i] with { Text = text };
        Assert.Contains("does not fit", Assert.Throws<ArgumentException>(() => template.Write([With(0, "256"), values[1], values[2]])).Message);
        Assert.Equal(0xFF, template.Write([With(0, "$FF"), values[1], values[2]])[0]);
        Assert.Contains("longer than 3", Assert.Throws<ArgumentException>(() => template.Write([values[0], With(1, "abcd"), values[2]])).Message);
        Assert.Equal("ab  "u8.ToArray(), template.Write([values[0], values[1], With(2, "ab")])[5..]);
        Assert.Contains("not a number", Assert.Throws<ArgumentException>(() => template.Write([With(0, "x"), values[1], values[2]])).Message);
    }

    [Fact]
    public void Templates_are_found_by_name()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("TMPL"), 128, Tmpl(("x", "DWRD"))) { Name = MacString.FromMacRoman("abcd") });
        Assert.NotNull(ResourceTemplate.Find(fork, FourCC.FromString("abcd")));
        Assert.Null(ResourceTemplate.Find(fork, FourCC.FromString("ABCD")));
    }
}
