using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    // `unpack`: every Mac file inside the input, through containers and disk images, written to a folder with both
    // forks and Finder info (AppleDouble or Basilisk II layout). Folders inside volumes become folders; a container
    // that holds one file in the end (MacBinary, BinHex, …) is replaced by that file; one that holds several, or
    // folders (a disk image or archive inside a volume), becomes a folder named after it.
    internal sealed class UnpackCommand(TextWriter output, TextWriter error)
    {
        // Room kept in the path for the companion's prefix (".rsrc/" or "._").
        private const int CompanionRoom = 7;

        private readonly Dictionary<string, HashSet<string>> taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);
        private HostLayout layout;
        private int files;
        private long bytes;
        private bool ioFailed;

        public int Run(FileInfo input, DirectoryInfo? outputDirectory, HostWriteOptions options, ContainerReadOptions readOptions,
            bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            layout = options.Layout;
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
            var diagnosticsOut = new List<Diagnostic>();
            if (opened.Root.Children.Count == 0) Write(opened.Root.File, root, [], options, diagnosticsOut);
            else Walk(opened.Root, root, [], null, options, diagnosticsOut);
            reporter.Write(input.Name, diagnosticsOut);

            output.WriteLine($"{files} files, {bytes} bytes, to {root}");
            return ioFailed ? ExitCodes.IoError : reporter.ExitCode;
        }

        // pending: the name of the outermost container in a chain of single-child wrappers (Inner.img → NDIF → disk),
        // which names the folder once the chain reaches a node holding several files.
        private void Walk(ContainerNode node, string root, List<string> folder, MacString? pending, HostWriteOptions options, List<Diagnostic> diagnostics)
        {
            foreach (var child in node.Children)
            {
                var here = new List<string>(folder);
                foreach (var part in child.File.FolderPath) here.Add(FolderName(root, here, part));
                if (child.Children.Count == 0)
                {
                    Write(child.File, root, here, options, diagnostics);
                    continue;
                }
                // A container that ends up holding several files, or folders (a disk image, an archive), becomes a
                // folder named after it; one that holds a single file is replaced by it.
                if (!HoldsSeveral(child)) Walk(child, root, here, null, options, diagnostics);
                else if (child.Children.Count == 1) Walk(child, root, here, pending ?? child.File.Name, options, diagnostics);
                else
                {
                    here.Add(FolderName(root, here, pending ?? child.File.Name));
                    Walk(child, root, here, null, options, diagnostics);
                }
            }
        }

        private static bool HoldsSeveral(ContainerNode node) =>
            node.Leaves().Skip(1).Any() || Descendants(node).Any(d => d.File.FolderPath.Count > 0);

        private static IEnumerable<ContainerNode> Descendants(ContainerNode node) =>
            node.Children.SelectMany(c => Descendants(c).Prepend(c));

        private string FolderName(string root, List<string> parents, MacString name)
        {
            var directory = Path.Combine([root, .. parents]);
            var host = HostFiles.ToHostName(name, layout);
            // A folder met again (it holds several files) keeps the name it was given.
            var key = directory + "/" + host;
            if (!folders.TryGetValue(key, out var chosen)) folders[key] = chosen = HostNames.MakeUnique(host, Taken(directory));
            return chosen;
        }

        private HashSet<string> Taken(string directory)
        {
            if (!taken.TryGetValue(directory, out var set)) taken[directory] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return set;
        }

        private void Write(MacFile file, string root, List<string> folder, HostWriteOptions options, List<Diagnostic> diagnostics)
        {
            var directory = Path.Combine([root, .. folder]);
            var relative = string.Join('/', folder).Length + (folder.Count > 0 ? 1 : 0);
            var host = HostFiles.ToHostName(file.Name, options.Layout, Math.Max(8, options.MaxPathLength - relative - CompanionRoom));
            if (options.Layout == HostLayout.BasiliskII && HostFiles.ToMacName(host, basilisk: true, new ContainerContext()) != file.Name)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-changed",
                    $"\"{file.MacPath}\" is written as \"{host}\": SheepShaver's shared folders cannot hold its name as it is.", null));
            }
            var set = Taken(directory);
            var name = HostNames.MakeUnique(host, set);
            if (name != host)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-collision",
                    $"\"{file.MacPath}\" is written as \"{name}\": \"{host}\" is taken in that folder.", null));
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
    }
}
