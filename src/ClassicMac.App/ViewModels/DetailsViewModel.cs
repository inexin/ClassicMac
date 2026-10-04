using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using ClassicMac.Resources.Compression;

namespace ClassicMac.App.ViewModels;

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

    /// <summary>
    /// The details of <paramref name="node"/>, with the errors and warnings reported for it and, for an alias file, the
    /// Alias card from <paramref name="alias"/>.
    /// </summary>
    public static DetailsViewModel For(NodeViewModel? node, int problems = 0, AliasLink? alias = null) => node switch
    {
        InputNode input => Input(input, problems),
        ContainerFileNode container => File(container, container.File, container.ContentFormat, null, problems, alias),
        FileNode file => File(file, file.File, null, file.Resources, problems, alias),
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

        // A host file read as a volume or archive has no Mac dates of its own: no card of dashes.
        if (input.Root.File.Created is not null || input.Root.File.Modified is not null)
        {
            groups.Add(Dates(input.Root.File, DateNote(input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "")));
        }

        if (VolumeCard(input.Root) is { } volume)
        {
            groups.Add(volume);
        }

        groups.Add(new("How it was read", [], Wide: true) { Chain = Chain(input) });
        return new DetailsViewModel(input.Title, groups, problems: problems);
    }

    private static DetailsViewModel File(NodeViewModel node, MacFile file, string? holds, FileResources? resources, int problems, AliasLink? alias)
    {
        var info = file.FinderInfo;
        var kind = FileKinds.Of(node);
        var parent = TreeLayout.FolderOf(node);
        var fileRows = new List<DetailRow>
        {
            new("Name", file.Name.ToMacRoman()),
            new("Kind", kind.Text),
            new("Kind from", FileKinds.Source(kind)),
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
        var groups = new List<DetailGroup> { new("File", fileRows) };
        if (alias is not null)
        {
            groups.Add(AliasGroup(alias));
        }

        groups.AddRange(
        [
            new("Forks", forkRows),
            Dates(file, DateNote(chain.Count > 1 ? chain[^2].Text : "", VolumeAbove(node))),
            flags,
        ]);
        // A disk image's file: the volume in it.
        if (node is ContainerFileNode container && VolumeCard(container.Node) is { } volume)
        {
            groups.Add(volume);
        }

        groups.Add(new("How it was read", [], Wide: true) { Chain = chain });
        return new DetailsViewModel(file.Name.ToMacRoman(), groups, parent, problems);
    }

    // The Alias card (docs/formats/resources/aliases.md §5): where the alias points, whether that resolves and how, and
    // the record's IDs and dates.
    private static DetailGroup AliasGroup(AliasLink link)
    {
        var resolution = link.Resolution;
        var alias = resolution.Alias;
        var rows = new List<DetailRow>
        {
            new("Original", resolution.StoredPath, Mono: true),
            new("Volume", alias.VolumeName.ToMacRoman()),
            new("Found", resolution.Found ? $"Yes, {resolution.How}" : "No"),
        };
        if (!resolution.Found)
        {
            rows.Add(new("State", resolution.Explanation));
        }

        if (alias.Network is { } network)
        {
            foreach (var (label, value) in new[] { ("Server", network.Server), ("Zone", network.Zone), ("User", network.User) })
            {
                if (value is not null)
                {
                    rows.Add(new(label, value));
                }
            }
        }
        else if (alias.VolumeKindName is { } kind)
        {
            rows.Add(new("Disk kind", kind));
        }

        if (resolution.Found)
        {
            rows.Add(new("Now at", resolution.ResolvedPath, Mono: true));
        }

        var number = alias.TargetId.ToString(CultureInfo.InvariantCulture);
        rows.Add(new("Parent ID", alias.ParentId.ToString(CultureInfo.InvariantCulture), Mono: true));
        rows.Add(alias.Kind == AliasKind.Folder ? new("Folder ID", number, Mono: true) : new("File ID", number, Mono: true));
        rows.Add(new("Created", DisplayDate(alias.TargetCreated, false), Mono: true));
        rows.Add(new("Volume created", DisplayDate(alias.VolumeCreated, false), Mono: true));
        return new DetailGroup("Alias", rows);
    }

    private const string LocalNote = "Mac local time, as stored. No time zone.";
    private const string UtcNote = "Stored in UTC, shown in your time zone.";

    private static DetailGroup Dates(MacFile file, string? note)
    {
        var utc = note == UtcNote;
        return new("Dates", [new("Created", DisplayDate(file.Created, utc), Mono: true), new("Modified", DisplayDate(file.Modified, utc), Mono: true)])
        {
            Note = note,
        };
    }

    // The volume a container holds: its own, or that of the one file it holds (a disk image's disk), and so on down.
    private static VolumeInfo? VolumeOf(ContainerNode node) => VolumeNode(node)?.Volume;

    private static ContainerNode? VolumeNode(ContainerNode node)
    {
        for (var at = node; ; at = at.Children[0])
        {
            if (at.Volume is not null)
            {
                return at;
            }

            if (at.Children.Count != 1)
            {
                return null;
            }
        }
    }

    // The Volume card of the volume a container holds, with an HFS volume's fragmentation.
    private static DetailGroup? VolumeCard(ContainerNode node) =>
        VolumeNode(node) is { Volume: { } volume } at
            ? VolumeGroup(volume, volume.Format == "HFS" ? HfsReader.Instance.ReadLayout(at.File.DataFork) : null)
            : null;

    // The volume a file was read from: the nearest input or disk image above it that holds one.
    private static VolumeInfo? VolumeAbove(NodeViewModel node)
    {
        for (var at = node.Parent; at is not null; at = at.Parent)
        {
            var volume = at switch
            {
                ContainerFileNode container => VolumeOf(container.Node),
                InputNode input => VolumeOf(input.Root),
                _ => null,
            };
            if (volume is not null || at is ContainerFileNode or InputNode)
            {
                return volume;
            }
        }

        return null;
    }

    /// <summary>
    /// The Volume card: the volume's format, its block size, size and free space, its files and folders, an HFS
    /// volume's fragmentation (hfs.md §5.7), and its dates — creation as stored (local time on every Mac volume), the
    /// others in local time on HFS and MFS and in UTC, shown in local time, on HFS Plus; MFS has no modification date.
    /// </summary>
    public static DetailGroup VolumeGroup(VolumeInfo volume, VolumeLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var utc = volume.UtcAfterCreation;
        var rows = new List<DetailRow> { new("Format", $"{volume.Format} volume") };
        if (volume.BlockSize > 0)
        {
            static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
            rows.Add(new("Block size", $"{N(volume.BlockSize)} bytes", Mono: true));
            rows.Add(new("Size", $"{N(volume.TotalBytes)} bytes in {N(volume.TotalBlocks)} blocks", Mono: true));
            rows.Add(new("Free", $"{N(volume.FreeBytes)} bytes in {N(volume.FreeBlocks)} blocks", Mono: true));
        }

        if (volume.Files is { } files)
        {
            rows.Add(volume.Folders is { } folders
                ? new("Files / folders", string.Create(CultureInfo.InvariantCulture, $"{files:N0} / {folders:N0}"), Mono: true)
                : new("Files", files.ToString("N0", CultureInfo.InvariantCulture), Mono: true));
        }

        if (layout is { } pieces)
        {
            rows.Add(new("Fragmented files", string.Create(CultureInfo.InvariantCulture,
                $"{pieces.SplitFiles:N0} of {pieces.Files:N0}{(pieces.SplitFiles > 0 ? $" (at most {pieces.MostExtents:N0} extents)" : "")}"), Mono: true));
            rows.Add(new("Free space", string.Create(CultureInfo.InvariantCulture,
                $"{pieces.FreeRuns.Count:N0} {(pieces.FreeRuns.Count == 1 ? "run" : "runs")}, the largest {pieces.LargestFreeRun:N0} blocks"), Mono: true));
        }

        rows.Add(new("Created", DisplayDate(volume.Created, utc: false), Mono: true));
        if (volume.Format != "MFS")
        {
            rows.Add(new("Modified", DisplayDate(volume.Modified, utc), Mono: true));
        }

        rows.Add(volume.BackedUp is { } backup ? new("Backed up", DisplayDate(backup, utc), Mono: true) : new("Backed up", "never"));
        return new("Volume", rows)
        {
            Note = utc ? "Created in Mac local time, as stored; the others stored in UTC, shown in your time zone." : LocalNote,
        };
    }

    /// <summary>A date as <c>yyyy-MM-dd HH:mm:ss</c>: as stored, or, when it is stored in UTC, in this computer's time zone; "—" for none.</summary>
    public static string DisplayDate(MacDate? date, bool utc)
    {
        if (date is not { } d)
        {
            return "—";
        }

        var time = utc ? DateTime.SpecifyKind(d.ToDateTime(), DateTimeKind.Utc).ToLocalTime() : d.ToDateTime();
        return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How dates were kept: by the volume the file was read from when known (HFS and HFS Plus share a reader name),
    /// else by the format that held it — HFS and MFS in Mac local time, HFS Plus, zip and tar in UTC; null for others.
    /// </summary>
    public static string? DateNote(string format, VolumeInfo? volume = null)
    {
        if (volume is not null)
        {
            return volume.Format == "HFS Plus" ? UtcNote : LocalNote;
        }

        var f = format.ToUpperInvariant();
        if (f.Contains("HFS PLUS", StringComparison.Ordinal) || f.Contains("HFS+", StringComparison.Ordinal) || f.Contains("ZIP", StringComparison.Ordinal)
            || f.Contains("TAR", StringComparison.Ordinal))
        {
            return UtcNote;
        }

        return f.Contains("HFS", StringComparison.Ordinal) || f.Contains("MFS", StringComparison.Ordinal) ? LocalNote : null;
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
        for (var at = node; at is not null; at = TreeLayout.FolderOf(at))
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

    private static string Bytes(long count) => count.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
}
