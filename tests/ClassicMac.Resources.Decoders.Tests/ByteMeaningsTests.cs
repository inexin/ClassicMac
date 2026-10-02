using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.Resources.Decoders.Tests;

// What each byte of a resource means (the hex inspector's "In this resource", design/boards/hex.md E8): 'STR ', 'STR#'
// and the types a template describes; the field's range comes with it, so the field can be highlighted.
public class ByteMeaningsTests
{
    private static readonly FourCC Str = FourCC.FromString("STR ");
    private static readonly FourCC StrList = FourCC.FromString("STR#");

    private static byte[] Pascal(string text) => [(byte)text.Length, .. MacRoman.Encode(text)];

    [Fact]
    public void A_string_s_length_byte_and_characters()
    {
        byte[] data = Pascal("Untitled");
        Assert.Equal(new ByteMeaning("Length of the string", 0, 1, "8"), ByteMeanings.MeaningAt(Str, data, 0, null));
        Assert.Equal(new ByteMeaning("Character 1 of the string, “Untitled”", 1, 8, "U"), ByteMeanings.MeaningAt(Str, data, 1, null));
        Assert.Equal(new ByteMeaning("Character 8 of the string, “Untitled”", 1, 8, "d"), ByteMeanings.MeaningAt(Str, data, 8, null));
        Assert.Equal(new ByteMeaning("After the string", 9, 2, null), ByteMeanings.MeaningAt(Str, [.. data, 0, 0], 9, null));
        Assert.Null(ByteMeanings.MeaningAt(Str, data, 9, null));                       // past the end
        Assert.Null(ByteMeanings.MeaningAt(Str, data, -1, null));
        Assert.Equal(new ByteMeaning("Character 1 of the string, “ab”", 1, 2, "a"), ByteMeanings.MeaningAt(Str, [5, (byte)'a', (byte)'b'], 1, null)); // cut short
        Assert.Equal(new ByteMeaning("Length of the string", 0, 1, "0"), ByteMeanings.MeaningAt(Str, [0], 0, null));
    }

    [Fact]
    public void A_string_list_s_count_lengths_and_characters()
    {
        byte[] data = [0, 2, .. Pascal("Untitled"), .. Pascal("Go")];
        Assert.Equal(new ByteMeaning("Number of strings", 0, 2, "2"), ByteMeanings.MeaningAt(StrList, data, 1, null));
        Assert.Equal(new ByteMeaning("Length of string 1", 2, 1, "8"), ByteMeanings.MeaningAt(StrList, data, 2, null));
        Assert.Equal(new ByteMeaning("Character 1 of string 1, “Untitled”", 3, 8, "U"), ByteMeanings.MeaningAt(StrList, data, 3, null));
        Assert.Equal(new ByteMeaning("Length of string 2", 11, 1, "2"), ByteMeanings.MeaningAt(StrList, data, 11, null));
        Assert.Equal(new ByteMeaning("Character 2 of string 2, “Go”", 12, 2, "o"), ByteMeanings.MeaningAt(StrList, data, 13, null));
        Assert.Equal(new ByteMeaning("After the strings", 14, 1, null), ByteMeanings.MeaningAt(StrList, [.. data, 9], 14, null));
        Assert.Null(ByteMeanings.MeaningAt(StrList, data, 14, null));
        Assert.Equal(new ByteMeaning("Number of strings", 0, 2, "0"), ByteMeanings.MeaningAt(StrList, [0], 0, null)); // cut short
    }

    private static ResourceTemplate Template(params (string Label, string Type)[] fields) => ResourceTemplate.Parse(TemplateTests.Tmpl(fields));

