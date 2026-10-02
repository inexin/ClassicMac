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
    /// <summary>A line of the template panel's field list: the field, indented by its list nesting.</summary>
    public sealed record TemplateOutlineLine(int Depth, string Label, string Type)
    {
        /// <summary>The indent in DIPs: 16 per level.</summary>
        public int Indent => Depth * 16;
    }

    /// <summary>A resource shown through a <c>TMPL</c>: one row per field, lists with their items, as ResEdit's
    /// template editor shows it. On the read-then-edit host (design/boards/template-form.md, E6): values read only, inputs
    /// while editing; counts always read only, kept in step with their lists.</summary>
    public sealed class TemplateForm : ResourceForm
    {
        /// <summary>Which template is used when several open files hold one (<see cref="MainViewModel"/>'s FindTemplate).</summary>
        public static string SourceRule =>
            "The resource’s own file is searched first, then the other open files whose resources are read, in the order they were opened; the first 'TMPL' named for the type is used.";

        private readonly ResourceTemplate template;
        private readonly ReadOnlyMemory<byte> extra;

        public TemplateForm(Resource resource, ResourceTemplate template, string source, ReadOnlySpan<byte> data) : base(resource)
        {
            this.template = template;
            Source = source;
            Outline = MakeOutline(template);
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IsEditing))
                {
                    TemplateRows.SetEditing(Fields, IsEditing);
                }
            };
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

        public override bool HasReadOnlyView => true;

        public override string EditHint => "Counts follow their lists. Esc cancels, Ctrl+Enter applies.";

        /// <summary>The template's fields, indented by nesting, for the template panel.</summary>
        public IReadOnlyList<TemplateOutlineLine> Outline { get; }

        private static List<TemplateOutlineLine> MakeOutline(ResourceTemplate template)
        {
            var lines = new List<TemplateOutlineLine>();
            var depth = 0;
            foreach (var field in template.Fields)
            {
                if (field.Type == "LSTE")
                {
                    depth = Math.Max(0, depth - 1);
                }

                lines.Add(new TemplateOutlineLine(depth, field.Label, field.Type));
                if (field.Type is "LSTB" or "LSTC" or "LSTZ")
                {
                    depth++;
                }
            }

            return lines;
        }

        // Every field's value, and every list's items (added or removed, and their own fields), count as edits; the
        // rows' mode and display texts do not.
        private void WatchRows(ObservableCollection<TemplateRow> rows)
        {
            foreach (var row in rows)
            {
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TemplateScalarRow.Text))
                    {
                        RaiseEdited();
                    }
                };
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

        /// <summary>The header's fact: "'TMPL' 1000 “BNDL” in ResEdit"; null when not known.</summary>
        public string? ShownThrough { get; init; }

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
    public abstract partial class TemplateRow(TemplateNode node) : ObservableObject
    {
        public TemplateNode Node { get; } = node;

        /// <summary>Whether the host is editing the form (inputs) or showing it read only; set through the form.</summary>
        [ObservableProperty]
        private bool isEditing;

        public string Label => Node.Label.Length > 0 ? Node.Label : Node.Type;

        public string Type => Node.Type;

        public abstract TemplateValue ToValue();
    }

    /// <summary>A number, string, hex or flag field.</summary>
    public sealed partial class TemplateScalarRow(TemplateNode node, string text) : TemplateRow(node)
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Flag), nameof(DisplayText))]
        private string text = text;

        /// <summary>The value read only: four-character codes quoted, flags as Yes or No.</summary>
        public string DisplayText => Node.IsFlag ? (Flag ? "Yes" : "No") : Node.Type == "TNAM" ? $"'{Text}'" : Text;

        /// <summary>Whether editing shows a text input: text fields other than counts.</summary>
        public bool IsEditableText => IsText && !IsReadOnly;

        /// <summary>A four-character code (shown quoted, in mono).</summary>
        public bool IsCode => Node.Type == "TNAM";

        /// <summary>A count's note; null for other fields.</summary>
        public string? CountNote => IsReadOnly ? "kept in step with the list" : null;

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

            Items.CollectionChanged += (_, e) =>
            {
                // New items take the list's mode.
                foreach (var item in e.NewItems?.OfType<TemplateItemRow>() ?? [])
                {
                    TemplateRows.SetEditing(item.Fields, IsEditing);
                }

                Renumber();
            };
            Renumber();
        }

        public ObservableCollection<TemplateItemRow> Items { get; } = [];

        /// <summary>The list's heading: its label, or its count's label for a "*****" list ("Number of types"), else "Items".</summary>
        public string Heading => !Node.Label.All(c => c == '*') && Node.Label.Length > 0 ? Node.Label : count?.Label ?? "Items";

        /// <summary>The add button's label, from the list's own items: "Add Type".</summary>
        public string AddLabel => Node.Children.FirstOrDefault(c => !c.IsCount && !c.IsList && !c.IsHidden) is { } first && first.Label.Length > 0
            ? $"Add {first.Label}"
            : "Add item";

        /// <summary>A list inside a list's item: shown as a compact table.</summary>
        public bool IsCompact => depth > 0;

        /// <summary>The compact table's columns: the item's fields.</summary>
        public IReadOnlyList<string> Columns => [.. Node.Children.Where(c => !c.IsList && !c.IsHidden).Select(c => c.Label.Length > 0 ? c.Label : c.Type)];

        // The mode passes on to the items' fields.
        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(IsEditing))
            {
                foreach (var item in Items)
                {
                    TemplateRows.SetEditing(item.Fields, IsEditing);
                }
            }
        }

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
    public sealed partial class TemplateItemRow : ObservableObject
    {
        public TemplateItemRow(TemplateListRow list, ObservableCollection<TemplateRow> fields)
        {
            List = list;
            Fields = fields;
            foreach (var field in fields)
            {
                field.PropertyChanged += (_, e) =>
                {
                    if (field is TemplateListRow && e.PropertyName == nameof(TemplateListRow.Summary))
                    {
                        OnPropertyChanged(nameof(ItemSummary));
                    }
                    else if (ReferenceEquals(field, KeyField) && e.PropertyName == nameof(TemplateScalarRow.DisplayText))
                    {
                        OnPropertyChanged(nameof(KeyText));
                    }
                };
            }
        }

        public TemplateListRow List { get; }

        public ObservableCollection<TemplateRow> Fields { get; }

        private TemplateScalarRow? KeyField => Fields.OfType<TemplateScalarRow>().FirstOrDefault(f => !f.IsReadOnly);

        /// <summary>The item's key field, shown in its card's header ("'ICN#'").</summary>
        public string? KeyText => KeyField?.DisplayText;

        /// <summary>Its lists' sizes, in the card's header ("3 items"); null without lists.</summary>
        public string? ItemSummary => Fields.OfType<TemplateListRow>().Select(l => l.Summary).ToList() is { Count: > 0 } summaries
            ? string.Join(" · ", summaries)
            : null;

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

        // The host's mode, to every row (a list passes it on to its items).
        public static void SetEditing(IEnumerable<TemplateRow> rows, bool editing)
        {
            foreach (var row in rows)
            {
                row.IsEditing = editing;
            }
        }
    }

    public sealed partial class MainViewModel
    {
        // The TMPL for a type, as ResEdit finds one: in the resource's own file, then in the other open files whose
        // resources are loaded; else ClassicMac's built-in template for the type; null when there is none.
        private (ResourceTemplate Template, string Source, string ShownThrough)? FindTemplate(FourCC type, ResourceFork own, NodeViewModel? ownFile)
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
                    return (ResourceTemplate.Parse(data.Span), $"Template: TMPL {tmpl.Id} “{type}” in {name}", $"'TMPL' {tmpl.Id} “{type}” in {name}");
                }
                catch (InvalidDataException)
                {
                    continue;
                }
            }
            return BuiltInTemplates.For(type) is { } builtIn ? (builtIn.Template, $"Template: built in, from {builtIn.Source}", $"ClassicMac's built-in '{type}' template") : null;
        }

        /// <summary>
        /// What the byte at <paramref name="offset"/> of a resource's data (the bytes the hex view shows) means, with the
        /// field's range, for the hex inspector (design/boards/hex.md E8): <c>'STR '</c> and <c>'STR#'</c> by their
        /// layout, other types through a <c>TMPL</c> found as the template form finds one; null for other types, for a
        /// compressed resource (the hex view shows its compressed bytes) and outside the data.
        /// </summary>
        public ByteMeaning? MeaningAt(ResourceNode node, int offset)
        {
            ArgumentNullException.ThrowIfNull(node);
            return MeaningsFor(node) is { } meanings ? meanings(node.Resource.GetData().ToArray(), offset) : null;
        }

        /// <summary>
        /// The meanings of a resource's bytes, for any bytes of it (the hex editor's as edited): its template found once;
        /// null for a compressed resource.
        /// </summary>
        internal Func<byte[], int, ByteMeaning?>? MeaningsFor(ResourceNode node)
        {
            if ((node.Resource.Attributes & ResourceAttributes.Compressed) != 0)
            {
                return null;
            }

            var type = node.Resource.Type;
            var template = type.ToString() is "STR " or "STR#" ? null : FindTemplate(type, node.Fork, FileOwner(node))?.Template;
            return (data, offset) => ClassicMac.Resources.Decoders.Templates.ByteMeanings.MeaningAt(type, data, offset, template);
        }

        // A template form for a resource without a typed form, when a template for its type is at hand.
        private TemplateForm? TemplateFormFor(ResourceNode node, NodeViewModel owner)
        {
            if (FindTemplate(node.Resource.Type, node.Fork, owner) is not { } found)
            {
                return null;
            }

            var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, ReadOptions, []);
            return new TemplateForm(node.Resource, found.Template, found.Source, data.Span) { ShownThrough = found.ShownThrough };
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
