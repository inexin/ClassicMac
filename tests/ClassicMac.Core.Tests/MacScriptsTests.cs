using ClassicMac.Core;

namespace ClassicMac.Core.Tests;

// Which Mac encoding a script, a region or a font family means (docs/formats/codecs/text-encodings.md §2.1).
public sealed class MacScriptsTests
{
    [Theory]
    [InlineData(3, 0)]                                                              // Geneva: Roman
    [InlineData(0x3FFF, 0)]
    [InlineData(0x4000, 1)]                                                         // the first Japanese family
    [InlineData(0x41FF, 1)]
    [InlineData(0x4200, 2)]                                                         // Traditional Chinese
    [InlineData(0x4400, 3)]                                                         // Korean
    [InlineData(0x7000, 25)]                                                        // Simplified Chinese
    [InlineData(0xBFFF, 64)]
    [InlineData(0xC000, 0)]                                                         // past the Script Manager's range
    public void A_font_familys_ID_gives_its_script(int family, int script)
    {
        Assert.Equal(script, MacScripts.ScriptOfFontFamily(family));
    }

    [Theory]
    [InlineData(1, 0, MacTextEncoding.Japanese)]
    [InlineData(25, 52, MacTextEncoding.ChineseSimplified)]
    [InlineData(0, 21, MacTextEncoding.Icelandic)]                                  // Roman script, Iceland
    [InlineData(0, 24, MacTextEncoding.Turkish)]
    [InlineData(0, 68, MacTextEncoding.Croatian)]
    [InlineData(0, 25, MacTextEncoding.Croatian)]                                   // verYugoCroatian
    [InlineData(0, 39, MacTextEncoding.Romanian)]
    [InlineData(0, 20, MacTextEncoding.Greek)]                                      // the Greek system uses smRoman
    [InlineData(0, 12, MacTextEncoding.Roman)]                                      // Norway
    [InlineData(7, 62, MacTextEncoding.Ukrainian)]
    [InlineData(7, 49, MacTextEncoding.Cyrillic)]
    [InlineData(29, 42, MacTextEncoding.CentralEuropean)]
    public void A_script_and_region_give_the_encoding(int script, int region, MacTextEncoding encoding)
    {
        Assert.Equal(encoding, MacScripts.Encoding(script, region));
    }

    [Fact]
    public void Scripts_ClassicMac_has_no_encoding_for_give_none()
    {
        Assert.Null(MacScripts.Encoding(9, 33));                                    // Devanagari
        Assert.Null(MacScripts.Encoding(32, 0));                                    // uninterpreted
    }

    [Theory]
    [InlineData(14, MacTextEncoding.Japanese)]
    [InlineData(53, MacTextEncoding.ChineseTraditional)]
    [InlineData(52, MacTextEncoding.ChineseSimplified)]
    [InlineData(51, MacTextEncoding.Korean)]
    [InlineData(13, MacTextEncoding.Hebrew)]
    [InlineData(16, MacTextEncoding.Arabic)]
    [InlineData(49, MacTextEncoding.Cyrillic)]
    [InlineData(62, MacTextEncoding.Ukrainian)]
    [InlineData(54, MacTextEncoding.Thai)]
    [InlineData(56, MacTextEncoding.CentralEuropean)]
    [InlineData(24, MacTextEncoding.Turkish)]
    [InlineData(0, MacTextEncoding.Roman)]
    [InlineData(12, MacTextEncoding.Roman)]
    [InlineData(3, MacTextEncoding.Roman)]
    public void A_region_alone_gives_its_systems_encoding(int region, MacTextEncoding encoding)
    {
        Assert.Equal(encoding, MacScripts.EncodingOfRegion(region));
    }
}
