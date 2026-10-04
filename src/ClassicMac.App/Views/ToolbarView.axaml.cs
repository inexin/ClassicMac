using Avalonia.Controls;

namespace ClassicMac.App.Views;

// The toolbar (boards/main-window.md, S2): Open, Save, Get Info, Edit Hex, Export, Extract All, Play, Zoom and Depth,
// each following its command.
internal sealed partial class ToolbarView : UserControl
{
    public ToolbarView()
    {
        InitializeComponent();
    }
}
