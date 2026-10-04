using Avalonia.Controls;

namespace ClassicMac.App.Views;

// The diagnostics panel (boards/diagnostics.md): the header bar with its counts, filter and grouping, the column
// headers, and the rows grouped by file.
internal sealed partial class DiagnosticsPane : UserControl
{
    public DiagnosticsPane()
    {
        InitializeComponent();
    }
}
