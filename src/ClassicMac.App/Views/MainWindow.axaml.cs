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
using ClassicMac.App.Behaviors;
using ClassicMac.App.Controls;
using ClassicMac.App.Dialogs;
using ClassicMac.App.Services;
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
        helpWebView = new HelpWebView(this, WebPagePane.HelpHost);
        shell = new WindowShell(this, MenuBarView.WindowMenu);
        // The custom title bar (S1): Windows and macOS extend the client area into the decorations; Linux keeps the
        // system title bar, whose support for this varies by desktop.
        if (!OperatingSystem.IsLinux())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 34;
        }

        TitleBarView.TitleBar.Classes.Set("mac", OperatingSystem.IsMacOS());
        // Avalonia's own drawn title bar keeps its caption buttons but not its title text (see the theme).
        WindowDecorationsTheme = (Avalonia.Styling.ControlTheme)Resources["CmWindowDecorations"]!;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
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
            model.AttachPlatform(new AppPlatform(this, new EditDialogs(this), new AvaloniaImageReader(), audio, shell));
            if (!ReferenceEquals(boundPanel, model.DiagnosticsPanel))
            {
                boundPanel = model.DiagnosticsPanel;
                DiagnosticsRow.Bind(Body.RowDefinitions[1], Body.RowDefinitions[2], boundPanel);
                model.ItemShown += TreePane.ShowItem;
                shell.Bind(model);
            }
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            audio.Dispose();
            (DataContext as MainViewModel)?.DragOut.CleanUpDragOut();
        };
    }

    private DiagnosticsPanel? boundPanel;

    // A new preview's images start at the top.
    private void OnPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Preview))
        {
            ImagePane.ScrollToTop();
        }

        if (e.PropertyName == nameof(MainViewModel.Selected) && sender is MainViewModel model)
        {
            TreePane.FollowSelection(model);
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
            if (property == nameof(MainViewModel.Selected))
            {
                TreePane.RestoreSelection(model);
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
        e.DragEffects = !TreePane.IsDraggingOut && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
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
        if (TreePane.IsDraggingOut || DataContext is not MainViewModel model)
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
            TreePane.FocusFilter();
            e.Handled = true;
        }
    }
}
