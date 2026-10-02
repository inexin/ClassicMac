using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.Resources.Decoders.Tests;

// The Finder Preferences file's views font and grid (docs/formats/file-systems/finder-windows.md §1.5).
public class FinderPreferencesTests
{
    private static byte[] Fvl8(int font = 4, int size = 9, int length = 0x2C)
    {
        var data = new byte[Math.Max(length, 0x2C)];
        data[0x18] = (byte)(font >> 24); data[0x19] = (byte)(font >> 16); data[0x1A] = (byte)(font >> 8); data[0x1B] = (byte)font;
        data[0x1C] = (byte)(size >> 24); data[0x1D] = (byte)(size >> 16); data[0x1E] = (byte)(size >> 8); data[0x1F] = (byte)size;
        byte[] grid = [0, 150, 0, 100, 0, 200, 0, 120];
        grid.CopyTo(data, 0x24);
        return data[..length];
    }

    [Fact]
    public void The_views_font_size_and_grid_come_from_fvl8()
    {
        var preferences = FinderPreferences.Read(Fvl8())!;

        Assert.Equal((4, 9), (preferences.ViewsFontId, preferences.ViewsFontSize));
        Assert.Equal([150, 100, 200, 120], preferences.GridSpacing);
    }

    [Fact]
    public void Without_the_grid_the_defaults_stand()
    {
        var preferences = FinderPreferences.Read(Fvl8(length: 0x20))!;

        Assert.Equal((4, 9), (preferences.ViewsFontId, preferences.ViewsFontSize));
        Assert.Equal(FinderPreferences.Default.GridSpacing, preferences.GridSpacing);
    }

    [Theory]
    [InlineData(0x1F, 3, 10)]
    [InlineData(0x2C, -1, 10)]
    [InlineData(0x2C, 4, 0)]
    [InlineData(0x2C, 4, 128)]
    [InlineData(0x2C, 0x10000, 10)]
    public void Short_or_out_of_range_preferences_are_not_used(int length, int font, int size)
    {
        Assert.Null(FinderPreferences.Read(Fvl8(font, size, length)));
    }

    [Fact]
    public void The_defaults_are_Geneva_10_and_the_Finder_s_grid()
    {
        Assert.Equal((3, 10), (FinderPreferences.Default.ViewsFontId, FinderPreferences.Default.ViewsFontSize));
        Assert.Equal([200, 120, 200, 120], FinderPreferences.Default.GridSpacing);
    }

    [Fact]
    public void A_fork_s_fvl8_128_is_read()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("fvl8"), 128, Fvl8(font: 21, size: 12)));

        Assert.Equal(21, FinderPreferences.FromFork(fork)!.ViewsFontId);
        Assert.Null(FinderPreferences.FromFork(new ResourceFork()));
        Assert.Throws<ArgumentNullException>(() => FinderPreferences.FromFork(null!));
    }
}
