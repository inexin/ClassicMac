using System.Globalization;
using System.Runtime.CompilerServices;
using ClassicMac.Core;

namespace ClassicMac.Core.Tests;

// The Mac OS text encodings (docs/formats/codecs/text-encodings.md): Mac OS Roman, the other single-byte scripts and the
// multi-byte ones, decoded and encoded, named, and checked against Apple's mapping tables when they are at hand.
public sealed class MacEncodingsTests
{
    [Fact]
    public void Each_encoding_has_its_Text_Encoding_Converter_number_and_name()
    {
        Assert.Equal((0, "macintosh"), ((int)MacTextEncoding.Roman, MacEncodings.Name(MacTextEncoding.Roman)));
        Assert.Equal((1, "x-mac-japanese"), ((int)MacTextEncoding.Japanese, MacEncodings.Name(MacTextEncoding.Japanese)));
        Assert.Equal((25, "x-mac-chinesesimp"), ((int)MacTextEncoding.ChineseSimplified, MacEncodings.Name(MacTextEncoding.ChineseSimplified)));
        Assert.Equal(152, (int)MacTextEncoding.Ukrainian);
        Assert.All(Enum.GetValues<MacTextEncoding>(), e => Assert.NotEmpty(MacEncodings.Name(e)));
    }

    [Theory]
    [InlineData("japanese", MacTextEncoding.Japanese)]
    [InlineData("Japanese", MacTextEncoding.Japanese)]
    [InlineData("x-mac-japanese", MacTextEncoding.Japanese)]
    [InlineData("chinese-traditional", MacTextEncoding.ChineseTraditional)]
    [InlineData("macintosh", MacTextEncoding.Roman)]
    [InlineData("central-european", MacTextEncoding.CentralEuropean)]
    public void Names_are_parsed_as_the_enum_or_the_IANA_name(string name, MacTextEncoding expected)
    {
        Assert.True(MacEncodings.TryParse(name, out var encoding));
        Assert.Equal(expected, encoding);
        Assert.False(MacEncodings.TryParse("ebcdic", out _));
    }

    [Fact]
    public void Roman_is_Mac_OS_Roman()
    {
        Assert.Equal("é•", MacEncodings.Decode([0x8E, 0xA5], MacTextEncoding.Roman));
        Assert.Equal([0x8E, 0xA5], MacEncodings.Encode("é•", MacTextEncoding.Roman));
    }

    [Theory]
    [InlineData(MacTextEncoding.Japanese, new byte[] { 0x82, 0xA0, 0x93, 0xFA, 0x96, 0x7B, 0x41 }, "あ日本A")]
    [InlineData(MacTextEncoding.ChineseTraditional, new byte[] { 0xA4, 0xA4, 0xA4, 0xE5 }, "中文")]
    [InlineData(MacTextEncoding.ChineseSimplified, new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, "中文")]
    [InlineData(MacTextEncoding.Korean, new byte[] { 0xC7, 0xD1, 0xB1, 0xDB }, "한글")]
    [InlineData(MacTextEncoding.Cyrillic, new byte[] { 0x8F, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 }, "Привет")]
    [InlineData(MacTextEncoding.Greek, new byte[] { 0xE1, 0xE2 }, "αβ")]
    public void Scripts_decode_and_encode_back(MacTextEncoding encoding, byte[] bytes, string text)
    {
        Assert.Equal(text, MacEncodings.Decode(bytes, encoding));
        Assert.Equal(bytes, MacEncodings.Encode(text, encoding));
    }

    [Fact]
    public void A_lead_byte_without_its_second_byte_is_a_replacement_character()
    {
        Assert.Equal("A�", MacEncodings.Decode([0x41, 0x82], MacTextEncoding.Japanese));
        Assert.Equal("�\n", MacEncodings.Decode([0x82, 0x0A], MacTextEncoding.Japanese));     // not a second byte: kept
    }

    [Fact]
    public void Text_an_encoding_cannot_hold_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MacEncodings.Encode("日本", MacTextEncoding.Greek));
        Assert.Throws<ArgumentException>(() => MacEncodings.Encode("ก", MacTextEncoding.Japanese));
    }

    // Apple's mapping tables (unicode.org VENDORS/APPLE, Apple's licence; not committed): put them in
    // tests/golden/encodings to check every code of every encoding they cover.
    [Theory]
    [InlineData("ROMAN.TXT", MacTextEncoding.Roman)]
    [InlineData("JAPANESE.TXT", MacTextEncoding.Japanese)]
    [InlineData("CHINTRAD.TXT", MacTextEncoding.ChineseTraditional)]
    [InlineData("CHINSIMP.TXT", MacTextEncoding.ChineseSimplified)]
    [InlineData("KOREAN.TXT", MacTextEncoding.Korean)]
    [InlineData("ARABIC.TXT", MacTextEncoding.Arabic)]
    [InlineData("HEBREW.TXT", MacTextEncoding.Hebrew)]
    [InlineData("GREEK.TXT", MacTextEncoding.Greek)]
    [InlineData("CYRILLIC.TXT", MacTextEncoding.Cyrillic)]
    [InlineData("CENTEURO.TXT", MacTextEncoding.CentralEuropean)]
    [InlineData("ICELAND.TXT", MacTextEncoding.Icelandic)]
    [InlineData("TURKISH.TXT", MacTextEncoding.Turkish)]
    [InlineData("CROATIAN.TXT", MacTextEncoding.Croatian)]
    [InlineData("ROMANIAN.TXT", MacTextEncoding.Romanian)]
    [InlineData("UKRAINE.TXT", MacTextEncoding.Ukrainian)]
    [InlineData("THAI.TXT", MacTextEncoding.Thai)]
    public void Every_code_decodes_as_Apples_mapping_table_says(string file, MacTextEncoding encoding)
    {
        var path = Path.Combine(GoldenFolder(), "encodings", file);
        Assert.SkipWhen(!File.Exists(path), $"Apple's {file} is not in tests/golden/encodings.");
        var wrong = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('#')[0].Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2 || !fields[0].StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            var code = Convert.FromHexString(fields[0][2..]);
            var expected = string.Concat(fields[1].Split('+').Select(Unicode));
            var actual = MacEncodings.Decode(code, encoding);
            if (actual != expected)
            {
                wrong.Add($"{fields[0]}: {Codes(actual)}, Apple {Codes(expected)}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} codes differ:\n{string.Join('\n', wrong.Take(200))}");
    }

    // "0x00A5", or with a direction hint "<RL>+0x0627" (the hint dropped).
    private static string Unicode(string field) =>
        field.StartsWith("0x", StringComparison.Ordinal) ? char.ConvertFromUtf32(int.Parse(field[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : "";

    private static string Codes(string text) => string.Join("+", text.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));

    private static string GoldenFolder([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "golden"));
}
