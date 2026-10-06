using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Images;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static ClassicMac.App.ViewModels.NodeFormat;
using static ClassicMac.App.ViewModels.NodeImages;
using static ClassicMac.App.ViewModels.TreeLayout;

namespace ClassicMac.App.ViewModels;

/// <summary>What a tree node stands for; the view picks its icon from this.</summary>
public enum NodeKind
{
    Input,
    Container,
    Folder,
    File,
    ResourceType,
    Resource,
    Loading,

    /// <summary>A folder's files with no name, grouped.</summary>
    NoNameGroup,
}

/// <summary>
/// A node of the browse tree. Children that cost a read (a file's resources) load when the node is first expanded;
/// until then a "Loading…" placeholder keeps the expander visible.
/// </summary>
public abstract partial class NodeViewModel : ObservableObject
{
    private Task? loading;

    protected NodeViewModel(string title, NodeKind kind, NodeViewModel? parent)
    {
        this.title = title;
        BaseTitle = title;
        Kind = kind;
        Parent = parent;
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
    }

    /// <summary>How many rows are above this one in the tree: 0 for an input.</summary>
    public int Depth
    {
        get
        {
            var depth = 0;
            for (var up = Parent; up is not null; up = up.Parent)
            {
                depth++;
            }

            return depth;
        }
    }

    /// <summary>Whether the row has rows below it (its expander shows).</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>The title: the name, marked while the node has unsaved edits.</summary>
    [ObservableProperty]
    private string title;

    /// <summary>What the tree shows as the name ("(no name)", or the name with its whitespace made visible), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    private string? alias;

    /// <summary>Whether the tree shows the title in italics (no name).</summary>
    [ObservableProperty]
    private bool isItalic;

    /// <summary>A name of whitespace as the tree shows it: a token per run of one character ("sp×3", "tab"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameTokens))]
    private IReadOnlyList<NameToken>? nameTokens;

    /// <summary>The name's bytes in hex ("20 20 CA"), shown on hover over its tokens; null for other names.</summary>
    [ObservableProperty]
    private string? nameBytes;

    public bool HasNameTokens => NameTokens is { Count: > 0 };

    /// <summary>Whether this is an alias file (the Finder's isAlias flag): the tree shows it in italics with a badge.</summary>
    public bool IsAliasFile => this switch
    {
        FileNode file => ClassicMac.Files.AliasResolver.IsAlias(file.File),
        ContainerFileNode container => ClassicMac.Files.AliasResolver.IsAlias(container.File),
        _ => false,
    };

    /// <summary>The title without the unsaved-edits mark.</summary>
    public string BaseTitle { get; private set; }

    // A Mac name as text in the input's name encoding (View ▸ Text Encoding).
    internal static string NameText(NodeViewModel parent, MacString name) =>
        MacEncodings.Decode(name.Bytes, parent.Input.ContainerOptions.NameEncoding);

    // The title this node takes in the input's name encoding, or null when it has no Mac name.
    internal virtual string? TitleInEncoding() => null;

    // Gives the node (and every node below it, shown or not) the titles of the input's name encoding, keeping any mark.
    internal void Retitle()
    {
        if (TitleInEncoding() is { } title && title != BaseTitle)
        {
            Title = title + (Title.StartsWith(BaseTitle, StringComparison.Ordinal) ? Title[BaseTitle.Length..] : "");
            BaseTitle = title;
        }

        foreach (var child in (Items ?? []).Concat(Children).Distinct())
        {
            child.Retitle();
        }
    }

    public NodeKind Kind { get; }

    /// <summary>The node above in the tree (a "No name" group for the files in one).</summary>
    public NodeViewModel? Parent { get; internal set; }

    /// <summary>The nodes shown below this one.</summary>
    public ObservableCollection<NodeViewModel> Children { get; } = [];

    /// <summary>
    /// For a node holding files and folders (an input, a read container file, a folder): all of them, in the
    /// container's order, including those the tree hides or groups; <see cref="Children"/> is what it shows.
    /// Null for other nodes.
    /// </summary>
    internal List<NodeViewModel>? Items { get; set; }

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>The input this node belongs to.</summary>
    public InputNode Input => this as InputNode ?? Parent!.Input;

