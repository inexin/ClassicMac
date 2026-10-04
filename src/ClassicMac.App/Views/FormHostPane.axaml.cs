using Avalonia.Controls;

namespace ClassicMac.App.Views;

// The read-then-edit host (boards/read-then-edit.md, E1) with every form's templates: read only first, then edited in
// place with Cancel and Apply.
internal sealed partial class FormHostPane : UserControl
{
    // Esc, Ctrl+Enter, the double-click to edit and the row selection.
    private readonly FormHostInput input;

    public FormHostPane()
    {
        InitializeComponent();
        input = new FormHostInput(FormHost);
    }
}
