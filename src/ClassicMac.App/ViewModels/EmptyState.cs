using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A file in the empty state's Recent list: its path, and whether it was there when the list was shown.</summary>
public sealed record RecentFile(string Path, bool Exists)
{
    private static readonly HashSet<string> DiskImages =
        new([".img", ".image", ".dsk", ".hfv", ".hda", ".dmg", ".iso", ".toast", ".cdr", ".smi", ".dc42", ".2mg"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Archives =
        new([".sit", ".sea", ".sitx", ".cpt", ".pit", ".zip", ".lha", ".lzh", ".gz", ".tar"], StringComparer.OrdinalIgnoreCase);

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>
    /// The kind's icon, from the name's extension alone (the file is not read): a hard disk for disk images, a parcel
    /// for archives, else a document [ClassicMac].
    /// </summary>
    public TreeIconKind IconKind => System.IO.Path.GetExtension(Path) switch
    {
        var e when DiskImages.Contains(e) => TreeIconKind.HardDisk,
        var e when Archives.Contains(e) => TreeIconKind.Parcel,
        _ => TreeIconKind.Document,
    };
}

// The empty state (design/boards/empty-state.md, S5): shown while nothing is open, with the files opened last.
public sealed partial class EmptyState(IAppSelection appSelection, IAppServices appServices) : ObservableObject
{
    private const int RecentLimit = 10;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>The files opened last, newest first (at most ten), kept in the settings.</summary>
    public ObservableCollection<RecentFile> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>Whether nothing is open: the inspector shows the drop zone and the Recent list.</summary>
    public bool IsEmpty => appSelection.Roots.Count == 0;

    /// <summary>Whether files are being dragged over the window (the drop zone is marked).</summary>
    [ObservableProperty]
    private bool isDropTarget;

    // Whether the empty state collapsed the diagnostics panel (it opens it again when a file opens, unless the user
    // opened or closed it since); set while the panel is changed here.
    private bool panelCollapsedForEmpty;
    private bool changingPanel;

    // Called once from the constructor: the list from the settings, and the empty state following the open inputs.
    internal void InitRecentFiles(IReadOnlyList<string> paths)
    {
        ShowRecent(paths);
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        appSelection.Roots.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            if (IsEmpty)
            {
                // Back to the empty state: whether each file is still there is looked at again.
                ShowRecent(RecentFiles.Select(r => r.Path).ToList());
                EnterEmptyState();
            }
            else
            {
                LeaveEmptyState();
            }
        };
        appServices.DiagnosticsPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiagnosticsPanel.IsExpanded) && !changingPanel)
            {
                panelCollapsedForEmpty = false;
            }
        };
        appServices.DiagnosticsPanel.Entries.CollectionChanged += (_, _) =>
        {
            if (appServices.DiagnosticsPanel.Entries.Count > 0 && appServices.DiagnosticsPanel.Placeholder is not null)
            {
                LeaveEmptyState();
            }
        };
        EnterEmptyState();
    }

    // Nothing open (board: empty-state.md): the status says "Ready"; with nothing to report, the diagnostics panel
    // collapses to its header, which says "Nothing opened yet".
    private void EnterEmptyState()
    {
        appServices.Status = "Ready";
        if (appServices.DiagnosticsPanel.Entries.Count > 0)
        {
            return;
        }
        appServices.DiagnosticsPanel.Placeholder = "Nothing opened yet";
        if (appServices.DiagnosticsPanel.IsExpanded)
        {
            SetPanelExpanded(false);
            panelCollapsedForEmpty = true;
        }
    }

    // Something opened (or reported): the placeholder goes, and a panel the empty state collapsed opens again.
    private void LeaveEmptyState()
    {
        appServices.DiagnosticsPanel.Placeholder = null;
        if (panelCollapsedForEmpty)
        {
            SetPanelExpanded(true);
            panelCollapsedForEmpty = false;
        }
    }

    private void SetPanelExpanded(bool expanded)
    {
        changingPanel = true;
        try
        {
            appServices.DiagnosticsPanel.IsExpanded = expanded;
        }
        finally
        {
            changingPanel = false;
        }
    }

    private void ShowRecent(IEnumerable<string> paths)
    {
        var files = paths.Take(RecentLimit).Select(p => new RecentFile(p, File.Exists(p))).ToList();
        RecentFiles.Clear();
        foreach (var file in files)
        {
            RecentFiles.Add(file);
        }
    }

    // A file read: it goes to the top of the list (once), which keeps ten.
    internal void AddRecent(string path)
    {
        var full = Path.GetFullPath(path);
        var paths = RecentFiles.Select(r => r.Path).Where(p => !PathComparer.Equals(p, full)).Prepend(full).ToList();
        ShowRecent(paths);
        appServices.SaveSettings();
    }

    /// <summary>Opens a recent file; one no longer there is removed from the list instead.</summary>
    [RelayCommand]
    private async Task OpenRecent(RecentFile? file)
    {
        if (file is null)
        {
            return;
        }
        if (!File.Exists(file.Path))
        {
            RecentFiles.Remove(file);
            appServices.SaveSettings();
            appServices.Status = $"{file.Name} was not found; it is removed from the recent files.";
            return;
        }
        await appSelection.OpenAsync(file.Path);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        appServices.SaveSettings();
    }
}