    /// <summary>The path shown for this node in diagnostics: the input, then the Mac path.</summary>
    public virtual string Source => Parent?.Source ?? Title;

    /// <summary>Loads lazy children (once); completes at once for nodes without any.</summary>
    public Task EnsureLoadedAsync() => loading ??= LoadAsync();

    protected virtual Task LoadAsync() => Task.CompletedTask;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = EnsureLoadedAsync();
    }

    public override string ToString() => Title;

    // What a browse-tree row shows besides its title (design/boards/browse-tree.md): its icon, its own icon once
    // resolved, the right-aligned meta, the unsaved mark, the "not read" chip and the drag-source outline.

    private const string UnsavedMark = " •";

    private Task? iconLoading;

    /// <summary>The node's own 16 × 16 icon as PNG (a file's Finder icon, an icon resource's), once resolved; else null.</summary>
    [ObservableProperty]
    private byte[]? iconPng;

    /// <summary>Whether the node is being dragged out of the tree (its row is outlined).</summary>
    [ObservableProperty]
    private bool isDragSource;

    /// <summary>The icon for the node's kind, shown until (or instead of) its own.</summary>
    public TreeIconKind IconKind => this switch
    {
        InputNode => TreeIconKind.HardDisk,
        ContainerFileNode container => IsArchive(container.ContentFormat) ? TreeIconKind.Parcel : TreeIconKind.Floppy,
        FileNode file => FileTypeIcons.For(file.File.FinderInfo.Type, file.Title),
        ResourceTypeNode => TreeIconKind.ResourceType,
        NoNameGroupNode => TreeIconKind.NoNameGroup,
        ResourceNode => TreeIconKind.Resource,
        LoadingNode => TreeIconKind.Loading,
        _ => TreeIconKind.Folder,
    };

    /// <summary>The "Loading…" placeholder: a spinner, no icon.</summary>
    public bool IsLoading => this is LoadingNode;

    /// <summary>A resource type's title (<c>'ICN#' (12)</c>) is set in mono.</summary>
    public bool IsResourceType => this is ResourceTypeNode;

    /// <summary>The name shown: the title without the unsaved mark, or the alias for a file with no name.</summary>
    public string Name => Alias ?? (IsUnsaved ? Title[..^UnsavedMark.Length] : Title);

    /// <summary>Whether the node has unsaved edits (its title carries " •").</summary>
    public bool IsUnsaved => Title != BaseTitle && Title.EndsWith(UnsavedMark, StringComparison.Ordinal);

    /// <summary>A container file not read yet: it is read when expanded.</summary>
    public bool IsUnread => this is ContainerFileNode { Node.UnreadFormat: not null };

    /// <summary>
    /// The row's right-aligned meta: type · creator for a file, the size for a resource, format and size for the
    /// input; null for the others.
    /// </summary>
    public string? Meta => DetailsShown ? Details : null;

    /// <summary>
    /// The tooltip of a file's type · creator: its kind and where it came from ("SimpleText text document", "from
    /// SimpleText’s 'kind' 128"), found only when the tooltip is shown; null for other rows.
    /// </summary>
    public object? KindTip => this is FileNode or ContainerFileNode ? new KindTip(this) : null;

    // Whether the tree shows the details column (Tree display ▸ Show details column; off when the node is in no input).
    private bool DetailsShown
    {
        get
        {
            var at = this;
            while (at.Parent is { } parent)
            {
                at = parent;
            }
            return at is InputNode { Display.ShowDetails: true };
        }
    }

    private string? Details => this switch
    {
        FileNode file => FormatTypeCreator(file.File.FinderInfo.Type, file.File.FinderInfo.Creator),
        ContainerFileNode container => FormatTypeCreator(container.File.FinderInfo.Type, container.File.FinderInfo.Creator),
        ResourceNode resource => FormatSize(resource.Resource.Length),
        NoNameGroupNode group => string.Create(CultureInfo.InvariantCulture, $"{group.Children.Count} files"),
        InputNode input => $"{(input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "resource fork")} · {FormatSize(HostSize(input))}",
        _ => null,
    };

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsUnsaved));
        OnNameChanged();
    }

    /// <summary>Tells the row the container was read, so its "not read" chip goes.</summary>
    internal void OnRead() => OnPropertyChanged(nameof(IsUnread));

    /// <summary>Tells the row its meta changed (a "No name" group's count).</summary>
    internal void OnMetaChanged() => OnPropertyChanged(nameof(Meta));

    /// <summary>
    /// Resolves the node's own icon (once), off the UI thread: an icon resource's small icon (a file's row shows its
    /// type's icon; its own Finder icon is the inspector's). The tree asks only for rows on screen; an alias row learns
    /// its state here too.
    /// </summary>
    public Task RequestIconAsync() => iconLoading ??= LoadIconAsync();

    /// <summary>
    /// An alias row's state, learnt with its icon (rows on screen only): found, or why not (aliases.md §5); null for
    /// other rows and before it is known.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrokenAlias))]
    private ClassicMac.Files.AliasState? aliasState;

    /// <summary>Whether the row is an alias whose original is not found: dimmed, a mark on its badge.</summary>
    public bool IsBrokenAlias => AliasState is { } state && state != ClassicMac.Files.AliasState.Found;

    private async Task LoadIconAsync()
    {
        System.Threading.Interlocked.Increment(ref Input.IconRequests);

        if (IsAliasFile && Input is { } input)
        {
            var inputs = input.Display.Inputs().ToList();
            AliasState = await Task.Run(() => Aliases.Of(this, inputs)?.Resolution.State);
        }

        Func<byte[]?>? load = this switch
        {
            ResourceNode resource when IsIconResource(resource.Resource.Type.ToString()) => () => ResourceIcon(resource),
            _ => null,
        };
        if (load is null)
        {
            return;
        }

        if (await Task.Run(load) is { } png)
        {
            IconPng = png;
        }
    }

    // A row's part in the tree's filter and type-ahead (design/boards/browse-tree.md, S6).

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

