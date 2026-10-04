using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.Controls;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The browse tree's column (boards/browse-tree.md): the filter and display options, the tree with its rows, the hidden-
// items footer and the type-ahead pill.
internal sealed partial class TreePane : UserControl
{
    public TreePane()
    {
        InitializeComponent();
        Tree.AddHandler(TextInputEvent, OnTreeTextInput, RoutingStrategies.Tunnel);
        Tree.SelectionChanged += OnTreeSelectionChanged;
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => dragPress = null, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Puts the focus in the filter field, its text selected (Ctrl+F outside the Hex tab).</summary>
    public void FocusFilter()
    {
        TreeFilter.Focus();
        TreeFilter.SelectAll();
    }

    /// <summary>A selection the view-model refused: the tree shows the view-model's again.</summary>
    public void RestoreSelection(MainViewModel model)
    {
        if (!ReferenceEquals(Tree.SelectedItem, model.Selected))
        {
            Tree.SelectedItem = model.Selected;
        }
    }

    // "Show item" and type-ahead: once the opened ancestors have their rows, the node's row scrolls into view and takes
    // the focus.
    public void ShowItem(NodeViewModel node) => Dispatcher.UIThread.Post(() => ShowNode(node), DispatcherPriority.Background);

    /// <summary>Scrolls the node's row into view and focuses it (its ancestors already open).</summary>
    internal void ShowNode(NodeViewModel node)
    {
        if (RevealNode(node) is { } row)
        {
            row.Focus();
        }
    }

    // Any selection made away from the tree (the Details tab's "In", a diagnostic's row, a form's link) scrolls the tree
    // to it once its ancestors' rows are made; a click in the tree is already in view.
    public void FollowSelection(MainViewModel model)
    {
        if (model.Selected is { } node)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(model.Selected, node))
                {
                    RevealNode(node);
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Scrolls the node's row into view and returns it (null when the row does not show: an ancestor closed or the
    /// node filtered out). The rows are one flat list of fixed height, so its index places it exactly.
    /// </summary>
    private Control? RevealNode(NodeViewModel node)
    {
        if (DataContext is not MainViewModel model || model.TreeRows.IndexOf(node) is not (>= 0 and var index))
        {
            return null;
        }

        Tree.ScrollIntoView(index);
        Tree.UpdateLayout();
        return Tree.ContainerFromIndex(index);
    }

    // The expander: a press opens or closes the row (without selecting it); with Alt, the row and every row below it.
    private void OnExpanderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: NodeViewModel node } || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            BrowseTree.SetExpandedDeep(node, !node.IsExpanded);
        }
        else
        {
            node.IsExpanded = !node.IsExpanded;
        }

        e.Handled = true;
    }

    // Drag out of the tree: a press on a file or resource that moves a few pixels writes it to the drag folder, then
    // hands those files to the platform's drag (a file manager copies them).
    private PointerPressedEventArgs? dragPress;
    private Point dragOrigin;
    private bool draggingOut;

    /// <summary>Whether a drag out of the tree is under way (the window does not take it back as a drop).</summary>
    public bool IsDraggingOut => draggingOut;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        dragPress = e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed ? e : null;
        dragOrigin = e.GetPosition(Tree);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragPress is not { } press || draggingOut)
        {
            return;
        }

        var moved = e.GetPosition(Tree) - dragOrigin;
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6)
        {
            return;
        }

        var node = (press.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as NodeViewModel;
        if (DataContext is not MainViewModel model || !DragOut.CanDragOut(node))
        {
            dragPress = null;
            return;
        }
        draggingOut = true;
        node!.IsDragSource = true; // outlined while its files are written and dragged
        try
        {
            var paths = await model.DragOut.PrepareDragOutAsync(node!);
            // Released while the files were written: no drag (it would drop wherever the pointer is).
            if (paths.Count == 0 || dragPress != press)
            {
                return;
            }

            var data = new DataTransfer();
            foreach (var path in paths)
            {
                if (await TopLevel.GetTopLevel(this)!.StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file)
                {
                    data.Add(DataTransferItem.CreateFile(file));
                }
            }

            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }
        finally
        {
            dragPress = null;
            draggingOut = false;
            node.IsDragSource = false;
        }
    }

    // The tree's selection goes to the view-model (the binding only brings the view-model's to the tree). The rows are
    // virtualized: when the selected node's rows are rebuilt (an applied edit) the tree drops its selection though the
    // view-model has moved on to the new node, so a selection gone to nothing is put back rather than passed on.
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        if (Tree.SelectedItem is NodeViewModel node)
        {
            if (!ReferenceEquals(model.Selected, node))
            {
                model.Selected = node;
            }

            return;
        }

        if (model.Selected is { } selected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Tree.SelectedItem is null && ReferenceEquals(model.Selected, selected))
                {
                    Tree.SelectedItem = selected;
                }
            });
        }
    }

    // Typing in the tree opens the type-ahead; F3 and Shift+F3 step through its matches, Backspace removes a letter,
    // Esc closes it.
    private void OnTreeTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not MainViewModel model || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }
        model.TreeSearch.TypeAhead(e.Text);
        e.Handled = true;
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { TreeSearch.IsTypeAheadOpen: true } model)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.F3 when e.KeyModifiers == KeyModifiers.Shift:
                model.TreeSearch.PreviousMatchCommand.Execute(null);
                break;
            case Key.F3:
                model.TreeSearch.NextMatchCommand.Execute(null);
                break;
            case Key.Back:
                model.TreeSearch.TypeAheadBackspace();
                break;
            case Key.Escape:
                model.TreeSearch.ClearTypeAheadCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
