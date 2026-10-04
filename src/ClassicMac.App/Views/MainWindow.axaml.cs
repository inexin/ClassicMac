using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.Audio;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A window is not disposed: the audio player is disposed when it closes.")]
internal sealed partial class MainWindow : Window, IFilePicker
{
    // The window's parts: the playhead's timer, the help page's web view, the shell.
    private readonly PlayheadFollower playhead;
    private readonly HelpWebView helpWebView;
    private readonly WindowShell shell;

    public MainWindow()
    {
        InitializeComponent();
        playhead = new PlayheadFollower(this);
        helpWebView = new HelpWebView(this, HelpHost);
        shell = new WindowShell(this, WindowMenu);
        // The custom title bar (S1): Windows and macOS extend the client area into the decorations; Linux keeps the
        // system title bar, whose support for this varies by desktop.
        if (!OperatingSystem.IsLinux())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 34;
        }

        TitleBar.Classes.Set("mac", OperatingSystem.IsMacOS());
        // Avalonia's own drawn title bar keeps its caption buttons but not its title text (see the theme).
        WindowDecorationsTheme = (Avalonia.Styling.ControlTheme)Resources["CmWindowDecorations"]!;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(TextInputEvent, OnTreeTextInput, RoutingStrategies.Tunnel);
        Tree.SelectionChanged += OnTreeSelectionChanged;
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        // The image grid's rows hold as many cards as fit the scroller (P1).
        ImageScroller.SizeChanged += (_, e) =>
        {
            if (DataContext is MainViewModel model)
            {
                model.ImageGrid.ImageViewportWidth = e.NewSize.Width;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel model)
            {
                return;
            }

            model.PropertyChanged -= OnPreviewChanged;
            model.PropertyChanged += OnPreviewChanged;
            model.Drafts.ChangeRefused -= OnChangeRefused;
            model.Drafts.ChangeRefused += OnChangeRefused;
            model.HexFind.HexLineShown -= OnHexLineShown;
            model.HexFind.HexLineShown += OnHexLineShown;
            model.FilePicker = this;
            model.EditDialogs ??= new EditDialogs(this);
            model.SoundPlayback.AudioPlayer ??= audio;
            if (!ReferenceEquals(boundPanel, model.DiagnosticsPanel))
            {
                boundPanel = model.DiagnosticsPanel;
                DiagnosticsRow.Bind(Body.RowDefinitions[1], Body.RowDefinitions[2], boundPanel);
                model.ItemShown += ShowInTree;
                shell.Bind(model);
            }
        };
        Closing += OnClosing;
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => dragPress = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) =>
        {
            audio.Dispose();
            (DataContext as MainViewModel)?.DragOut.CleanUpDragOut();
        };
    }

    private DiagnosticsPanel? boundPanel;

    // "Show item" and type-ahead: once the opened ancestors have their rows, the node's row scrolls into view and takes
    // the focus.
    private void ShowInTree(NodeViewModel node) => Dispatcher.UIThread.Post(() => ShowNode(node), DispatcherPriority.Background);

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
    private void OnSelectedChanged(MainViewModel model)
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
                if (await StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file)
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

    // A new preview's images start at the top.
    private void OnPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Preview))
        {
            ImageScroller.Offset = default;
        }

        if (e.PropertyName == nameof(MainViewModel.Selected) && sender is MainViewModel model)
        {
            OnSelectedChanged(model);
        }
    }

    // A change the view-model refused (a selection or the template box, while it asks about unapplied edits) is
    // undone in the control once its own handling is over: a binding ignores the source while writing to it, and
    // afterwards does not push a value it thinks the control has.
    private void OnChangeRefused(object? sender, string property)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (property == nameof(MainViewModel.Selected) && !ReferenceEquals(Tree.SelectedItem, model.Selected))
            {
                Tree.SelectedItem = model.Selected;
            }

            if (property == nameof(Forms.UseTemplate) && FormHostPane.TemplateBox.IsChecked != model.Forms.UseTemplate)
            {
                FormHostPane.TemplateBox.IsChecked = model.Forms.UseTemplate;
            }
        });
    }

    private bool quitting;

    // Unapplied or unsaved edits: the window stays open while the user decides, then closes when they are applied or
    // discarded, and saved or discarded.
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (quitting || DataContext is not MainViewModel model || !model.Drafts.HasDraft && !model.EditActions.HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;
        if (!await model.EditActions.ConfirmQuitAsync())
        {
            return;
        }

        quitting = true;
        Close();
    }

    private readonly SoundFlowPlayer audio = new();

    /// <summary>Whether the playhead timer runs (while a sound plays).</summary>
    internal bool IsFollowingPlayhead => playhead.IsFollowing;

    /// <summary>The About box while it is open (tests close it).</summary>
    internal Window? About => shell.About;

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Mac files, disk images or resource forks",
            AllowMultiple = true,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter =
            [
                .. extensions.Select(e => new FilePickerFileType(e.TrimStart('.').ToUpperInvariant()) { Patterns = ["*" + e] }),
                FilePickerFileTypes.All,
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extensions.FirstOrDefault()?.TrimStart('.'),
            FileTypeChoices = extensions.Select(e => new FilePickerFileType(e.TrimStart('.').ToUpperInvariant()) { Patterns = ["*" + e] }).ToList(),
        });
        return file?.TryGetLocalPath();
    }

    // Files dropped in open; the app's own drag out is not dropped back in. While files are over the window the
    // empty state's drop zone is marked.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !draggingOut && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        if (DataContext is MainViewModel model)
        {
            model.EmptyState.IsDropTarget = e.DragEffects != DragDropEffects.None;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.EmptyState.IsDropTarget = false;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel dropped)
        {
            dropped.EmptyState.IsDropTarget = false;
        }
        if (draggingOut || DataContext is not MainViewModel model)
        {
            return;
        }

        foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
        {
            if (item.TryGetLocalPath() is { } path)
            {
                await model.OpenAsync(path);
            }
        }
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();

    // Find's match: its line scrolls into view.
    private void OnHexLineShown(int line) => HexTab.ScrollToLine(line);

    // Ctrl+F: the hex view's Find box while the Hex tab has the focus, else the tree's filter field. F3 and Shift+F3
    // in the Hex tab find the next and previous match.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var inHex = HexTab.IsKeyboardFocusWithin && DataContext is MainViewModel;
        if (inHex && e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            HexTab.FocusFind();
            e.Handled = true;
            return;
        }

        if (inHex && e.Key == Key.F3 && DataContext is MainViewModel hexModel)
        {
            (e.KeyModifiers == KeyModifiers.Shift ? hexModel.HexFind.FindPreviousCommand : hexModel.HexFind.FindNextCommand).Execute(null);
            e.Handled = true;
            return;
        }

        // Editing a menu: Alt+Up and Alt+Down move the selected row (boards/read-then-edit.md).
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down
            && DataContext is MainViewModel { FormEditing.IsEditingForm: true, Forms.Form: MenuForm menu })
        {
            var move = e.Key == Key.Up ? menu.MoveSelectedUpCommand : menu.MoveSelectedDownCommand;
            if (move.CanExecute(null))
            {
                move.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            TreeFilter.Focus();
            TreeFilter.SelectAll();
            e.Handled = true;
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
