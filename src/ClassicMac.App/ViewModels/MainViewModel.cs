using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A diagnostic with where it came from, and the tree node it belongs to when there is one.</summary>
    public sealed record DiagnosticEntry(Diagnostic Diagnostic, string Source, NodeViewModel? Node)
    {
        public string Severity => Diagnostic.Severity.ToString();

        public string Code => Diagnostic.Code;

        public string Message => Diagnostic.Message;
    }

    /// <summary>Which diagnostics the list shows.</summary>
    public enum DiagnosticFilter
    {
        All,
        WarningsAndErrors,
        Errors,
    }

    /// <summary>Picks files to open; the window provides it, tests replace it.</summary>
    public interface IFilePicker
    {
        Task<IReadOnlyList<string>> PickFilesAsync();
    }

    /// <summary>The main window: the opened inputs as a tree, the selection's details, and the diagnostics.</summary>
    public sealed partial class MainViewModel : ObservableObject
    {
        private readonly List<DiagnosticEntry> allDiagnostics = [];

        public ObservableCollection<InputNode> Roots { get; } = [];

        public ObservableCollection<DiagnosticEntry> Diagnostics { get; } = [];

        public IReadOnlyList<DiagnosticFilter> Filters { get; } = Enum.GetValues<DiagnosticFilter>();

        public IFilePicker? FilePicker { get; set; }

        public ContainerReadOptions ContainerOptions { get; init; } = ContainerReadOptions.Default;

        public ReadOptions ReadOptions { get; init; } = ReadOptions.Default;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
        private NodeViewModel? selected;

        [ObservableProperty]
        private DetailsViewModel details = DetailsViewModel.Empty;

        [ObservableProperty]
        private DiagnosticEntry? selectedDiagnostic;

        [ObservableProperty]
        private DiagnosticFilter filter = DiagnosticFilter.All;

        [ObservableProperty]
        private string status = "Open a Mac file, disk image or resource fork (File ▸ Open, or drop it here).";

        /// <summary>Reads <paramref name="path"/> (off the UI thread) and adds it to the tree.</summary>
        public async Task<InputNode?> OpenAsync(string path)
        {
            Status = $"Reading {Path.GetFileName(path)}…";
            var diagnostics = new List<Diagnostic>();
            try
            {
                var (host, root) = await Task.Run(() =>
                {
                    var context = new ContainerContext(ContainerOptions, diagnostics);
                    var hostFile = HostFiles.Read(path, ContainerOptions, diagnostics);
                    var tree = ContainerUnwrapper.Default.Unwrap(hostFile.File, HostFiles.FormatName(hostFile.Layout),
                        context.For(null, HostFiles.Siblings(path, ContainerOptions, diagnostics)));
                    return (hostFile, tree);
                });
                var input = new InputNode(path, host, root, ReadOptions, Report);
                Roots.Add(input);
                foreach (var d in diagnostics) Report(new DiagnosticEntry(d, input.Title, input));
                Selected = input;
                input.IsExpanded = true;
                var files = root.Leaves().Count();
                Status = $"{input.Title}: {files} file{(files == 1 ? "" : "s")}, {diagnostics.Count} diagnostic{(diagnostics.Count == 1 ? "" : "s")}.";
                return input;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "input.unreadable", e.Message), Path.GetFileName(path), null));
                Status = $"{Path.GetFileName(path)} could not be read: {e.Message}";
                return null;
            }
        }

        [RelayCommand]
        private async Task Open()
        {
            if (FilePicker is null) return;
            foreach (var path in await FilePicker.PickFilesAsync()) await OpenAsync(path);
        }

        [RelayCommand(CanExecute = nameof(CanClose))]
        private void Close()
        {
            if (Selected?.Input is not { } input) return;
            Roots.Remove(input);
            allDiagnostics.RemoveAll(d => d.Node?.Input == input || d.Source == input.Title && d.Node is null);
            RefreshDiagnostics();
            Selected = null;
        }

        private bool CanClose() => Selected is not null;

        private void Report(DiagnosticEntry entry)
        {
            allDiagnostics.Add(entry);
            if (Shows(entry)) Diagnostics.Add(entry);
        }

        private bool Shows(DiagnosticEntry entry) => Filter switch
        {
            DiagnosticFilter.Errors => entry.Diagnostic.Severity == DiagnosticSeverity.Error,
            DiagnosticFilter.WarningsAndErrors => entry.Diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning,
            _ => true,
        };

        private void RefreshDiagnostics()
        {
            Diagnostics.Clear();
            foreach (var entry in allDiagnostics.Where(Shows)) Diagnostics.Add(entry);
        }

        partial void OnFilterChanged(DiagnosticFilter value) => RefreshDiagnostics();

        partial void OnSelectedChanged(NodeViewModel? value) => Details = DetailsViewModel.For(value);

        // Selecting a diagnostic shows its node: its ancestors open and it becomes the selection.
        partial void OnSelectedDiagnosticChanged(DiagnosticEntry? value)
        {
            if (value?.Node is not { } node) return;
            for (var at = node.Parent; at is not null; at = at.Parent) at.IsExpanded = true;
            Selected = node;
        }
    }
}
