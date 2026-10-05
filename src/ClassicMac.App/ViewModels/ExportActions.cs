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
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// Export from the viewer, with the same code as the CLI: a resource saved as a file; a file's resources (or one
// type's) exported with a manifest, a document among them as HTML; everything under a node extracted, unpacked or
// its documents converted. Folder exports go into a new subfolder named after the item, so nothing is overwritten.
public sealed partial class ExportActions(IAppSelection appSelection, IAppServices appServices, IAppView appView, IAppParts appParts) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveResourceAsCommand), nameof(ExportResourcesCommand), nameof(ExtractAllCommand), nameof(UnpackAppleDoubleCommand), nameof(UnpackBasiliskCommand), nameof(ConvertDocumentsCommand))]
    private bool isExporting;

    partial void OnIsExportingChanged(bool value)
    {
        appParts.VolumeActions.NewFileCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.ImportFileCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.NewFolderCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.DeleteItemCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.FirstAidCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.DefragmentCommand.NotifyCanExecuteChanged();
        appParts.VolumeActions.ResizeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The last export's task (tests wait for it).</summary>
    internal Task ExportTask { get; private set; } = Task.CompletedTask;

    internal DecodeOptions CurrentDecodeOptions => DecodeOptions.Default with
    {
        ScreenDepth = appView.ScreenDepth,
        QuickDraw = appServices.ReadOptions.ResourceManager,
        TextEncoding = appView.TextEncoding,
        ImageEncoder = appView.WebPImages ? WebPEncoder.Instance : PngEncoder.Instance,
        LoadableFonts = appView.LoadableFonts,
    };

    private bool CanSaveResource() => !IsExporting && appSelection.Selected is ResourceNode;

    private bool CanExportResources() => !IsExporting && appSelection.Selected is FileNode or ResourceTypeNode or InputNode { Root.Children.Count: 0 };

    private bool CanExtractAll() => !IsExporting && appSelection.Selected is InputNode or ContainerFileNode or FolderNode;

    private bool CanUnpack() => !IsExporting && appSelection.Selected is InputNode or ContainerFileNode or FolderNode or FileNode;

    [RelayCommand(CanExecute = nameof(CanSaveResource))]
    private Task SaveResourceAs() => Run(async () =>
    {
        if (appSelection.Selected is not ResourceNode node || appServices.FilePicker is null)
        {
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var (outputs, raw) = await Task.Run(() => Decode(node, diagnostics));
        var extensions = outputs.Select(o => o.Extension).Append(".bin").ToList();
        var stem = HostNames.ToHostName(Stem(node.Resource), 200);
        var path = await appServices.FilePicker.PickSaveFileAsync($"Save {node.Resource}", stem + extensions[0], extensions);
        if (path is null)
        {
            return;
        }

        var chosen = outputs.FirstOrDefault(o => string.Equals(o.Extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));
        await File.WriteAllBytesAsync(path, (chosen?.Content ?? raw).ToArray());
        foreach (var d in diagnostics)
        {
            appServices.Report(new DiagnosticEntry(d, node.Source, node));
        }

        appServices.Status = $"Saved {node.Resource} to {path}.";
    });

    [RelayCommand(CanExecute = nameof(CanExportResources))]
    private Task ExportResources() => Run(async () =>
    {
        var node = appSelection.Selected;
        var (fileNode, file, types) = node switch
        {
            ResourceTypeNode t => (t.Parent!, FileOf(t.Parent!), (IReadOnlySet<FourCC>?)new HashSet<FourCC> { t.Type }),
            _ => (node!, FileOf(node!), null),
        };
        await fileNode.EnsureLoadedAsync();
        var fork = fileNode switch { FileNode f => f.Resources?.Fork, InputNode i => i.RawResources?.Fork, _ => null };
        if (fork is null || await PickFolder("Export resources to") is not { } parent)
        {
            return;
        }

        var target = ExportFolders.CreateNew(parent, HostNames.ToHostName(file.Name) + " resources");
        var source = new ExportSource(file.Name, [], file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags)
        {
            TextEncoding = VolumeEncodings.Of(fileNode),
        };
        var progress = appParts.StatusLine.BeginProgress($"Exporting {file.Name.ToMacRoman()}…", fork.Resources.Count(r => types is null || types.Contains(r.Type)));
        ExportResult result;
        try
        {
            result = await Task.Run(() => ResourceExporter.Export(fork, target, source, ExportOptionsFor(types),
                () => file.DataFork.ToArray(appServices.ReadOptions.MaxResourceSize), progress));
        }
        finally
        {
            progress.Finish(null);
        }

        foreach (var d in result.Diagnostics.Skip(fork.Diagnostics.Count))
        {
            appServices.Report(new DiagnosticEntry(d, fileNode.Source, fileNode));
        }

        appServices.Status = $"{result.Manifest.Resources.Count} resources to {target}.";
    });

    [RelayCommand(CanExecute = nameof(CanExtractAll))]
    private Task ExtractAll() => Run(async () =>
    {
        if (appSelection.Selected is not { } node || await PickFolder("Extract all resources to") is not { } parent)
        {
            return;
        }

        var root = await Whole(node);
        var target = ExportFolders.CreateNew(parent, HostNameOf(node) + " resources");
        var progress = appParts.StatusLine.BeginProgress($"Extracting {NameOf(node)}…", root.Leaves().Count());
        var diagnostics = new List<(string Source, Diagnostic Diagnostic)>();
        var result = await Task.Run(() =>
        {
            var forks = new List<ForkToExtract>();
            foreach (var leaf in root.Leaves())
            {
                var found = MacFileResources.Read(leaf.File, appServices.ReadOptions);
                if (found.Fork is { Resources.Count: > 0 } fork)
                {
                    forks.Add(new ForkToExtract(leaf, [leaf.Format], fork));
                }
            }
            return Unpacker.Extract(root, forks, target, ExportOptionsFor(null), diagnostics, progress);
        });
        foreach (var (source, d) in diagnostics)
        {
            appServices.Report(new DiagnosticEntry(d, $"{node.Source} › {source}", node));
        }

        foreach (var failure in result.Failed)
        {
            appServices.Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", failure), node.Source, node));
        }

        progress.Finish($"{result.Resources} resources from {result.Files} files to {target}.");
    });

    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private Task ConvertDocuments() => Run(async () =>
    {
        if (appSelection.Selected is not { } node || await PickFolder("Convert documents to") is not { } parent)
        {
            return;
        }

        var root = await Whole(node);
        var name = HostNameOf(node);
        var diagnostics = new List<(string Source, Diagnostic Diagnostic)>();
        var progress = appParts.StatusLine.BeginProgress($"Converting {NameOf(node)}…", root.Leaves().Count());
        var (target, result) = await Task.Run(() =>
        {
            var forks = new List<ForkToExtract>();
            foreach (var leaf in root.Leaves())
            {
                var found = MacFileResources.Read(leaf.File, appServices.ReadOptions);
                if (found.Fork is { Resources.Count: > 0 } fork)
                {
                    forks.Add(new ForkToExtract(leaf, [leaf.Format], fork));
                }
                else if (ClassicMac.Resources.Decoders.DataForkDocuments.Applies(leaf.File.FinderInfo.Type))
                {
                    // A Word document or an AIFF sound is its data fork.
                    forks.Add(new ForkToExtract(leaf, [leaf.Format], found.Fork ?? new ResourceFork()));
                }
            }
            var target = ExportFolders.CreateNew(parent, name + " documents");
            var result = DocumentConverter.Convert(root, forks, target, ResourceDecoders.CreateDocumentConverters(CurrentDecodeOptions),
                appServices.ReadOptions, overwrite: false, diagnostics, progress);
            // No documents: the new folder, still empty, is not left behind.
            if (result.Documents.Count == 0 && result.Failed.Count == 0)
            {
                Directory.Delete(target);
            }

            return (target, result);
        });
        foreach (var (source, d) in diagnostics)
        {
            appServices.Report(new DiagnosticEntry(d, $"{node.Source} › {source}", node));
        }

        foreach (var failure in result.Failed)
        {
            appServices.Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", failure), node.Source, node));
        }

        progress.Finish(null);
        appServices.Status = result.Documents.Count switch
        {
            0 => $"No documents in {NameOf(node)}.",
            1 => $"1 document to {target}.",
            var n => $"{n} documents to {target}.",
        };
    });

    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private Task UnpackAppleDouble() => Unpack(HostLayout.AppleDouble);

    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private Task UnpackBasilisk() => Unpack(HostLayout.BasiliskII);

    private Task Unpack(HostLayout layout) => Run(async () =>
    {
        if (appSelection.Selected is not { } node || await PickFolder("Unpack to") is not { } parent)
        {
            return;
        }

        var root = await Whole(node);
        var target = ExportFolders.CreateNew(parent, HostNameOf(node) + " unpacked");
        var progress = appParts.StatusLine.BeginProgress($"Unpacking {NameOf(node)}…", root.Leaves().Count());
        var diagnostics = new List<Diagnostic>();
        var result = await Task.Run(() => Unpacker.Unpack(root, target, HostWriteOptions.Default with { Layout = layout, NameEncoding = appView.TextEncoding }, diagnostics, progress));
        foreach (var d in diagnostics)
        {
            appServices.Report(new DiagnosticEntry(d, node.Source, node));
        }

        foreach (var failure in result.Failed)
        {
            appServices.Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", failure), node.Source, node));
        }

        progress.Finish($"{result.Files} files ({result.Bytes:N0} bytes) to {target}.");
    });

    // One export at a time; failures to write are reported, not thrown.
    internal Task Run(Func<Task> export)
    {
        async Task Guarded()
        {
            IsExporting = true;
            try
            {
                await export();
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e) || e is InvalidDataException)
            {
                appServices.Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", e.Message), appSelection.Selected?.Source ?? "", appSelection.Selected));
                appServices.Status = $"Export failed: {e.Message}";
            }
            finally
            {
                IsExporting = false;
                appParts.StatusLine.ProgressText = null;         // ended, failed or not
            }
        }
        return ExportTask = Guarded();
    }

    private async Task<string?> PickFolder(string title) => appServices.FilePicker is null ? null : await appServices.FilePicker.PickFolderAsync(title);

    private ExportOptions ExportOptionsFor(IReadOnlySet<FourCC>? types) =>
        ExportOptions.Default with
        {
            NameEncoding = appView.TextEncoding,
            Decoders = ResourceDecoders.Create(CurrentDecodeOptions),
            Documents = ResourceDecoders.CreateDocumentConverters(CurrentDecodeOptions),
            // A file whose volume says its encoding is decoded in it (text-encodings.md §5).
            DecodersFor = ResourceDecoders.CreateFor(CurrentDecodeOptions),
            DocumentsFor = ResourceDecoders.CreateDocumentConvertersFor(CurrentDecodeOptions),
            Types = types,
            ReadOptions = appServices.ReadOptions,
        };

    // The decoded files for a resource, and its data as applications see it.
    internal (IReadOnlyList<DecodedFile> Outputs, ReadOnlyMemory<byte> Raw) Decode(ResourceNode node, List<Diagnostic> diagnostics)
    {
        var raw = ResourceDecompression.Default.GetData(node.Resource, node.Fork, appServices.ReadOptions, diagnostics);
        var options = CurrentDecodeOptions with { TextEncoding = VolumeEncodings.For(node, appView.TextEncoding) };
        var decoder = ResourceDecoders.Create(options).FirstOrDefault(d => d.CanDecode(node.Resource.Type));
        IReadOnlyList<DecodedFile> outputs = decoder?.Decode(new DecodeInput(node.Resource, raw, node.Fork, appServices.ReadOptions, diagnostics)) ?? [];
        // One file per kind, named by its last extension: the first of several numbered images (SICN, PAT#: ".1.png"
        // is offered as ".png"), and a sidecar JSON only when it is the only output.
        var offered = outputs
            .Where(o => !o.Extension.EndsWith(".json", StringComparison.Ordinal) || outputs.Count == 1)
            .GroupBy(o => Path.GetExtension(o.Extension), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with { Extension = g.Key })
            .ToList();
        return (offered, raw);
    }

    internal static MacString Stem(Resource resource)
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
        ContainerFileNode c => NodeViewModel.NameText(c.Parent!, c.File.Name),
        FileNode f => NodeViewModel.NameText(f.Parent!, f.File.Name),
        FolderNode { MacName: { } name } folder => NodeViewModel.NameText(folder.Parent!, name),
        _ => node.Title,
    };

    // A host name for what a node holds: the input's own file name, else its Mac name made a host name in the app's
    // encoding (text-encodings.md §5); a name with no Mac bytes (a grouping) as Mac OS Roman text, '?' for the rest.
    private string HostNameOf(NodeViewModel node) => node switch
    {
        InputNode i => Path.GetFileNameWithoutExtension(i.Path),
        _ => HostNames.ToHostName(MacNameOf(node), appView.TextEncoding),
    };

    private static MacString MacNameOf(NodeViewModel node) => node switch
    {
        ContainerFileNode c => c.File.Name,
        FileNode f => f.File.Name,
        FolderNode { MacName: { } name } => name,
        _ => new MacString([.. node.Title.Select(c => MacRoman.TryGetByte(c, out var b) ? b : (byte)'?').Take(255)]),
    };

    // The part of the tree a node stands for with the containers not yet expanded read (off the UI thread), as
    // exports take everything; what reading them finds is reported at the node.
    private async Task<ContainerNode> Whole(NodeViewModel node)
    {
        var part = Subtree(node);
        var diagnostics = new List<Diagnostic>();
        var options = appServices.ContainerOptions;
        var whole = await Task.Run(() => ContainerUnwrapper.Default.Expand(part, new ContainerContext(options, diagnostics)));
        foreach (var d in diagnostics)
        {
            appServices.Report(new DiagnosticEntry(d, Tree.SourceOf(node, d), node));
        }

        return whole;
    }

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
                for (NodeViewModel? at = folder; at is FolderNode; at = at.Parent)
                {
                    depth++;
                }

                var items = new List<ContainerNode>();
                Collect(folder, items, depth);
                return new ContainerNode("selection", new MacFile { Name = MacNameOf(folder) }, items);
            default:
                throw new InvalidOperationException("Nothing to export.");
        }
    }

    private static void Collect(NodeViewModel folder, List<ContainerNode> items, int depth)
    {
        foreach (var child in TreeLayout.Contents(folder))
        {
            var node = child switch { FileNode f => f.Node, ContainerFileNode c => c.Node, _ => null };
            if (node is not null)
            {
                items.Add(node with { File = node.File with { FolderPath = node.File.FolderPath.Skip(depth).ToList() } });
            }
            else if (child is FolderNode)
            {
                Collect(child, items, depth);
            }
        }
    }
}
