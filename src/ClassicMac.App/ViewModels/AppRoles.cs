using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using ClassicMac.Files;
using ClassicMac.Resources;

namespace ClassicMac.App.ViewModels;

// The roles MainViewModel plays for the main window's parts (EditActions, ExportActions, Forms …): each part takes
// only the roles it uses, so its constructor says what it depends on. MainViewModel is the one implementation.

/// <summary>The open inputs and the selection in the tree.</summary>
public interface IAppSelection : INotifyPropertyChanged
{
    /// <summary>The node selected in the tree, or null.</summary>
    NodeViewModel? Selected { get; set; }

    /// <summary>The open inputs, the tree's roots.</summary>
    ObservableCollection<InputNode> Roots { get; }

    /// <summary>Which files the tree hides or groups.</summary>
    TreeDisplayOptions TreeDisplay { get; }

    /// <summary>The last selection change held for a draft to be asked about (tests wait for it).</summary>
    Task DraftTask { get; set; }

    /// <summary>Opens a file as a new input.</summary>
    Task<InputNode?> OpenAsync(string path);

    /// <summary>Closes an input: it leaves the tree.</summary>
    void RemoveInput(InputNode input);

    /// <summary>Tells the view to bring a node "Show item" selected into view.</summary>
    void RaiseItemShown(NodeViewModel node);

    /// <summary>Raises <see cref="Selected"/>'s change again, so a view that moved its selection follows it back.</summary>
    void NotifySelectedChanged();
}

/// <summary>What the window gives every part: the status line, diagnostics, pickers, dialogs, options and settings.</summary>
public interface IAppServices
{
    /// <summary>The status text.</summary>
    string Status { get; set; }

    /// <summary>The diagnostics panel.</summary>
    DiagnosticsPanel DiagnosticsPanel { get; }

    /// <summary>The file picker, or null (no window).</summary>
    IFilePicker? FilePicker { get; }

    /// <summary>The editing dialogs, or null (no window).</summary>
    IEditDialogs? EditDialogs { get; }

    /// <summary>How resource forks are read.</summary>
    ReadOptions ReadOptions { get; }

    /// <summary>How containers are read.</summary>
    ContainerReadOptions ContainerOptions { get; }

    /// <summary>Where the app's settings are kept.</summary>
    ISettingsStore Settings { get; }

    /// <summary>Adds a diagnostic to the panel.</summary>
    void Report(DiagnosticEntry entry);

    /// <summary>Saves the display options and the recent files.</summary>
    void SaveSettings();
}

/// <summary>What the inspector shows for the selection: its preview, hex view, details, zoom, depth and tab.</summary>
public interface IAppView
{
    /// <summary>The selection's preview.</summary>
    PreviewViewModel Preview { get; }

    /// <summary>The selection's Details tab.</summary>
    DetailsViewModel Details { get; set; }

    /// <summary>The selection's hex view.</summary>
    HexViewModel Hex { get; set; }

    /// <summary>The fork the hex view shows.</summary>
    HexSource? HexSource { get; set; }

    /// <summary>The hex view's lines.</summary>
    HexLines? HexLines { get; set; }

    /// <summary>The image preview's zoom.</summary>
    int Zoom { get; set; }

    /// <summary>The zooms offered.</summary>
    IReadOnlyList<int> Zooms { get; }

    /// <summary>The screen depth previews are drawn at.</summary>
    int ScreenDepth { get; set; }

    /// <summary>The image preview's items at the zoom.</summary>
    IReadOnlyList<ImageItem> Images { get; set; }

    /// <summary>The inspector's tab: 0 Details, 1 Preview, 2 Hex.</summary>
    int SelectedTab { get; set; }

    /// <summary>A preview's images at a zoom, masks included when shown.</summary>
    IReadOnlyList<ImageItem> ItemsAt(PreviewViewModel preview, int zoom);

    /// <summary>Raises the change of whether the Hex tab shows.</summary>
    void NotifyHasHex();
}

/// <summary>The main window's other parts, for a part that works with them.</summary>
public interface IAppParts
{
    /// <summary>Editing: new, duplicate, delete, Get Info, hex editing, undo and redo, save.</summary>
    EditActions EditActions { get; }

    /// <summary>Exports and unpacking.</summary>
    ExportActions ExportActions { get; }

    /// <summary>The volume commands.</summary>
    VolumeActions VolumeActions { get; }

    /// <summary>Resource ▸ Import.</summary>
    ImportActions ImportActions { get; }

    /// <summary>Unapplied edits.</summary>
    Drafts Drafts { get; }

    /// <summary>The selection's form.</summary>
    Forms Forms { get; }

    /// <summary>The read-then-edit host.</summary>
    FormEditing FormEditing { get; }

    /// <summary>The form's error line and live preview.</summary>
    FormLivePreview FormLivePreview { get; }

    /// <summary>Finding a resource's template.</summary>
    TemplateFinder TemplateFinder { get; }

    /// <summary>The shell: title, zoom and depth, theme, Window menu.</summary>
    ShellActions ShellActions { get; }

    /// <summary>The status bar.</summary>
    StatusLine StatusLine { get; }

    /// <summary>Find in the Hex tab.</summary>
    HexFind HexFind { get; }

    /// <summary>The inspector header's actions.</summary>
    InspectorActions InspectorActions { get; }

    /// <summary>The Details tab's actions.</summary>
    DetailsActions DetailsActions { get; }

    /// <summary>The selected alias.</summary>
    AliasActions AliasActions { get; }

    /// <summary>A sound resource's actions.</summary>
    SoundHeaderActions SoundHeaderActions { get; }
}
