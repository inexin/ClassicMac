using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Commands;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Finder;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Cli
{
    // ls, stat, cat, find and get on Mac paths (docs/cli.md §2), as text or --json; problems go to stderr. The shell
    // (docs/cli.md §5) gives its session's tree and input: paths are then inside the input, and JSON is one line each.
    internal sealed class PathCommands(TextWriter output, TextWriter error, Stream binary, ContainerReadOptions options, ReadOptions readOptions,
        bool strict, bool quiet, MacPathTree? sessionTree = null, string? sessionInput = null)
    {
        /// <summary>--follow: an alias file stands for its original (through aliases of aliases), docs/cli.md §2.</summary>
        public bool Follow { get; init; }

        // The input the current command's paths are in, as results name it.
        private string input = "";

        // Opens the path's host file and resolves the rest; reports a path that names nothing (exit NotFound).
        private int With(string path, Func<MacPathTree, MacPathEntry, int> action)
        {
            if (sessionTree is not null)
            {
                input = sessionInput!;
                return sessionTree.Resolve(path) is { } found ? Run(path, sessionTree, found, action) : NotFound(path);
            }

            var reporter = new Reporter(error, strict, quiet);
            var diagnostics = new List<Diagnostic>();
            MacPathTree? tree;
            MacPathEntry? entry;
            try
            {
                tree = MacPathTree.OpenPath(path, out entry, options, readOptions, diagnostics);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
            if (Follow && tree.FollowAlias(entry) is not { } original)
            {
                var stored = tree.ResolveAlias(entry)?.StoredPath;
                error.WriteLine($"{path}: an alias whose original is not found{(stored is null ? "" : $" ({stored})")}.");
                return ExitCodes.NotFound;
            }

            try
            {
                return action(tree, Follow ? tree.FollowAlias(entry)! : entry);
            }
            catch (InvalidOperationException e)
            {
                error.WriteLine($"{path}: {e.Message}");
                return ExitCodes.Usage;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

            if (info.Alias is { } original)
            {
                output.WriteLine($"Original: {original.StoredPath}");
                output.WriteLine(original.Found ? $"Resolves: yes, {original.How}: {original.Target ?? original.ResolvedPath}" : "Resolves: no, the original is not found");
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

            // A resource without --hex: decoded, as JSON or text; otherwise a hex dump.
            if (entry.Kind == MacPathKind.Resource && !hex && MacPathJson.Decode(entry, all, readOptions) is { } decoded)
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

            WriteText(MacCommands.Text(bytes));
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
    }
}
