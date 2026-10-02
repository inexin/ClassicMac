using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Templates;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A resource shown through a <c>TMPL</c>: one row per field, lists with their items, as ResEdit's
    /// template editor shows it.</summary>
    public sealed class TemplateForm : ResourceForm
    {
        private readonly ResourceTemplate template;
        private readonly ReadOnlyMemory<byte> extra;

        public TemplateForm(Resource resource, ResourceTemplate template, string source, ReadOnlySpan<byte> data) : base(resource)
        {
            this.template = template;
            Source = source;
            if (template.Problems.Count > 0)
            {
                Note = "The template cannot be used: " + string.Join(" ", template.Problems);
                return;
            }
            var read = template.Read(data);
            extra = read.Extra;
            Fields = TemplateRows.Make(read.Values, 0);
            WatchRows(Fields);
            var notes = new List<string>();
            if (read.MissingBytes > 0)
            {
                notes.Add($"The data is {read.MissingBytes} byte{(read.MissingBytes == 1 ? "" : "s")} shorter than the template; Apply adds them as zeros.");
            }

            if (read.Extra.Length > 0)
            {
                notes.Add($"{read.Extra.Length} byte{(read.Extra.Length == 1 ? "" : "s")} after the template's last field are kept.");
            }

            Note = notes.Count > 0 ? string.Join(" ", notes) : null;
        }

        // Every field's value, and every list's items (added or removed, and their own fields), count as edits.
        private void WatchRows(ObservableCollection<TemplateRow> rows)
        {
            foreach (var row in rows)
            {
                row.PropertyChanged += (_, _) => RaiseEdited();
                if (row is not TemplateListRow list)
                {
                    continue;
                }

                foreach (var item in list.Items)
                {
                    WatchRows(item.Fields);
                }

                list.Items.CollectionChanged += (_, e) =>
                {
                    foreach (var item in e.NewItems?.OfType<TemplateItemRow>() ?? [])
                    {
                        WatchRows(item.Fields);
                    }

                    RaiseEdited();
                };
            }
        }

        /// <summary>Where the template comes from ("TMPL 'DLOG' in ResEdit").</summary>
        public string Source { get; }

        /// <summary>What does not fit the template, or why it cannot be used.</summary>
        public string? Note { get; }

        public bool HasNote => Note is not null;

        public bool IsUsable => template.Problems.Count == 0;

        public ObservableCollection<TemplateRow> Fields { get; } = [];

        public override byte[] BuildData()
        {
            if (!IsUsable)
            {
                throw new ArgumentException(Note);
            }

            return template.Write(TemplateRows.Values(Fields), extra.Span);
        }
    }

    /// <summary>One field of a <see cref="TemplateForm"/>.</summary>
    public abstract class TemplateRow(TemplateNode node) : ObservableObject
    {
        public TemplateNode Node { get; } = node;

        public string Label => Node.Label.Length > 0 ? Node.Label : Node.Type;

        public string Type => Node.Type;

        public abstract TemplateValue ToValue();
    }

    /// <summary>A number, string, hex or flag field.</summary>
    public sealed partial class TemplateScalarRow(TemplateNode node, string text) : TemplateRow(node)
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Flag))]
        private string text = text;

        public bool IsFlag => Node.IsFlag;

        public bool IsText => !Node.IsFlag;

        /// <summary>A count, kept in step with its list's items.</summary>
        public bool IsReadOnly => Node.IsCount;

        public bool Flag
        {
            get => Text == "1";
            set => Text = value ? "1" : "0";
        }

        public override TemplateValue ToValue() => new TemplateScalar(Node, Text);
    }

    /// <summary>A list field and its items.</summary>
    public sealed partial class TemplateListRow : TemplateRow
    {
        private readonly TemplateScalarRow? count;
        private readonly int depth;

        public TemplateListRow(TemplateNode node, IEnumerable<IReadOnlyList<TemplateValue>> items, TemplateScalarRow? count, int depth) : base(node)
        {
            this.count = count;
            this.depth = depth;
            foreach (var item in items)
            {
                Items.Add(new TemplateItemRow(this, TemplateRows.Make(item, depth + 1)));
            }

            Items.CollectionChanged += (_, _) => Renumber();
            Renumber();
        }

        public ObservableCollection<TemplateItemRow> Items { get; } = [];

        public string Summary => Items.Count == 1 ? "1 item" : $"{Items.Count} items";

        [RelayCommand]
        private void Add() => Items.Add(new TemplateItemRow(this, TemplateRows.Make(ResourceTemplate.NewItem(Node), depth + 1)));

        [RelayCommand]
        private void Remove(TemplateItemRow item) => Items.Remove(item);

        [RelayCommand]
        private void Insert(TemplateItemRow item) =>
            Items.Insert(Items.IndexOf(item), new TemplateItemRow(this, TemplateRows.Make(ResourceTemplate.NewItem(Node), depth + 1)));

        private void Renumber()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Number = i + 1;
            }

            if (count is not null)
            {
                count.Text = (count.Node.Type == "ZCNT" ? (Items.Count - 1) & 0xFFFF : Items.Count).ToString(CultureInfo.InvariantCulture);
            }

            OnPropertyChanged(nameof(Summary));
        }

        public override TemplateValue ToValue() => new TemplateList(Node, Items.Select(i => (IReadOnlyList<TemplateValue>)TemplateRows.Values(i.Fields)).ToList());
    }

    /// <summary>One item of a list.</summary>
    public sealed partial class TemplateItemRow(TemplateListRow list, ObservableCollection<TemplateRow> fields) : ObservableObject
    {
        public TemplateListRow List { get; } = list;

        public ObservableCollection<TemplateRow> Fields { get; } = fields;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Title))]
        private int number;

        public string Title => $"{Number})";
    }

    internal static class TemplateRows
    {
        public static ObservableCollection<TemplateRow> Make(IReadOnlyList<TemplateValue> values, int depth)
        {
            var rows = new ObservableCollection<TemplateRow>();
            TemplateScalarRow? lastCount = null;
            foreach (var value in values)
            {
                switch (value)
                {
                    case TemplateList list:
                        rows.Add(new TemplateListRow(list.Node, list.Items, list.Node.Type == "LSTC" ? lastCount : null, depth));
                        lastCount = null;
                        break;
                    case TemplateScalar scalar:
                        var row = new TemplateScalarRow(scalar.Node, scalar.Text);
                        lastCount = scalar.Node.IsCount ? row : null;
                        rows.Add(row);
                        break;
                }
            }
            return rows;
        }

        public static List<TemplateValue> Values(IEnumerable<TemplateRow> rows) => rows.Select(r => r.ToValue()).ToList();
    }

    public sealed partial class MainViewModel
    {
        // The TMPL for a type, as ResEdit finds one: in the resource's own file, then in the other open files whose
        // resources are loaded; null when none has one.
        private (ResourceTemplate Template, string Source)? FindTemplate(FourCC type, ResourceFork own, NodeViewModel? ownFile)
        {
            var forks = new List<(ResourceFork Fork, string Name)> { (own, ownFile?.BaseTitle ?? "this file") };
            foreach (var root in Roots)
            {
                foreach (var node in Loaded(root))
                {
                    if (!forks.Any(f => ReferenceEquals(f.Fork, node.Fork)))
                    {
                        forks.Add(node);
                    }
                }
            }

            foreach (var (fork, name) in forks)
            {
                if (ResourceTemplate.Find(fork, type) is not { } tmpl)
                {
                    continue;
                }

                try
                {
                    var data = ResourceDecompression.Default.GetData(tmpl, fork, ReadOptions, []);
                    return (ResourceTemplate.Parse(data.Span), $"Template: TMPL {tmpl.Id} “{type}” in {name}");
                }
                catch (InvalidDataException)
                {
                    continue;
                }
            }
            return null;
        }

        // A template form for a resource without a typed form, when a TMPL for its type is at hand.
        private TemplateForm? TemplateFormFor(ResourceNode node, NodeViewModel owner)
        {
            if (FindTemplate(node.Resource.Type, node.Fork, owner) is not { } found)
            {
                return null;
            }

            var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, ReadOptions, []);
            return new TemplateForm(node.Resource, found.Template, found.Source, data.Span);
        }

        private static IEnumerable<(ResourceFork Fork, string Name)> Loaded(NodeViewModel node)
        {
            if (node is FileNode file)
            {
                if (file.Resources?.Fork is { } fork)
                {
                    yield return (fork, node.BaseTitle);
                }

                yield break;
            }
            if (node is ResourceTypeNode or ResourceNode)
            {
                yield break;
            }

            if (node is InputNode { RawResources.Fork: { } raw })
            {
                yield return (raw, node.BaseTitle);
            }

            foreach (var child in Tree.Contents(node))
            {
                foreach (var found in Loaded(child))
                {
                    yield return found;
                }
            }
        }
    }
}