/// <summary>The "Loading…" placeholder.</summary>
public sealed class LoadingNode(NodeViewModel parent) : NodeViewModel("Loading…", NodeKind.Loading, parent);

/// <summary>An opened host file: its layout, companions and the diagnostics of reading it.</summary>
public sealed class InputNode : NodeViewModel
{
    // How many of its rows have asked for their icon (NodeImages.ResolvedIcons, for tests).
    internal int IconRequests;

    internal InputNode(string path, HostFile host, ContainerNode root, ContainerReadOptions containerOptions, ReadOptions options,
        Action<DiagnosticEntry> report, TreeDisplayOptions? display = null)
        : base(System.IO.Path.GetFileName(path), NodeKind.Input, null)
    {
        Display = display ?? new TreeDisplayOptions();
        Path = path;
        Host = host;
        Root = root;
        ContainerOptions = containerOptions;
        Options = options;
        Report = report;
        VolumeSession = InputEditSession.Open(path, host, root, containerOptions, options);
        if (root.Children.Count > 0)
        {
            Tree.AddContents(this, root.Children);
        }
        else
        {
            FileNode.AddResourcesPlaceholder(this, root.File, raw: host.Layout == HostLayout.Plain);
        }
    }

    public string Path { get; }

    public HostFile Host { get; }

    public ContainerNode Root { get; }

    internal ReadOptions Options { get; }

    internal ContainerReadOptions ContainerOptions { get; private set; }

    // A new name encoding for the input's names (View ▸ Text Encoding): every node retitled; containers read later use it.
    internal void SetNameEncoding(MacTextEncoding encoding)
    {
        ContainerOptions = ContainerOptions with { NameEncoding = encoding };
        VolumeSession.NameEncoding = encoding;
        Retitle();
    }

    internal Action<DiagnosticEntry> Report { get; }

    /// <summary>Which files the tree hides or groups.</summary>
    internal TreeDisplayOptions Display { get; }

    /// <summary>The resources of a plain input read as a fork of its own, once loaded.</summary>
    public FileResources? RawResources { get; internal set; }

    /// <summary>The edits made to the input's own resources, once any are.</summary>
    public EditState? Editing { get; internal set; }

