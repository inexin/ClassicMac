using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files;

/// <summary>
/// The encoding a volume says its files' text is in (docs/formats/codecs/text-encodings.md §2.1, §5): a file's own HFS
/// Plus text encoding hint, else its volume's System file's region. Null where neither says anything but Mac OS Roman.
/// </summary>
public static class FileEncodings
{
    private static readonly FourCC Zsys = FourCC.FromString("zsys"), Macs = FourCC.FromString("MACS"), Vers = FourCC.FromString("vers");

    /// <summary>
    /// The encoding of the system a volume's System file (<c>'zsys'</c>/<c>'MACS'</c>; the one in the blessed folder when
    /// there are several) was made for: its <c>'vers'</c> 1 region's system's encoding (<see cref="MacScripts.EncodingOfRegion"/>).
    /// Null with no System file, an unreadable one, or one for a Mac OS Roman system.
    /// </summary>
    public static MacTextEncoding? OfSystem(IEnumerable<MacFile> files, uint? blessedFolderId)
    {
        ArgumentNullException.ThrowIfNull(files);
        var systems = files.Where(f => f.FinderInfo.Type == Zsys && f.FinderInfo.Creator == Macs).ToList();
        var system = systems.FirstOrDefault(f => blessedFolderId is { } blessed && f.ParentId == blessed) ?? systems.FirstOrDefault();
        if (system is null)
        {
            return null;
        }

        try
        {
            var vers = ResourceFork.Read(system.ResourceFork.ToArray()).Find(Vers, 1)?.GetData();
            if (vers is not { Length: >= 6 } data)
            {
                return null;
            }

            var encoding = MacScripts.EncodingOfRegion(new BigEndianReader(data).ReadUInt16At(4));
            return encoding == MacTextEncoding.Roman ? null : encoding;
        }
        catch (Exception e) when (ExceptionFilters.IsMalformed(e))
        {
            return null;
        }
    }

    /// <summary>
    /// For each node under <paramref name="root"/>, the encoding its volume says its file is in: the file's own
    /// <see cref="MacFile.TextEncoding"/>, else (when the node is on a volume) <see cref="OfSystem"/> of that volume, read
    /// once per volume. Null for a node on no volume, and where neither says anything.
    /// </summary>
    public static Func<ContainerNode, MacTextEncoding?> Of(ContainerNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var volumes = new Dictionary<ContainerNode, ContainerNode>(ReferenceEqualityComparer.Instance);
        void Walk(ContainerNode node, ContainerNode? volume)
        {
            foreach (var child in node.Children)
            {
                var on = node.Volume is not null ? node : volume;
                if (on is not null)
                {
                    volumes[child] = on;
                }

                Walk(child, on);
            }
        }

        Walk(root, null);
        var systems = new Dictionary<ContainerNode, MacTextEncoding?>(ReferenceEqualityComparer.Instance);
        return node =>
        {
            if (node.File.TextEncoding is { } own)
            {
                return own;
            }

            if (!volumes.TryGetValue(node, out var volume))
            {
                return null;
            }

            if (!systems.TryGetValue(volume, out var system))
            {
                systems[volume] = system = OfSystem(Leaves(volume).Select(n => n.File), volume.Volume!.BlessedFolderId);
            }

            return system;
        };
    }

    // A volume's files: its nodes, not those inside its files (a disk image on it is another volume).
    private static IEnumerable<ContainerNode> Leaves(ContainerNode volume) => volume.Children;
}
