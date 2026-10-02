using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
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

    /// <summary>An image of the preview at the chosen zoom.</summary>
    public sealed record ImageItem(PreviewImage Image, double Width, double Height);

    /// <summary>Picks files to open; the window provides it, tests replace it.</summary>
    public interface IFilePicker
    {
        Task<IReadOnlyList<string>> PickFilesAsync();

        /// <summary>A folder to export into, or null when cancelled.</summary>
        Task<string?> PickFolderAsync(string title);

        /// <summary>
        /// A file to save as, offering <paramref name="extensions"/> (with the dot) as file types, or null when cancelled.
        /// </summary>
        Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions);
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
        [NotifyCanExecuteChangedFor(nameof(CloseCommand), nameof(SaveResourceAsCommand), nameof(ExportResourcesCommand), nameof(ExtractAllCommand), nameof(UnpackAppleDoubleCommand), nameof(UnpackBasiliskCommand))]
        private NodeViewModel? selected;

        [ObservableProperty]
        private DetailsViewModel details = DetailsViewModel.Empty;

        [ObservableProperty]
        private DiagnosticEntry? selectedDiagnostic;

        [ObservableProperty]
        private DiagnosticFilter filter = DiagnosticFilter.All;

        [ObservableProperty]
        private string status = "Open a Mac file, disk image or resource fork (File ▸ Open, or drop it here).";

        private CancellationTokenSource? previewing;

        public IReadOnlyList<int> ScreenDepths { get; } = [1, 2, 4, 8, 16, 32];

        public IReadOnlyList<int> Zooms { get; } = [1, 2, 4, 8];

        [ObservableProperty]
        private int screenDepth = 32;

        [ObservableProperty]
        private int zoom = 1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PlaySoundCommand))]
        private PreviewViewModel preview = PreviewViewModel.None;

        [ObservableProperty]
        private IReadOnlyList<ImageItem> images = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasHex))]
        private HexViewModel hex = HexViewModel.Empty;

        /// <summary>
        /// Whether the Hex tab shows: for a resource with no preview (an unknown type), or while bytes are edited there.
        /// </summary>
        public bool HasHex => Hex.Sources.Count > 0 || IsHexEditing;

        [ObservableProperty]
        private HexSource? hexSource;

        [ObservableProperty]
        private HexLines? hexLines;

        /// <summary>The tab shown: 0 details, 1 preview, 2 hex.</summary>
        [ObservableProperty]
        private int selectedTab;

        /// <summary>The preview being made for the selection (tests wait for it).</summary>
        internal Task PreviewTask { get; private set; } = Task.CompletedTask;

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
                    // One level: the containers inside (archives, disk images on a disk) are read when expanded.
                    var tree = ContainerUnwrapper.Default.Unwrap(hostFile.File, HostFiles.FormatName(hostFile.Layout),
                        context.For(null, HostFiles.Siblings(path, ContainerOptions, diagnostics)), levels: 1);
                    return (hostFile, tree);
                });
                var input = new InputNode(path, host, root, ContainerOptions, ReadOptions, Report);
                Roots.Add(input);
                foreach (var d in diagnostics) Report(new DiagnosticEntry(d, Tree.SourceOf(input, d), input));
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
        private async Task Close()
        {
            if (Selected?.Input is not { } input) return;
            if (!await ConfirmCloseAsync([input])) return;
            RemoveInput(input);
        }

        private void RemoveInput(InputNode input)
        {
            Roots.Remove(input);
            allDiagnostics.RemoveAll(d => d.Node?.Input == input || d.Source == input.BaseTitle && d.Node is null);
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

        partial void OnSelectedChanged(NodeViewModel? value)
        {
            var pendingHexEdit = TakeHexEdit();
            Details = DetailsViewModel.For(value);
            // The hex view comes once the preview is known: only a resource without one shows its bytes.
            Hex = HexViewModel.Empty;
            HexSource = null;
            PreviewTask = MakePreviewAsync(value);
            if (pendingHexEdit is { } pending) CommitHexEdit(pending, value);
        }

        partial void OnScreenDepthChanged(int value) => PreviewTask = MakePreviewAsync(Selected);

        partial void OnZoomChanged(int value) => Images = ItemsAt(Preview, value);

        partial void OnPreviewChanged(PreviewViewModel value) => Images = ItemsAt(value, Zoom);

        partial void OnHexSourceChanged(HexSource? value) => HexLines = value is null ? null : new HexLines(value.Data);

        private static IReadOnlyList<ImageItem> ItemsAt(PreviewViewModel preview, int zoom) =>
            preview.Images.Select(i => new ImageItem(i, i.Width * zoom, i.Height * zoom)).ToList();

        // Decodes the selection's preview off the UI thread; a newer selection cancels an older one.
        private async Task MakePreviewAsync(NodeViewModel? node)
        {
            previewing?.Cancel();
            var cancellation = previewing = new CancellationTokenSource();
            if (node is null)
            {
                Preview = PreviewViewModel.None;
                return;
            }
            Preview = PreviewViewModel.Loading;
            var diagnostics = new List<Diagnostic>();
            PreviewViewModel result;
            try
            {
                // An unread container is read first, as expanding it does, so its contents show.
                if (node is ContainerFileNode { Node.UnreadFormat: not null } unread) await unread.EnsureLoadedAsync();
                if (cancellation.IsCancellationRequested) return;
                result = await PreviewViewModel.BuildAsync(node, DecodeOptions.Default with { ScreenDepth = ScreenDepth, QuickDraw = ReadOptions.ResourceManager },
                    ReadOptions, diagnostics, cancellation.Token,
                    node is ResourceNode { Resource.Type: var type } && type.ToString() is "DLOG" or "ALRT" or "DITL"
                        || node is FolderNode or InputNode or ContainerFileNode ? DialogSources.From(Roots) : null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or NotSupportedException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "preview.failed", e.Message));
                result = PreviewViewModel.None;
            }
            if (cancellation.IsCancellationRequested) return;
            foreach (var d in diagnostics) Report(new DiagnosticEntry(d, node.Source, node));
            // Small images (icons, patterns) open enlarged.
            if (result.Kind == PreviewKind.Image) Zoom = result.Images[0] is { Width: <= 64, Height: <= 64 } ? (result.Images.Any(i => i.Width > 256) ? 2 : 4) : 1;
            else if (result.Kind is PreviewKind.Dialog or PreviewKind.Menu) Zoom = 2;
            else if (result.Kind == PreviewKind.Folder) Zoom = 1;
            Preview = result;
            if (result.HasPreview)
            {
                SelectedTab = 1;
            }
            else if (node is ResourceNode unknown)
            {
                Hex = HexViewModel.For(unknown);
                HexSource = Hex.Sources.FirstOrDefault();
                SelectedTab = 2;
            }
            else if (SelectedTab is 1 or 2)
            {
                SelectedTab = 0;
            }
        }

        // Selecting a diagnostic shows its node: its ancestors open and it becomes the selection.
        partial void OnSelectedDiagnosticChanged(DiagnosticEntry? value)
        {
            if (value?.Node is not { } node) return;
            for (var at = node.Parent; at is not null; at = at.Parent) at.IsExpanded = true;
            Selected = node;
        }
    }
}
