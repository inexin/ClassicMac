using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// Files' kinds as the Finder names them (docs/formats/resources/finder.md §2.3, §5): one resolver per volume, over its
    /// applications and System, made the first time one of its files' kinds is asked for, then the built-in table.
    /// </summary>
    internal static class FileKinds
    {
        private static readonly ConditionalWeakTable<ContainerNode, FinderKindResolver> Resolvers = [];

        /// <summary>The kind of a file (an alias, the Finder's kind of its type, its application's, the table's).</summary>
        public static FinderKind Of(NodeViewModel node)
        {
            var file = node switch
            {
                FileNode f => f.File,
                ContainerFileNode c => c.File,
                _ => throw new ArgumentException("Only files have kinds.", nameof(node)),
            };
            var info = file.FinderInfo;
            if ((info.Flags & FinderFlags.IsAlias) != 0)
            {
                return new FinderKind("alias", FinderKindSource.BuiltIn, null, null);
            }

            return KnownKinds.Resolve(ResolverFor(node), info.Type, info.Creator);
        }

        /// <summary>Where a kind came from, for people: "from SimpleText’s 'kind' 128", "from Teach, by its name", "built-in".</summary>
        public static string Source(FinderKind kind) => KnownKinds.Describe(kind);

        /// <summary>"Kind in owner" with a capital, for the inspector's kind line.</summary>
        public static string Capitalized(string kind) => kind.Length == 0 ? kind : char.ToUpper(kind[0], CultureInfo.InvariantCulture) + kind[1..];

        /// <summary>The resolver of the volume (or container) holding <paramref name="node"/>, or null when it is in none.</summary>
        public static FinderKindResolver? ResolverFor(NodeViewModel node) =>
            Holder(node) is { } holder ? Resolvers.GetValue(holder, h => Make(h, node.Input.Options)) : null;

        /// <summary>Whether the input's volume has a resolver yet (tests: kinds are found only when asked for).</summary>
        public static bool HasResolver(InputNode input) => Holder(input.Root) is { } holder && Resolvers.TryGetValue(holder, out _);

        private static FinderKindResolver Make(ContainerNode holder, ReadOptions readOptions) =>
            FinderKindResolver.ForFiles(holder.Children.Select(c => c.File), f => f.FinderInfo.Type, f => f.FinderInfo.Creator,
                f => (ushort)f.FinderInfo.Flags, f => f.Name.ToMacRoman(), f => Fork(f, readOptions), readOptions);

        private static ResourceFork? Fork(MacFile file, ReadOptions readOptions)
        {
            if (file.ResourceFork.Length == 0)
            {
                return null;
            }

            try
            {
                return MacFileResources.Read(file, readOptions).Fork;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
            {
                return null;
            }
        }

        // The container whose files are the node's neighbours: the input's, or the disk image's or archive's above it; a
        // container holding one file that holds others (a disk image around a disk) stands for that file.
        private static ContainerNode? Holder(NodeViewModel node)
        {
            for (var at = node.Parent; at is not null; at = at.Parent)
            {
                switch (at)
                {
                    case InputNode input:
                        return Holder(input.Root);
                    case ContainerFileNode container:
                        return Holder(container.Node);
                }
            }

            return null;
        }

        private static ContainerNode Holder(ContainerNode holder)
        {
            while (holder is { Children: [{ Children.Count: > 0 } only] })
            {
                holder = only;
            }

            return holder;
        }
    }

    /// <summary>A file's kind and where it came from, found only when shown (the tree's tooltip on "type · creator").</summary>
    internal sealed class KindTip(NodeViewModel node)
    {
        private string? text;

        public override string ToString()
        {
            if (text is null)
            {
                var kind = FileKinds.Of(node);
                text = $"{kind.Text}\n{FileKinds.Source(kind)}";
            }

            return text;
        }
    }
}
