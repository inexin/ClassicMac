using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels
{
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
        }

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

        /// <summary>Whether the tree shows the title in a monospaced font (a name of whitespace made visible).</summary>
        [ObservableProperty]
        private bool isMono;

        /// <summary>The title without the unsaved-edits mark.</summary>
        public string BaseTitle { get; }

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
    }

    /// <summary>The "Loading…" placeholder.</summary>
    public sealed class LoadingNode(NodeViewModel parent) : NodeViewModel("Loading…", NodeKind.Loading, parent);

    /// <summary>An opened host file: its layout, companions and the diagnostics of reading it.</summary>
    public sealed class InputNode : NodeViewModel
    {
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

        internal ContainerReadOptions ContainerOptions { get; }

        internal Action<DiagnosticEntry> Report { get; }

        /// <summary>Which files the tree hides or groups.</summary>
        internal TreeDisplayOptions Display { get; }

        /// <summary>The resources of a plain input read as a fork of its own, once loaded.</summary>
        public FileResources? RawResources { get; internal set; }

        /// <summary>The edits made to the input's own resources, once any are.</summary>
        public EditState? Editing { get; internal set; }

        /// <summary>
        /// Whether the input is a plain HFS volume image (no partition map or disk-image wrapper), whose files and
        /// folders can be created and deleted.
        /// </summary>
        public bool IsWritableHfs => Host.Layout == HostLayout.Plain && Root.Children.Count > 0
            && Root.Children.All(c => c.Format == ClassicMac.Files.Hfs.HfsReader.Instance.FormatName);

        /// <summary>The volume with the files and folders created and deleted so far, or null when there are none.</summary>
        public byte[]? EditedVolume { get; internal set; }

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
            : base($"{node.File.Name.ToMacRoman()} ({ContentFormatOf(node)})", NodeKind.Container, parent)
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

        public override string Source => $"{Parent!.Source}:{Title}";
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
            : base(node.File.Name.ToMacRoman(), NodeKind.File, parent)
        {
            Node = node;
            AddResourcesPlaceholder(this, node.File, raw: false);
        }

        public ContainerNode Node { get; }

        public MacFile File => Node.File;

        public FileResources? Resources { get; internal set; }

        /// <summary>The edits made to the file's resources, once any are.</summary>
        public EditState? Editing { get; internal set; }

        public override string Source => Tree.FolderOf(this) is FolderNode folder ? $"{folder.Source}:{Title}" : $"{Parent!.Source} › {Title}";

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
        : NodeViewModel(resource.Name is { } n ? $"{resource.Id} “{n.ToMacRoman()}”" : resource.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), NodeKind.Resource, parent)
    {
        public ResourceFork Fork { get; } = fork;

        public Resource Resource { get; } = resource;

        public override string Source => $"{Parent!.Source} › {Resource}";
    }

    // Builds the nodes for what a container holds: its files, grouped into folder nodes by their folder paths.
    internal static partial class Tree
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
                        folders[key] = folder = new FolderNode(at, part.ToMacRoman());
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
}
