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
    public void ToString_escapes_bytes_outside_printable_ascii()
    {
        Assert.Equal("snd ", FourCC.FromString("snd ").ToString());
        Assert.Equal("ab\\x00\\xA5", new FourCC(0x616200A5).ToString());
        Assert.Equal("a\\x5Cbc", FourCC.FromString("a\\bc").ToString());
    }

    [Fact]
    public void FromString_rejects_wrong_lengths_and_wide_characters()
    {
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abc"));
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abĀc"));
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
