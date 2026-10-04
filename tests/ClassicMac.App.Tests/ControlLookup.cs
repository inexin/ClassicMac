using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace ClassicMac.App.Tests;

// Finds a named control under a window. The main window is made of UserControls, each a name scope of its own, so
// FindControl on the window does not reach their controls; the logical tree does, realized or not on a hidden tab.
internal static class ControlLookup
{
    public static T? Named<T>(this ILogical root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
}
