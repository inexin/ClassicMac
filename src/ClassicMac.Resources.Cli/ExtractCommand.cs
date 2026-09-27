using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Cli
{
    // `extract`: the resources of every file inside the input into a folder with a manifest (ResourceExporter). One
    // fork is written straight into the output folder; several get a folder each, placed as `unpack` places files.
    internal sealed class ExtractCommand(TextWriter output, TextWriter error)
    {
        public int Run(FileInfo input, DirectoryInfo? outputDirectory, ExportOptions options, ContainerReadOptions containerOptions,
            bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            Input opened;
            List<ForkEntry> forks;
            try
            {
                var diagnostics = new List<Diagnostic>();
                opened = Input.Open(input, containerOptions, diagnostics);
                reporter.Write(input.Name, diagnostics);
                forks = opened.Forks(input, options.ReadOptions, reporter)
                    .Where(f => f.Fork is { } fork && fork.Resources.Any(r => options.Types is null || options.Types.Contains(r.Type)))
                    .ToList();
            }
            catch (InvalidDataException e)
            {
                error.WriteLine($"{input.Name}: not a Mac container or a resource fork: {e.Message}");
                return ExitCodes.Unreadable;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            var root = outputDirectory?.FullName
                ?? Path.Combine(input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + " resources");
            var tree = new OutputTree(name => HostNames.ToHostName(name));
            var folders = new Dictionary<ContainerNode, List<string>>(ReferenceEqualityComparer.Instance);
            foreach (var (leaf, folder) in tree.Place(opened.Root)) folders[leaf] = folder;
            int resources = 0, files = 0;
            var ioFailed = false;
            foreach (var entry in forks)
            {
                var file = entry.Node.File;
                var parts = new List<string>();
                if (forks.Count > 1)
                {
                    var folder = folders.GetValueOrDefault(entry.Node) ?? [];
                    parts = [.. folder, tree.Unique(folder, HostNames.ToHostName(file.Name), out _)];
                }
                var directory = Path.Combine([root, .. parts]);
                var relative = string.Join('/', parts).Length + (parts.Count > 0 ? 1 : 0);
                var source = new ExportSource(file.Name, entry.Chain, file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags);
                try
                {
                    var result = ResourceExporter.Export(entry.Fork!, directory, source,
                        options with { MaxPathLength = Math.Max(24, options.MaxPathLength - relative) });
                    // The fork's own diagnostics were reported when it was read.
                    reporter.Write(Input.Source(input, file), result.Diagnostics.Skip(entry.Fork!.Diagnostics.Count));
                    resources += result.Manifest.Resources.Count;
                    files++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error.WriteLine($"{directory}: {e.Message}");
                    ioFailed = true;
                }
            }

            output.WriteLine($"{resources} resources from {files} files, to {root}");
            return ioFailed ? ExitCodes.IoError : reporter.ExitCode;
        }
    }
}
