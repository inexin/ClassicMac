using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClassicMac.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>How the diagnostics are ordered: as they arrived, or by severity.</summary>
public enum SeveritySort
{
    None,
    ErrorsFirst,
    InfoFirst,
}

/// <summary>The diagnostics of one file (the node path without a resource), with its counts; a header row.</summary>
public sealed partial class DiagnosticGroup : ObservableObject
{
    private readonly List<DiagnosticEntry> entries = [];

    internal DiagnosticGroup(string path, NodeKind kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    /// <summary>The file's kind, for its icon.</summary>
    public NodeKind Kind { get; }

    /// <summary>The entries the filters let through, in display order.</summary>
    public IReadOnlyList<DiagnosticEntry> Entries => entries;

    /// <summary>Open: its entries show under it. Info-only groups start closed until the user opens one.</summary>
    [ObservableProperty]
    private bool isExpanded;

    /// <summary>Opened or closed by the user, so no longer following <see cref="IsInfoOnly"/>.</summary>
    internal bool Toggled { get; set; }

    public bool IsInfoOnly => entries.Count > 0 && entries.All(e => e.Diagnostic.Severity == DiagnosticSeverity.Info);

    /// <summary>"1 error · 2 info".</summary>
    public string Counts => string.Join(" · ", new[]
    {
        DiagnosticsPanel.Count(entries.Count(e => e.Diagnostic.Severity == DiagnosticSeverity.Error), "error", "errors"),
        DiagnosticsPanel.Count(entries.Count(e => e.Diagnostic.Severity == DiagnosticSeverity.Warning), "warning", "warnings"),
        DiagnosticsPanel.Count(entries.Count(e => e.Diagnostic.Severity == DiagnosticSeverity.Info), "info", "info"),
    }.Where(s => s is not null));

    internal List<DiagnosticEntry> Items => entries;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(IsInfoOnly));
    }
}

/// <summary>
/// The diagnostics panel (design/boards/diagnostics.md): counts, the severity filter and a text filter, sorting by
/// severity, grouping by file, collapsing to its header, and the latest problem. <see cref="Rows"/> is what the list
/// shows: group headers and entries when grouped by file, else the entries.
/// </summary>
public sealed partial class DiagnosticsPanel : ObservableObject
{
    /// <summary>The header bar's height, which is all that shows when collapsed (CmPanelHeader).</summary>
    public const double HeaderHeight = 34;

    /// <summary>The expanded height until the splitter moves (CmDiagnosticsHeight).</summary>
    public const double DefaultHeight = 196;

    private readonly List<DiagnosticEntry> all = [];

    /// <summary>Every diagnostic, whatever the filters.</summary>
    internal IReadOnlyList<DiagnosticEntry> All => all;
    private readonly Dictionary<string, DiagnosticGroup> groupsByPath = new(StringComparer.Ordinal);
    private readonly List<DiagnosticGroup> groups = [];
    private readonly Action<DiagnosticEntry>? select;

    public DiagnosticsPanel(Action<DiagnosticEntry>? select = null) => this.select = select;

    /// <summary>The entries the filters let through, in arrival order.</summary>
    public ObservableCollection<DiagnosticEntry> Entries { get; } = [];

    /// <summary>The list's rows: <see cref="DiagnosticGroup"/> headers and <see cref="DiagnosticEntry"/> rows.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>Every group holding an entry the filters let through, in order of their first diagnostic.</summary>
    public IReadOnlyList<DiagnosticGroup> Groups => groups.Where(g => g.Items.Count > 0).ToList();

    public IReadOnlyList<DiagnosticFilter> Filters { get; } = Enum.GetValues<DiagnosticFilter>();

    [ObservableProperty]
    private DiagnosticFilter filter = DiagnosticFilter.All;

    /// <summary>Text that a message, code or source must contain (ignoring case); empty for all.</summary>
    [ObservableProperty]
    private string search = "";

    [ObservableProperty]
    private bool byFile = true;