    [Fact]
    public void Template_fields_name_their_bytes_and_list_items()
    {
        var template = Template(("Version", "DWRD"), ("Count", "OCNT"), ("*****", "LSTC"), ("ID", "DWRD"), ("Name", "PSTR"), ("*****", "LSTE"),
            ("Flags", "HBYT"));
        byte[] data = [0, 1, 0, 2, 0, 128, .. Pascal("Ab"), 0, 129, .. Pascal(""), 0xFF];
        var type = FourCC.FromString("Xmpl");
        Assert.Equal(new ByteMeaning("Version", 0, 2, "1"), ByteMeanings.MeaningAt(type, data, 1, template));
        Assert.Equal(new ByteMeaning("Count", 2, 2, "2"), ByteMeanings.MeaningAt(type, data, 2, template));
        Assert.Equal(new ByteMeaning("ID of item 1", 4, 2, "128"), ByteMeanings.MeaningAt(type, data, 5, template));
        Assert.Equal(new ByteMeaning("Length of Name of item 1", 6, 1, "2"), ByteMeanings.MeaningAt(type, data, 6, template));
        Assert.Equal(new ByteMeaning("Character 2 of Name of item 1, “Ab”", 7, 2, "b"), ByteMeanings.MeaningAt(type, data, 8, template));
        Assert.Equal(new ByteMeaning("ID of item 2", 9, 2, "129"), ByteMeanings.MeaningAt(type, data, 9, template));
        Assert.Equal(new ByteMeaning("Length of Name of item 2", 11, 1, "0"), ByteMeanings.MeaningAt(type, data, 11, template));
        Assert.Equal(new ByteMeaning("Flags", 12, 1, "$FF"), ByteMeanings.MeaningAt(type, data, 12, template));
        Assert.Null(ByteMeanings.MeaningAt(type, data, 13, template));
        Assert.Equal(new ByteMeaning("After the template's fields", 13, 2, null), ByteMeanings.MeaningAt(type, [.. data, 1, 2], 14, template));
    }

    [Fact]
    public void Nested_lists_number_their_items_from_the_outside_in()
    {
        var template = Template(("Groups", "OCNT"), ("*****", "LSTC"), ("Members", "OCNT"), ("*****", "LSTC"), ("Value", "HWRD"), ("*****", "LSTE"),
            ("*****", "LSTE"));
        byte[] data = [0, 2, 0, 1, 0xAA, 0xBB, 0, 2, 0, 1, 0, 2];
        var type = FourCC.FromString("Nest");
        Assert.Equal(new ByteMeaning("Members of item 1", 2, 2, "1"), ByteMeanings.MeaningAt(type, data, 3, template));
        Assert.Equal(new ByteMeaning("Value of item 1.1", 4, 2, "$AABB"), ByteMeanings.MeaningAt(type, data, 4, template));
        Assert.Equal(new ByteMeaning("Value of item 2.2", 10, 2, "$0002"), ByteMeanings.MeaningAt(type, data, 11, template));
    }

    [Fact]
    public void Strings_of_every_template_kind()
    {
        var type = FourCC.FromString("Strs");
        // An even-padded Pascal string, a C string, a word-length string, a 4-byte fixed Pascal string (P003), a
        // fixed C string (C004) and the rest as hex.
        var template = Template(("E", "ESTR"), ("C", "CSTR"), ("W", "WSTR"), ("P", "P003"), ("F", "C004"), ("Rest", "HEXD"));
        byte[] data = [2, (byte)'h', (byte)'i', 0, (byte)'x', 0, 0, 1, (byte)'w', 1, (byte)'p', 0, 0, (byte)'a', (byte)'b', 0, 0, 0xEE, 0xFF];
        Assert.Equal(new ByteMeaning("Length of E", 0, 1, "2"), ByteMeanings.MeaningAt(type, data, 0, template));
        Assert.Equal(new ByteMeaning("Character 2 of E, “hi”", 1, 2, "i"), ByteMeanings.MeaningAt(type, data, 2, template));
        Assert.Equal(new ByteMeaning("Padding of E", 3, 1, null), ByteMeanings.MeaningAt(type, data, 3, template));
        Assert.Equal(new ByteMeaning("Character 1 of C, “x”", 4, 1, "x"), ByteMeanings.MeaningAt(type, data, 4, template));
        Assert.Equal(new ByteMeaning("End of C", 5, 1, null), ByteMeanings.MeaningAt(type, data, 5, template));
        Assert.Equal(new ByteMeaning("Length of W", 6, 2, "1"), ByteMeanings.MeaningAt(type, data, 7, template));
        Assert.Equal(new ByteMeaning("Character 1 of W, “w”", 8, 1, "w"), ByteMeanings.MeaningAt(type, data, 8, template));
        Assert.Equal(new ByteMeaning("Length of P", 9, 1, "1"), ByteMeanings.MeaningAt(type, data, 9, template));
        Assert.Equal(new ByteMeaning("Character 1 of P, “p”", 10, 1, "p"), ByteMeanings.MeaningAt(type, data, 10, template));
        Assert.Equal(new ByteMeaning("Padding of P", 11, 2, null), ByteMeanings.MeaningAt(type, data, 12, template));
        Assert.Equal(new ByteMeaning("Character 2 of F, “ab”", 13, 2, "b"), ByteMeanings.MeaningAt(type, data, 14, template));
        Assert.Equal(new ByteMeaning("Padding of F", 15, 2, null), ByteMeanings.MeaningAt(type, data, 16, template));
        Assert.Equal(new ByteMeaning("Byte 2 of Rest", 17, 2, "$FF"), ByteMeanings.MeaningAt(type, data, 18, template));
    }

