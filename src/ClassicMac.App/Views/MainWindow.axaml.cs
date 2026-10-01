using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ClassicMac.App.Audio;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    internal sealed partial class MainWindow : Window, IFilePicker
    {
        public MainWindow()
        {
            InitializeComponent();
            AddHandler(DragDrop.DropEvent, OnDrop);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            DataContextChanged += (_, _) =>
            {
                if (DataContext is not MainViewModel model) return;
                model.FilePicker = this;
                model.EditDialogs ??= new EditDialogs(this);
                model.AudioPlayer ??= audio;
            };
            Closing += OnClosing;
            HexList.KeyDown += OnHexKeyDown;
            Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            Tree.AddHandler(PointerReleasedEvent, (_, _) => dragPress = null, RoutingStrategies.Tunnel, handledEventsToo: true);
            Closed += (_, _) =>
            {
                audio.Dispose();
                (DataContext as MainViewModel)?.CleanUpDragOut();
            };
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
            if (dragPress is not { } press || draggingOut) return;
            var moved = e.GetPosition(Tree) - dragOrigin;
            if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6) return;
            var node = (press.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NodeViewModel;
            if (DataContext is not MainViewModel model || !MainViewModel.CanDragOut(node))
            {
                dragPress = null;
                return;
            }
            draggingOut = true;
            try
            {
                var paths = await model.PrepareDragOutAsync(node!);
                // Released while the files were written: no drag (it would drop wherever the pointer is).
                if (paths.Count == 0 || dragPress != press) return;
                var data = new DataTransfer();
                foreach (var path in paths)
                    if (await StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file) data.Add(DataTransferItem.CreateFile(file));
                await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
            }
            finally
            {
                dragPress = null;
                draggingOut = false;
            }
        }

        // Keys of the hex view go to the byte editor while it is on; the cursor's line is kept in view.
        private void OnHexKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel { HexEdit: { } editor } || !editor.OnKey(e.Key, e.KeyModifiers)) return;
            e.Handled = true;
            HexList.ScrollIntoView(editor.CursorLine);
        }

        private bool quitting;

        // Unsaved edits: the window stays open while the user decides, then closes when they are saved or discarded.
        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (quitting || DataContext is not MainViewModel { HasUnsavedChanges: true } model) return;
            e.Cancel = true;
            if (!await model.ConfirmQuitAsync()) return;
            quitting = true;
            Close();
        }

        private readonly SoundFlowPlayer audio = new();

        public async Task<IReadOnlyList<string>> PickFilesAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Mac files, disk images or resource forks",
                AllowMultiple = true,
            });
            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
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

        // Files dropped in open; the app's own drag out is not dropped back in.
        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = !draggingOut && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (draggingOut || DataContext is not MainViewModel model) return;
            foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
            {
                if (item.TryGetLocalPath() is { } path) await model.OpenAsync(path);
            }
        }

        private void OnQuit(object? sender, RoutedEventArgs e) => Close();
    }
}
