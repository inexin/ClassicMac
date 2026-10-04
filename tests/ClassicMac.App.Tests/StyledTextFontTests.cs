using ClassicMac.App.Controls;

namespace ClassicMac.App.Tests;

// A styled text's Mac fonts map to close host relatives, else the app's own font (the screenshots use the app's own,
// so they do not depend on the fonts a machine has).
public class StyledTextFontTests
{
    [Theory]
    [InlineData("Times", "Times New Roman, Times, serif")]
    [InlineData("New York", "Times New Roman, Times, serif")]
    [InlineData("Monaco", "Cascadia Mono, Consolas, Menlo, Courier New, monospace")]
    [InlineData("Palatino", "Palatino, Palatino Linotype, Book Antiqua, serif")]
    [InlineData("Zapf Chancery", "Zapf Chancery, Monotype Corsiva, cursive")]
    public void Mac_fonts_map_to_host_relatives(string mac, string host)
    {
        Assert.Equal(host, string.Join(", ", StyledTextView.Family(mac).FamilyNames));
    }

    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Geneva")]
    [InlineData("Chicago")]
    public void The_system_fonts_map_to_the_apps_own(string mac)
    {
        Assert.Equal(Avalonia.Media.FontFamily.Default, StyledTextView.Family(mac));
    }
}
