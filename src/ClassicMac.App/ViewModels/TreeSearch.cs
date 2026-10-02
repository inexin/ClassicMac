using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    // A row's part in the tree's filter and type-ahead (design/boards/browse-tree.md, S6).
    public abstract partial class NodeViewModel
    {
        /// <summary>Whether the filter hides the row: neither it, a node above it nor one below it matches.</summary>
        [ObservableProperty]
        private bool isFilteredOut;

        /// <summary>Whether the row is dimmed: the type-ahead is open and the row does not match.</summary>
        [ObservableProperty]
        private bool isDimmed;

        /// <summary>Whether the row is the type-ahead's current match.</summary>
        [ObservableProperty]
        private bool isCurrentMatch;

        /// <summary>Where the filter or type-ahead text is in <see cref="Name"/> (start, length), or null.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(NameBefore), nameof(NameMatch), nameof(NameAfter), nameof(HasMatch))]
        private (int Start, int Length)? match;

        /// <summary>Whether the filter or type-ahead text is in the name (its letters are highlighted).</summary>
        public bool HasMatch => Match is not null;

        /// <summary>The name up to the matched letters (all of it when nothing matches).</summary>
        public string NameBefore => Match is { } m && m.Start + m.Length <= Name.Length ? Name[..m.Start] : Name;

        /// <summary>The matched letters, highlighted.</summary>
        public string NameMatch => Match is { } m && m.Start + m.Length <= Name.Length ? Name.Substring(m.Start, m.Length) : "";

        /// <summary>The name after the matched letters.</summary>
        public string NameAfter => Match is { } m && m.Start + m.Length <= Name.Length ? Name[(m.Start + m.Length)..] : "";

        partial void OnAliasChanged(string? value) => OnNameChanged();

        // The name changed (an alias, the unsaved mark): its parts follow.
        internal void OnNameChanged()
        {
            OnPropertyChanged(nameof(NameBefore));
            OnPropertyChanged(nameof(NameMatch));
            OnPropertyChanged(nameof(NameAfter));
        }
    }

    // The tree's filter (Ctrl+F) and type-ahead: both match a row's shown name, ignoring case. Files in a "No name"
    // group, the "Loading…" placeholders and rows the filter hides are never matches (the group row itself matches
    // "no name"); the type-ahead looks only at loaded rows, the filter also reads the containers not yet read.
    public sealed partial class MainViewModel
    {
        private int filterVersion;
        private List<NodeViewModel> matches = [];

        /// <summary>The filter field's text; rows that do not match (nor hold or sit in a match) are hidden.</summary>
        [ObservableProperty]
        private string filterText = "";

        /// <summary>The containers the filter is reading (tests wait for it).</summary>
        internal Task FilterTask { get; private set; } = Task.CompletedTask;

        /// <summary>What has been typed into the tree; empty when the type-ahead is closed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTypeAheadOpen))]
        private string typeAheadText = "";

        public bool IsTypeAheadOpen => TypeAheadText.Length > 0;

        /// <summary>How many loaded rows match the typed text.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TypeAheadSummary))]
        private int matchCount;

        /// <summary>The current match's place among them (from 1); 0 when there is none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TypeAheadSummary))]
        private int matchNumber;

        /// <summary>The pill's count: "1 of 2 loaded matches".</summary>
        public string TypeAheadSummary => MatchCount == 0
            ? "No loaded matches"
            : string.Create(CultureInfo.InvariantCulture, $"{MatchNumber} of {MatchCount} loaded {(MatchCount == 1 ? "match" : "matches")}");

        partial void OnFilterTextChanged(string value)
        {
            ReapplySearch();
            var version = ++filterVersion;
            FilterTask = value.Trim().Length > 0 ? ReadUnreadContainersAsync(version) : Task.CompletedTask;
        }

        [RelayCommand]
        private void ClearFilter() => FilterText = "";

        /// <summary>Adds typed text to the type-ahead and goes to the first match at or after the selection.</summary>
        public void TypeAhead(string text)
        {
            TypeAheadText += text;
            FindMatches(fromSelection: true);
        }

        /// <summary>Removes the last typed character; the type-ahead closes when nothing is left.</summary>
        public void TypeAheadBackspace()
        {
            if (TypeAheadText.Length == 0)
            {
                return;
            }
            TypeAheadText = TypeAheadText[..^1];
            if (TypeAheadText.Length == 0)
            {
                ClearTypeAhead();
                return;
            }
            FindMatches(fromSelection: true);
        }

        /// <summary>F3: the next match, wrapping round.</summary>
        [RelayCommand]
        private void NextMatch() => Step(1);

        /// <summary>Shift+F3: the previous match, wrapping round.</summary>
        [RelayCommand]
        private void PreviousMatch() => Step(-1);

        /// <summary>Esc: closes the type-ahead; the filter's highlights come back.</summary>
        [RelayCommand]
        private void ClearTypeAhead()
        {
            TypeAheadText = "";
            matches = [];
            MatchCount = 0;
            MatchNumber = 0;
            ReapplySearch();
        }

        private void Step(int by)
        {
            if (matches.Count == 0)
            {
                return;
            }
            var at = matches.IndexOf(Selected!);
            var next = at < 0 ? 0 : (at + by + matches.Count) % matches.Count;
            GoTo(next);
        }

        // Matches the typed text against the loaded rows and goes to the first match at or after the selection.
        private void FindMatches(bool fromSelection)
        {
            var rows = SearchableRows().ToList();
            matches = rows.Where(r => IndexIn(r.Name, TypeAheadText) >= 0).ToList();
            var matched = matches.ToHashSet();
            MatchCount = matches.Count;
            foreach (var row in AllRows())
            {
                var index = IndexIn(row.Name, TypeAheadText);
                var searchable = index >= 0 && matched.Contains(row);
                row.Match = searchable ? (index, TypeAheadText.Length) : null;
                row.IsDimmed = !searchable;
                row.IsCurrentMatch = false;
            }
            if (matches.Count == 0)
            {
                MatchNumber = 0;
                return;
            }
            var start = fromSelection && Selected is { } selected ? rows.IndexOf(selected) : -1;
            var first = start < 0 ? 0 : matches.FindIndex(m => rows.IndexOf(m) >= start);
            GoTo(first < 0 ? 0 : first);
        }

        private void GoTo(int index)
        {
            var node = matches[index];
            foreach (var row in matches)
            {
                row.IsCurrentMatch = ReferenceEquals(row, node);
            }
            MatchNumber = index + 1;
            for (var at = node.Parent; at is not null; at = at.Parent)
            {
                at.IsExpanded = true;
            }
            Selected = node;
            if (ReferenceEquals(Selected, node))
            {
                ItemShown?.Invoke(node);
            }
        }

        // The rows a search can find, in the tree's order: every loaded row the filter shows, but the placeholders and
        // the files in a "No name" group.
        private IEnumerable<NodeViewModel> SearchableRows() => AllRows().Where(r => Searchable(r) && !r.IsFilteredOut);

        private static bool Searchable(NodeViewModel row) => row is not LoadingNode && row.Parent is not NoNameGroupNode;

        private IEnumerable<NodeViewModel> AllRows()
        {
            IEnumerable<NodeViewModel> Below(NodeViewModel node) => node.Children.SelectMany(c => Below(c).Prepend(c));
            return Roots.SelectMany(r => Below(r).Prepend(r));
        }

        private static int IndexIn(string name, string text) =>
            text.Length == 0 ? -1 : name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase);

        // The filter and the highlights laid on the tree as it is now (after a change to the filter or the tree).
        private void ReapplySearch()
        {
            var text = FilterText.Trim();
            foreach (var root in Roots)
            {
                FilterRows(root, text, aboveMatches: false);
            }
            if (IsTypeAheadOpen)
            {
                FindMatches(fromSelection: false);
            }
        }

        // Shows a row when it, a row above it or a row below it matches, opening the rows that hold a match; true when
        // it or a row below it matches.
        private bool FilterRows(NodeViewModel node, string text, bool aboveMatches)
        {
            var index = Searchable(node) ? IndexIn(node.Name, text) : -1;
            var below = false;
            foreach (var child in node.Children)
            {
                below |= FilterRows(child, text, aboveMatches || index >= 0);
            }
            node.IsFilteredOut = text.Length > 0 && index < 0 && !below && !aboveMatches;
            node.Match = index >= 0 ? (index, text.Length) : null;
            node.IsDimmed = false;
            node.IsCurrentMatch = false;
            if (below && index < 0)
            {
                node.IsExpanded = true;
            }
            return index >= 0 || below;
        }

        // The filter reads the containers not yet read (one level at a time, until none is left or the filter changes),
        // so what they hold can match.
        private async Task ReadUnreadContainersAsync(int version)
        {
            var tried = new HashSet<ContainerFileNode>();
            while (version == filterVersion)
            {
                var unread = AllRows().OfType<ContainerFileNode>().Where(c => c.IsUnread && tried.Add(c)).ToList();
                if (unread.Count == 0)
                {
                    return;
                }
                foreach (var container in unread)
                {
                    try
                    {
                        await container.EnsureLoadedAsync();
                    }
                    catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException)
                    {
                        // Unreadable: it stays as it is (opening it reports why).
                    }
                    if (version != filterVersion)
                    {
                        return;
                    }
                }
                ReapplySearch();
            }
        }
    }
}
