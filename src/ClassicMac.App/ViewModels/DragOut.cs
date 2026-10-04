using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels;

/// <summary>How a file dragged out of the tree carries its resource fork and Finder info.</summary>
public enum DragOutFormat
{
    /// <summary>The data fork as the file, with an AppleDouble <c>._</c> file beside it.</summary>
    AppleDouble,

    /// <summary>A MacBinary III file when there is a resource fork; the data fork alone otherwise.</summary>
    MacBinary,
}

// Drag and drop out of the tree: the dragged item is written into a per-session temporary folder first (file
// managers take dropped files by path), with the same writers as Unpack and Save Resource As, and the window hands
// the paths to the platform's drag. The folder is deleted when the window closes.
public sealed partial class DragOut(IAppServices appServices, IAppParts appParts) : ObservableObject
{
    [ObservableProperty]
    private DragOutFormat dragOutFormat = DragOutFormat.AppleDouble;

    /// <summary>The Export menu's check box: MacBinary rather than AppleDouble.</summary>
    public bool DragOutAsMacBinary
    {
        get => DragOutFormat == DragOutFormat.MacBinary;
        set => DragOutFormat = value ? DragOutFormat.MacBinary : DragOutFormat.AppleDouble;
    }

    partial void OnDragOutFormatChanged(DragOutFormat value) => OnPropertyChanged(nameof(DragOutAsMacBinary));

    /// <summary>The folder dragged items are written into, one subfolder per drag.</summary>
    public string DragFolder { get; set; } = Path.Combine(Path.GetTempPath(), $"ClassicMac-drag-{Environment.ProcessId}");

    /// <summary>Whether <paramref name="node"/> can be dragged out: a file (a container file as it is) or a resource.</summary>
    public static bool CanDragOut(NodeViewModel? node) => node is FileNode or ContainerFileNode or ResourceNode;

    /// <summary>
    /// Writes <paramref name="node"/> as files to drop: a file's data fork with its resource fork and Finder info as
    /// <see cref="DragOutFormat"/> says (the resource edits not yet saved included), or a resource as Save Resource As
    /// writes it by default (its first decoded form, else its data as <c>.bin</c>). Returns the paths, or none when it
    /// cannot be dragged or writing failed (reported).
    /// </summary>
    public async Task<IReadOnlyList<string>> PrepareDragOutAsync(NodeViewModel node)
    {
        if (!CanDragOut(node))
        {
            return [];
        }

        var folder = Path.Combine(DragFolder, Guid.NewGuid().ToString("N"));
        var diagnostics = new List<Diagnostic>();
        // The status bar says what is written while the drag waits for it (boards/browse-tree.md, drag source).
        var before = appServices.Status;
        appServices.Status = node is ResourceNode ? $"Writing {node.BaseTitle}…"
            : DragOutFormat == DragOutFormat.MacBinary ? "Writing MacBinary…" : "Writing AppleDouble…";
        try
        {
            Directory.CreateDirectory(folder);
            IReadOnlyList<string> paths = node switch
            {
                ResourceNode resource => await Task.Run(() => WriteResource(resource, folder, diagnostics)),
                FileNode file => await Task.Run(() => WriteFile(WithEdits(file), folder)),
                ContainerFileNode container => await Task.Run(() => WriteFile(container.File, folder)),
                _ => [],
            };
            foreach (var d in diagnostics)
            {
                appServices.Report(new DiagnosticEntry(d, node.Source, node));
            }

            appServices.Status = before;
            return paths;
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e) || e is InvalidDataException)
        {
            appServices.Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "export.failed", e.Message), node.Source, node));
            appServices.Status = $"{node.BaseTitle} could not be dragged out: {e.Message}";
            return [];
        }
    }

    /// <summary>Deletes the drag folder (when the window closes); a file still in use is left for the system's cleanup.</summary>
    public void CleanUpDragOut()
    {
        try
        {
            if (Directory.Exists(DragFolder))
            {
                Directory.Delete(DragFolder, recursive: true);
            }
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
        }
    }

    // The file with its resource edits, saved or not.
    private static MacFile WithEdits(FileNode node)
    {
        if (node.Editing is not { } state)
        {
            return node.File;
        }

        var fork = ForkData.FromBytes(state.Session.Fork.ToArray());
        return state.ForkInDataFork ? node.File with { DataFork = fork } : node.File with { ResourceFork = fork };
    }

    private IReadOnlyList<string> WriteFile(MacFile file, string folder)
    {
        if (DragOutFormat == DragOutFormat.MacBinary)
        {
            var hasResources = file.ResourceFork.Length > 0;
            var path = Path.Combine(folder, HostNames.ToHostName(file.Name, 200) + (hasResources ? ".bin" : ""));
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (hasResources)
                {
                    MacBinaryWriter.Write(file, output);
                }
                else
                {
                    using var data = file.DataFork.Open();
                    data.CopyTo(output);
                }
            }
            return [path];
        }
        return HostFiles.Write(file, folder, HostWriteOptions.Default with { Layout = HostLayout.AppleDouble }, HostNames.ToHostName(file.Name, 200));
    }

    private IReadOnlyList<string> WriteResource(ResourceNode node, string folder, List<Diagnostic> diagnostics)
    {
        var (outputs, raw) = appParts.ExportActions.Decode(node, diagnostics);
        var chosen = outputs.FirstOrDefault();
        var path = Path.Combine(folder, HostNames.ToHostName(ExportActions.Stem(node.Resource), 200) + (chosen?.Extension ?? ".bin"));
        File.WriteAllBytes(path, (chosen?.Content ?? raw).ToArray());
        return [path];
    }
}
