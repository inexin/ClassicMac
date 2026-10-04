using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources;

namespace ClassicMac.Cli;

// `info`: how the input was read (companions, container chain) and each file's Finder info, dates and fork sizes.
internal sealed class InfoCommand(TextWriter output, TextWriter error)
{
    // The user's type and creator database (--type-creator-db, finder.md §2.6), asked for kinds last.
    private TypeCreatorDatabase? database;

    public int Run(FileInfo input, ContainerReadOptions options, bool strict, bool quiet, FileInfo? typeCreatorDatabase = null)
    {
        if (typeCreatorDatabase is not null)
        {
            try
            {
                using var stream = typeCreatorDatabase.OpenRead();
                database = TypeCreatorDatabase.Load(stream);
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e) || e is InvalidDataException)
            {
                error.WriteLine($"{typeCreatorDatabase.Name}: not a type and creator database (xlsx): {e.Message}");
                return ExitCodes.Usage;
            }
        }

        var reporter = new Reporter(error, strict, quiet);
        Input opened;
        try
        {
            var diagnostics = new List<Diagnostic>();
            opened = Input.Open(input, options, diagnostics);
            reporter.Write(input.Name, diagnostics);
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
            error.WriteLine($"{input.Name}: {e.Message}");
            return ExitCodes.IoError;
        }

        output.WriteLine($"{input.FullName}");
        foreach (var companion in opened.Host.Companions)
        {
            output.WriteLine($"  with {companion}");
        }

        Write(opened.Root, 0, null);
        return reporter.ExitCode;
    }

    // A node and the files it holds; `volume` names kinds from the applications beside the node (finder.md §2.3).
    private void Write(ContainerNode node, int depth, FinderKindResolver? volume)
    {
        var indent = new string(' ', depth * 2);
        var file = node.File;
        var info = file.FinderInfo;
        output.WriteLine($"{indent}{node.Format}: \"{file.MacPath}\"");
        output.WriteLine($"{indent}  type '{info.Type}'  creator '{info.Creator}'  flags {Flags(info.Flags)}");
        var kind = (info.Flags & FinderFlags.IsAlias) != 0
            ? new FinderKind("alias", FinderKindSource.BuiltIn, null, null)
            : KnownKinds.Resolve(volume, info.Type, info.Creator, database);
        output.WriteLine($"{indent}  kind \"{kind.Text}\" ({KnownKinds.Describe(kind)})");
        if (file.Created is not null || file.Modified is not null)
        {
            output.WriteLine($"{indent}  created {Date(file.Created)}  modified {Date(file.Modified)}");
        }

        output.WriteLine($"{indent}  data fork {file.DataFork.Length} bytes  resource fork {file.ResourceFork.Length} bytes");
        if (node.Volume is { } held)
        {
            // The volume this file holds, by its own name (the files under it are labelled by the format they came from).
            output.WriteLine(held.Name is null ? $"{indent}  {held.Format} volume" : $"{indent}  {held.Format} volume \"{held.Name}\"");
        }

        var neighbours = node.Children.Count == 0 ? null : Resolver(node);
        foreach (var child in node.Children)
        {
            Write(child, depth + 1, neighbours);
        }
    }

    // The files a container holds, as a volume of applications and System for kinds; forks are read only when asked.
    private static FinderKindResolver Resolver(ContainerNode node) =>
        FinderKindResolver.ForFiles(node.Children.Select(c => c.File), f => f.FinderInfo.Type, f => f.FinderInfo.Creator,
            f => (ushort)f.FinderInfo.Flags, f => f.Name.ToMacRoman(), Fork);

    private static ResourceFork? Fork(MacFile file)
    {
        if (file.ResourceFork.Length == 0)
        {
            return null;
        }

        try
        {
            return MacFileResources.Read(file, ReadOptions.Default).Fork;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }
    }

    private static string Flags(FinderFlags flags) =>
        flags == FinderFlags.None ? "none" : $"${(ushort)flags:X4} ({flags})";

    private static string Date(MacDate? date) => date?.ToString() ?? "-";
}
