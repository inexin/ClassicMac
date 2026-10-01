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

        /// <summary>The title shown: the name, marked while the node has unsaved edits.</summary>
        [ObservableProperty]
        private string title;

        /// <summary>The title without the unsaved-edits mark.</summary>
        public string BaseTitle { get; }

        public NodeKind Kind { get; }

        public NodeViewModel? Parent { get; }

        public ObservableCollection<NodeViewModel> Children { get; } = [];

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
            if (value) _ = EnsureLoadedAsync();
        }

        public override string ToString() => Title;
    }

    /// <summary>The "Loading…" placeholder.</summary>
    public sealed class LoadingNode(NodeViewModel parent) : NodeViewModel("Loading…", NodeKind.Loading, parent);

    /// <summary>An opened host file: its layout, companions and the diagnostics of reading it.</summary>
    public sealed class InputNode : NodeViewModel
    {
        internal InputNode(string path, HostFile host, ContainerNode root, ReadOptions options, Action<DiagnosticEntry> report)
            : base(System.IO.Path.GetFileName(path), NodeKind.Input, null)
        {
            Path = path;
            Host = host;
            Root = root;
            Options = options;
            Report = report;
            if (root.Children.Count > 0) Tree.AddContents(this, root.Children);
            else FileNode.AddResourcesPlaceholder(this, root.File, raw: host.Layout == HostLayout.Plain);
        }

        public string Path { get; }

        public HostFile Host { get; }

        public ContainerNode Root { get; }

        internal ReadOptions Options { get; }

        internal Action<DiagnosticEntry> Report { get; }

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

    /// <summary>A file that is itself a container (a disk image, a MacBinary file, …): what it holds, by folder.</summary>
    public sealed class ContainerFileNode : NodeViewModel
    {
        internal ContainerFileNode(NodeViewModel parent, ContainerNode node)
            : base($"{node.File.Name.ToMacRoman()} ({node.Children[0].Format})", NodeKind.Container, parent)
        {
            Node = node;
            Tree.AddContents(this, node.Children);
        }

        public ContainerNode Node { get; }

        public MacFile File => Node.File;

        public override string Source => $"{Parent!.Source} › {Node.File.Name.ToMacRoman()}";
    }

    /// <summary>A folder inside a volume or archive.</summary>
    public sealed class FolderNode(NodeViewModel parent, string name) : NodeViewModel(name, NodeKind.Folder, parent)
    {
        public override string Source => $"{Parent!.Source}:{Title}";
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

        public override string Source => Parent is FolderNode ? $"{Parent.Source}:{Title}" : $"{Parent!.Source} › {Title}";

        protected override Task LoadAsync() => LoadResourcesAsync(this, File, raw: false);

        internal static void AddResourcesPlaceholder(NodeViewModel node, MacFile file, bool raw)
        {
            if (file.ResourceFork.Length > 0 || (file.DataFork.Length > 0 && MacFileResources.LooksLikeFork(file.DataFork)))
                node.Children.Add(new LoadingNode(node));
        }

        // Reads the file's resources off the UI thread, then shows its types.
        internal static async Task LoadResourcesAsync(NodeViewModel node, MacFile file, bool raw)
        {
            if (node.Children.Count == 0) return;
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
            if (node is FileNode fileNode) fileNode.Resources = found;
            else if (node is InputNode input) input.RawResources = found;
            node.Children.Clear();
            if (found.Fork is { } fork) ShowTypes(node, fork);
            foreach (var d in diagnostics) node.Input.Report(new DiagnosticEntry(d, node.Source, node));
        }

        // (Re)builds a file node's type nodes from its fork, keeping which types were open.
        internal static void ShowTypes(NodeViewModel node, ResourceFork fork)
        {
            var open = node.Children.OfType<ResourceTypeNode>().Where(t => t.IsExpanded).Select(t => t.Type).ToHashSet();
            node.Children.Clear();
            foreach (var type in fork.Types.OrderBy(t => t.ToString(), StringComparer.Ordinal))
                node.Children.Add(new ResourceTypeNode(node, fork, type) { IsExpanded = open.Contains(type) });
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
            foreach (var resource in fork.OfType(type).OrderBy(r => r.Id)) Children.Add(new ResourceNode(this, fork, resource));
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
    internal static class Tree
    {
        public static void AddContents(NodeViewModel parent, IReadOnlyList<ContainerNode> contents)
        {
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
                        at.Children.Add(folder);
                    }
                    at = folder;
                }
                return at;
            }
            foreach (var child in contents)
            {
                var into = FolderFor(child.File.FolderPath);
                into.Children.Add(child.Children.Count > 0 ? new ContainerFileNode(into, child) : new FileNode(into, child));
            }
        }
    }
}
