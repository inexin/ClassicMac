using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Export;
using ClassicMac.Files;
using ClassicMac.Resources.Export;
using ClassicMac.Resources;

namespace ClassicMac.Cli;

// `convert`: every document inside the input (DOCMaker, SimpleText) as an HTML folder, by DocumentConverter.Convert.
// One document is written straight into the output folder; several get a folder each, placed as `unpack` places files.
internal sealed class ConvertCommand(TextWriter output, TextWriter error)
{
    public int Run(FileInfo input, DirectoryInfo? outputDirectory, IReadOnlyList<IDocumentConverter> converters, ReadOptions readOptions,
        ContainerReadOptions containerOptions, bool overwrite, bool strict, bool quiet,
        Func<MacTextEncoding, IReadOnlyList<IDocumentConverter>>? convertersFor = null)
    {
        var reporter = new Reporter(error, strict, quiet);
        Input opened;
        List<ForkEntry> forks;
        try
        {
            var diagnostics = new List<Diagnostic>();
            opened = Input.Open(input, containerOptions, diagnostics);
            reporter.Write(input.Name, diagnostics);
            // Files with a resource fork, and Word documents and AIFF sounds (their data fork is the document) with or without one.
            forks = opened.Forks(input, readOptions, reporter)
                .Where(f => f.Fork is not null || ClassicMac.Resources.Decoders.DataForkDocuments.Applies(f.Node.File.FinderInfo.Type)).ToList();
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
            ?? Path.Combine(input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + " documents");
        var found = new List<(string Source, Diagnostic Diagnostic)>();
        ConvertResult result;
        try
        {
            result = DocumentConverter.Convert(opened.Root, forks.Select(f => new ForkToExtract(f.Node, f.Chain, f.Fork ?? new ResourceFork())).ToList(), root,
                converters, readOptions, overwrite, found, convertersFor: convertersFor);
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
            error.WriteLine(e.Message);
            return ExitCodes.IoError;
        }
        foreach (var (source, diagnostic) in found)
        {
            reporter.Write(source == input.Name ? input.Name : $"{input.Name} > {source}", [diagnostic]);
        }

        foreach (var failure in result.Failed)
        {
            error.WriteLine(failure);
        }

        if (!quiet)
        {
            foreach (var (macPath, document) in result.Documents)
            {
                output.WriteLine($"{macPath}: {document.Path}");
            }
        }
        output.WriteLine(result.Documents.Count == 0 ? $"No documents in {input.Name}." : $"{result.Documents.Count} document{(result.Documents.Count == 1 ? "" : "s")}, to {root}");
        return result.Failed.Count > 0 ? ExitCodes.IoError : reporter.ExitCode;
    }
}
