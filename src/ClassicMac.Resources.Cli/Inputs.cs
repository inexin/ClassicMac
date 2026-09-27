using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    // An input file read as the Mac saw it: the host file with its companions, unwrapped through any containers, with
    // the path of formats down to each file.
    internal sealed record Input(HostFile Host, ContainerNode Root, IReadOnlyList<(ContainerNode Node, IReadOnlyList<string> Chain)> Leaves)
    {
        // Whether the input is a plain host file that no container reader recognised: then its data may be a raw
        // resource fork (a .rsrc file, or a fork copied out on its own).
        public bool IsPlain => Host.Layout == HostLayout.Plain && Root.Children.Count == 0;

        public static Input Open(FileInfo file, ContainerReadOptions options, ICollection<Diagnostic> diagnostics)
        {
            var host = HostFiles.Read(file.FullName, options, diagnostics);
            var root = ContainerUnwrapper.Default.Unwrap(
                host.File, HostFiles.FormatName(host.Layout),
                new ContainerContext(options, diagnostics, siblings: HostFiles.Siblings(file.FullName, options, diagnostics)));
            var leaves = new List<(ContainerNode, IReadOnlyList<string>)>();
            Collect(root, [], leaves);
            return new Input(host, root, leaves);
        }

        // The resource forks inside the input, as list and extract read them: a plain file that is no container is
        // itself a resource fork (InvalidDataException when it is not one either); otherwise each leaf's resource
        // fork, or its data fork when that holds a resource fork (Realmz .rsf files). Leaves with neither come back with
        // no fork. Diagnostics go to the reporter.
        public List<ForkEntry> Forks(FileInfo input, ReadOptions options, Reporter reporter)
        {
            var entries = new List<ForkEntry>();
            if (IsPlain && Root.File.ResourceFork.Length == 0)
            {
                var raw = ReadFork(Root.File.DataFork, options);
                reporter.Write(input.Name, raw.Diagnostics);
                entries.Add(new ForkEntry(Root, ["raw resource fork"], raw));
                return entries;
            }
            foreach (var (node, chain) in Leaves)
            {
                if (node.File.ResourceFork.Length == 0 && TryDataForkAsFork(node.File.DataFork, options) is { } inData)
                {
                    reporter.Write(Source(input, node.File), inData.Diagnostics);
                    entries.Add(new ForkEntry(node, [.. chain, "data fork as resource fork"], inData));
                    continue;
                }
                ResourceFork? fork = null;
                if (node.File.ResourceFork.Length > 0)
                {
                    try
                    {
                        fork = ReadFork(node.File.ResourceFork, options);
                        reporter.Write(Source(input, node.File), fork.Diagnostics);
                    }
                    catch (InvalidDataException e)
                    {
                        reporter.Write(Source(input, node.File), [new Diagnostic(DiagnosticSeverity.Error, "fork.unreadable", e.Message)]);
                    }
                }
                entries.Add(new ForkEntry(node, chain, fork));
            }
            return entries;
        }

        internal static ResourceFork ReadFork(ForkData fork, ReadOptions options)
        {
            using var stream = fork.Open();
            return ResourceFork.Read(stream, options);
        }

        // Where a diagnostic came from: the input, and the Mac file inside it when that has another name.
        internal static string Source(FileInfo input, MacFile file)
        {
            var name = file.MacPath;
            return name == input.Name ? input.Name : $"{input.Name} > {name}";
        }

        // A data fork that reads as a resource fork with at least one resource and no errors, or null.
        private static ResourceFork? TryDataForkAsFork(ForkData data, ReadOptions options)
        {
            if (data.Length is < 256 or > ResourceFork.MaxForkLength) return null;
            // A cheap look at the header first, so a volume's thousands of data files are not all read in full: the
            // data and map areas must lie inside the fork, the map after the reserved area.
            var header = data.ReadPrefix(16);
            long dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            long mapOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            long dataLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            long mapLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            if (dataOffset < 16 || mapOffset < 16 || mapLength < 30 || dataOffset + dataLength > data.Length
                || mapOffset + mapLength > data.Length) return null;
            try
            {
                var fork = ReadFork(data, options);
                return fork.Resources.Count > 0 && !fork.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
                    ? fork
                    : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static void Collect(ContainerNode node, List<string> chain, List<(ContainerNode, IReadOnlyList<string>)> leaves)
        {
            var here = new List<string>(chain) { node.Format };
            if (node.Children.Count == 0) leaves.Add((node, here));
            foreach (var child in node.Children) Collect(child, here, leaves);
        }
    }

    // A resource fork found in an input: the file (tree node) it belongs to, the formats down to it, and the fork.
    internal sealed record ForkEntry(ContainerNode Node, IReadOnlyList<string> Chain, ResourceFork? Fork);

    // Diagnostics to stderr and the exit code they imply.
    internal sealed class Reporter(TextWriter error, bool strict, bool quiet)
    {
        public bool Failed { get; private set; }

        public void Write(string source, IEnumerable<Diagnostic> diagnostics)
        {
            foreach (var d in diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error || (strict && d.Severity == DiagnosticSeverity.Warning)) Failed = true;
                if (quiet && d.Severity != DiagnosticSeverity.Error) continue;
                var at = d.Offset is { } offset ? $" at {offset}" : "";
                error.WriteLine($"{source}: {d.Severity.ToString().ToLowerInvariant()}{at}: {d.Message} [{d.Code}]");
            }
        }

        public int ExitCode => Failed ? ExitCodes.Damaged : ExitCodes.Success;
    }
}