    /// <summary>
    /// The input's edit session for its volume: files and folders created and deleted, saved by Save As ▸ HFS Volume
    /// Image in the input's own format.
    /// </summary>
    internal InputEditSession VolumeSession { get; }

    /// <summary>
    /// Whether the input holds an HFS volume whose files and folders can be created and deleted: a plain volume image,
    /// a partitioned disk with an HFS partition, or a Disk Copy 4.2 or NDIF image of an HFS disk.
    /// </summary>
    public bool IsWritableHfs => VolumeSession.Kind == InputEditKind.HfsVolume;

    /// <summary>Whether First Aid checks and repairs the input's volume: an HFS one as above, or an HFS Plus one.</summary>
    public bool IsFirstAidVolume => VolumeSession.Kind is InputEditKind.HfsVolume or InputEditKind.HfsPlusVolume;

    /// <summary>Whether files or folders have been created or deleted in the volume (nothing is read to tell).</summary>
    public bool HasVolumeChanges => VolumeSession.HasChanges;

    /// <summary>The volume with the files and folders created and deleted so far, made whole in memory; null when there are none.</summary>
    public byte[]? EditedVolume => VolumeSession.HasChanges ? VolumeSession.Volume : null;

    /// <summary>
    /// The volume a node is in: the node the volume's files and folders are under, and the name its paths in the edit
    /// session start with ("" for the input's only volume; on a disk with several partitions, the partition's name, its
    /// node under the input); null when the node is in no volume the session edits.
    /// </summary>
    internal (NodeViewModel Root, string Name)? VolumeOf(NodeViewModel node)
    {
        if (VolumeSession.PartitionNames.Count == 0)
        {
            return VolumeRoot is { } root ? (root, "") : null;
        }

        var top = node;
        while (top.Parent is { } parent && parent != this)
        {
            top = parent;
        }

        return top is ContainerFileNode partition && top.Parent == this && VolumeSession.PartitionNames.Contains(partition.File.Name.ToMacRoman())
            ? (top, partition.File.Name.ToMacRoman())
            : null;
    }

    /// <summary>A path in the volume a node is in, as the edit session takes it: after the partition's name on a disk with several.</summary>
    internal string SessionPath(NodeViewModel node, string pathInVolume) =>
        VolumeOf(node) is { Name.Length: > 0 } volume ? pathInVolume.Length == 0 ? volume.Name : volume.Name + ":" + pathInVolume : pathInVolume;

    // The node the input's only volume's files and folders are under: the input for a plain volume image, else the
    // disk's node (a partition, a Disk Copy disk) under the input's wrappers; null when the input is no writable volume.
    private NodeViewModel? VolumeRoot
    {
        get
        {
            if (!IsWritableHfs)
            {
                return null;
            }

            if (Root.Volume?.Format == "HFS")
            {
                return this;
            }

            NodeViewModel at = this;
            while (at.Children.OfType<ContainerFileNode>().ToList() is [var only])
            {
                if (only.Node.Volume?.Format == "HFS")
                {
                    return only;
                }

                at = only;
            }

            return null;
        }
    }

    // A plain file that is no container may itself be a resource fork (a .rsrc file).
    protected override Task LoadAsync() =>
        Root.Children.Count > 0 ? Task.CompletedTask : FileNode.LoadResourcesAsync(this, Root.File, raw: Host.Layout == HostLayout.Plain);
}

/// <summary>
/// A file that is itself a container (a disk image, a MacBinary file, …): what it holds, by folder. One that was
/// opened unread (inside a disk or an archive) is read when it is first expanded.
/// </summary>
public sealed class ContainerFileNode : NodeViewModel
{
    internal ContainerFileNode(NodeViewModel parent, ContainerNode node)
        : base($"{NameText(parent, node.File.Name)} ({ContentFormatOf(node)})", NodeKind.Container, parent)
    {
        Node = node;
        if (node.UnreadFormat is not null)
        {
            Children.Add(new LoadingNode(this));
        }
        else
        {
            Tree.AddContents(this, node.Children);
        }
    }

    /// <summary>The container as read so far (its contents once it has been expanded).</summary>
    public ContainerNode Node { get; private set; }

