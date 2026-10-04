using Avalonia;
using ClassicMac.App.Controls;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

// The window form's right panel (design/boards/window-alert.md): the bounds on a half-scale screen with its menu bar,
// the content rectangle and, for windows that have one, the title bar above it.
public sealed class ScreenBoundsTests
{
    [Fact]
    public void A_document_window_is_drawn_at_half_scale_with_its_title_bar()
    {
        var g = ScreenBoundsView.Layout(512, 342, 40, 6, 322, 506, titleBar: true);
        Assert.Equal(new Rect(0, 0, 256, 171), g.Screen);
        Assert.Equal(new Rect(0, 0, 256, 10), g.MenuBar);
        Assert.Equal(new Rect(3, 20, 250, 141), g.Content);
        Assert.Equal(new Rect(3, 11, 250, 9), g.TitleBar);
    }

    [Fact]
    public void A_dialog_box_has_no_title_bar_and_odd_bounds_are_kept_in_order()
    {
        var g = ScreenBoundsView.Layout(640, 480, 100, 200, 50, 150, titleBar: false);
        Assert.Equal(new Rect(0, 0, 320, 240), g.Screen);
        Assert.Null(g.TitleBar);
        Assert.Equal(new Rect(75, 25, 25, 25), g.Content); // bottom above top: the rectangle between them
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    public void Which_windows_have_a_title_bar(int definition, bool titleBar) =>
        Assert.Equal(titleBar, ScreenBoundsView.HasTitleBar(definition));
}
