using Avalonia.Controls;

namespace ClassicMac.App.Views;

// The status bar (boards/main-window.md, S4): the selected input's summary, then the work in progress or the status
// text.
internal sealed partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }
}
