using Avalonia.Controls;

namespace ClassicMac.App.Views;

// The custom title bar (boards/main-window.md, S1) on Windows and macOS, where the window extends into its decorations:
// the app icon and the title; the caption buttons are drawn over it. Linux keeps the system's.
internal sealed partial class TitleBarView : UserControl
{
    public TitleBarView()
    {
        InitializeComponent();
    }
}
