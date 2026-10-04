using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Export;

/// <summary>
/// Where each file of an unwrapped input goes in an output folder, for unpacking and extracting: folders inside
/// volumes become folders; a container that holds one file in the end (MacBinary, BinHex, …) is replaced by that
/// file; one that holds several, or folders (a disk image or archive inside a volume), becomes a folder named after
/// it. Names are made host-safe by the given function and kept distinct within each folder.
/// </summary>
public sealed class OutputLayout(Func<MacString, string> hostName)
{
    private readonly Dictionary<string, HashSet<string>> taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every leaf of <paramref name="root"/> with the folders (host names, outermost first) it goes into; a lone file goes to the top.</summary>
    public List<(ContainerNode Leaf, List<string> Folder)> Place(ContainerNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var placed = new List<(ContainerNode, List<string>)>();
        if (root.Children.Count == 0)
        {
            placed.Add((root, []));
        }
        else
        {
            Walk(root, [], null, placed);
        }

        return placed;
    }

    /// <summary>A name for a file or folder in <paramref name="folder"/>, distinct from the others there (" ~2" …).</summary>
    public string Unique(List<string> folder, string name, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var unique = HostNames.MakeUnique(name, Taken(folder));
        changed = unique != name;
        return unique;
    }

    // pending: the name of the outermost container in a chain of single-child wrappers (Inner.img → NDIF → disk),
    // which names the folder once the chain reaches a node holding several files.
    private void Walk(ContainerNode node, List<string> folder, MacString? pending, List<(ContainerNode, List<string>)> placed)
    {
        foreach (var child in node.Children)
        {
            var here = new List<string>(folder);
            foreach (var part in child.File.FolderPath)
            {
                here.Add(FolderName(here, part));
            }

            if (child.Children.Count == 0)
            {
                placed.Add((child, here));
            }
            else if (!HoldsSeveral(child))
            {
                Walk(child, here, null, placed);
            }
            else if (child.Children.Count == 1)
            {
                Walk(child, here, pending ?? child.File.Name, placed);
            }
            else
            {
                here.Add(FolderName(here, pending ?? child.File.Name));
                Walk(child, here, null, placed);
            }
        }
    }

    private static bool HoldsSeveral(ContainerNode node) =>
        node.Leaves().Skip(1).Any() || Descendants(node).Any(d => d.File.FolderPath.Count > 0);

    private static IEnumerable<ContainerNode> Descendants(ContainerNode node) =>
        node.Children.SelectMany(c => Descendants(c).Prepend(c));

    // A folder met again (it holds several files) keeps the name it was given.
    private string FolderName(List<string> parents, MacString name)
    {
        var host = hostName(name);
        var key = string.Join('/', parents) + "/" + host;
        if (!folders.TryGetValue(key, out var chosen))
        {
            folders[key] = chosen = HostNames.MakeUnique(host, Taken(parents));
        }

        return chosen;
    }

    private HashSet<string> Taken(List<string> folder)
    {
        var key = string.Join('/', folder);
        if (!taken.TryGetValue(key, out var set))
        {
            taken[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return set;
    }
}
