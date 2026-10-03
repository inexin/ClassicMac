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
    // ls, stat, cat, find and get on Mac paths (docs/cli.md §2), as text or --json; problems go to stderr.
    internal sealed class PathCommands(TextWriter output, TextWriter error, Stream binary, ContainerReadOptions options, ReadOptions readOptions,
        bool strict, bool quiet)
    {
        private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        // Opens the path's host file and resolves the rest; reports a path that names nothing (exit NotFound).
        private int With(string path, Func<MacPathTree, MacPathEntry, int> action)
        {
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
                int code;
                if (entry is null)
                {
                    error.WriteLine($"{path}: names nothing.");
                    code = ExitCodes.NotFound;
                }
                else
                {
                    try
                    {
                        code = action(tree, entry);
                    }
                    catch (InvalidOperationException e)
                    {
                        error.WriteLine($"{path}: {e.Message}");
                        code = ExitCodes.Usage;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        error.WriteLine($"{path}: {e.Message}");
                        code = ExitCodes.IoError;
                    }
                }

                reporter.Write(tree.Root.Name, diagnostics);
                return code == ExitCodes.Success ? reporter.ExitCode : code;
            }
        }

        public int Ls(string path, bool json) => With(path, (tree, entry) =>
        {
            var entries = MacCommands.List(tree, entry);
            if (json)
            {
                WriteJson(w =>
                {
                    Where(w, tree, entry.Path);
                    w.WriteStartArray("entries");
                    foreach (var e in entries)
                    {
                        w.WriteStartObject();
                        WriteEntry(w, tree, e);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
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
            var kind = entry.File is { } file && entry.Kind is MacPathKind.File or MacPathKind.Container
                ? (file.FinderInfo.Flags & FinderFlags.IsAlias) != 0
                    ? new FinderKind("alias", FinderKindSource.BuiltIn, null, null)
                    : KnownKinds.Resolve(null, file.FinderInfo.Type, file.FinderInfo.Creator)
                : null;
            if (json)
            {
                WriteJson(w =>
                {
                    w.WriteString("input", tree.Root.Path);
                    WriteEntry(w, tree, info);
                    if (kind is not null)
                    {
                        w.WriteString("kindName", kind.Text);
                        w.WriteString("kindSource", KnownKinds.Describe(kind));
                    }

                    w.WriteStartArray("chain");
                    foreach (var step in info.Chain)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", step.Name);
                        w.WriteString("format", step.Format);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    Optional(w, "resourceForkSource", info.ResourceForkSource);
                    Optional(w, "resourceAttributes", info.ResourceAttributes);
                });
                return ExitCodes.Success;
            }

            output.WriteLine($"Path: {info.Path}");
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
                error.WriteLine($"{entry.Path}: shows the first {bytes.Length} of {all.Length} bytes (--max-bytes).");
            }

            if (raw)
            {
                binary.Write(bytes);
                binary.Flush();
                return ExitCodes.Success;
            }

            // A resource without --hex: decoded, as JSON or text; otherwise a hex dump.
            if (entry.Kind == MacPathKind.Resource && !hex && Decode(entry, all) is { } decoded)
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

        // A resource decoded by the built-in decoders: its main file, or null when none decodes it.
        private DecodedFile? Decode(MacPathEntry entry, byte[] data)
        {
            var resource = entry.Resource!;
            var decoder = ResourceDecoders.Create(DecodeOptions.Default).FirstOrDefault(d => d.CanDecode(resource.Type));
            return decoder?.Decode(new DecodeInput(resource, data, entry.Resources!, readOptions, new List<Diagnostic>())).FirstOrDefault();
        }

        public int Find(string path, MacFindQuery query, int limit, bool json) => With(path, (tree, entry) =>
        {
            var matches = MacCommands.Find(tree, entry, query).Take(limit + 1).ToList();
            var truncated = matches.Count > limit;
            if (truncated)
            {
                matches.RemoveAt(matches.Count - 1);
                error.WriteLine($"{entry.Path}: shows the first {limit} matches (--limit).");
            }

            if (json)
            {
                WriteJson(w =>
                {
                    Where(w, tree, entry.Path);
                    w.WriteStartArray("matches");
                    foreach (var match in matches)
                    {
                        w.WriteStartObject();
                        WriteEntry(w, tree, match);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteBoolean("truncated", truncated);
                });
            }
            else
            {
                foreach (var match in matches)
                {
                    output.WriteLine(match.Path);
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
                    w.WriteStartArray("written");
                    foreach (var file in written)
                    {
                        w.WriteStringValue(file);
                    }

                    w.WriteEndArray();
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

        // One entry's facts; those that do not apply are left out.
        private static void WriteEntry(Utf8JsonWriter w, MacPathTree tree, MacEntryInfo e)
        {
            w.WriteString("name", e.Name);
            w.WriteString("path", Inside(tree, e.Path));
            w.WriteString("kind", e.Kind);
            Optional(w, "type", e.Type);
            Optional(w, "creator", e.Creator);
            Optional(w, "dataSize", e.DataSize);
            Optional(w, "resourceSize", e.ResourceSize);
            Optional(w, "created", e.Created?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            Optional(w, "modified", e.Modified?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            Optional(w, "flags", e.Flags);
            if (e.FlagNames.Count > 0)
            {
                w.WriteStartArray("flagNames");
                foreach (var name in e.FlagNames)
                {
                    w.WriteStringValue(name);
                }

                w.WriteEndArray();
            }

            if (e.Locked == true)
            {
                w.WriteBoolean("locked", true);
            }

            Optional(w, "format", e.Format);
            Optional(w, "count", e.Count);
            Optional(w, "resourceType", e.ResourceType);
            Optional(w, "resourceId", e.ResourceId);
            Optional(w, "resourceName", e.ResourceName);
        }

        // The input (the host file) and an entry's path inside it, as the write commands name them (docs/cli.md §3.3).
        private static void Where(Utf8JsonWriter w, MacPathTree tree, string path)
        {
            w.WriteString("input", tree.Root.Path);
            w.WriteString("path", Inside(tree, path));
        }

        // A full Mac path without its host file: the names inside the input ("" for the input itself).
        private static string Inside(MacPathTree tree, string path) => path.Length > tree.Root.Path.Length ? path[(tree.Root.Path.Length + 1)..] : "";

        private static void Optional(Utf8JsonWriter w, string name, string? value)
        {
            if (value is not null)
            {
                w.WriteString(name, value);
            }
        }

        private static void Optional(Utf8JsonWriter w, string name, long? value)
        {
            if (value is { } number)
            {
                w.WriteNumber(name, number);
            }
        }

        private void WriteJson(Action<Utf8JsonWriter> body)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, JsonOptions))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }

            output.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }

        private static string Date(DateTime? date) => date?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
    }
}