    public MacFile File => Node.File;

    /// <summary>The container format of the file's data fork.</summary>
    public string ContentFormat => ContentFormatOf(Node);

    public override string Source => $"{Parent!.Source} › {Node.File.Name.ToMacRoman()}";

    internal override string? TitleInEncoding() => $"{NameText(Parent!, Node.File.Name)} ({ContentFormatOf(Node)})";

    private static string ContentFormatOf(ContainerNode node) => node.UnreadFormat ?? node.Children[0].Format;

    // Reads the container one level down, off the UI thread, then shows what it holds.
    protected override async Task LoadAsync()
    {
        if (Node.UnreadFormat is null)
        {
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var options = Input.ContainerOptions;
        var siblings = Tree.Siblings(this);
        var node = Node;
        Node = await Task.Run(() => ContainerUnwrapper.Default.Expand(node, new ContainerContext(options, diagnostics, siblings: siblings), levels: 1));
        Children.Clear();
        Tree.AddContents(this, Node.Children);
        OnRead();
        foreach (var d in diagnostics)
        {
            Input.Report(new DiagnosticEntry(d, Tree.SourceOf(this, d), this));
        }
    }
}

/// <summary>A folder inside a volume or archive.</summary>
public sealed class FolderNode : NodeViewModel
{
    public FolderNode(NodeViewModel parent, string name) : base(name, NodeKind.Folder, parent) => Items = [];

    internal FolderNode(NodeViewModel parent, MacString name) : this(parent, NameText(parent, name)) => MacName = name;

    // The folder's name as stored, when it came from a volume or archive.
    internal MacString? MacName { get; }

    public override string Source => $"{Parent!.Source}:{Title}";

    internal override string? TitleInEncoding() => MacName is { } name ? NameText(Parent!, name) : null;
}

/// <summary>A folder's files whose names are empty or only whitespace, when it has two or more (shown collapsed).</summary>
public sealed class NoNameGroupNode : NodeViewModel
{
    internal NoNameGroupNode(NodeViewModel folder) : base("No name", NodeKind.NoNameGroup, folder) => IsItalic = true;

    public override string Source => Parent!.Source;
}

/// <summary>A Mac file; its resources load when it is first expanded.</summary>
public sealed class FileNode : NodeViewModel
{
    internal FileNode(NodeViewModel parent, ContainerNode node)
        : base(NameText(parent, node.File.Name), NodeKind.File, parent)
    {
        Node = node;
        AddResourcesPlaceholder(this, node.File, raw: false);
    }

    public ContainerNode Node { get; }

    public MacFile File => Node.File;

    public FileResources? Resources { get; internal set; }

    internal override string? TitleInEncoding() => NameText(Parent!, Node.File.Name);

    /// <summary>The edits made to the file's resources, once any are.</summary>
    public EditState? Editing { get; internal set; }

    public override string Source => TreeLayout.FolderOf(this) is FolderNode folder ? $"{folder.Source}:{Title}" : $"{Parent!.Source} › {Title}";

    protected override Task LoadAsync() => LoadResourcesAsync(this, File, raw: false);

    internal static void AddResourcesPlaceholder(NodeViewModel node, MacFile file, bool raw)
    {
        if (file.ResourceFork.Length > 0 || (file.DataFork.Length > 0 && MacFileResources.LooksLikeFork(file.DataFork)))
        {
            node.Children.Add(new LoadingNode(node));
        }
    }

    // Reads the file's resources off the UI thread, then shows its types.
    internal static async Task LoadResourcesAsync(NodeViewModel node, MacFile file, bool raw)
    {
        if (node.Children.Count == 0)
        {
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var options = node.Input.Options;
        FileResources found;
        try
        {
            found = await Task.Run(() => raw && file.ResourceFork.Length == 0
                ? MacFileResources.ReadRaw(file.DataFork, options, diagnostics)
                : MacFileResources.Read(file, options, diagnostics));
        }
        catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.IOException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "fork.unreadable", e.Message));
            found = new FileResources(null, ResourceForkSource.None);
        }
        if (node is FileNode fileNode)
        {
            fileNode.Resources = found;
        }
        else if (node is InputNode input)
        {
            input.RawResources = found;
        }

