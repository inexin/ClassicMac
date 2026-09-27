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
                host.File, HostFiles.FormatName(host.Layout), new ContainerContext(options, diagnostics));
            var leaves = new List<(ContainerNode, IReadOnlyList<string>)>();
            Collect(root, [], leaves);
            return new Input(host, root, leaves);
        }

        private static void Collect(ContainerNode node, List<string> chain, List<(ContainerNode, IReadOnlyList<string>)> leaves)
        {
            var here = new List<string>(chain) { node.Format };
            if (node.Children.Count == 0) leaves.Add((node, here));
            foreach (var child in node.Children) Collect(child, here, leaves);
        }
    }

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
