using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClassicMac.Files;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>An alias file, what resolving it found, and the original's node in the tree (null when not found).</summary>
public sealed record AliasLink(NodeViewModel Alias, AliasResolution Resolution, NodeViewModel? Target)
{
    /// <summary>The original's name: the found file's or folder's, else the one the alias recorded.</summary>
    public string TargetName => Resolution.File?.Name.ToMacRoman() ?? Resolution.Folder?.Name.ToMacRoman() ?? Resolution.Alias.Name.ToMacRoman();

    /// <summary>Where the original is (as Get Info shows a path): now, when found; else where the alias recorded it.</summary>
    public string Path => Resolution.ResolvedPath;
}

/// <summary>
/// Alias files in the tree (docs/formats/resources/aliases.md §5): resolved on the open inputs' volumes, the alias's own
/// first, each volume read once.
/// </summary>
internal static class Aliases
{
    private static readonly ConditionalWeakTable<ContainerNode, VolumeBox> Volumes = [];

    private sealed record VolumeBox(AliasVolume? Volume);

    /// <summary>The alias <paramref name="node"/> is, resolved on the volumes of <paramref name="inputs"/>; null for other nodes or an alias with no record.</summary>
    public static AliasLink? Of(NodeViewModel? node, IEnumerable<InputNode> inputs)
    {
        if (node is null || !node.IsAliasFile)
        {
            return null;
        }

        var file = node switch
        {
            FileNode f => f.File,
            ContainerFileNode c => c.File,
            _ => null,
        };
        if (file is null || AliasResolver.ReadAlias(file, node.Input.Options) is not { } alias)
        {
            return null;
        }

        var volumes = new List<AliasVolume>();
        var own = VolumeOf(node);
        if (own is not null)
        {
            volumes.Add(own);
        }

        volumes.AddRange(inputs.Select(i => Volume(FileKinds.Holder(i.Root), i)).OfType<AliasVolume>().Where(v => !ReferenceEquals(v, own)));
        var resolution = AliasResolver.Resolve(alias, volumes, file);
        return new AliasLink(node, resolution, TargetOf(resolution));
    }

    // The volume holding a node's file, tagged with the tree node of the volume's root (the input or container file).
    private static AliasVolume? VolumeOf(NodeViewModel node)
    {
        var root = HelpPagePreview.RootOf(node);
        return root switch
        {
            InputNode input => Volume(FileKinds.Holder(input.Root), input),
            ContainerFileNode container => Volume(FileKinds.Holder(container.Node), container),
            _ => null,
        };
    }

    private static AliasVolume? Volume(ContainerNode holder, NodeViewModel tag) =>
        Volumes.GetValue(holder, h => new VolumeBox(AliasVolume.Read(h, tag))).Volume;

    // The original's node: from the volume's root node down the folders to its name.
    private static NodeViewModel? TargetOf(AliasResolution resolution)
    {
        if (resolution.Volume?.Tag is not NodeViewModel root)
        {
            return null;
        }

        IEnumerable<string> names = resolution.File is { } file
            ? [.. file.FolderPath.Select(n => n.ToMacRoman()), file.Name.ToMacRoman()]
            : resolution.Folder?.Path.Select(n => n.ToMacRoman()) ?? [];
        return HelpPagePreview.Find(root, [.. names]);
    }
}

// The selected alias: the header's original and Show Original, the preview's strip or not-found card, Details' card.
public sealed partial class AliasActions(IAppSelection appSelection) : ObservableObject
{
    /// <summary>The selected alias file and its original; null when the selection is no alias.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AliasStrip), nameof(AliasNotFound), nameof(AliasNotFoundLines), nameof(AliasNotFoundTitle), nameof(AliasNotFoundReason))]
    [NotifyCanExecuteChangedFor(nameof(ShowOriginalCommand))]
    private AliasLink? selectedAlias;

    /// <summary>The preview's strip over an alias's original ("Alias of Mac OS 9: System Folder: Note Pad"); null otherwise.</summary>
    public string? AliasStrip => SelectedAlias is { Target: not null } link ? $"Alias of {link.Path}" : null;

    /// <summary>Whether the selected alias's original is not found (the preview shows a card instead).</summary>
    public bool AliasNotFound => SelectedAlias is { Target: null };

    /// <summary>The not-found card's title: "Original missing", "On a disk that is not open" or "On a network volume".</summary>
    public string? AliasNotFoundTitle => SelectedAlias is { Target: null } link
        ? link.Resolution.State switch
        {
            AliasState.Missing => "Original missing",
            AliasState.Network => "On a network volume",
            _ => "On a disk that is not open",
        }
        : null;

    /// <summary>The not-found card's sentence: why the original is not found (<see cref="AliasResolution.Explanation"/>).</summary>
    public string? AliasNotFoundReason => SelectedAlias is { Target: null } link ? link.Resolution.Explanation : null;

    /// <summary>The not-found card's lines: the volume, the recorded path and the alias's dates.</summary>
    public IReadOnlyList<string> AliasNotFoundLines => SelectedAlias is { Target: null } link
        ?
        [
            $"Volume: {link.Resolution.Alias.VolumeName.ToMacRoman()}",
            link.Resolution.StoredPath,
            $"Alias created {Date((link.Alias as FileNode)?.File.Created ?? (link.Alias as ContainerFileNode)?.File.Created)}, " +
            $"modified {Date((link.Alias as FileNode)?.File.Modified ?? (link.Alias as ContainerFileNode)?.File.Modified)}",
        ]
        : [];

    private static string Date(ClassicMac.Core.MacDate? date) => date?.ToString() ?? "—";

    private bool CanShowOriginal() => SelectedAlias?.Target is not null;

    /// <summary>File ▸ Show Original (Ctrl+R, the Finder's ⌘R): selects the alias's original, its folders opened.</summary>
    [RelayCommand(CanExecute = nameof(CanShowOriginal))]
    private void ShowOriginal()
    {
        if (SelectedAlias?.Target is not { } target)
        {
            return;
        }

        for (var at = target.Parent; at is not null; at = at.Parent)
        {
            at.IsExpanded = true;
        }

        appSelection.Selected = target;
    }
}
