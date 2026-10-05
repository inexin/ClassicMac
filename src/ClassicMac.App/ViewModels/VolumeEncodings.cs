using System.Linq;
using System.Runtime.CompilerServices;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// The encoding a node's volume says its file's text is in (docs/formats/codecs/text-encodings.md §5): the file's own
/// hint, else its volume's System file's region (read once per volume). The app reads in it while its own encoding is
/// the default Mac OS Roman.
/// </summary>
internal static class VolumeEncodings
{
    private static readonly ConditionalWeakTable<ContainerNode, StrongBox<MacTextEncoding?>> Systems = [];

    /// <summary>The encoding to read <paramref name="node"/>'s text in when the app's is <paramref name="chosen"/>.</summary>
    public static MacTextEncoding For(NodeViewModel? node, MacTextEncoding chosen) =>
        chosen == MacTextEncoding.Roman && node is not null && Of(node) is { } said ? said : chosen;

    /// <summary>What <paramref name="node"/>'s file's volume says, or null.</summary>
    public static MacTextEncoding? Of(NodeViewModel node)
    {
        // The file the node is, or is in.
        ContainerNode? file = null;
        var at = node;
        for (; at is not null; at = at.Parent)
        {
            file = at switch
            {
                FileNode f => f.Node,
                ContainerFileNode c => c.Node,
                _ => null,
            };
            if (file is not null)
            {
                break;
            }
        }

        if (file is null)
        {
            return null;
        }

        if (file.File.TextEncoding is { } own)
        {
            return own;
        }

        // The volume holding it: the nearest container above, past the wrappers that hold one thing each.
        for (var above = at!.Parent; above is not null; above = above.Parent)
        {
            var container = above switch
            {
                ContainerFileNode c => c.Node,
                InputNode i => i.Root,
                _ => null,
            };
            if (container is null)
            {
                continue;
            }

            for (var volume = container; volume is not null; volume = volume.Children is [var only] ? only : null)
            {
                if (volume.Volume is { } info)
                {
                    return Systems.GetValue(volume, v => new StrongBox<MacTextEncoding?>(FileEncodings.OfSystem(v.Children.Select(c => c.File), info.BlessedFolderId))).Value;
                }
            }

            return null;
        }

        return null;
    }
}