    [ObservableProperty]
    private SeveritySort sort = SeveritySort.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelHeight))]
    private bool isExpanded = true;

    /// <summary>What the collapsed header says when there is nothing to report ("Nothing opened yet"), or null.</summary>
    [ObservableProperty]
    private string? placeholder;

    private double expandedHeight = DefaultHeight;

    /// <summary>The panel's height: the remembered one when expanded, the header's when collapsed.</summary>
    public double PanelHeight
    {
        get => IsExpanded ? expandedHeight : HeaderHeight;
        set
        {
            if (!IsExpanded)
            {
                return;
            }

            var height = Math.Max(HeaderHeight, value);
            if (height == expandedHeight)
            {
                return;
            }

            expandedHeight = height;
            OnPropertyChanged();
        }
    }

    private object? selectedRow;

    /// <summary>The selected row: an entry selects its diagnostic; a group header opens or closes the group.</summary>
    public object? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (value is DiagnosticGroup group)
            {
                group.Toggled = true;
                SetExpanded(group, !group.IsExpanded);
                selectedRow = null;
                OnPropertyChanged();
                return;
            }
            if (!SetProperty(ref selectedRow, value))
            {
                return;
            }

            if (value is DiagnosticEntry entry)
            {
                select?.Invoke(entry);
            }
        }
    }

    // By severity (Info, Warning, Error), kept as diagnostics come and go.
    private readonly int[] counts = new int[3];

    public int ErrorCount => counts[(int)DiagnosticSeverity.Error];

    public int WarningCount => counts[(int)DiagnosticSeverity.Warning];

    public int InfoCount => counts[(int)DiagnosticSeverity.Info];

    public bool HasErrors => ErrorCount > 0;

    public bool HasWarnings => WarningCount > 0;

    public bool HasInfos => InfoCount > 0;

    public string ErrorText => Count(ErrorCount, "error", "errors") ?? "0 errors";

    public string WarningText => Count(WarningCount, "warning", "warnings") ?? "0 warnings";

    public string InfoText => Count(InfoCount, "info", "info") ?? "0 info";

    /// <summary>The newest error or warning, whatever the filters.</summary>
    public DiagnosticEntry? Latest { get; private set; }

    public bool HasLatest => Latest is not null;

    /// <summary>"in &lt;source&gt;", after the latest problem's code.</summary>
    public string LatestPlace => Latest is { } latest ? $"in {latest.Source}" : "";

    internal static string? Count(int count, string one, string many) => count == 0 ? null : $"{count} {(count == 1 ? one : many)}";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SortBySeverity() => Sort = Sort switch
    {
        SeveritySort.None => SeveritySort.ErrorsFirst,
        SeveritySort.ErrorsFirst => SeveritySort.InfoFirst,
        _ => SeveritySort.None,
    };

    public void Add(DiagnosticEntry entry)
    {
        all.Add(entry);
        counts[(int)entry.Diagnostic.Severity]++;
        if (entry.Diagnostic.Severity != DiagnosticSeverity.Info)
        {
            Latest = entry;
        }

        CountsChanged(entry.Diagnostic.Severity);
        if (!Shows(entry))
        {
            return;
        }

        Entries.Add(entry);
        if (!ByFile)
        {
            if (Sort == SeveritySort.None)
            {
                Rows.Add(entry);
            }
            else
            {
                Rows.Insert(SortedIndex(Rows.Cast<DiagnosticEntry>().ToList(), entry), entry);
            }

            return;
        }
        var group = GroupFor(entry);
        var wasShown = group.Items.Count > 0;
        var wasExpanded = group.IsExpanded;
        var at = SortedIndex(group.Items, entry);
        group.Items.Insert(at, entry);
        group.Changed();
        if (!wasShown)
        {
            // A new header goes after the groups that come before it.
            var header = RowIndexAfterGroupsBefore(group);
            Rows.Insert(header, group);
            if (!group.Toggled)
            {
                group.IsExpanded = !group.IsInfoOnly;
            }

            if (group.IsExpanded)
            {
                Rows.Insert(header + 1, entry);
            }

            return;
        }
        if (!group.Toggled && group.IsExpanded != !group.IsInfoOnly)
        {
            SetExpanded(group, !group.IsInfoOnly);
            return;
        }
        if (wasExpanded)
        {
            Rows.Insert(Rows.IndexOf(group) + 1 + at, entry);
        }
    }

    /// <summary>The errors and warnings among all entries (filtered or not) that <paramref name="match"/> takes.</summary>
    public (int Errors, int Warnings) CountsFor(Func<DiagnosticEntry, bool> match)
    {
        var taken = all.Where(match).ToList();
        return (taken.Count(e => e.IsError), taken.Count(e => e.IsWarning));
    }

    public void RemoveAll(Predicate<DiagnosticEntry> match)
    {
        if (all.RemoveAll(match) == 0)
        {
            return;
        }

        Array.Clear(counts);
        foreach (var e in all)
        {
            counts[(int)e.Diagnostic.Severity]++;
        }

        Latest = all.LastOrDefault(e => e.Diagnostic.Severity != DiagnosticSeverity.Info);
        CountsChanged(null);
        Refresh();
    }

    /// <summary>Which group an entry belongs to: its file (a resource's file), or the nested file it is about.</summary>
    public static (string Path, NodeKind Kind) GroupOf(DiagnosticEntry entry)
    {
        var at = entry.Node;
        while (at is ResourceNode or ResourceTypeNode or LoadingNode)
        {
            at = at.Parent;
        }

        return at is null ? (entry.Source, NodeKind.Input) : GroupOf(entry.Source, entry.Node!.Kind, at.Source, at.Kind);
    }

    /// <summary>
    /// The group of a diagnostic at <paramref name="source"/> reported on a node of <paramref name="nodeKind"/> whose
    /// file is at <paramref name="fileSource"/>: a resource's go under the file; one about a nested file (a longer
    /// source) under that file.
    /// </summary>
    public static (string Path, NodeKind Kind) GroupOf(string source, NodeKind nodeKind, string fileSource, NodeKind fileKind) =>
        nodeKind is NodeKind.Resource or NodeKind.ResourceType or NodeKind.Loading ? (fileSource, fileKind)
        : source == fileSource ? (source, fileKind)
        : (source, NodeKind.File);

    partial void OnFilterChanged(DiagnosticFilter value) => Refresh();

    partial void OnSearchChanged(string value) => Refresh();

    partial void OnByFileChanged(bool value) => Refresh();

    partial void OnSortChanged(SeveritySort value) => Refresh();

    private bool Shows(DiagnosticEntry entry) =>
        Filter switch
        {
            DiagnosticFilter.Errors => entry.Diagnostic.Severity == DiagnosticSeverity.Error,
            DiagnosticFilter.WarningsAndErrors => entry.Diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning,
            _ => true,
        }
        && (string.IsNullOrEmpty(Search)
            || entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || entry.Code.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(Search, StringComparison.OrdinalIgnoreCase));

    private int Rank(DiagnosticEntry entry) => Sort switch
    {
        SeveritySort.ErrorsFirst => -(int)entry.Diagnostic.Severity,
        SeveritySort.InfoFirst => (int)entry.Diagnostic.Severity,
        _ => 0,
    };

    // After the last entry that ranks with or before it (arrival order among equals).
    private int SortedIndex(IReadOnlyList<DiagnosticEntry> list, DiagnosticEntry entry)
    {
        if (Sort == SeveritySort.None)
        {
            return list.Count;
        }

        var rank = Rank(entry);
        var at = list.Count;
        while (at > 0 && Rank(list[at - 1]) > rank)
        {
            at--;
        }

        return at;
    }

    private DiagnosticGroup GroupFor(DiagnosticEntry entry)
    {
        var (path, kind) = GroupOf(entry);
        if (!groupsByPath.TryGetValue(path, out var group))
        {
            groupsByPath[path] = group = new DiagnosticGroup(path, kind);
            groups.Add(group);
        }
        return group;
    }

    private int RowIndexAfterGroupsBefore(DiagnosticGroup group)
    {
        var index = 0;
        foreach (var g in groups)
        {
            if (g == group)
            {
                break;
            }

            if (g.Items.Count == 0)
            {
                continue;
            }

            index += 1 + (g.IsExpanded ? g.Items.Count : 0);
        }
        return index;
    }

    private void SetExpanded(DiagnosticGroup group, bool expanded)
    {
        if (group.IsExpanded == expanded)
        {
            return;
        }

        group.IsExpanded = expanded;
        var header = Rows.IndexOf(group);
        if (header < 0)
        {
            return;
        }

        if (expanded)
        {
            for (var i = 0; i < group.Items.Count; i++)
            {
                Rows.Insert(header + 1 + i, group.Items[i]);
            }
        }
        else
        {
            while (header + 1 < Rows.Count && Rows[header + 1] is DiagnosticEntry)
            {
                Rows.RemoveAt(header + 1);
            }
        }
    }

    // Rebuilds the entries, groups and rows from every diagnostic (a filter, the grouping or the sort changed).
    private void Refresh()
    {
        var shown = all.Where(Shows).ToList();
        Entries.Clear();
        foreach (var e in shown)
        {
            Entries.Add(e);
        }

        foreach (var g in groups)
        {
            g.Items.Clear();
        }

        Rows.Clear();
        var sorted = Sort == SeveritySort.None ? shown : shown.OrderBy(Rank).ToList(); // stable
        if (!ByFile)
        {
            foreach (var e in sorted)
            {
                Rows.Add(e);
            }

            return;
        }
        foreach (var e in sorted)
        {
            GroupFor(e).Items.Add(e);
        }
        // Groups whose diagnostics were all removed (their input closed) go.
        var paths = all.Select(e => GroupOf(e).Path).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in groups.Where(g => !paths.Contains(g.Path)).ToList())
        {
            groups.Remove(gone);
            groupsByPath.Remove(gone.Path);
        }
        foreach (var g in groups.Where(g => g.Items.Count > 0))
        {
            g.Changed();
            if (!g.Toggled)
            {
                g.IsExpanded = !g.IsInfoOnly;
            }

            Rows.Add(g);
            if (g.IsExpanded)
            {
                foreach (var e in g.Items)
                {
                    Rows.Add(e);
                }
            }
        }
    }

    private void CountsChanged(DiagnosticSeverity? severity)
    {
        if (severity is null or DiagnosticSeverity.Error)
        {
            foreach (var name in new[] { nameof(ErrorCount), nameof(ErrorText), nameof(HasErrors) })
            {
                OnPropertyChanged(name);
            }
        }

        if (severity is null or DiagnosticSeverity.Warning)
        {
            foreach (var name in new[] { nameof(WarningCount), nameof(WarningText), nameof(HasWarnings) })
            {
                OnPropertyChanged(name);
            }
        }

        if (severity is null or DiagnosticSeverity.Info)
        {
            foreach (var name in new[] { nameof(InfoCount), nameof(InfoText), nameof(HasInfos) })
            {
                OnPropertyChanged(name);
            }
        }

        if (severity != DiagnosticSeverity.Info)
        {
            foreach (var name in new[] { nameof(Latest), nameof(HasLatest), nameof(LatestPlace) })
            {
                OnPropertyChanged(name);
            }
        }
    }
}
