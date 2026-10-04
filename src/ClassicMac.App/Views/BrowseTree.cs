using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

/// <summary>
/// The browse tree (design/boards/browse-tree.md) as one virtualized list of the visible rows
/// (<see cref="VisibleRows"/>), each a fixed CmRowTree high and indented by its depth, so the scroll extent is exact:
/// a TreeView nests a panel per open row and estimates the rows it has not made, so its extent, thumb and position
/// moved while scrolling past an open folder. The keys a tree has are kept: Right opens a row or goes to its first
/// child, Left closes it or goes to its parent, + and − open and close, * opens a row and every row below it; Up, Down,
/// Home, End, Page Up and Page Down are the list's.
/// </summary>
internal sealed class BrowseTree : ListBox
{
    /// <summary>CmTreeIndent: the indent per level.</summary>
    public const double Indent = 16;

    /// <summary>A row's depth as its indent.</summary>
    public static IValueConverter IndentConverter { get; } = new FuncValueConverter<int, double>(depth => depth * Indent);

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new BrowseRow();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<BrowseRow>(item, out recycleKey);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SelectedItem is NodeViewModel node && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && Handle(node, e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    // A tree's keys on the selected row; false for keys the list handles.
    private bool Handle(NodeViewModel node, Key key)
    {
        switch (key)
        {
            case Key.Right when node.HasChildren && !node.IsExpanded:
            case Key.Add or Key.OemPlus when node.HasChildren:
                node.IsExpanded = true;
                return true;
            case Key.Right when node.IsExpanded && node.Children.Count > 0:
                Select(node.Children[0]);
                return true;
            case Key.Left when node.IsExpanded:
            case Key.Subtract or Key.OemMinus:
                node.IsExpanded = false;
                return true;
            case Key.Left when node.Parent is { } parent:
                Select(parent);
                return true;
            case Key.Multiply:
                SetExpandedDeep(node, true);
                return true;
            case Key.Left or Key.Right:
                return true;
            default:
                return false;
        }
    }

    // Selects a row and moves the focus to it.
    private void Select(NodeViewModel node)
    {
        SelectedItem = node;
        ScrollIntoView(node);
        if (ContainerFromItem(node) is Control row)
        {
            row.Focus();
        }
    }

    /// <summary>Opens or closes a row and every row below it that is read.</summary>
    public static void SetExpandedDeep(NodeViewModel node, bool expanded)
    {
        if (!node.HasChildren)
        {
            return;
        }

        if (expanded)
        {
            node.IsExpanded = true;
        }

        foreach (var child in node.Children)
        {
            SetExpandedDeep(child, expanded);
        }

        if (!expanded)
        {
            node.IsExpanded = false;
        }
    }
}

/// <summary>A row of the browse tree: a double click opens or closes it.</summary>
internal sealed class BrowseRow : ListBoxItem
{
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        // Two quick presses on the expander are two toggles, not a double click on the row.
        var onExpander = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("tree-expander")) == true;
        if (DataContext is NodeViewModel { HasChildren: true } node && !e.Handled && !onExpander)
        {
            node.IsExpanded = !node.IsExpanded;
            e.Handled = true;
        }
    }
}