        node.Children.Clear();
        if (found.Fork is { } fork)
        {
            ShowTypes(node, fork);
        }

        foreach (var d in diagnostics)
        {
            node.Input.Report(new DiagnosticEntry(d, node.Source, node));
        }
    }

    // (Re)builds a file node's type nodes from its fork, keeping which types were open.
    internal static void ShowTypes(NodeViewModel node, ResourceFork fork)
    {
        var open = node.Children.OfType<ResourceTypeNode>().Where(t => t.IsExpanded).Select(t => t.Type).ToHashSet();
        node.Children.Clear();
        foreach (var type in fork.Types.OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            node.Children.Add(new ResourceTypeNode(node, fork, type) { IsExpanded = open.Contains(type) });
        }
    }
}

/// <summary>All resources of one type in a fork.</summary>
public sealed class ResourceTypeNode : NodeViewModel
{
    internal ResourceTypeNode(NodeViewModel parent, ResourceFork fork, FourCC type)
        : base($"'{type}' ({fork.OfType(type).Count()})", NodeKind.ResourceType, parent)
    {
        Fork = fork;
        Type = type;
        foreach (var resource in fork.OfType(type).OrderBy(r => r.Id))
        {
            Children.Add(new ResourceNode(this, fork, resource));
        }
    }

    public ResourceFork Fork { get; }

    public FourCC Type { get; }

    public override string Source => Parent!.Source;
}

/// <summary>One resource.</summary>
public sealed class ResourceNode(NodeViewModel parent, ResourceFork fork, Resource resource)
    : NodeViewModel(TitleOf(parent, resource), NodeKind.Resource, parent)
{
    private static string TitleOf(NodeViewModel parent, Resource resource) => resource.Name is { } n
        ? $"{resource.Id} “{NameText(parent, n)}”"
        : resource.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal override string? TitleInEncoding() => TitleOf(Parent!, Resource);

    public ResourceFork Fork { get; } = fork;

    public Resource Resource { get; } = resource;

    public override string Source => $"{Parent!.Source} › {Resource}";
}

// Builds the nodes for what a container holds: its files, grouped into folder nodes by their folder paths.
internal static class Tree
{
    public static void AddContents(NodeViewModel parent, IReadOnlyList<ContainerNode> contents)
    {
        parent.Items = [];
        var folders = new Dictionary<string, FolderNode>(StringComparer.Ordinal);
        NodeViewModel FolderFor(IReadOnlyList<MacString> path)
        {
            NodeViewModel at = parent;
            var key = "";
            foreach (var part in path)
            {
                key += ":" + part.ToMacRoman();
                if (!folders.TryGetValue(key, out var folder))
                {
                    folders[key] = folder = new FolderNode(at, part);
                    at.Items!.Add(folder);
                }
                at = folder;
            }
            return at;
        }
        foreach (var child in contents)
        {
            var into = FolderFor(child.File.FolderPath);
            into.Items!.Add(child.Children.Count > 0 || child.UnreadFormat is not null ? new ContainerFileNode(into, child) : new FileNode(into, child));
        }
        Relayout(parent);
    }

    // Where a diagnostic found while reading under a node came from: the node, and the nested file it is about.
    public static string SourceOf(NodeViewModel node, Diagnostic diagnostic) =>
        diagnostic.Location is { } location ? $"{node.Source} › {location}" : node.Source;

    // The files beside a container file in the container that holds it, in the same folder (for formats split
    // across files, such as segmented disk images).
    public static Func<IEnumerable<MacFile>> Siblings(ContainerFileNode node)
    {
        NodeViewModel? at = node.Parent;
        while (at is FolderNode or NoNameGroupNode)
        {
            at = at.Parent;
        }

        var holder = at switch { InputNode input => input.Root, ContainerFileNode container => container.Node, _ => null };
        var file = node.File;
        return () => holder is null ? []
            : holder.Children.Select(c => c.File).Where(f => !ReferenceEquals(f, file) && f.FolderPath.SequenceEqual(file.FolderPath));
    }
}
