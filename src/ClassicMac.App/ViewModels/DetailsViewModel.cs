using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Compression;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One line of the details panel: mono for codes, paths, sizes and dates; a fork's bar (0–1) relative to the larger fork.</summary>
    public sealed record DetailRow(string Label, string Value, bool Mono = false, double? Bar = null)
    {
        /// <summary>Whether the value is a link (the File card's "In", to the folder holding the file).</summary>
        public bool Link { get; init; }

        public bool HasBar => Bar is not null;
    }

    /// <summary>A Finder flag as a chip: set ones filled, unset ones dashed and muted.</summary>
    public sealed record FinderFlagChip(string Name, bool IsSet);

    /// <summary>A step of the container chain ("Disk.img → HFS volume → Realmz · both forks"); the last one highlighted.</summary>
    public sealed record ReadStep(string Text, bool IsLast);

    /// <summary>A card of the Details tab: its rows, and the File card's flag chips, the chain or a note.</summary>
    public sealed record DetailGroup(string Title, IReadOnlyList<DetailRow> Rows, bool Wide = false)
    {
        public IReadOnlyList<FinderFlagChip> Flags { get; init; } = [];

        public IReadOnlyList<ReadStep> Chain { get; init; } = [];

        /// <summary>A note under the rows (the dates' time zone), or null.</summary>
        public string? Note { get; init; }

        /// <summary>The card's header: the title in capitals (a caption).</summary>
        public string Caption => Title.ToUpperInvariant();

        public bool HasNote => Note is not null;

        public bool HasFlags => Flags.Count > 0;

        public bool HasChain => Chain.Count > 0;
    }

    /// <summary>
    /// What the Details tab shows for the selected node (design/boards/details.md, P4): cards of rows — for a file File,
    /// Forks, Dates, Finder flags and How it was read; for inputs, resources, types and folders cards of their own — and
    /// the same rows as plain text for Copy all.
    /// </summary>
    public sealed class DetailsViewModel
    {
        private static readonly (FinderFlags Flag, string Name)[] FlagNames =
        [
            (FinderFlags.HasBundle, "Has bundle"), (FinderFlags.HasBeenInited, "Inited"), (FinderFlags.IsShared, "Shared"),
            (FinderFlags.IsInvisible, "Invisible"), (FinderFlags.None, "Locked"), (FinderFlags.HasCustomIcon, "Custom icon"),
            (FinderFlags.IsStationery, "Stationery"), (FinderFlags.IsAlias, "Alias"),
        ];

        // The Finder's default label names (Mac OS 8 and 9), by the colour bits.
        private static readonly string[] LabelNames = ["None", "Essential", "Hot", "In Progress", "Cool", "Personal", "Project 1", "Project 2"];

        private DetailsViewModel(string heading, IReadOnlyList<DetailGroup> groups, NodeViewModel? inNode = null, int problems = 0)
        {
            Heading = heading;
            Groups = groups;
            InNode = inNode;
            Problems = problems;
        }

        public static DetailsViewModel Empty { get; } = new("", []);

        public string Heading { get; }

        public IReadOnlyList<DetailGroup> Groups { get; }

        /// <summary>The cards in the two-column grid.</summary>
        public IReadOnlyList<DetailGroup> Cards => Groups.Where(g => !g.Wide).ToList();

        /// <summary>The full-width cards under them.</summary>
        public IReadOnlyList<DetailGroup> WideCards => Groups.Where(g => g.Wide).ToList();

        /// <summary>Every row, in card order (what the tests and the older views read).</summary>
        public IReadOnlyList<DetailRow> Rows => Groups.SelectMany(g => g.Rows).ToList();

        /// <summary>The node the File card's "In" leads to (the folder or volume holding the file), or null.</summary>
        public NodeViewModel? InNode { get; }

        /// <summary>The errors and warnings reported for this file (the chain card says how many).</summary>
        public int Problems { get; }

        public bool HasProblems => Problems > 0;

        public bool HasChain => Groups.Any(g => g.HasChain);

        public string ProblemsText => Problems switch
        {
            0 => "No problems found in this file",
            1 => "1 problem",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} problems"),
        };

        /// <summary>Copy all: every row as a plain "Label: value" line, the flags and the chain as one line each.</summary>
        public string CopyText
        {
            get
            {
                var lines = new List<string>();
                foreach (var group in Groups)
                {
                    lines.AddRange(group.Rows.Select(r => $"{r.Label}: {r.Value}"));
                    if (group.HasFlags)
                    {
                        var set = group.Flags.Where(f => f.IsSet).Select(f => f.Name).ToList();
                        lines.Add($"Finder flags: {(set.Count == 0 ? "none" : string.Join(", ", set))}");
                    }

                    if (group.HasNote)
                    {
                        lines.Add($"Note: {group.Note}");
                    }

                    if (group.HasChain)
                    {
                        lines.Add($"Read as: {string.Join(" → ", group.Chain.Select(s => s.Text))}");
                        lines.Add($"Problems: {ProblemsText}");
                    }
                }

                return string.Join("\n", lines);
            }
        }

        /// <summary>The details of <paramref name="node"/>, with the errors and warnings reported for it.</summary>
        public static DetailsViewModel For(NodeViewModel? node, int problems = 0) => node switch
        {
            InputNode input => Input(input, problems),
            ContainerFileNode container => File(container, container.File, container.ContentFormat, null, problems),
            FileNode file => File(file, file.File, null, file.Resources, problems),
            FolderNode folder => new DetailsViewModel(folder.Title,
                [new("Folder", [new("Kind", "Folder"), new("Items", folder.Items!.Count.ToString(CultureInfo.InvariantCulture))])]),
            NoNameGroupNode group => new DetailsViewModel(group.Title,
                [new("Files with no name", [new("Kind", "Files with no name"), new("Items", group.Children.Count.ToString(CultureInfo.InvariantCulture))])]),
            ResourceTypeNode type => Type(type),
            ResourceNode resource => Resource(resource, problems),
            _ => Empty,
        };

        private static DetailsViewModel Input(InputNode input, int problems)
        {
            var rows = new List<DetailRow>
            {
                new("Path", input.Path, Mono: true),
                new("Read as", HostFiles.FormatName(input.Host.Layout)),
            };
            rows.AddRange(input.Host.Companions.Select(c => new DetailRow("With", c, Mono: true)));
            if (input.Root.Children.Count > 0)
            {
                rows.Add(new("Holds", input.Root.Children[0].Format));
            }

            var groups = new List<DetailGroup> { new("Input", rows) };
            if (input.RawResources is { Fork: { } fork })
            {
                groups.Add(new("Forks", [
                    new("Resources", $"{fork.Resources.Count} in {fork.Types.Count} types (the file is a resource fork)"),
                    new("Holds", Holds(fork), Mono: true)]));
            }

            groups.Add(Dates(input.Root.File, DateNote(input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "")));
            groups.Add(new("How it was read", [], Wide: true) { Chain = Chain(input) });
            return new DetailsViewModel(input.Title, groups, problems: problems);
        }

        private static DetailsViewModel File(NodeViewModel node, MacFile file, string? holds, FileResources? resources, int problems)
        {
            var info = file.FinderInfo;
            var kind = InspectorHeader.For(node)?.Kind is { } k && k.IndexOf(" in ", StringComparison.Ordinal) is var at and > 0 ? k[..at] : "File";
            var parent = Tree.FolderOf(node);
            var fileRows = new List<DetailRow>
            {
                new("Name", file.Name.ToMacRoman()),
                new("Kind", kind),
                new("Type / creator", $"'{info.Type}' / '{info.Creator}'", Mono: true),
                new("Mac path", file.MacPath, Mono: true),
            };
            if (parent is not null)
            {
                fileRows.Add(new("In", Path(parent)) { Link = true });
            }

            var larger = Math.Max(1, Math.Max(file.DataFork.Length, file.ResourceFork.Length));
            var forkRows = new List<DetailRow>
            {
                new("Data fork", Bytes(file.DataFork.Length), Mono: true, Bar: (double)file.DataFork.Length / larger),
                new("Resource fork", Bytes(file.ResourceFork.Length), Mono: true, Bar: (double)file.ResourceFork.Length / larger),
            };
            if (holds is not null)
            {
                forkRows.Add(new("Holds", holds));
            }

            if (resources is { } found)
            {
                forkRows.Add(new("Resources", found.Fork is { } fork
                    ? $"{fork.Resources.Count} in {fork.Types.Count} types, from the {(found.Source == ResourceForkSource.DataFork ? "data fork" : "resource fork")}"
                    : "none"));
                if (found.Fork is { } read)
                {
                    if (Compression(read) is { } compressed)
                    {
                        forkRows.Add(new("Compressed", compressed));
                    }

                    if (read.Types.Count > 0)
                    {
                        forkRows.Add(new("Holds", Holds(read), Mono: true));
                    }
                }
            }

            var label = ((int)(info.Flags & FinderFlags.ColorMask)) >> 1;
            var flags = new DetailGroup("Finder flags",
            [
                new("Raw", $"0x{(ushort)info.Flags:X4}", Mono: true),
                new("Label", label == 0 ? "None" : $"{LabelNames[label]} ({label})"),
                new("Location", $"({info.Location.V}, {info.Location.H})", Mono: true),
            ])
            {
                Flags = FlagNames.Select(f => new FinderFlagChip(f.Name, f.Flag == FinderFlags.None ? file.IsLocked : (info.Flags & f.Flag) != 0)).ToList(),
            };
            var chain = Chain(node);
            var groups = new List<DetailGroup>
            {
                new("File", fileRows),
                new("Forks", forkRows),
                Dates(file, DateNote(chain.Count > 1 ? chain[^2].Text : "")),
                flags,
                new("How it was read", [], Wide: true) { Chain = chain },
            };
            return new DetailsViewModel(file.Name.ToMacRoman(), groups, parent, problems);
        }

        private static DetailGroup Dates(MacFile file, string? note) =>
            new("Dates", [new("Created", Date(file.Created), Mono: true), new("Modified", Date(file.Modified), Mono: true)]) { Note = note };

        /// <summary>
        /// How dates were kept, by the format that held the file: HFS and MFS in Mac local time, HFS Plus, zip and tar in
        /// UTC; null for others.
        /// </summary>
        public static string? DateNote(string format)
        {
            var f = format.ToUpperInvariant();
            if (f.Contains("HFS PLUS", StringComparison.Ordinal) || f.Contains("HFS+", StringComparison.Ordinal) || f.Contains("ZIP", StringComparison.Ordinal)
                || f.Contains("TAR", StringComparison.Ordinal))
            {
                return "Stored in UTC, shown in your time zone.";
            }

            return f.Contains("HFS", StringComparison.Ordinal) || f.Contains("MFS", StringComparison.Ordinal) ? "Mac local time, as stored. No time zone." : null;
        }

        /// <summary>The first types of a fork in mono, and how many more ("'ICN#' 'STR#' +54 more").</summary>
        public static string Holds(ResourceFork fork)
        {
            var types = fork.Types.Select(t => $"'{t}'").Order(StringComparer.Ordinal).ToList();
            var shown = string.Join(" ", types.Take(5));
            return types.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $"{shown} +{types.Count - 5} more") : shown;
        }

        /// <summary>The compressed resources and their decompressors ("21 resources ('dcmp' 2)"), or null when there are none.</summary>
        public static string? Compression(ResourceFork fork)
        {
            var ids = new SortedSet<short>();
            var count = 0;
            foreach (var resource in fork.Resources)
            {
                if ((resource.Attributes & ResourceAttributes.Compressed) != 0
                    && CompressedResourceHeader.TryRead(resource.GetData(), out var header) && header.IsCompressed)
                {
                    count++;
                    ids.Add(header.DecompressorId);
                }
            }

            return count == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{count} resource{(count == 1 ? "" : "s")} ('dcmp' {string.Join(", ", ids)})");
        }

        // The containers the node was read through, from the input down: the input, how it was read, what it holds, then
        // each container file and its format, then the file and its forks (and a resource's type and ID).
        private static List<ReadStep> Chain(NodeViewModel node)
        {
            var path = new List<NodeViewModel>();
            for (var at = node; at is not null; at = at.Parent)
            {
                path.Insert(0, at);
            }

            var steps = new List<string>();
            foreach (var step in path)
            {
                switch (step)
                {
                    case InputNode input:
                        steps.Add(input.Title);
                        if (input.Host.Layout != HostLayout.Plain)
                        {
                            steps.Add(HostFiles.FormatName(input.Host.Layout));
                        }

                        if (input.Root.Children.Count > 0)
                        {
                            steps.Add(input.Root.Children[0].Format);
                        }

                        break;
                    case ContainerFileNode container:
                        steps.Add(container.File.Name.ToMacRoman());
                        steps.Add(container.ContentFormat);
                        break;
                    case FileNode file:
                        steps.Add($"{file.File.Name.ToMacRoman()} · {Forks(file.File)}");
                        break;
                    case ResourceNode resource:
                        steps.Add($"'{resource.Resource.Type}' {resource.Resource.Id}");
                        break;
                }
            }

            return steps.Select((s, i) => new ReadStep(s, i == steps.Count - 1)).ToList();
        }

        private static string Forks(MacFile file) => (file.DataFork.Length > 0, file.ResourceFork.Length > 0) switch
        {
            (true, true) => "both forks",
            (true, false) => "data fork",
            (false, true) => "resource fork",
            _ => "no forks",
        };

        // A node's place: the input and the folders down to it.
        private static string Path(NodeViewModel node)
        {
            var names = new List<string>();
            for (var at = node; at is not null; at = Tree.FolderOf(at))
            {
                names.Insert(0, at is InputNode input ? input.Title : at.Name);
            }

            return string.Join(" › ", names);
        }

        private static DetailsViewModel Type(ResourceTypeNode type)
        {
            var ids = type.Fork.OfType(type.Type).Select(r => r.Id).Order().ToList();
            return new DetailsViewModel($"'{type.Type}'", [new("Type", [
                new("Resources", ids.Count.ToString(CultureInfo.InvariantCulture)),
                new("IDs", string.Join(", ", ids), Mono: true),
                new("Total size", Bytes(type.Fork.OfType(type.Type).Sum(r => (long)r.Length)), Mono: true),
            ])]);
        }

        private static DetailsViewModel Resource(ResourceNode node, int problems)
        {
            var resource = node.Resource;
            var rows = new List<DetailRow>
            {
                new("Type", $"'{resource.Type}'", Mono: true),
                new("ID", resource.Id.ToString(CultureInfo.InvariantCulture), Mono: true),
                new("Name", resource.Name?.ToMacRoman() ?? "(none)"),
                new("Attributes", resource.Attributes == ResourceAttributes.None ? "none" : resource.Attributes.ToString()),
                new("Size", Bytes(resource.Length), Mono: true),
            };
            if ((resource.Attributes & ResourceAttributes.Compressed) != 0
                && CompressedResourceHeader.TryRead(resource.GetData(), out var header) && header.IsCompressed)
            {
                rows.Add(new("Compression", $"'dcmp' {header.DecompressorId}, {Bytes(header.DecompressedSize)} when expanded"));
            }

            return new DetailsViewModel(resource.ToString(),
                [new("Resource", rows), new("How it was read", [], Wide: true) { Chain = Chain(node) }], problems: problems);
        }

        private static string Date(MacDate? date) =>
            date is { } d ? d.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

        private static string Bytes(long count) => count.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
    }
}
