using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The Hex tab (boards/hex.md, E7): the bytes in a grid, edited in place, with Go to, Find and the byte inspector.
internal sealed partial class HexTab : UserControl
{
    public HexTab()
    {
        InitializeComponent();
        HexList.KeyDown += OnHexKeyDown;
        HexList.AddHandler(PointerPressedEvent, OnHexPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Scrolls a line of the grid into view (Find's match).</summary>
    public void ScrollToLine(int line) => HexList.ScrollIntoView(line);

    /// <summary>Puts the focus in the Find box, its text selected (Ctrl+F in the Hex tab).</summary>
    public void FocusFind()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    // A click on a byte (or its character) puts the cursor on it while editing, else selects it.
    private void OnHexPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel model && (e.Source as StyledElement)?.DataContext is HexCell cell)
        {
            model.EditActions.SelectHexByte(cell.Offset);
            HexList.Focus();
        }
    }

    // Keys of the hex view go to the byte editor while it is on; the cursor's line is kept in view.
    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { EditActions.HexEdit: { } editor } || !HexKeys.Handle(editor, e.Key, e.KeyModifiers))
        {
            return;
        }

        e.Handled = true;
        HexList.ScrollIntoView(editor.CursorLine);
    }
}
