using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;

namespace ClassicMac.Resources.Cli
{
    // `unpack`: every Mac file inside the input, through containers and disk images, written to a folder with both
    // forks and Finder info (AppleDouble or Basilisk II layout), by Unpacker.
    internal sealed class UnpackCommand(TextWriter output, TextWriter error)
    {
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
            var written = new List<Diagnostic>();
            var result = Unpacker.Unpack(opened.Root, root, options, written);
            reporter.Write(input.Name, written);
            foreach (var failure in result.Failed) error.WriteLine(failure);

            output.WriteLine($"{result.Files} files, {result.Bytes} bytes, to {root}");
            return result.Failed.Count > 0 ? ExitCodes.IoError : reporter.ExitCode;
        }
    }
}
