using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;
using ClassicMac.Resources.Decoders.Code;

namespace ClassicMac.Resources.Cli
{
    // `disasm`: every Mac file's code inside the input, by CodeExport: a listing (.s) per 68k segment, code resource and
    // fragment (the data fork's included), and code.json. One file with code is written straight into the output folder;
    // several get a folder each, placed as `unpack` places files.
    internal sealed class DisasmCommand(TextWriter output, TextWriter error)
    {
        public int Run(FileInfo input, DirectoryInfo? outputDirectory, CodeCpu cpu, bool overwrite, ReadOptions readOptions,
            ContainerReadOptions containerOptions, bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            Input opened;
            List<ForkEntry> forks;
            try
            {
                var diagnostics = new List<Diagnostic>();
                opened = Input.Open(input, containerOptions, diagnostics);
                reporter.Write(input.Name, diagnostics);
                forks = opened.Forks(input, readOptions, reporter).Where(f => f.Fork is not null).ToList();
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

            // Listed first, to know whether there are several files with code.
            var listed = new List<(ForkEntry Entry, IReadOnlyList<CodeFile> Files)>();
            foreach (var entry in forks)
            {
                var file = entry.Node.File;
                var diagnostics = new List<Diagnostic>();
                var files = CodeExport.Disassemble(entry.Fork!, () => file.DataFork.ToArray(readOptions.MaxResourceSize), cpu, readOptions, diagnostics);
                reporter.Write(Input.Source(input, file), diagnostics);
                if (files.Count > 0)
                {
                    listed.Add((entry, files));
                }
            }
            if (listed.Count == 0)
            {
                output.WriteLine($"No code in {input.Name}.");
                return reporter.ExitCode;
            }

            var root = outputDirectory?.FullName
                ?? Path.Combine(input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + " code");
            if (!overwrite && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                error.WriteLine($"{root} is not empty (use --overwrite to write into it).");
                return ExitCodes.IoError;
            }
            var layout = new OutputLayout(name => HostNames.ToHostName(name));
            var folders = new Dictionary<ContainerNode, List<string>>(ReferenceEqualityComparer.Instance);
            foreach (var (leaf, place) in layout.Place(opened.Root))
            {
                folders[leaf] = place;
            }

            int listings = 0;
            try
            {
                foreach (var (entry, files) in listed)
                {
                    var parts = new List<string>();
                    if (listed.Count > 1)
                    {
                        var place = folders.GetValueOrDefault(entry.Node) ?? [];
                        parts = [.. place, layout.Unique(place, HostNames.ToHostName(entry.Node.File.Name), out _)];
                    }
                    var target = Path.Combine([root, .. parts]);
                    Directory.CreateDirectory(target);
                    foreach (var file in files)
                    {
                        File.WriteAllBytes(Path.Combine(target, file.Name), file.Content.ToArray());
                    }

                    listings += files.Count(f => f.Name.EndsWith(".s", StringComparison.Ordinal));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine(e.Message);
                return ExitCodes.IoError;
            }
            output.WriteLine($"{listings} listing{(listings == 1 ? "" : "s")} from {listed.Count} file{(listed.Count == 1 ? "" : "s")}, to {root}");
            return reporter.ExitCode;
        }
    }
}
