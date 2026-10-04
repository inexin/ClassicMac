using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// A structured resource's preview (boards/property-view.md, P2): property cards with links, or the JSON.
internal sealed partial class PropertyPane : UserControl
{
    public PropertyPane()
    {
        InitializeComponent();
    }

    // A property row's right-click Copy as decimal, hex or JSON (P2): the menu item carries the text.
    private void OnCopyProperty(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is MenuItem { Tag: string text })
        {
            model.PropertyLinks.CopyPropertyCommand.Execute(text);
        }
    }
}
