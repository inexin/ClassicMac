using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;
using ClassicMac.Resources.Export;

namespace ClassicMac.Cli;

// `extract`: the resources of every file inside the input into a folder with a manifest, by Unpacker.Extract. One
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
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
            error.WriteLine($"{input.Name}: {e.Message}");
            return ExitCodes.IoError;
        }

        var root = outputDirectory?.FullName
            ?? Path.Combine(input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + " resources");
        var exported = new List<(string Source, Diagnostic Diagnostic)>();
        var result = Unpacker.Extract(opened.Root, forks.Select(f => new ForkToExtract(f.Node, f.Chain, f.Fork!)).ToList(), root, options, exported);
        foreach (var (source, diagnostic) in exported)
        {
            reporter.Write(source == input.Name ? input.Name : $"{input.Name} > {source}", [diagnostic]);
        }

        foreach (var failure in result.Failed)
        {
            error.WriteLine(failure);
        }

        output.WriteLine($"{result.Resources} resources from {result.Files} files, to {root}");
        return result.Failed.Count > 0 ? ExitCodes.IoError : reporter.ExitCode;
    }
}
