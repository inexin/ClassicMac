using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    // Export from the viewer, with the same code as the CLI: a resource saved as a file; a file's resources (or one
    // type's) exported with a manifest; everything under a node extracted or unpacked. Folder exports go into a new
    // subfolder named after the item, so nothing is overwritten.
    public sealed partial class MainViewModel
    {
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveResourceAsCommand), nameof(ExportResourcesCommand), nameof(ExtractAllCommand), nameof(UnpackAppleDoubleCommand), nameof(UnpackBasiliskCommand))]
        private bool isExporting;

        /// <summary>The last export's task (tests wait for it).</summary>
        internal Task ExportTask { get; private set; } = Task.CompletedTask;

        private DecodeOptions CurrentDecodeOptions => DecodeOptions.Default with { ScreenDepth = ScreenDepth, QuickDraw = ReadOptions.ResourceManager };

        private bool CanSaveResource() => !IsExporting && Selected is ResourceNode;

        private bool CanExportResources() => !IsExporting && Selected is FileNode or ResourceTypeNode or InputNode { Root.Children.Count: 0 };

        private bool CanExtractAll() => !IsExporting && Selected is InputNode or ContainerFileNode or FolderNode;

        private bool CanUnpack() => !IsExporting && Selected is InputNode or ContainerFileNode or FolderNode or FileNode;

        [RelayCommand(CanExecute = nameof(CanSaveResource))]
        private Task SaveResourceAs() => Run(async () =>
        {
            if (Selected is not ResourceNode node || FilePicker is null) return;
            var diagnostics = new List<Diagnostic>();
            var (outputs, raw) = await Task.Run(() => Decode(node, diagnostics));
            var extensions = outputs.Select(o => o.Extension).Append(".bin").Distinct().ToList();
            var stem = HostNames.ToHostName(Stem(node.Resource), 200);
            var path = await FilePicker.PickSaveFileAsync($"Save {node.Resource}", stem + extensions[0], extensions);
            if (path is null) return;
            var chosen = outputs.FirstOrDefault(o => string.Equals(o.Extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));
            await File.WriteAllBytesAsync(path, (chosen?.Content ?? raw).ToArray());
            foreach (var d in diagnostics) Report(new DiagnosticEntry(d, node.Source, node));
            Status = $"Saved {node.Resource} to {path}.";
        });

        [RelayCommand(CanExecute = nameof(CanExportResources))]
        private Task ExportResources() => Run(async () =>
        {
            var node = Selected;
            var (fileNode, file, types) = node switch
            {
                ResourceTypeNode t => (t.Parent!, FileOf(t.Parent!), (IReadOnlySet<FourCC>?)new HashSet<FourCC> { t.Type }),
                _ => (node!, FileOf(node!), null),
            };
            await fileNode.EnsureLoadedAsync();
            var fork = fileNode switch { FileNode f => f.Resources?.Fork, InputNode i => i.RawResources?.Fork, _ => null };
            if (fork is null || await PickFolder("Export resources to") is not { } parent) return;
            var target = ExportFolders.CreateNew(parent, HostNames.ToHostName(file.Name) + " resources");
            var source = new ExportSource(file.Name, [], file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags);
            var result = await Task.Run(() => ResourceExporter.Export(fork, target, source, ExportOptionsFor(types)));
            foreach (var d in result.Diagnostics.Skip(fork.Diagnostics.Count)) Report(new DiagnosticEntry(d, fileNode.Source, fileNode));
            Status = $"{result.Manifest.Resources.Count} resources to {target}.";
        });

        [RelayCommand(CanExecute = nameof(CanExtractAll))]
        private Task ExtractAll() => Run(async () =>
        {
            if (Selected is not { } node || await PickFolder("Extract all resources to") is not { } parent) return;
            var root = Subtree(node);
            var target = ExportFolders.CreateNew(parent, HostNames.ToHostName(MacString.FromMacRoman(NameOf(node))) + " resources");
            var total = root.Leaves().Count();
            var progress = new Progress<int>(n => Status = $"Extracting {n} of {total} files…");
            var diagnostics = new List<(string Source, Diagnostic Diagnostic)>();
            var result = await Task.Run(() =>
            {
                var forks = new List<ForkToExtract>();
                foreach (var leaf in root.Leaves())
                {
                    var found = MacFileResources.Read(leaf.File, ReadOptions);
                    if (found.Fork is { Resources.Count: > 0 } fork) forks.Add(new ForkToExtract(leaf, [leaf.Format], fork));
                }
                return Unpacker.Extract(root, forks, target, ExportOptionsFor(null), diagnostics, progress);
            });
            foreach (var (source, d) in diagnostics) Report(new DiagnosticEntry(d, $"{node.Source} › {source}", node));
            foreach (var failure in result.Failed) Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", failure), node.Source, node));
            Status = $"{result.Resources} resources from {result.Files} files to {target}.";
        });

        [RelayCommand(CanExecute = nameof(CanUnpack))]
        private Task UnpackAppleDouble() => Unpack(HostLayout.AppleDouble);

        [RelayCommand(CanExecute = nameof(CanUnpack))]
        private Task UnpackBasilisk() => Unpack(HostLayout.BasiliskII);

        private Task Unpack(HostLayout layout) => Run(async () =>
        {
            if (Selected is not { } node || await PickFolder("Unpack to") is not { } parent) return;
            var root = Subtree(node);
            var target = ExportFolders.CreateNew(parent, HostNames.ToHostName(MacString.FromMacRoman(NameOf(node))) + " unpacked");
            var total = root.Leaves().Count();
            var progress = new Progress<int>(n => Status = $"Unpacking {n} of {total} files…");
            var diagnostics = new List<Diagnostic>();
            var result = await Task.Run(() => Unpacker.Unpack(root, target, HostWriteOptions.Default with { Layout = layout }, diagnostics, progress));
            foreach (var d in diagnostics) Report(new DiagnosticEntry(d, node.Source, node));
            foreach (var failure in result.Failed) Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", failure), node.Source, node));
            Status = $"{result.Files} files ({result.Bytes:N0} bytes) to {target}.";
        });

        // One export at a time; failures to write are reported, not thrown.
        private Task Run(Func<Task> export)
        {
            async Task Guarded()
            {
                IsExporting = true;
                try
                {
                    await export();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", e.Message), Selected?.Source ?? "", Selected));
                    Status = $"Export failed: {e.Message}";
                }
                finally
                {
                    IsExporting = false;
                }
            }
            return ExportTask = Guarded();
        }

        private async Task<string?> PickFolder(string title) => FilePicker is null ? null : await FilePicker.PickFolderAsync(title);

        private ExportOptions ExportOptionsFor(IReadOnlySet<FourCC>? types) =>
            ExportOptions.Default with { Decoders = ResourceDecoders.Create(CurrentDecodeOptions), Types = types, ReadOptions = ReadOptions };

        // The decoded files for a resource, and its data as applications see it.
        private (IReadOnlyList<DecodedFile> Outputs, ReadOnlyMemory<byte> Raw) Decode(ResourceNode node, List<Diagnostic> diagnostics)
        {
            var raw = ResourceDecompression.Default.GetData(node.Resource, node.Fork, ReadOptions, diagnostics);
            var decoder = ResourceDecoders.Create(CurrentDecodeOptions).FirstOrDefault(d => d.CanDecode(node.Resource.Type));
            IReadOnlyList<DecodedFile> outputs = decoder?.Decode(new DecodeInput(node.Resource, raw, node.Fork, ReadOptions, diagnostics)) ?? [];
            // Several numbered images (SICN, PAT#): the first is offered.
            return (outputs.Where(o => !o.Extension.EndsWith(".json", StringComparison.Ordinal) || outputs.Count == 1).ToList(), raw);
        }

        private static MacString Stem(Resource resource)
        {
            var id = Encoding.ASCII.GetBytes(resource.Id.ToString(CultureInfo.InvariantCulture));
            return resource.Name is { } name && name.Bytes.Length > 0 ? new MacString([.. id, (byte)' ', .. name.Bytes]) : new MacString(id);
        }

        private static MacFile FileOf(NodeViewModel node) => node switch
        {
            FileNode f => f.File,
            InputNode i => i.Root.File,
            _ => throw new InvalidOperationException("Not a file."),
        };

        private static string NameOf(NodeViewModel node) => node switch
        {
            InputNode i => Path.GetFileNameWithoutExtension(i.Path),
            ContainerFileNode c => c.File.Name.ToMacRoman(),
            FileNode f => f.File.Name.ToMacRoman(),
            _ => node.Title,
        };

        // The part of the tree a node stands for, as a tree the exporters take: an input or container as it is; a folder
        // as the files and containers under it (folder paths shortened to below it); a file alone.
        private static ContainerNode Subtree(NodeViewModel node)
        {
            switch (node)
            {
                case InputNode input:
                    return input.Root;
                case ContainerFileNode container:
                    return container.Node;
                case FileNode file:
                    return new ContainerNode("selection", file.File, [new ContainerNode(file.Node.Format, file.File with { FolderPath = [] }, [])]);
                case FolderNode folder:
                    var depth = 0;
                    for (NodeViewModel? at = folder; at is FolderNode; at = at.Parent) depth++;
                    var items = new List<ContainerNode>();
                    Collect(folder, items, depth);
                    return new ContainerNode("selection", new MacFile { Name = MacString.FromMacRoman(folder.Title) }, items);
                default:
                    throw new InvalidOperationException("Nothing to export.");
            }
        }

        private static void Collect(NodeViewModel folder, List<ContainerNode> items, int depth)
        {
            foreach (var child in folder.Children)
            {
                var node = child switch { FileNode f => f.Node, ContainerFileNode c => c.Node, _ => null };
                if (node is not null) items.Add(node with { File = node.File with { FolderPath = node.File.FolderPath.Skip(depth).ToList() } });
                else if (child is FolderNode) Collect(child, items, depth);
            }
        }
    }
}
