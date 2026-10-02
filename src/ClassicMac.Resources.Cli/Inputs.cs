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
                var diagnostics = new List<Diagnostic>();
                var raw = MacFileResources.ReadRaw(Root.File.DataFork, options, diagnostics);
                reporter.Write(input.Name, diagnostics);
                entries.Add(new ForkEntry(Root, ["raw resource fork"], raw.Fork));
                return entries;
            }
            foreach (var (node, chain) in Leaves)
            {
                var diagnostics = new List<Diagnostic>();
                var found = MacFileResources.Read(node.File, options, diagnostics);
                reporter.Write(Source(input, node.File), diagnostics);
                entries.Add(new ForkEntry(node, found.Source == ResourceForkSource.DataFork ? [.. chain, "data fork as resource fork"] : chain, found.Fork));
            }
            return entries;
        }

        // A leaf's Mac path within the input: its own path, under the folders of the files that wrap it alone. A file
        // in an archive's folder that is itself a container (a DiskDoubler or MacBinary file) comes out of that
        // container with no folders; the folders are the wrapper's, as unpacking places it.
        private Dictionary<ContainerNode, ContainerNode>? parents;

        public string MacPath(ContainerNode leaf)
        {
            if (parents is null)
            {
                parents = new Dictionary<ContainerNode, ContainerNode>(ReferenceEqualityComparer.Instance);
                MapParents(Root, parents);
            }
            var folders = new List<string>();
            for (var node = leaf; parents.TryGetValue(node, out var parent) && parent != Root && parent.Children.Count == 1; node = parent)
            {
                var file = parent.File;
                folders.InsertRange(0, file.UnicodeFolderPath ?? file.FolderPath.Select(n => n.ToString()).ToArray());
            }
            return string.Join(":", folders.Append(leaf.File.MacPath));
        }

        private static void MapParents(ContainerNode node, Dictionary<ContainerNode, ContainerNode> parents)
        {
            foreach (var child in node.Children)
            {
                parents[child] = node;
                MapParents(child, parents);
            }
        }

        // Where a diagnostic came from: the input, and the Mac file inside it when that has another name.
        internal static string Source(FileInfo input, MacFile file)
        {
            var name = file.MacPath;
            return name == input.Name ? input.Name : $"{input.Name} > {name}";
        }

        private static void Collect(ContainerNode node, List<string> chain, List<(ContainerNode, IReadOnlyList<string>)> leaves)
        {
            var here = new List<string>(chain) { node.Format };
            if (node.Children.Count == 0)
            {
                leaves.Add((node, here));
            }

            foreach (var child in node.Children)
            {
                Collect(child, here, leaves);
            }
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
                if (d.Severity == DiagnosticSeverity.Error || (strict && d.Severity == DiagnosticSeverity.Warning))
                {
                    Failed = true;
                }

                if (quiet && d.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                var at = d.Offset is { } offset ? $" at {offset}" : "";
                var where = d.Location is { } location ? $"{source} > {location}" : source;
                error.WriteLine($"{where}: {d.Severity.ToString().ToLowerInvariant()}{at}: {d.Message} [{d.Code}]");
            }
        }

        public int ExitCode => Failed ? ExitCodes.Damaged : ExitCodes.Success;
    }
}
