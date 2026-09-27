using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
                if (DataContext is MainViewModel model) model.FilePicker = this;
            };
        }

        public async Task<IReadOnlyList<string>> PickFilesAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Mac files, disk images or resource forks",
                AllowMultiple = true,
            });
            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
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
