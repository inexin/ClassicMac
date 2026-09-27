namespace ClassicMac.Core.Tests;

public class FourCCTests
{
    [Fact]
    public void FromString_is_big_endian()
    {
        Assert.Equal(0x50494354u, FourCC.FromString("PICT").Value);
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        Assert.NotEqual(FourCC.FromString("PICT"), FourCC.FromString("pict"));
    }

    [Fact]
    public void ToString_is_Mac_OS_Roman_with_control_characters_escaped()
    {
        Assert.Equal("snd ", FourCC.FromString("snd ").ToString());
        Assert.Equal("ab\\x00•", new FourCC(0x616200A5).ToString());
        Assert.Equal("a\\x5Cbc", new FourCC(0x615C6263).ToString());
        Assert.Equal("©abc", new FourCC(0xA9616263).ToString());
    }

    [Theory]
    [InlineData(0x616200A5u)]
    [InlineData(0x615C6263u)]
    [InlineData(0xF0000D7Fu)]
    [InlineData(0x50494354u)]
    public void ToString_reads_back(uint value)
    {
        var code = new FourCC(value);
        Assert.Equal(code, FourCC.FromString(code.ToString()));
    }

    [Fact]
    public void FromString_rejects_wrong_lengths_and_characters_Mac_OS_Roman_lacks()
    {
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abc"));
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abcde"));
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abĀc"));
        Assert.False(FourCC.TryParse(null, out _));
    }
}

public class MacRomanTests
{
    [Fact]
    public void Every_byte_round_trips()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var text = MacRoman.Decode(all);
        Assert.Equal(256, text.Distinct().Count());
        Assert.Equal(all, MacRoman.Encode(text));
    }

    [Theory]
    [InlineData(0x80, 'Ä')]
    [InlineData(0xA5, '•')]
    [InlineData(0xBD, 'Ω')]
    [InlineData(0xDB, '€')]
    [InlineData(0xF0, '')]
    [InlineData(0xFF, 'ˇ')]
    public void Matches_the_Unicode_mapping(byte value, char expected)
    {
        Assert.Equal(expected, MacRoman.ToChar(value));
    }

    [Fact]
    public void Older_mappings_encode_too()
    {
        Assert.Equal([0xDB, 0xBD], MacRoman.Encode("¤Ω"));
        Assert.False(MacRoman.TryEncode("日本", out _));
    }

    [Fact]
    public void MacString_converts_through_Mac_OS_Roman()
    {
        var name = MacString.FromMacRoman("Hax 1.0 Ä");
        Assert.Equal(0x80, name.Bytes[^1]);
        Assert.Equal("Hax 1.0 Ä", name.ToMacRoman());
        Assert.Equal("Icon\\x0D", new MacString("Icon\r"u8).ToString());
    }
}

public class MacStringTests
{
    [Fact]
    public void Compares_by_bytes()
    {
        Assert.Equal(new MacString([1, 2, 3]), new MacString([1, 2, 3]));
        Assert.NotEqual(new MacString([1, 2, 3]), new MacString([1, 2]));
        Assert.Equal(default, new MacString([]));
    }

    [Fact]
    public void Holds_at_most_255_bytes()
    {
        Assert.Equal(255, new MacString(new byte[255]).Length);
        Assert.Throws<ArgumentException>(() => new MacString(new byte[256]));
    }
}
