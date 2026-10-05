using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassicMac.App.Behaviors;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The Hex tab (boards/hex.md, E7): the bytes in a grid, edited in place, with Go to, Find and the byte inspector.
internal sealed partial class HexTab : UserControl
{
    public HexTab()
    {
        InitializeComponent();
        HexList.KeyDown += OnHexKeyDown;
        HexList.AddHandler(TextInputEvent, OnHexTextInput, RoutingStrategies.Tunnel);
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

    // A click on a byte (or its character) puts the cursor on it while editing, typing in the column clicked, else
    // selects it.
    private void OnHexPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel model && (e.Source as StyledElement)?.DataContext is HexCell cell)
        {
            var text = e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("hex-char")) is not null;
            model.EditActions.SelectHexByte(cell.Offset, text);
            HexList.Focus();
        }
    }

    // Text typed in the Mac OS Roman column while editing: its characters' bytes.
    private void OnHexTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not MainViewModel { EditActions.HexEdit: { TextColumn: true } editor } || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        editor.TypeText(e.Text);
        e.Handled = true;
        HexList.ScrollIntoView(editor.CursorLine);
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
