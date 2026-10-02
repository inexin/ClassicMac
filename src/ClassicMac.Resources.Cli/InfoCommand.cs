using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    // `info`: how the input was read (companions, container chain) and each file's Finder info, dates and fork sizes.
    internal sealed class InfoCommand(TextWriter output, TextWriter error)
    {
        public int Run(FileInfo input, ContainerReadOptions options, bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            Input opened;
            try
            {
                var diagnostics = new List<Diagnostic>();
                opened = Input.Open(input, options, diagnostics);
                reporter.Write(input.Name, diagnostics);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            output.WriteLine($"{input.FullName}");
            foreach (var companion in opened.Host.Companions)
            {
                output.WriteLine($"  with {companion}");
            }

            Write(opened.Root, 0);
            return reporter.ExitCode;
        }

        private void Write(ContainerNode node, int depth)
        {
            var indent = new string(' ', depth * 2);
            var file = node.File;
            var info = file.FinderInfo;
            output.WriteLine($"{indent}{node.Format}: \"{file.MacPath}\"");
            output.WriteLine($"{indent}  type '{info.Type}'  creator '{info.Creator}'  flags {Flags(info.Flags)}");
            if (file.Created is not null || file.Modified is not null)
            {
                output.WriteLine($"{indent}  created {Date(file.Created)}  modified {Date(file.Modified)}");
            }

            output.WriteLine($"{indent}  data fork {file.DataFork.Length} bytes  resource fork {file.ResourceFork.Length} bytes");
            foreach (var child in node.Children)
            {
                Write(child, depth + 1);
            }
        }

        private static string Flags(FinderFlags flags) =>
            flags == FinderFlags.None ? "none" : $"${(ushort)flags:X4} ({flags})";

        private static string Date(MacDate? date) => date?.ToString() ?? "-";
    }
}
