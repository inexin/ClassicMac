using Avalonia.Controls;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// An image resource's preview (boards/main-window.md, P1): the summary line, Show masks and Finder states, and the
// cards in rows that fit the width.
internal sealed partial class ImagePane : UserControl
{
    public ImagePane()
    {
        InitializeComponent();
        // The image grid's rows hold as many cards as fit the scroller (P1).
        ImageScroller.SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.ImageGrid.ImageViewportWidth = e.NewSize.Width;
            }
        };
    }

    /// <summary>Scrolls back to the first card (a new preview).</summary>
    public void ScrollToTop() => ImageScroller.Offset = default;
}
