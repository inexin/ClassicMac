using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Compression;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One line of the details panel.</summary>
    public sealed record DetailRow(string Label, string Value);

    /// <summary>What the details panel shows for the selected node.</summary>
    public sealed class DetailsViewModel
    {
        private DetailsViewModel(string heading, IReadOnlyList<DetailRow> rows)
        {
            Heading = heading;
            Rows = rows;
        }

        public static DetailsViewModel Empty { get; } = new("", []);

        public string Heading { get; }

        public IReadOnlyList<DetailRow> Rows { get; }

        public static DetailsViewModel For(NodeViewModel? node) => node switch
        {
            InputNode input => Input(input),
            ContainerFileNode container => File(container.File, container.Node.Format, container.Node.Children[0].Format, null),
            FileNode file => File(file.File, file.Node.Format, null, file.Resources),
            FolderNode folder => new DetailsViewModel(folder.Title, [new("Kind", "Folder"), new("Items", folder.Children.Count.ToString(CultureInfo.InvariantCulture))]),
            ResourceTypeNode type => Type(type),
            ResourceNode resource => Resource(resource),
            _ => Empty,
        };

        private static DetailsViewModel Input(InputNode input)
        {
            var rows = new List<DetailRow>
            {
                new("Path", input.Path),
                new("Read as", HostFiles.FormatName(input.Host.Layout)),
            };
            rows.AddRange(input.Host.Companions.Select(c => new DetailRow("With", c)));
            if (input.Root.Children.Count > 0) rows.Add(new("Holds", input.Root.Children[0].Format));
            rows.AddRange(FileRows(input.Root.File));
            if (input.RawResources is { Fork: { } fork }) rows.Add(new("Resources", $"{fork.Resources.Count} in {fork.Types.Count} types (the file is a resource fork)"));
            return new DetailsViewModel(input.Title, rows);
        }

        private static DetailsViewModel File(MacFile file, string foundIn, string? holds, FileResources? resources)
        {
            var rows = new List<DetailRow>
            {
                new("Mac path", file.MacPath),
                new("Found in", foundIn),
            };
            if (holds is not null) rows.Add(new("Holds", holds));
            rows.AddRange(FileRows(file));
            if (resources is { } found)
            {
                rows.Add(new("Resources", found.Fork is { } fork
                    ? $"{fork.Resources.Count} in {fork.Types.Count} types, from the {(found.Source == ResourceForkSource.DataFork ? "data fork" : "resource fork")}"
                    : "none"));
            }
            return new DetailsViewModel(file.Name.ToMacRoman(), rows);
        }

        private static IEnumerable<DetailRow> FileRows(MacFile file)
        {
            var info = file.FinderInfo;
            yield return new("Type / creator", $"'{info.Type}' / '{info.Creator}'");
            yield return new("Finder flags", info.Flags == FinderFlags.None ? "none" : $"${(ushort)info.Flags:X4} ({info.Flags})");
            yield return new("Location", $"{info.Location.V}, {info.Location.H}");
            yield return new("Created", Date(file.Created));
            yield return new("Modified", Date(file.Modified));
            yield return new("Data fork", Bytes(file.DataFork.Length));
            yield return new("Resource fork", Bytes(file.ResourceFork.Length));
        }

        private static DetailsViewModel Type(ResourceTypeNode type)
        {
            var ids = type.Fork.OfType(type.Type).Select(r => r.Id).Order().ToList();
            return new DetailsViewModel($"'{type.Type}'", [
                new("Resources", ids.Count.ToString(CultureInfo.InvariantCulture)),
                new("IDs", string.Join(", ", ids)),
                new("Total size", Bytes(type.Fork.OfType(type.Type).Sum(r => (long)r.Length))),
            ]);
        }

        private static DetailsViewModel Resource(ResourceNode node)
        {
            var resource = node.Resource;
            var rows = new List<DetailRow>
            {
                new("Type", $"'{resource.Type}'"),
                new("ID", resource.Id.ToString(CultureInfo.InvariantCulture)),
                new("Name", resource.Name?.ToMacRoman() ?? "(none)"),
                new("Attributes", resource.Attributes == ResourceAttributes.None ? "none" : resource.Attributes.ToString()),
                new("Stored size", Bytes(resource.Length)),
            };
            if ((resource.Attributes & ResourceAttributes.Compressed) != 0
                && CompressedResourceHeader.TryRead(resource.GetData(), out var header) && header.IsCompressed)
            {
                rows.Add(new("Compressed", $"'dcmp' {header.DecompressorId}, {Bytes(header.DecompressedSize)} when expanded"));
            }
            return new DetailsViewModel(resource.ToString(), rows);
        }

        private static string Date(MacDate? date) =>
            date is { } d ? d.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

        private static string Bytes(long count) => count.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
    }
}
