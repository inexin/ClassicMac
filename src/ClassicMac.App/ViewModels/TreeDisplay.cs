using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>The tree's display options (Tree display popover); both on by default and kept between sessions.</summary>
    public sealed partial class TreeDisplayOptions : ObservableObject
    {
        /// <summary>Whether a folder's files with no name (2 or more) are grouped under one "No name" node.</summary>
        [ObservableProperty]
        private bool groupNoName = true;

        /// <summary>Whether files with the Finder's invisible flag are left out of the tree (folders never are).</summary>
        [ObservableProperty]
        private bool hideInvisible = true;

        /// <summary>Raised after a part of the tree was laid out (files read or changed).</summary>
        internal event Action? LaidOut;

        internal void RaiseLaidOut() => LaidOut?.Invoke();
    }

    // The tree's display of a folder's items: invisible files hidden (T1), files with no name grouped (T2). Only the
    // tree changes; Items keeps every file for exports, previews and the volume commands.
    internal static partial class Tree
    {
        /// <summary>The folder (or input or container file) a node is in: its parent, past a "No name" group.</summary>
        public static NodeViewModel? FolderOf(NodeViewModel node) => node.Parent is NoNameGroupNode group ? group.Parent : node.Parent;

        /// <summary>Everything a node holds, shown or not: its items when it has them, else its children.</summary>
        public static IEnumerable<NodeViewModel> Contents(NodeViewModel node) => node.Items ?? (IEnumerable<NodeViewModel>)node.Children;

        /// <summary>Whether a name is empty or only whitespace: space, option-space ($CA), tab, return and other control characters.</summary>
        public static bool HasNoName(MacString name)
        {
            foreach (var b in name.Bytes)
                if (b is > 0x20 and not 0x7F and not 0xCA) return false;
            return true;
        }

        /// <summary>A name with its whitespace made visible: ␣ space, ⍽ option-space, ↵ return, control pictures for the other control characters.</summary>
        public static string VisibleName(MacString name)
        {
            if (name.Bytes.Length == 0) return "(empty)";
            var text = new StringBuilder();
            foreach (var b in name.Bytes)
            {
                text.Append(b switch
                {
                    0x20 => "␣",
                    0xCA => "⍽",
                    0x0D => "↵",
                    0x7F => "␡",
                    < 0x20 => ((char)(0x2400 + b)).ToString(),
                    _ => MacRoman.Decode([b]),
                });
            }
            return text.ToString();
        }

        private static MacFile? FileOf(NodeViewModel node) => node switch
        {
            FileNode f => f.File,
            ContainerFileNode c => c.File,
            _ => null,
        };

        private static bool IsInvisibleFile(NodeViewModel node) => FileOf(node) is { } file && (file.FinderInfo.Flags & FinderFlags.IsInvisible) != 0;

        private static bool IsNoNameFile(NodeViewModel node) => FileOf(node) is { } file && HasNoName(file.Name);

        /// <summary>Lays out a node and everything below it, then tells the options' listeners.</summary>
        public static void Relayout(NodeViewModel node)
        {
            Layout(node);
            node.Input.Display.RaiseLaidOut();
        }

        // Shows a node's items: invisible files left out, files with no name grouped first among its files (2 or more)
        // or titled "(no name)"; the nodes are kept, so their expansion and the selection stay.
        private static void Layout(NodeViewModel node)
        {
            if (node.Items is not { } items) return;
            var display = node.Input.Display;
            var shown = items.Where(i => !(display.HideInvisible && IsInvisibleFile(i))).ToList();
            var noName = shown.Where(IsNoNameFile).ToList();
            var grouped = display.GroupNoName && noName.Count >= 2;
            foreach (var item in items.Where(i => FileOf(i) is not null))
            {
                item.Parent = node;
                (item.Alias, item.IsItalic, item.IsMono) = (null, false, false);
            }
            var group = node.Children.OfType<NoNameGroupNode>().FirstOrDefault();
            List<NodeViewModel> desired;
            if (grouped)
            {
                group ??= new NoNameGroupNode(node);
                foreach (var file in noName)
                {
                    file.Parent = group;
                    (file.Alias, file.IsMono) = (VisibleName(FileOf(file)!.Name), true);
                }
                desired = shown.Except(noName).ToList();
                var firstFile = desired.FindIndex(i => i is not FolderNode);
                desired.Insert(firstFile < 0 ? desired.Count : firstFile, group);
                Sync(group.Children, noName);
                group.OnMetaChanged();
            }
            else
            {
                group?.Children.Clear();
                foreach (var file in noName) (file.Alias, file.IsItalic) = ("(no name)", true);
                desired = shown;
            }
            Sync(node.Children, desired);
            foreach (var item in items) Layout(item);
        }

        // Makes a collection hold the nodes wanted, in order, moving the ones it has rather than re-adding them.
        private static void Sync(ObservableCollection<NodeViewModel> target, IReadOnlyList<NodeViewModel> wanted)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
                var at = -1;
                for (var j = i + 1; j < target.Count && at < 0; j++)
                    if (ReferenceEquals(target[j], wanted[i])) at = j;
                if (at >= 0) target.Move(at, i);
                else target.Insert(i, wanted[i]);
            }
            while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
        }

        /// <summary>How many files below a node the tree hides.</summary>
        public static int HiddenCount(NodeViewModel node)
        {
            if (node.Items is not { } items) return 0;
            var own = node.Input.Display.HideInvisible ? items.Count(IsInvisibleFile) : 0;
            return own + items.Sum(HiddenCount);
        }

        /// <summary>Whether a node is in the tree as shown: each node up from it is among its parent's children.</summary>
        public static bool IsShown(NodeViewModel node, IEnumerable<NodeViewModel> roots)
        {
            var at = node;
            for (; at.Parent is { } parent; at = parent)
                if (!parent.Children.Contains(at)) return false;
            return roots.Contains(at);
        }
    }

    public sealed partial class MainViewModel
    {
        private readonly ISettingsStore settings;

        public MainViewModel() : this(new MemorySettingsStore())
        {
        }

        /// <summary>A model whose settings (the tree's display options) are read from and saved to <paramref name="settings"/>.</summary>
        public MainViewModel(ISettingsStore settings)
        {
            this.settings = settings;
            DiagnosticsPanel = new DiagnosticsPanel(entry => SelectedDiagnostic = entry);
            var saved = settings.Load();
            TreeDisplay = new TreeDisplayOptions { GroupNoName = saved.GroupNoName, HideInvisible = saved.HideInvisible };
            TreeDisplay.PropertyChanged += (_, _) => OnTreeDisplayChanged();
            TreeDisplay.LaidOut += UpdateHiddenCount;
        }

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

        // An option changed: it is saved and every tree laid out again; the selection stays, or moves to its folder
        // when it is now hidden.
        private void OnTreeDisplayChanged()
        {
            settings.Save(new AppSettings(TreeDisplay.GroupNoName, TreeDisplay.HideInvisible));
            var kept = Selected;
            foreach (var root in Roots) Tree.Relayout(root);
            UpdateHiddenCount();
            if (kept is null) return;
            var shown = kept;
            while (shown is not null && !Tree.IsShown(shown, Roots)) shown = shown.Parent;
            if (!ReferenceEquals(Selected, shown)) Selected = shown;
        }
    }
}
