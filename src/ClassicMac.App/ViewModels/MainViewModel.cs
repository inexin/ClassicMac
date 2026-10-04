using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

namespace ClassicMac.App.ViewModels;

/// <summary>A diagnostic with where it came from, and the tree node it belongs to when there is one.</summary>
public sealed record DiagnosticEntry(Diagnostic Diagnostic, string Source, NodeViewModel? Node)
{
    public string Severity => Diagnostic.Severity.ToString();

    public string Code => Diagnostic.Code;

    public string Message => Diagnostic.Message;

    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;

    public bool IsInfo => Diagnostic.Severity == DiagnosticSeverity.Info;

    /// <summary>Whether it belongs to a tree node, which "Show item" can show.</summary>
    public bool HasNode => Node is not null;
}

/// <summary>Which diagnostics the list shows.</summary>
public enum DiagnosticFilter
{
    All,
    WarningsAndErrors,
    Errors,
}

/// <summary>
/// An image of the preview at the chosen zoom: its size at 100% display scaling (the view draws it in whole device
/// pixels per Mac pixel at any scaling).
/// </summary>
public sealed record ImageItem(PreviewImage Image, double Width, double Height, int Zoom);

/// <summary>Picks files to open; the window provides it, tests replace it.</summary>
public interface IFilePicker
{
    Task<IReadOnlyList<string>> PickFilesAsync();

