using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassicMac.App.Views;

// The menu bar: File, Edit, Resource, View, Window and Help, with their shortcuts.
internal sealed partial class MenuBarView : UserControl
{
    public MenuBarView()
    {
        InitializeComponent();
    }

    // File ▸ Quit: closes the window the menu bar is in.
    private void OnQuit(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
