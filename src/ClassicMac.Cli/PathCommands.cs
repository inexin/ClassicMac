using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Export;
using ClassicMac.Resources;

namespace ClassicMac.Cli;

// ls, stat, cat, find and get on Mac paths (docs/cli.md §2), as text or --json; problems go to stderr. The shell
// (docs/cli.md §5) gives its session's tree and input: paths are then inside the input, and JSON is one line each.
internal sealed class PathCommands(TextWriter output, TextWriter error, Stream binary, ContainerReadOptions options, ReadOptions readOptions,
    bool strict, bool quiet, MacPathTree? sessionTree = null, string? sessionInput = null)
{
    /// <summary>--follow: an alias file stands for its original, a symbolic link for its target (through either), docs/cli.md §2.</summary>
    public bool Follow { get; init; }

    /// <summary>--encoding: the Mac encoding text is read in (docs/cli.md §1).</summary>
    public MacTextEncoding TextEncoding { get; init; }

    // The input the current command's paths are in, as results name it.
    private string input = "";

    // Opens the path's host file and resolves the rest; reports a path that names nothing (exit NotFound).
    private int With(string path, Func<MacPathTree, MacPathEntry, int> action)
    {
        if (sessionTree is not null)
        {
            input = sessionInput!;
            return sessionTree.Resolve(path, Follow) is { } found ? Run(path, sessionTree, found, action) : NotFound(path);
        }

        var reporter = new Reporter(error, strict, quiet);
        var diagnostics = new List<Diagnostic>();
        MacPathTree? tree;
        MacPathEntry? entry;
        try
        {
            tree = MacPathTree.OpenPath(path, out entry, Follow, options, readOptions, diagnostics);
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
            error.WriteLine($"{path}: {e.Message}");
            return ExitCodes.IoError;
        }

        if (tree is null)
        {
            error.WriteLine($"{path}: no host file (the path starts with no existing file).");
            return ExitCodes.NotFound;
        }

        using (tree)
        {
            input = tree.Root.Path;
            var code = entry is null ? NotFound(path) : Run(path, tree, entry, action);
            reporter.Write(tree.Root.Name, diagnostics);
            return code == ExitCodes.Success ? reporter.ExitCode : code;
        }
    }

    private int NotFound(string path)
    {
        error.WriteLine($"{path}: names nothing.");
        return ExitCodes.NotFound;
    }

    private int Run(string path, MacPathTree tree, MacPathEntry entry, Func<MacPathTree, MacPathEntry, int> action)
    {
        if (Follow && tree.Follow(entry) is not { } original)
        {
            if (entry.File?.SymbolicLinkTarget is { } target && tree.ResolveAlias(entry) is null)
            {
                error.WriteLine($"{path}: a symbolic link whose target is not found ({target}).");
            }
            else
            {
                var stored = tree.ResolveAlias(entry)?.StoredPath;
                error.WriteLine($"{path}: an alias whose original is not found{(stored is null ? "" : $" ({stored})")}.");
            }

            return ExitCodes.NotFound;
        }

        try
        {
            return action(tree, Follow ? tree.Follow(entry)! : entry);
        }
        catch (InvalidOperationException e)
        {
            error.WriteLine($"{path}: {e.Message}");
            return ExitCodes.Usage;
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
            error.WriteLine($"{path}: {e.Message}");
            return ExitCodes.IoError;
        }
    }

    // An entry's full path as printed: the input, then the names inside it.
    private string Display(MacPathTree tree, string path) => MacPathJson.Inside(tree, path) is { Length: > 0 } inside ? input + ":" + inside : input;

    public int Ls(string path, bool json) => With(path, (tree, entry) =>
    {
        var entries = MacCommands.List(tree, entry);
        if (json)
        {
            WriteJson(w =>
            {
                Where(w, tree, entry.Path);
                MacPathJson.Entries(w, "entries", tree, entries);
            });
            return ExitCodes.Success;
        }

        foreach (var e in entries)
        {
            var typeCreator = e.Type is { } type ? $"{type} {e.Creator}" : e.ResourceType is { } rt ? rt : "";
            var data = e.DataSize?.ToString(CultureInfo.InvariantCulture) ?? (e.Count?.ToString(CultureInfo.InvariantCulture) ?? "-");
            var rsrc = e.ResourceSize?.ToString(CultureInfo.InvariantCulture) ?? "-";
            output.WriteLine($"{e.Kind,-13} {typeCreator,-9} {data,10} {rsrc,10}  {Date(e.Modified),-19}  {e.Name}");
        }

        return ExitCodes.Success;
    });

    public int Stat(string path, bool json) => With(path, (tree, entry) =>
    {
        var info = MacCommands.Stat(tree, entry);
        var kind = MacPathJson.KindOf(entry);
        if (json)
        {
            WriteJson(w => MacPathJson.Stat(w, input, tree, entry, info));
            return ExitCodes.Success;
        }

        output.WriteLine($"Path: {Display(tree, info.Path)}");
        output.WriteLine($"Kind: {info.Kind}");
        Line("Format", info.Format);
        if (info.Type is not null && info.Creator is not null)
        {
            output.WriteLine($"Type / creator: '{info.Type}' / '{info.Creator}'");
        }

        if (kind is not null)
        {
            output.WriteLine($"Finder kind: {kind.Text} ({KnownKinds.Describe(kind)})");
        }

        if (info.SymbolicLink is { } link)
        {
            output.WriteLine($"Symbolic link: {link.Target}");
            output.WriteLine(link.ResolvedPath is { } leads ? $"Leads to: {leads}" : "Leads to: nothing on this volume");
        }

        if (info.Alias is { } original)
        {
            output.WriteLine($"Original: {original.StoredPath}");
            output.WriteLine(original.Found ? $"Resolves: yes, {original.How}: {original.Target ?? original.ResolvedPath}" : $"Resolves: no. {original.Explanation}");
        }

        Line("Data fork", info.DataSize?.ToString("N0", CultureInfo.InvariantCulture) + (info.DataSize is null ? null : " bytes"));
        Line("Resource fork", info.ResourceSize?.ToString("N0", CultureInfo.InvariantCulture) + (info.ResourceSize is null ? null : " bytes"));
        Line("Created", info.Created is null ? null : Date(info.Created));
        Line("Modified", info.Modified is null ? null : Date(info.Modified));
        if (info.Flags is { } flags)
        {
            output.WriteLine($"Finder flags: ${flags:X4}{(info.FlagNames.Count > 0 ? " (" + string.Join(", ", info.FlagNames) + ")" : "")}");
        }

        Line("Locked", info.Locked == true ? "yes" : null);
        Line("Items", info.Count?.ToString(CultureInfo.InvariantCulture));
        Line("Resource", info.ResourceId is { } id ? $"'{info.ResourceType}' {id}{(info.ResourceName is { } n ? $" \"{n}\"" : "")}" : null);
        Line("Attributes", info.ResourceAttributes);
        Line("Read from", info.ResourceForkSource);
        if (info.Volume is { } volume)
        {
            output.WriteLine(volume.Name is { } volumeName ? $"Volume: {volume.Format} \"{volumeName}\"" : $"Volume: {volume.Format}");
            output.WriteLine($"Size: {volume.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes ({Bytes(volume.TotalBytes)}) in " +
                $"{volume.TotalBlocks.ToString("N0", CultureInfo.InvariantCulture)} blocks of {volume.BlockSize.ToString("N0", CultureInfo.InvariantCulture)} bytes");
            output.WriteLine($"Free: {volume.FreeBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes ({Bytes(volume.FreeBytes)}), " +
                $"{volume.FreeBlocks.ToString("N0", CultureInfo.InvariantCulture)} blocks");
            output.WriteLine(volume.Folders is { } folders ? $"Files / folders: {volume.Files:N0} / {folders:N0}" : $"Files: {volume.Files:N0}");
            if (info.Layout is { } layout)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Fragmentation: {layout.SplitFiles} of {layout.Files} files in more than one extent; free space in {layout.FreeRuns.Count:N0} {(layout.FreeRuns.Count == 1 ? "run" : "runs")}, the largest {layout.LargestFreeRun:N0} blocks"));
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Smallest size: {layout.SmallestSize:N0} bytes now, {layout.SmallestSizeDefragmented:N0} bytes defragmented"));
            }

            Line("Volume created", volume.Created is { } created ? Date(created.ToDateTime()) : null);
            Line("Volume modified", volume.Modified is { } modified ? Date(modified.ToDateTime()) + (volume.UtcAfterCreation ? " UTC" : "") : null);
            Line("Backed up", volume.BackedUp is { } backedUp ? Date(backedUp.ToDateTime()) + (volume.UtcAfterCreation ? " UTC" : "") : null);
            Line("Blessed folder", info.BlessedFolder ?? (volume.BlessedFolderId is { } blessedId ? $"ID {blessedId} (not found)" : null));
            Line("Volume locked", volume.SoftwareLocked || volume.HardwareLocked
                ? string.Join(" and ", new[] { volume.SoftwareLocked ? "by software" : null, volume.HardwareLocked ? "by hardware" : null }.OfType<string>())
                : null);
        }

        output.WriteLine("Read as: " + string.Join(" > ", info.Chain.Select(s => $"{s.Name} ({s.Format})")));
        return ExitCodes.Success;

        void Line(string label, string? value)
        {
            if (value is not null)
            {
                output.WriteLine($"{label}: {value}");
            }
        }
    });

    /// <summary>
    /// derez (docs/cli.md §2.8): a file's resource fork as Rez source, MPW DeRez's or the portable subset, to standard
    /// output or <paramref name="outputFile"/>; what the portable dialect cannot hold is reported.
    /// </summary>
    public int Derez(string path, ClassicMac.Resources.Rez.RezOptions options, string? outputFile) => With(path, (tree, entry) =>
    {
        if (entry.Kind is not (MacPathKind.File or MacPathKind.ResourceFork))
        {
            throw new InvalidOperationException($"is a {MacCommands.KindName(entry.Kind)}; derez takes a file.");
        }

        var fork = ClassicMac.Resources.ResourceFork.Read(MacCommands.ReadBytes(tree, entry, MacFork.Resource), readOptions);
        var diagnostics = new List<Diagnostic>();
        var bytes = ClassicMac.Resources.Rez.RezWriter.Write(fork, options, diagnostics);
        foreach (var d in diagnostics)
        {
            error.WriteLine($"{Display(tree, entry.Path)}: {d.Message} ({d.Code})");
        }

        if (outputFile is null)
        {
            binary.Write(bytes);
            binary.Flush();
        }
        else
        {
            File.WriteAllBytes(outputFile, bytes);
        }

        return strict && diagnostics.Count > 0 ? ExitCodes.Damaged : ExitCodes.Success;
    });

    /// <summary>
    /// rez (docs/cli.md §2.9): compiles a Rez source file into a resource fork written to <paramref name="outputFile"/>;
    /// <c>read</c> and <c>include</c> name files beside the source (in its folder, or its host folder).
    /// </summary>
    public int Rez(string path, string outputFile, bool retro68) => With(path, (tree, entry) =>
    {
        if (entry.Kind != MacPathKind.File)
        {
            throw new InvalidOperationException($"is a {MacCommands.KindName(entry.Kind)}; rez takes a source file.");
        }

        var source = MacCommands.ReadBytes(tree, entry, MacFork.Data);
        byte[] Fork(string name, MacFork fork)
        {
            if (tree.Parent(entry) is { } folder && entry.Parent is not null)
            {
                var sibling = tree.Children(folder).FirstOrDefault(c => c.Kind == MacPathKind.File && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException($"no file {name} beside the source");
                return MacCommands.ReadBytes(tree, sibling, fork);
            }

            var host = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(entry.Path)) ?? ".", name);
            var other = MacPathTree.OpenPath(host, out var found, options, readOptions, []) ?? throw new FileNotFoundException($"no file {name} beside the source");
            return MacCommands.ReadBytes(other, found ?? other.Root, fork);
        }

        var diagnostics = new List<Diagnostic>();
        var compiled = ClassicMac.Resources.Rez.RezCompiler.Compile(source, new ClassicMac.Resources.Rez.RezCompileOptions
        {
            Retro68Escapes = retro68,
            ReadFile = name => Fork(name, MacFork.Data),
            ReadResourceFork = name => ClassicMac.Resources.ResourceFork.Read(Fork(name, MacFork.Resource), readOptions),
        }, diagnostics);
        foreach (var d in diagnostics)
        {
            error.WriteLine($"{Display(tree, entry.Path)}: {d.Message} ({d.Code})");
        }

        if (compiled is null)
        {
            error.WriteLine($"{Display(tree, entry.Path)}: since there were errors, no resource fork was written.");
            return ExitCodes.Damaged;
        }

        File.WriteAllBytes(outputFile, compiled.ToArray());
        if (!quiet)
        {
            output.WriteLine($"{compiled.Resources.Count} {(compiled.Resources.Count == 1 ? "resource" : "resources")} to {outputFile}");
        }

        return strict && diagnostics.Any(d => d.Severity != DiagnosticSeverity.Info) ? ExitCodes.Damaged : ExitCodes.Success;
    });

    public int Cat(string path, bool hex, bool raw, MacFork fork, long maxBytes, bool json) => With(path, (tree, entry) =>
    {
        if (entry.Kind is MacPathKind.Folder or MacPathKind.ResourceType)
        {
            throw new InvalidOperationException($"is a {MacCommands.KindName(entry.Kind)}; use ls.");
        }

        var all = MacCommands.ReadBytes(tree, entry, fork);
        var bytes = all.Length > maxBytes ? all[..(int)maxBytes] : all;
        var truncated = bytes.Length < all.Length;
        if (truncated)
        {
            error.WriteLine($"{Display(tree, entry.Path)}: shows the first {bytes.Length} of {all.Length} bytes (--max-bytes).");
        }

        if (raw)
        {
            binary.Write(bytes);
            binary.Flush();
            return ExitCodes.Success;
        }

        // Text in the encoding the file's volume says, in place of the default Mac OS Roman (text-encodings.md §5).
        var encoding = TextEncoding == MacTextEncoding.Roman && tree.EncodingOf(entry) is { } said ? said : TextEncoding;

        // A resource without --hex: decoded, as JSON or text; otherwise a hex dump.
        if (entry.Kind == MacPathKind.Resource && !hex && MacPathJson.Decode(entry, all, readOptions, encoding) is { } decoded)
        {
            if (decoded.Extension == ".json")
            {
                if (json)
                {
                    using var document = JsonDocument.Parse(decoded.Content);
                    WriteJson(w =>
                    {
                        Header(w, "json");
                        w.WritePropertyName("json");
                        document.RootElement.WriteTo(w);
                    });
                }
                else
                {
                    output.WriteLine(Encoding.UTF8.GetString(decoded.Content.Span).TrimEnd('\n'));
                }

                return ExitCodes.Success;
            }

            if (decoded.Extension == ".txt")
            {
                WriteText(Encoding.UTF8.GetString(decoded.Content.Span));
                return ExitCodes.Success;
            }
        }

        if (hex || entry.Kind is MacPathKind.Resource or MacPathKind.ResourceFork || fork == MacFork.Resource)
        {
            if (json)
            {
                WriteJson(w =>
                {
                    Header(w, "hex");
                    w.WriteString("hex", Convert.ToHexString(bytes));
                    w.WriteNumber("size", all.Length);
                });
            }
            else
            {
                output.Write(MacCommands.Hex(bytes).Replace("\n", Environment.NewLine, StringComparison.Ordinal));
            }

            return ExitCodes.Success;
        }

        WriteText(MacCommands.Text(bytes, encoding));
        return ExitCodes.Success;

        void Header(Utf8JsonWriter w, string encoding)
        {
            Where(w, tree, entry.Path);
            w.WriteString("encoding", encoding);
            w.WriteBoolean("truncated", truncated);
        }

        void WriteText(string text)
        {
            if (json)
            {
                WriteJson(w =>
                {
                    Header(w, "text");
                    w.WriteString("text", text);
                });
            }
            else
            {
                output.Write(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
                if (!text.EndsWith('\n'))
                {
                    output.WriteLine();
                }
            }
        }
    });

    public int Find(string path, MacFindQuery query, int limit, bool json) => With(path, (tree, entry) =>
    {
        var matches = MacCommands.Find(tree, entry, query).Take(limit + 1).ToList();
        var truncated = matches.Count > limit;
        if (truncated)
        {
            matches.RemoveAt(matches.Count - 1);
            error.WriteLine($"{Display(tree, entry.Path)}: shows the first {limit} matches (--limit).");
        }

        if (json)
        {
            WriteJson(w =>
            {
                Where(w, tree, entry.Path);
                MacPathJson.Entries(w, "matches", tree, matches);
                w.WriteBoolean("truncated", truncated);
            });
        }
        else
        {
            foreach (var match in matches)
            {
                output.WriteLine(Display(tree, match.Path));
            }
        }

        return ExitCodes.Success;
    });

    public int Get(string path, string directory, MacGetFormat format, bool overwrite, bool enter, bool json) => With(path, (tree, entry) =>
    {
        Directory.CreateDirectory(directory);
        var written = MacCommands.Get(tree, entry, directory, format, overwrite, enter);
        if (json)
        {
            WriteJson(w =>
            {
                Where(w, tree, entry.Path);
                MacPathJson.Strings(w, "written", written);
            });
        }
        else
        {
            foreach (var file in written)
            {
                output.WriteLine(file);
            }
        }

        return ExitCodes.Success;
    });

    private void Where(Utf8JsonWriter w, MacPathTree tree, string path) => MacPathJson.Where(w, input, tree, path);

    private void WriteJson(Action<Utf8JsonWriter> body) => output.WriteLine(MacPathJson.Document(body, indented: sessionTree is null));

    private static string Date(DateTime? date) => date?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";

    // A size in KiB, MiB or GiB, one decimal.
    private static string Bytes(long bytes)
    {
        var (value, unit) = bytes >= 1L << 30 ? (bytes / (double)(1L << 30), "GiB") : bytes >= 1L << 20 ? (bytes / (double)(1L << 20), "MiB") : (bytes / 1024.0, "KiB");
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
    }
}