    /// <summary>One file to open, titled <paramref name="title"/> and offering <paramref name="extensions"/> (with the dot), or null when cancelled.</summary>
    async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions) => (await PickFilesAsync()).FirstOrDefault();

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
    private ExportActions? exportActions;

    /// <summary>Save Resource As, Export, Extract All, Convert Documents and Unpack.</summary>
    public ExportActions ExportActions => exportActions ??= new(this);

    private InspectorActions? inspectorActions;

    /// <summary>The inspector's header: the selection's title, icon, kind and actions.</summary>
    public InspectorActions InspectorActions => inspectorActions ??= new(this);

    private FormEditing? formEditing;

    /// <summary>The read-then-edit host: Edit, Apply and Cancel for the selection's form, and its footer.</summary>
    public FormEditing FormEditing => formEditing ??= new(this);

    private TreeSearch? treeSearch;

    /// <summary>The tree's filter and type-ahead, and Show item.</summary>
    public TreeSearch TreeSearch => treeSearch ??= new(this);

    private ShellActions? shellActions;

    /// <summary>The shell: the title, zoom and screen depth, the theme, the Window menu and About.</summary>
    public ShellActions ShellActions => shellActions ??= new(this);

    private SoundPlayback? soundPlayback;

    /// <summary>The sound preview's transport: play, stop, the playhead and the loop.</summary>
    public SoundPlayback SoundPlayback => soundPlayback ??= new(this);

    private TypeCreatorActions? typeCreatorActions;

    /// <summary>The type and creator database: its entry for the selection, and forgetting it.</summary>
    public TypeCreatorActions TypeCreatorActions => typeCreatorActions ??= new(this);

    private EmptyState? emptyState;

    /// <summary>The empty state shown while nothing is open, with the files opened last.</summary>
    public EmptyState EmptyState => emptyState ??= new(this);

    private VolumeActions? volumeActions;

    /// <summary>New file, import, new folder and delete in a volume.</summary>
    public VolumeActions VolumeActions => volumeActions ??= new(this);

    private HelpPreview? helpPreview;

    /// <summary>The help page's Rendered | Source switch and its links.</summary>
    public HelpPreview HelpPreview => helpPreview ??= new(this);

    private ImportActions? importActions;

    /// <summary>Resource ▸ Import: an image or WAV file made into a resource.</summary>
    public ImportActions ImportActions => importActions ??= new(this);

    private HexFind? hexFind;

    /// <summary>Find in the Hex tab.</summary>
    public HexFind HexFind => hexFind ??= new(this);

    private StatusLine? statusLine;

    /// <summary>The status bar: the selected input's summary, the work in progress and the status text.</summary>
    public StatusLine StatusLine => statusLine ??= new(this);

    private Drafts? drafts;

    /// <summary>Unapplied edits: asked about before the selection moves, an undo or redo, or a close.</summary>
    public Drafts Drafts => drafts ??= new(this);

    private DragOut? dragOut;

    /// <summary>Drag and drop out of the tree, through a temporary folder.</summary>
    public DragOut DragOut => dragOut ??= new(this);

    private AliasActions? aliasActions;

    /// <summary>The selected alias: its original, Show Original and the not-found card.</summary>
    public AliasActions AliasActions => aliasActions ??= new(this);

    private SoundHeaderActions? soundHeaderActions;

    /// <summary>A sound resource's Save as WAV and Replace from WAV.</summary>
    public SoundHeaderActions SoundHeaderActions => soundHeaderActions ??= new(this);

    private ImageGrid? imageGrid;

    /// <summary>The image grid of an image resource's preview: its items, masks, Finder states and layout.</summary>
    public ImageGrid ImageGrid => imageGrid ??= new(this);

    private PropertyLinks? propertyLinks;

    /// <summary>The Properties tab's links and copy actions.</summary>
    public PropertyLinks PropertyLinks => propertyLinks ??= new(this);

    // For the window's parts: raises PropertyChanged for one of this view model's properties.
    internal void RaisePropertyChanged(string name) => OnPropertyChanged(name);

    private DetailsActions? detailsActions;

    /// <summary>The Details tab's actions: Copy all, the File card's In link, the chain card's link to problems.</summary>
    public DetailsActions DetailsActions => detailsActions ??= new(this);

    internal readonly ISettingsStore settings;

    public MainViewModel() : this(new MemorySettingsStore())
    {
    }

    /// <summary>A model whose settings (the tree's display options) are read from and saved to <paramref name="settings"/>.</summary>
    public MainViewModel(ISettingsStore settings)
    {
        this.settings = settings;
        DiagnosticsPanel = new DiagnosticsPanel(entry => SelectedDiagnostic = entry);
        var saved = settings.Load();
        TreeDisplay = new TreeDisplayOptions { GroupNoName = saved.GroupNoName, HideInvisible = saved.HideInvisible, ShowDetails = saved.ShowDetails };
        TreeDisplay.Inputs = () => Roots.OfType<InputNode>();
        TreeDisplay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeDisplayOptions.ShowDetails))
            {
                OnShowDetailsChanged();
            }
            else
            {
                OnTreeDisplayChanged();
            }
        };
        TreeDisplay.LaidOut += UpdateHiddenCount;
        StatusLine.WatchSummary();
        TreeDisplay.LaidOut += TreeSearch.ReapplySearch;
        EmptyState.InitRecentFiles(saved.RecentFiles);
        TypeCreatorActions.InitTypeCreatorDatabase(saved.TypeCreatorDatabase);
    }

    // What the app remembers: the display options and the recent files, over what else is stored (the theme).
    internal void SaveSettings() =>
        settings.Save(settings.Load() with
        {
            GroupNoName = TreeDisplay.GroupNoName,
            HideInvisible = TreeDisplay.HideInvisible,
            ShowDetails = TreeDisplay.ShowDetails,
            RecentFiles = EmptyState.RecentFiles.Select(r => r.Path).ToList(),
        });

    /// <summary>Which files the tree hides or groups.</summary>
    public TreeDisplayOptions TreeDisplay { get; }

    /// <summary>How many invisible files the tree hides.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenSummary))]
    private int hiddenCount;

    /// <summary>The tree's footer ("2 invisible items hidden"), or null when nothing is hidden.</summary>
    public string? HiddenSummary => HiddenCount switch
    {
        0 => null,
        1 => "1 invisible item hidden",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} invisible items hidden"),
    };

    /// <summary>The footer's Show: invisible files are shown.</summary>
    [RelayCommand]
    private void ShowHidden() => TreeDisplay.HideInvisible = false;

    private void UpdateHiddenCount() => HiddenCount = Roots.Sum(Tree.HiddenCount);

    // The details column switched: saved, and every row's meta shown or hidden.
    private void OnShowDetailsChanged()
    {
        SaveSettings();
        foreach (var row in TreeSearch.AllRows())
        {
            row.OnMetaChanged();
        }
    }

    // An option changed: it is saved and every tree laid out again; the selection stays, or moves to its folder
    // when it is now hidden.
    private void OnTreeDisplayChanged()
    {
        SaveSettings();
        var kept = Selected;
        foreach (var root in Roots)
        {
            Tree.Relayout(root);
        }

        UpdateHiddenCount();
        if (kept is null)
        {
            return;
        }

        var shown = kept;
        while (shown is not null && !Tree.IsShown(shown, Roots))
        {
            shown = shown.Parent;
        }

        if (!ReferenceEquals(Selected, shown))
        {
            Selected = shown;
        }
    }

    public ObservableCollection<InputNode> Roots { get; } = [];

    /// <summary>The tree's visible rows, flattened for the view's one virtualized list.</summary>
    public VisibleRows TreeRows => treeRows ??= new VisibleRows(Roots);

    private VisibleRows? treeRows;

    /// <summary>The diagnostics panel: counts, filters, grouping by file, collapsing.</summary>
    public DiagnosticsPanel DiagnosticsPanel { get; }

    /// <summary>The diagnostics the panel's filters let through, in arrival order.</summary>
    public ObservableCollection<DiagnosticEntry> Diagnostics => DiagnosticsPanel.Entries;

    /// <summary>The panel's severity filter.</summary>
    public DiagnosticFilter Filter
    {
        get => DiagnosticsPanel.Filter;
        set => DiagnosticsPanel.Filter = value;
    }

    public IFilePicker? FilePicker { get; set; }

    public ContainerReadOptions ContainerOptions { get; init; } = ContainerReadOptions.Default;

    public ReadOptions ReadOptions { get; init; } = ReadOptions.Default;

    private NodeViewModel? selected;

    /// <summary>
    /// The selected node. While a form or the hex view holds unapplied edits, a new selection asks what to do with
    /// them first (<see cref="ResolveDraftAsync"/>) and is made only once they are applied or discarded: until then
    /// (and when cancelled) the old node stays selected and nothing is rebuilt.
    /// </summary>
    public NodeViewModel? Selected
    {
        get => selected;
        set
        {
            if (ReferenceEquals(selected, value))
            {
                return;
            }

            if (Drafts.askingDraft || Drafts.HasDraft)
            {
                if (!Drafts.askingDraft)
                {
                    DraftTask = Drafts.SelectAfterDraftAsync(value);
                }
                // The tree (bound two-way) already shows the new node: told again, it shows the kept one.
                if (!ReferenceEquals(selected, value))
                {
                    Drafts.Refuse(nameof(Selected));
                }

                return;
            }
            var old = selected;
            OnPropertyChanging(nameof(Selected));
            selected = value;
            OnPropertyChanged(nameof(Selected));
            foreach (var command in new IRelayCommand[] { CloseCommand, ExportActions.SaveResourceAsCommand, ExportActions.ExportResourcesCommand, ExportActions.ExtractAllCommand, ExportActions.UnpackAppleDoubleCommand,
                ExportActions.UnpackBasiliskCommand })
            {
                command.NotifyCanExecuteChanged();
            }

            OnSelectedChanged(value);
            OnSelectedChanged(old, value);
        }
    }

    /// <summary>The last selection or template switch made after asking about a draft (tests wait for it).</summary>
    internal Task DraftTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private DetailsViewModel details = DetailsViewModel.Empty;

    // The Details tab's actions follow its content.
    partial void OnDetailsChanged(DetailsViewModel value)
    {
        DetailsActions.CopyDetailsCommand.NotifyCanExecuteChanged();
        DetailsActions.GoToInCommand.NotifyCanExecuteChanged();
        DetailsActions.ShowProblemsCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private DiagnosticEntry? selectedDiagnostic;

    [ObservableProperty]
    private string status = "Ready";

    private CancellationTokenSource? previewing;

    public IReadOnlyList<int> ScreenDepths { get; } = [1, 2, 4, 8, 16, 32];

    public IReadOnlyList<int> Zooms { get; } = [1, 2, 4, 8];

    [ObservableProperty]
    private int screenDepth = 32;

    [ObservableProperty]
    private int zoom = 1;

    [ObservableProperty]
    private PreviewViewModel preview = PreviewViewModel.None;

    [ObservableProperty]
    private IReadOnlyList<ImageItem> images = [];

    partial void OnImagesChanged(IReadOnlyList<ImageItem> value) => ImageGrid.ImagesChanged();

    partial void OnFormErrorChanged(string? value) => ApplyFormCommand.NotifyCanExecuteChanged();

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
        var reading = StatusLine.BeginProgress($"Reading {Path.GetFileName(path)}…", 0);
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
            var input = new InputNode(path, host, root, ContainerOptions, ReadOptions, Report, TreeDisplay);
            Roots.Add(input);
            UpdateHiddenCount();
            EmptyState.AddRecent(path);
            TreeSearch.ReapplySearch();
            foreach (var d in diagnostics)
            {
                Report(new DiagnosticEntry(d, Tree.SourceOf(input, d), input));
            }

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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ReportUnexpected(e, $"Opening {Path.GetFileName(path)}");
            return null;
        }
        finally
        {
            reading.Finish(null);
        }
    }

    /// <summary>
    /// The last resort for an exception nothing else handles (a reader's bug on a damaged file, a failure in an async
    /// event handler): shown as an error diagnostic and in the status line, so the app goes on.
    /// </summary>
    public void ReportUnexpected(Exception exception, string during)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "app.unexpected-error",
            $"{exception.GetType().Name}: {exception.Message} (a ClassicMac error; please report it)"), during, null));
        Status = $"{during} failed: {exception.Message}";
    }

    [RelayCommand]
    private async Task Open()
    {
        if (FilePicker is null)
        {
            return;
        }

        foreach (var path in await FilePicker.PickFilesAsync())
        {
            await OpenAsync(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task Close()
    {
        if (Selected?.Input is not { } input)
        {
            return;
        }

        if (!await ConfirmCloseAsync([input]))
        {
            return;
        }

        RemoveInput(input);
    }

    private void RemoveInput(InputNode input)
    {
        Roots.Remove(input);
        DiagnosticsPanel.RemoveAll(d => d.Node?.Input == input || d.Source == input.BaseTitle && d.Node is null);
        UpdateHiddenCount();
        Selected = null;
    }

    private bool CanClose() => Selected is not null;

    internal void Report(DiagnosticEntry entry) => DiagnosticsPanel.Add(entry);

    private void OnSelectedChanged(NodeViewModel? value)
    {
        TakeHexEdit();                       // unchanged bytes (changed ones were applied or discarded before the move)
        AliasActions.SelectedAlias = Aliases.Of(value, Roots);
        Details = DetailsViewModel.For(value, DetailsActions.ProblemsIn(value), AliasActions.SelectedAlias);
        InspectorActions.OnSelectionChangedForInspector();
        // The hex view comes once the preview is known: only a resource without one shows its bytes.
        Hex = HexViewModel.Empty;
        HexSource = null;
        // An alias previews its original (with a strip above); one whose original is not found shows a card instead.
        PreviewTask = MakePreviewAsync(AliasActions.SelectedAlias is { } alias ? alias.Target : value);
    }

    partial void OnScreenDepthChanged(int value)
    {
        ShellActions.ViewChanged();
        PreviewTask = MakePreviewAsync(Selected);
    }

    partial void OnZoomChanged(int value)
    {
        ShellActions.ViewChanged();
        Images = ItemsAt(Preview, value);
    }

    partial void OnPreviewChanging(PreviewViewModel value) => SoundPlayback.OnPreviewChanging();

    partial void OnPreviewChanged(PreviewViewModel value)
    {
        SoundPlayback.PlaySoundCommand.NotifyCanExecuteChanged();
        ShellActions.ViewChanged();
        Images = ItemsAt(value, Zoom);
        SoundPlayback.OnSoundPreviewChanged();
        PropertyLinks.OnPropertyPreviewChanged();
        HelpPreview.OnHelpPreviewChanged();
    }

    partial void OnHexSourceChanged(HexSource? value) => HexLines = value is null ? null : new HexLines(value.Data);

    // The cards: each image at the zoom, with its mask after it when "Show masks" is on.
    internal IReadOnlyList<ImageItem> ItemsAt(PreviewViewModel preview, int zoom) =>
        preview.Images.SelectMany(i => ImageGrid.ShowMasks ? preview.Masks.Where(m => m.Title == i.Title + " mask").Prepend(i) : [i])
            .Select(i => new ImageItem(i, i.Width * zoom, i.Height * zoom, zoom)).ToList();

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
            if (node is ContainerFileNode { Node.UnreadFormat: not null } unread)
            {
                await unread.EnsureLoadedAsync();
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            result = await PreviewViewModel.BuildAsync(node, DecodeOptions.Default with { ScreenDepth = ScreenDepth, QuickDraw = ReadOptions.ResourceManager },
                ReadOptions, diagnostics, cancellation.Token,
                node is ResourceNode { Resource.Type: var type } && type.ToString() is "DLOG" or "ALRT" or "DITL"
                    || node is FolderNode or InputNode or ContainerFileNode or NoNameGroupNode ? DialogSources.From(Roots) : null);
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
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        foreach (var d in diagnostics)
        {
            Report(new DiagnosticEntry(d, node.Source, node));
        }
        // Small images (icons, patterns) open enlarged.
        if (result.Kind == PreviewKind.Image)
        {
            Zoom = result.Images[0] is { Width: <= 64, Height: <= 64 } ? (result.Images.Any(i => i.Width > 256) ? 2 : 4) : 1;
        }
        else if (result.Kind is PreviewKind.Dialog or PreviewKind.Menu)
        {
            Zoom = 2;
        }
        else if (result.Kind == PreviewKind.Folder)
        {
            Zoom = 1;
        }

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
        if (value?.Node is not { } node)
            return;
        for (var at = node.Parent; at is not null; at = at.Parent)
            at.IsExpanded = true;
        Selected = node;
    }

    /// <summary>Raised when "Show item" has selected a node: the view brings its tree row into view.</summary>
    public event Action<NodeViewModel>? ItemShown;

    internal void RaiseItemShown(NodeViewModel node) => ItemShown?.Invoke(node);

    /// <summary>
    /// "Show item" on a diagnostic's row: selects its node as a row click does (its ancestors open, and unapplied
    /// edits are asked about first), then has the view show it in the tree. Nothing is shown when the user cancels.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowItem))]
    private async Task ShowItem(DiagnosticEntry? entry)
    {
        if (entry?.Node is not { } node)
        {
            return;
        }

        var before = Selected;
        for (var at = node.Parent; at is not null; at = at.Parent)
        {
            at.IsExpanded = true;
        }

        Selected = node;
        await DraftTask;
        if (Selected is not { } shown || ReferenceEquals(shown, before) && !ReferenceEquals(before, node))
        {
            return;
        }

        ItemShown?.Invoke(shown);
    }

    private static bool CanShowItem(DiagnosticEntry? entry) => entry?.Node is not null;
}