    [Fact]
    public void Bits_fillers_alignment_and_zero_terminated_lists()
    {
        var type = FourCC.FromString("Bits");
        var template = Template(("High", "BBIT"), ("Mid", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("", "BBIT"), ("", "BBIT"),
            ("Low", "BBIT"), ("", "AWRD"), ("", "FBYT"), ("Items", "LSTZ"), ("Code", "HBYT"), ("*****", "LSTE"));
        byte[] data = [0x81, 0, 0, 7, 9, 0];
        Assert.Equal(new ByteMeaning("Bits: High, Mid, Low", 0, 1, "$81"), ByteMeanings.MeaningAt(type, data, 0, template));
        Assert.Equal(new ByteMeaning("Alignment", 1, 1, null), ByteMeanings.MeaningAt(type, data, 1, template));
        Assert.Equal(new ByteMeaning("Filler", 2, 1, null), ByteMeanings.MeaningAt(type, data, 2, template));
        Assert.Equal(new ByteMeaning("Code of item 2", 4, 1, "$09"), ByteMeanings.MeaningAt(type, data, 4, template));
        Assert.Equal(new ByteMeaning("End of Items", 5, 1, null), ByteMeanings.MeaningAt(type, data, 5, template));
    }

    [Fact]
    public void Other_types_and_unusable_templates_have_no_meanings()
    {
        Assert.Null(ByteMeanings.MeaningAt(FourCC.FromString("ICN#"), new byte[256], 0, null));
        var broken = Template(("Rest", "HEXD"), ("After", "DWRD"));                    // HEXD not last: a problem
        Assert.NotEmpty(broken.Problems);
        Assert.Null(ByteMeanings.MeaningAt(FourCC.FromString("Brkn"), new byte[4], 0, broken));
        // 'STR ' and 'STR#' are read as strings even when a template is given.
        Assert.Equal("Length of the string", ByteMeanings.MeaningAt(Str, Pascal("a"), 0, Template(("X", "HWRD")))!.Text);
    }

    [Fact]
    public void Map_gives_each_field_s_place_in_the_data()
    {
        var template = Template(("ID", "DWRD"), ("", "FBYT"), ("", "AWRD"), ("Items", "LSTZ"), ("Code", "HBYT"), ("*****", "LSTE"));
        var spans = template.Map([0, 5, 9, 0, 7, 0]);
        Assert.Equal(
            [("ID", 0, 2, "", "5", false), ("", 2, 1, "", "", false), ("", 3, 1, "", "", false), ("Code", 4, 1, "1", "$07", false),
             ("Items", 5, 1, "", "", true)],
            spans.Select(s => (s.Node.Label, s.Offset, s.Length, string.Join(".", s.Items), s.Text, s.IsListEnd)));
        Assert.Throws<InvalidOperationException>(() => Template(("Rest", "HEXD"), ("After", "DWRD")).Map([1]));
    }
}
