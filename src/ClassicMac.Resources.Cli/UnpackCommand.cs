using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    // `unpack`: every Mac file inside the input, through containers and disk images, written to a folder with both
    // forks and Finder info (AppleDouble or Basilisk II layout), placed as OutputTree says.
    internal sealed class UnpackCommand(TextWriter output, TextWriter error)
    {
        // Room kept in the path for the companion's prefix (".rsrc/" or "._").
        private const int CompanionRoom = 7;

        public int Run(FileInfo input, DirectoryInfo? outputDirectory, HostWriteOptions options, ContainerReadOptions readOptions,
            bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            Input opened;
            try
            {
                var diagnostics = new List<Diagnostic>();
                opened = Input.Open(input, readOptions with { TimeZone = options.TimeZone }, diagnostics);
                reporter.Write(input.Name, diagnostics);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            var root = outputDirectory?.FullName
                ?? Path.Combine(input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + " unpacked");
            var tree = new OutputTree(name => HostFiles.ToHostName(name, options.Layout));
            var diagnosticsOut = new List<Diagnostic>();
            int files = 0;
            long bytes = 0;
            var ioFailed = false;
            foreach (var (leaf, folder) in tree.Place(opened.Root))
            {
                var file = leaf.File;
                var directory = Path.Combine([root, .. folder]);
                var relative = string.Join('/', folder).Length + (folder.Count > 0 ? 1 : 0);
                var host = HostFiles.ToHostName(file.Name, options.Layout, Math.Max(8, options.MaxPathLength - relative - CompanionRoom));
                if (options.Layout == HostLayout.BasiliskII && HostFiles.ToMacName(host, basilisk: true, new ContainerContext()) != file.Name)
                {
                    diagnosticsOut.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-changed",
                        $"\"{file.MacPath}\" is written as \"{host}\": SheepShaver's shared folders cannot hold its name as it is."));
                }
                var name = tree.Unique(folder, host, out var changed);
                if (changed)
                {
                    diagnosticsOut.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-collision",
                        $"\"{file.MacPath}\" is written as \"{name}\": \"{host}\" is taken in that folder."));
                }
                try
                {
                    HostFiles.Write(file, directory, options, name);
                    files++;
                    bytes += file.DataFork.Length + file.ResourceFork.Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    error.WriteLine($"{Path.Combine(directory, name)}: {e.Message}");
                    ioFailed = true;
                }
            }
            reporter.Write(input.Name, diagnosticsOut);

            output.WriteLine($"{files} files, {bytes} bytes, to {root}");
            return ioFailed ? ExitCodes.IoError : reporter.ExitCode;
        }
    }
}
