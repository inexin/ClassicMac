using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Editing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClassicMac.Resources.Cli.Mcp
{
    /// <summary>
    /// <c>classicmac mcp</c> (docs/cli.md §4): the file commands as MCP tools over a session per opened input. Reads go
    /// through <see cref="MacCommands"/>, writes through <see cref="MacEdits"/> into the session, and only <c>save_as</c>
    /// writes a file (over the input only with <c>in_place</c>). Results are the JSON objects of docs/cli.md; long
    /// lists and reads come in pages, with a <c>more</c> cursor for the next. The tools are described by hand-written
    /// schemas and run without reflection, so the CLI stays trimmable.
    /// </summary>
    internal sealed class MacMcpServer : IDisposable
    {
        /// <summary>The most entries a list gives when not asked, and the most it gives at all.</summary>
        public const int ListPage = 200;

        /// <summary>The most matches a search gives when not asked.</summary>
        public const int SearchPage = 100;

        /// <summary>The most a page of entries or matches may hold.</summary>
        public const int MaxPage = 1000;

        /// <summary>The bytes (or characters of text) a read gives when not asked.</summary>
        public const int ReadPage = 64 * 1024;

        /// <summary>The most bytes (or characters) a page of a read may hold.</summary>
        public const int MaxReadPage = 1024 * 1024;

        private const string Instructions =
            "ClassicMac reads and changes classic Mac OS files: disk images (HFS, MFS, ProDOS, ISO), archives (StuffIt, " +
            "Compact Pro, zip…), MacBinary/BinHex/AppleSingle/AppleDouble files and resource forks. Call open with a host " +
            "file to get a session, then give paths inside it: Mac names joined by ':' or '/' (\"\" for the input itself); " +
            "containers inside are entered as folders, '#rsrc' is a file's resource fork, then a type ('STR ') and an ID. " +
            "list, stat, read, search and extract read. put, mkdir, rm, rename, set, res_add and res_rm change the " +
            "session only (dry_run checks a change without making it); save_as writes them to a new file, or over the " +
            "input with in_place. Long results come in pages: pass the 'more' value back as cursor. close ends a session.";

        private readonly Dictionary<string, PathSession> sessions = new(StringComparer.Ordinal);
        private readonly Lock gate = new();
        private readonly ContainerReadOptions options;
        private readonly ReadOptions readOptions;
        private readonly IReadOnlyList<ToolDefinition> tools;
        private int next;

        public MacMcpServer(ContainerReadOptions options, ReadOptions readOptions)
        {
            this.options = options;
            this.readOptions = readOptions;
            tools = Definitions();
        }

        private sealed record ToolDefinition(string Name, string Title, string Description, string Schema, bool ReadOnly, bool Destructive,
            Func<Arguments, string> Run);

        // A refused call: arguments missing or of the wrong kind.
        private sealed class BadArguments(string message) : Exception(message);

        /// <summary>The server's options: its name, instructions and the tools.</summary>
        public McpServerOptions Options() => new()
        {
            ServerInfo = new Implementation
            {
                Name = "classicmac",
                Version = typeof(MacMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. tools.Select(Describe)] }),
                CallToolHandler = (request, _) => ValueTask.FromResult(Call(request.Params?.Name ?? "", request.Params?.Arguments)),
            },
        };

        /// <summary>Serves over standard input and output until the client leaves.</summary>
        public async Task RunStdioAsync(CancellationToken cancellationToken)
        {
            await using var server = McpServer.Create(new StdioServerTransport("classicmac"), Options());
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>The open sessions.</summary>
        public IReadOnlyCollection<string> Sessions
        {
            get
            {
                lock (gate)
                {
                    return [.. sessions.Keys];
                }
            }
        }

        /// <summary>Closes every session, deleting their working copies.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                foreach (var session in sessions.Values)
                {
                    session.Dispose();
                }

                sessions.Clear();
            }
        }

        private static string Json(Action<Utf8JsonWriter> body) => MacPathJson.Document(body, indented: false);

        private static Tool Describe(ToolDefinition tool) => new()
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = JsonDocument.Parse(tool.Schema).RootElement.Clone(),
            Annotations = new ToolAnnotations
            {
                Title = tool.Title,
                ReadOnlyHint = tool.ReadOnly,
                DestructiveHint = tool.Destructive,
                OpenWorldHint = false,
            },
        };

        /// <summary>Runs a tool: its JSON result, or an error result with a message and a code.</summary>
        public CallToolResult Call(string name, IDictionary<string, JsonElement>? arguments)
        {
            string text;
            string? code = null;
            try
            {
                var tool = tools.FirstOrDefault(t => t.Name == name) ?? throw new BadArguments($"There is no tool {name}.");
                lock (gate)
                {
                    text = tool.Run(new Arguments(arguments));
                }
            }
            catch (PathNotFound e)
            {
                (text, code) = (e.Message, "notFound");
            }
            catch (FileNotFoundException e)
            {
                (text, code) = (e.Message, "notFound");
            }
            catch (DirectoryNotFoundException e)
            {
                (text, code) = (e.Message, "notFound");
            }
            catch (BadArguments e)
            {
                (text, code) = (e.Message, "badArguments");
            }
            catch (Exception e) when (e is WriteRefused or InvalidOperationException or InvalidDataException or ArgumentException or EndOfStreamException
                or NotSupportedException)
            {
                (text, code) = (e.Message, "refused");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                (text, code) = (e.Message, "ioError");
            }

            if (code is not null)
            {
                text = Json(w =>
                {
                    w.WriteString("error", text);
                    w.WriteString("code", code);
                });
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }],
                StructuredContent = JsonDocument.Parse(text).RootElement.Clone(),
                IsError = code is not null,
            };
        }

        // The arguments of a call, read by name.
        private sealed class Arguments(IDictionary<string, JsonElement>? values)
        {
            private JsonElement? Value(string name) =>
                values is not null && values.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

            public string? String(string name) => Value(name) switch
            {
                null => null,
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                _ => throw new BadArguments($"{name} must be a string."),
            };

            public string Required(string name) => String(name) ?? throw new BadArguments($"Give {name}.");

            public bool Bool(string name) => Value(name) switch
            {
                null => false,
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                _ => throw new BadArguments($"{name} must be true or false."),
            };

            public int Int(string name, int fallback, int max)
            {
                if (Value(name) is not { } value)
                {
                    return fallback;
                }

                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1)
                {
                    throw new BadArguments($"{name} must be a whole number from 1.");
                }

                return Math.Min(number, max);
            }

            // A page's start: the cursor a previous result gave as 'more' (0 without one).
            public int Cursor()
            {
                var text = String("cursor");
                if (text is null)
                {
                    return 0;
                }

                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    ? offset
                    : throw new BadArguments($"'{text}' is not a cursor this server gave.");
            }
        }

        private PathSession Session(Arguments args)
        {
            var id = args.Required("session");
            return sessions.TryGetValue(id, out var session) ? session : throw new BadArguments($"There is no open session {id}; call open first.");
        }

        // The entry a path inside the session's input names.
        private static MacPathEntry Entry(PathSession session, Arguments args) => MacEdits.Existing(session.Tree, args.String("path") ?? "");

        private static void Where(Utf8JsonWriter w, PathSession session, MacPathEntry entry) =>
            MacPathJson.Where(w, session.Input, session.Tree, entry.Path);

        private static void More(Utf8JsonWriter w, long next, long total)
        {
            w.WriteBoolean("truncated", next < total);
            if (next < total)
            {
                w.WriteString("more", next.ToString(CultureInfo.InvariantCulture));
            }
        }

        private string Open(Arguments args)
        {
            var id = (++next).ToString(CultureInfo.InvariantCulture);
            var session = PathSession.Open(id, args.Required("path"), options, readOptions);
            sessions[id] = session;
            var root = MacCommands.Stat(session.Tree, session.Tree.Root);
            return Json(w =>
            {
                w.WriteString("session", id);
                w.WriteString("input", session.Input);
                w.WriteString("path", "");
                w.WriteString("kind", root.Kind);
                MacPathJson.Optional(w, "format", root.Format);
                w.WriteString("editable", session.Kind switch
                {
                    InputEditKind.HfsVolume => "volume",
                    InputEditKind.SingleFile => "file",
                    _ => "no",
                });
            });
        }

        private string Close(Arguments args)
        {
            var session = Session(args);
            if (session.Unsaved > 0 && !args.Bool("discard"))
            {
                throw new WriteRefused($"{session.Unsaved} change(s) are not saved; call save_as, or close with discard.");
            }

            sessions.Remove(session.Id);
            session.Dispose();
            return Json(w =>
            {
                w.WriteString("session", session.Id);
                w.WriteString("input", session.Input);
                w.WriteBoolean("closed", true);
            });
        }

        private string List(Arguments args)
        {
            var session = Session(args);
            var entry = Entry(session, args);
            var all = MacCommands.List(session.Tree, entry);
            var start = args.Cursor();
            var limit = args.Int("limit", ListPage, MaxPage);
            var page = all.Skip(start).Take(limit).ToList();
            return Json(w =>
            {
                Where(w, session, entry);
                MacPathJson.Entries(w, "entries", session.Tree, page);
                w.WriteNumber("count", all.Count);
                More(w, (long)start + page.Count, all.Count);
            });
        }

        private string Stat(Arguments args)
        {
            var session = Session(args);
            var entry = Entry(session, args);
            var info = MacCommands.Stat(session.Tree, entry);
            return Json(w => MacPathJson.Stat(w, session.Input, session.Tree, entry, info));
        }

        private string Read(Arguments args)
        {
            var session = Session(args);
            var entry = Entry(session, args);
            if (entry.Kind is MacPathKind.Folder or MacPathKind.ResourceType)
            {
                throw new WriteRefused($"{entry.Path} is a {MacCommands.KindName(entry.Kind)}; use list.");
            }

            var fork = args.String("fork") switch
            {
                null or "data" => MacFork.Data,
                "rsrc" => MacFork.Resource,
                var other => throw new BadArguments($"fork is data or rsrc, not {other}."),
            };
            var hex = args.Bool("hex");
            var start = args.Cursor();
            var size = args.Int("max_bytes", ReadPage, MaxReadPage);
            var all = MacCommands.ReadBytes(session.Tree, entry, fork);

            // A resource without hex: decoded, as JSON or text (as cat does); otherwise text, or hex for forks and resources.
            if (entry.Kind == MacPathKind.Resource && !hex && MacPathJson.Decode(entry, all, readOptions) is { } decoded
                && decoded.Extension is ".json" or ".txt")
            {
                var text = Encoding.UTF8.GetString(decoded.Content.Span);
                var json = decoded.Extension == ".json";
                if (json && start == 0 && text.Length <= size)
                {
                    using var document = JsonDocument.Parse(decoded.Content);
                    return Json(w =>
                    {
                        Where(w, session, entry);
                        w.WriteString("encoding", "json");
                        w.WritePropertyName("json");
                        document.RootElement.WriteTo(w);
                        w.WriteNumber("size", all.Length);
                        More(w, 0, 0);
                    });
                }

                return TextPage(json ? "json" : "text", text);
            }

            if (hex || entry.Kind is MacPathKind.Resource or MacPathKind.ResourceFork || fork == MacFork.Resource)
            {
                var from = Math.Min(start, all.Length);
                var page = all.AsSpan(from, Math.Min(size, all.Length - from));
                var hexText = Convert.ToHexString(page);
                return Json(w =>
                {
                    Where(w, session, entry);
                    w.WriteString("encoding", "hex");
                    w.WriteString("hex", hexText);
                    w.WriteNumber("offset", from);
                    w.WriteNumber("size", all.Length);
                    More(w, (long)from + hexText.Length / 2, all.Length);
                });
            }

            return TextPage("text", MacCommands.Text(all));

            // A page of text, by characters (a decoded resource's JSON too long for one page comes as text).
            string TextPage(string encoding, string text)
            {
                var from = Math.Min(start, text.Length);
                var page = text.Substring(from, Math.Min(size, text.Length - from));
                return Json(w =>
                {
                    Where(w, session, entry);
                    w.WriteString("encoding", encoding);
                    w.WriteString("text", page);
                    w.WriteNumber("offset", from);
                    w.WriteNumber("length", text.Length);
                    w.WriteNumber("size", all.Length);
                    More(w, (long)from + page.Length, text.Length);
                });
            }
        }

        private string Search(Arguments args)
        {
            var session = Session(args);
            var entry = Entry(session, args);
            var contains = args.String("contains");
            var containsHex = args.String("contains_hex");
            byte[]? needle = contains is not null ? MacRoman.Encode(contains)
                : containsHex is not null ? CommandLine.TryHex(containsHex, out var bytes) ? bytes : throw new BadArguments($"'{containsHex}' is not hex.")
                : null;
            var query = new MacFindQuery
            {
                Name = args.String("name"),
                Type = Code(args, "type"),
                Creator = Code(args, "creator"),
                Kind = args.String("kind") switch
                {
                    null => null,
                    "folder" => MacPathKind.Folder,
                    "file" => MacPathKind.File,
                    "container" => MacPathKind.Container,
                    var other => throw new BadArguments($"kind is folder, file or container, not {other}."),
                },
                ResourceType = Code(args, "resource_type"),
                Contains = needle,
                MaxDepth = args.Int("max_depth", 8, 64),
            };
            var start = args.Cursor();
            var limit = args.Int("limit", SearchPage, MaxPage);
            var page = MacCommands.Find(session.Tree, entry, query).Skip(start).Take(limit + 1).ToList();
            var more = page.Count > limit;
            if (more)
            {
                page.RemoveAt(page.Count - 1);
            }

            return Json(w =>
            {
                Where(w, session, entry);
                MacPathJson.Entries(w, "matches", session.Tree, page);
                w.WriteBoolean("truncated", more);
                if (more)
                {
                    w.WriteString("more", (start + limit).ToString(CultureInfo.InvariantCulture));
                }
            });
        }

        private static FourCC? Code(Arguments args, string name) => args.String(name) switch
        {
            null => null,
            var text when FourCC.TryParse(text, out var code) => code,
            var text => throw new BadArguments($"{name} '{text}' is not a four-character code."),
        };

        private string Extract(Arguments args)
        {
            var session = Session(args);
            var entry = Entry(session, args);
            var directory = Path.GetFullPath(args.Required("directory"));
            var format = args.String("format") switch
            {
                null or "appledouble" => MacGetFormat.AppleDouble,
                "basilisk" => MacGetFormat.Basilisk,
                "macbinary" => MacGetFormat.MacBinary,
                "raw" => MacGetFormat.Raw,
                var other => throw new BadArguments($"format is appledouble, basilisk, macbinary or raw, not {other}."),
            };
            Directory.CreateDirectory(directory);
            var written = MacCommands.Get(session.Tree, entry, directory, format, args.Bool("overwrite"), args.Bool("enter"));
            return Json(w =>
            {
                Where(w, session, entry);
                MacPathJson.Strings(w, "written", written);
            });
        }

        // A write: the change made in the session (or only checked), as docs/cli.md §3.3 shows it, nothing written.
        private string Write(Arguments args, Func<InputEditKind, MacPathTree, string, Action<InputEditSession>> plan)
        {
            var session = Session(args);
            var dryRun = args.Bool("dry_run");
            var made = session.Apply(plan, args.Required("path"), dryRun);
            return Json(w =>
            {
                MacPathJson.Changes(w, session.Input, dryRun, [], made);
                w.WriteNumber("unsaved", session.Unsaved);
            });
        }

        private string SaveAs(Arguments args)
        {
            var session = Session(args);
            var destination = args.String("destination");
            var inPlace = args.Bool("in_place");
            if (destination is null == !inPlace)
            {
                throw new BadArguments("Give destination (a new file) or in_place: true, not both.");
            }

            var changes = session.Changes.ToList();
            IReadOnlyList<string> written;
            if (inPlace)
            {
                session.SaveInPlace();
                written = [session.Input];
            }
            else
            {
                written = session.SaveAs(Path.GetFullPath(destination!));
            }

            return Json(w =>
            {
                MacPathJson.Changes(w, session.Input, false, written, changes);
                w.WriteNumber("unsaved", session.Unsaved);
            });
        }

        private const string SessionProperty = "\"session\": { \"type\": \"string\", \"description\": \"The session open gave\" }";
        private const string PathProperty = "\"path\": { \"type\": \"string\", \"description\": \"A Mac path inside the input: names joined by ':' or '/' (\\\"\\\" for the input itself)\" }";
        private const string DryRunProperty = "\"dry_run\": { \"type\": \"boolean\", \"description\": \"Check the change and report it without making it\" }";
        private const string CursorProperty = "\"cursor\": { \"type\": \"string\", \"description\": \"The 'more' value of the previous page\" }";

        private static string Schema(string properties, params string[] required) =>
            $"{{ \"type\": \"object\", \"properties\": {{ {properties} }}, \"required\": [{string.Join(", ", required.Select(r => $"\"{r}\""))}] }}";

        private List<ToolDefinition> Definitions() =>
        [
            new("open", "Open an input",
                "Opens a host file (a disk image, archive, MacBinary/BinHex/AppleSingle/AppleDouble file, or a plain file) and returns a session for the other tools.",
                Schema("\"path\": { \"type\": \"string\", \"description\": \"The host file\" }", "path"), true, false, Open),
            new("close", "Close a session",
                "Ends a session; refused while changes are unsaved unless discard is true.",
                Schema($"{SessionProperty}, \"discard\": {{ \"type\": \"boolean\", \"description\": \"Drop unsaved changes\" }}", "session"), true, false, Close),
            new("list", "List",
                "Lists what a path holds: a container's or folder's files and folders, a file's resource fork, a fork's types, a type's resources (docs/cli.md §2.1).",
                Schema($"{SessionProperty}, {PathProperty}, {CursorProperty}, \"limit\": {{ \"type\": \"integer\", \"description\": \"Most entries per page (200; at most 1000)\" }}", "session"),
                true, false, List),
            new("stat", "Describe",
                "Everything known about one entry: kind, type and creator with the Finder kind, sizes, dates, flags, and how it was read (docs/cli.md §2.2).",
                Schema($"{SessionProperty}, {PathProperty}", "session"), true, false, Stat),
            new("read", "Read",
                "A file's text (Mac OS Roman as UTF-8), a resource decoded as JSON or text, or hex for forks and with hex: true; in pages (docs/cli.md §2.3).",
                Schema($"{SessionProperty}, {PathProperty}, \"fork\": {{ \"type\": \"string\", \"enum\": [\"data\", \"rsrc\"] }}, \"hex\": {{ \"type\": \"boolean\" }}, {CursorProperty}, \"max_bytes\": {{ \"type\": \"integer\", \"description\": \"Most bytes (or characters of text) per page (65536; at most 1048576)\" }}", "session", "path"),
                true, false, Read),
            new("search", "Search",
                "Finds folders and files below a path, through containers, by name pattern (* and ?), type, creator, kind, resource type or content (docs/cli.md §2.4).",
                Schema($"{SessionProperty}, {PathProperty}, \"name\": {{ \"type\": \"string\" }}, \"type\": {{ \"type\": \"string\" }}, \"creator\": {{ \"type\": \"string\" }}, \"kind\": {{ \"type\": \"string\", \"enum\": [\"folder\", \"file\", \"container\"] }}, \"resource_type\": {{ \"type\": \"string\" }}, \"contains\": {{ \"type\": \"string\", \"description\": \"Text (Mac OS Roman) either fork holds\" }}, \"contains_hex\": {{ \"type\": \"string\" }}, \"max_depth\": {{ \"type\": \"integer\" }}, {CursorProperty}, \"limit\": {{ \"type\": \"integer\", \"description\": \"Most matches per page (100; at most 1000)\" }}", "session"),
                true, false, Search),
            new("extract", "Extract",
                "Copies a file, folder or resource out to a host folder: AppleDouble (default), Basilisk II, MacBinary or raw forks (docs/cli.md §2.5).",
                Schema($"{SessionProperty}, {PathProperty}, \"directory\": {{ \"type\": \"string\", \"description\": \"The host folder to write into\" }}, \"format\": {{ \"type\": \"string\", \"enum\": [\"appledouble\", \"basilisk\", \"macbinary\", \"raw\"] }}, \"overwrite\": {{ \"type\": \"boolean\" }}, \"enter\": {{ \"type\": \"boolean\", \"description\": \"Extract a container's contents rather than the container\" }}", "session", "path", "directory"),
                false, false, Extract),
            new("put", "Add a host file",
                "Adds a host file to the session's volume: into a folder keeping its name, or at a new path (docs/cli.md §3.1).",
                Schema($"{SessionProperty}, \"source\": {{ \"type\": \"string\", \"description\": \"The host file\" }}, {PathProperty}, \"name\": {{ \"type\": \"string\" }}, \"type\": {{ \"type\": \"string\" }}, \"creator\": {{ \"type\": \"string\" }}, {DryRunProperty}", "session", "source", "path"),
                false, false, args => Write(args, (_, tree, rest) => MacEdits.Put(tree, rest, HostImport.Read(Path.GetFullPath(args.Required("source")), options),
                    args.String("name"), args.String("type"), args.String("creator")))),
            new("mkdir", "Make a folder", "Makes a folder in the session's volume.",
                Schema($"{SessionProperty}, {PathProperty}, {DryRunProperty}", "session", "path"), false, false,
                args => Write(args, (_, tree, rest) => MacEdits.Mkdir(tree, rest))),
            new("rm", "Delete", "Deletes a file or folder (with recursive, one with contents) from the session's volume.",
                Schema($"{SessionProperty}, {PathProperty}, \"recursive\": {{ \"type\": \"boolean\" }}, {DryRunProperty}", "session", "path"), false, false,
                args => Write(args, (_, tree, rest) => MacEdits.Rm(tree, rest, args.Bool("recursive")))),
            new("rename", "Rename", "Renames a file or folder in its folder.",
                Schema($"{SessionProperty}, {PathProperty}, \"name\": {{ \"type\": \"string\" }}, {DryRunProperty}", "session", "path", "name"), false, false,
                args => Write(args, (kind, tree, rest) => MacEdits.Rename(kind, tree, rest, args.Required("name")))),
            new("set", "Set Finder info",
                "Sets a file's type and creator, or the Finder flags (a number like 0x4000, or names joined with commas: Invisible,HasBundle; replaces them all).",
                Schema($"{SessionProperty}, {PathProperty}, \"type\": {{ \"type\": \"string\" }}, \"creator\": {{ \"type\": \"string\" }}, \"flags\": {{ \"type\": \"string\" }}, {DryRunProperty}", "session", "path"), false, false,
                args => Write(args, (kind, tree, rest) => MacEdits.Set(kind, tree, rest, args.String("type"), args.String("creator"), args.String("flags")))),
            new("res_add", "Add a resource",
                "Adds a resource at <file>:#rsrc:<type>:<ID> from a host file or hex; an existing one is replaced only with replace.",
                Schema($"{SessionProperty}, {PathProperty}, \"data_file\": {{ \"type\": \"string\", \"description\": \"A host file with the data\" }}, \"data_hex\": {{ \"type\": \"string\", \"description\": \"The data as hex\" }}, \"name\": {{ \"type\": \"string\" }}, \"replace\": {{ \"type\": \"boolean\" }}, {DryRunProperty}", "session", "path"),
                false, false, args => Write(args, (kind, tree, rest) => MacEdits.ResAdd(kind, tree, rest, Data(args), args.String("name"), args.Bool("replace")))),
            new("res_rm", "Delete a resource", "Deletes the resource at <file>:#rsrc:<type>:<ID>.",
                Schema($"{SessionProperty}, {PathProperty}, {DryRunProperty}", "session", "path"), false, false,
                args => Write(args, (kind, tree, rest) => MacEdits.ResRm(kind, tree, rest))),
            new("save_as", "Save",
                "Writes the session's changes to a new file (destination, never the input), or over the input with in_place: true (the original kept as .orig); each save is read back to verify it.",
                Schema($"{SessionProperty}, \"destination\": {{ \"type\": \"string\", \"description\": \"The new host file\" }}, \"in_place\": {{ \"type\": \"boolean\", \"description\": \"Write over the input itself\" }}", "session"),
                false, true, SaveAs),
        ];

        // res_add's data: a host file's bytes, or hex.
        private static byte[] Data(Arguments args)
        {
            var file = args.String("data_file");
            var hex = args.String("data_hex");
            if (file is null == hex is null)
            {
                throw new BadArguments("Give data_file or data_hex.");
            }

            if (file is not null)
            {
                return File.ReadAllBytes(Path.GetFullPath(file));
            }

            return hex!.Length == 0 ? [] : CommandLine.TryHex(hex, out var bytes) ? bytes : throw new BadArguments($"'{hex}' is not hex.");
        }
    }
}
