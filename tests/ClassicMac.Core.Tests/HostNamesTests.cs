using ClassicMac.Core;

namespace ClassicMac.Core.Tests;

public class HostNamesTests
{
    [Theory]
    [InlineData("Read Me", "Read Me")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("100% done?", "100%25 done%3F")]
    [InlineData("Icon\r", "Icon%0D")]
    [InlineData("trail.", "trail%2E")]
    [InlineData("space ", "space%20")]
    [InlineData("COM1", "COM%31")]
    [InlineData("con.txt", "co%6E.txt")]
    [InlineData("Résumé ƒ", "Résumé ƒ")]
    [InlineData("Scenario:Data", "Scenario%3AData")]
    public void Mac_names_become_safe_host_names(string mac, string host)
    {
        Assert.Equal(host, HostNames.ToHostName(new MacString(MacRoman.Encode(mac))));
    }

    // In another encoding (text-encodings.md §5): each character whole, a code that is no text escaped byte by byte.
    [Fact]
    public void Names_in_another_encoding_become_their_characters()
    {
        Assert.Equal("日本.txt", HostNames.ToHostName(new MacString([0x93, 0xFA, 0x96, 0x7B, .. ".txt"u8]), MacTextEncoding.Japanese));
        Assert.Equal("a%3Ab", HostNames.ToHostName(new MacString("a:b"u8), MacTextEncoding.Japanese));
        Assert.Equal("x%82", HostNames.ToHostName(new MacString([(byte)'x', 0x82]), MacTextEncoding.Japanese));          // a lead byte alone
        Assert.Equal("CO%4E", HostNames.ToHostName(new MacString("CON"u8), MacTextEncoding.Greek));
    }

    [Fact]
    public void Long_names_are_cut_before_the_extension()
    {
        var name = HostNames.ToHostName(MacString.FromMacRoman(new string('a', 40) + "?.sit"), maxLength: 20);
        Assert.Equal("aaaaaaaaaaaaaaaa.sit", name);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaa", HostNames.ToHostName(MacString.FromMacRoman(new string('a', 30)), maxLength: 20));
    }

    [Fact]
    public void Colliding_names_get_numbers()
    {
        var taken = new HashSet<string>();
        Assert.Equal("DUP.TXT", HostNames.MakeUnique("DUP.TXT", taken));
        Assert.Equal("DUP ~2.TXT", HostNames.MakeUnique("DUP.TXT", taken));
        Assert.Equal("dup ~3.txt", HostNames.MakeUnique("dup.txt", taken)); // case differs only: still taken
    }

    [Theory]
    [InlineData("PICT", false, "PICT")]
    [InlineData("snd ", false, "snd%20")]
    [InlineData("PAT ", false, "PAT%20")]
    [InlineData("PICT", true, "PICT~50494354")]
    [InlineData("pict", true, "pict~70696374")]
    [InlineData("STR#", false, "STR#")]
    [InlineData("a/b?", false, "a%2Fb%3F")]
    public void Resource_types_become_folder_names(string type, bool collides, string folder)
    {
        Assert.Equal(folder, HostNames.TypeFolder(FourCC.FromString(type), collides));
    }
}
