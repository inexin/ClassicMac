using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
                model.AudioPlayer ??= audio;
            };
            Closed += (_, _) => audio.Dispose();
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

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

        private async void OnDrop(object? sender, DragEventArgs e)
        {
            if (DataContext is not MainViewModel model) return;
            foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
            {
                if (item.TryGetLocalPath() is { } path) await model.OpenAsync(path);
            }
        }

        private void OnQuit(object? sender, RoutedEventArgs e) => Close();
    }
}
