using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

// The app's fonts are bundled (IBM Plex Sans and Plex Mono, design/TOKENS.md "Type"): text measured with CmFontSans
// and CmFontMono is shaped with Plex's own typefaces, not a system fallback, and Plex Sans is the default font.
public class FontTests
{
    // The typeface text in a font is shaped with: the first run's.
    private static GlyphTypeface Shaped(FontFamily family, FontWeight weight = FontWeight.Normal, FontStyle style = FontStyle.Normal)
    {
        using var layout = new TextLayout("Hello, ClassicMac 0123", new Typeface(family, style, weight), 13, Brushes.Black);
        Assert.True(layout.Width > 0);
        return layout.TextLines[0].TextRuns.OfType<ShapedTextRun>().First().GlyphRun.GlyphTypeface;
    }

    private static FontFamily Resource(string key) =>
        Assert.IsType<FontFamily>(Application.Current!.FindResource(key));

    [Fact]
    public void CmFontSans_is_IBM_Plex_Sans() => Headless.OnUiThread(() =>
    {
        var sans = Resource("CmFontSans");
        Assert.Equal("IBM Plex Sans", Shaped(sans).FamilyName);
        Assert.Equal(FontWeight.SemiBold, Shaped(sans, FontWeight.SemiBold).Weight);
        Assert.Equal(FontStyle.Italic, Shaped(sans, style: FontStyle.Italic).Style);
        var both = Shaped(sans, FontWeight.SemiBold, FontStyle.Italic);
        Assert.Equal((FontWeight.SemiBold, FontStyle.Italic), (both.Weight, both.Style));
    });

    [Fact]
    public void CmFontMono_is_IBM_Plex_Mono() => Headless.OnUiThread(() =>
    {
        var mono = Resource("CmFontMono");
        Assert.Equal("IBM Plex Mono", Shaped(mono).FamilyName);
        Assert.Equal(FontWeight.SemiBold, Shaped(mono, FontWeight.SemiBold).Weight);
    });

    [Fact]
    public void Plex_Sans_is_the_default_font() => Headless.OnUiThread(() =>
    {
        Assert.Equal("IBM Plex Sans", Shaped(FontFamily.Default).FamilyName);
        var text = new TextBlock { Text = "Default" };
        var window = new Window { Content = text };
        window.Show();
        Assert.Equal("IBM Plex Sans", Shaped(text.FontFamily).FamilyName);
        window.Close();
    });

    // The hex view and plain text use the mono font.
    [Fact]
    public void Hex_and_plain_text_use_the_mono_font() => Headless.OnUiThread(() =>
    {
        var window = new MainWindow { DataContext = new ViewModels.MainViewModel() };
        window.Show();
        var hex = window.FindControl<ListBox>("HexList")!;
        Assert.Equal("IBM Plex Mono", Shaped(hex.FontFamily).FamilyName);
        Assert.All(window.GetLogicalDescendants().OfType<SelectableTextBlock>().Where(t => t is not StyledTextView),
            t => Assert.Equal("IBM Plex Mono", Shaped(t.FontFamily).FamilyName));
        window.Close();
    });
}
