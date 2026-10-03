using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Editing;

namespace ClassicMac.Resources.Cli.Shell
{
    /// <summary>
    /// <c>classicmac shell &lt;input&gt;</c> (docs/cli.md §5): a DOS-like shell on one input. <c>cd</c> goes into folders,
    /// disk images and archives (as folders), forks and types; the read commands are the CLI's (§2), the changes the
    /// write commands' (§3), made in a <see cref="PathSession"/> until <c>save</c> or <c>save as</c>. With
    /// <paramref name="json"/> every command prints one JSON object on one line.
    /// </summary>
    internal sealed class MacShell(IShellConsole console, PathSession session, ContainerReadOptions options, ReadOptions readOptions, bool json)
    {
        private static readonly string[] CommandNames =
        [
            "cd", "dir", "ls", "type", "cat", "info", "stat", "res", "copy", "get", "put", "del", "rm", "md", "mkdir", "ren", "rename",
            "set", "find", "save", "exit", "quit", "help",
        ];

        private static readonly string Help = string.Join(Environment.NewLine,
            "cd [path]                     go into a folder, disk image, archive, fork or type (.. up, : the input); no path: where you are",
            "dir, ls [path]                what a path holds",
            "type, cat <path> [--hex] [--rsrc] [--max-bytes N]   a file's text, a resource decoded, or hex",
            "info, stat <path>             everything known about an entry",
            "res [file]                    a file's resources",
            "find [path] [--name P] [--type T] [--creator C] [--kind folder|file|container] [--resource-type T] [--contains TEXT] [--limit N]",
            "copy <path> <host folder> [--as appledouble|basilisk|macbinary|raw] [--overwrite]   copy out (also: get)",
            "copy <host file> <path> [--name N] [--type T] [--creator C]                        copy in (also: put)",
            "del, rm <path> [-r]           delete a file or folder",
            "md, mkdir <path>              make a folder",
            "ren, rename <path> <name>     rename",
            "move, mv <path> <folder>      move into another folder",
            "set <path> [--type T] [--creator C] [--flags F]",
            "lock, unlock <path>           lock or unlock a file",
            "bless <folder>                make it the System Folder (it must hold a System file)",
            "save                          write the changes over the input (keeping <input>.orig)",
            "save as <file>                write them to a new file",
            "exit, quit [--discard]        leave",
            "Names with spaces go in double quotes; Tab completes names, Up and Down recall lines.");

        private readonly string name = Path.GetFileName(session.Input);
        private string current = "";
        private bool leaving;
        private string running = "";

        /// <summary>Where the shell is, inside the input ("" for the input itself).</summary>
        public string Current => current;

        /// <summary>The prompt: the input's name and the path inside it (<c>Mac OS 9.hfv:System Folder&gt;</c>).</summary>
        public string Prompt => (current.Length == 0 ? name : name + ":" + current) + ">";

        /// <summary>Starts at a path inside the input (as <c>cd</c> goes, quietly).</summary>
        public void Enter(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            var entry = Existing(path);
            if (entry.Kind == MacPathKind.Resource)
            {
                entry = entry.Parent!;
            }

            current = Inside(entry);
        }

        /// <summary>Runs commands until the input ends or <c>exit</c>; returns the exit code.</summary>
        public int Run()
        {
            while (true)
            {
                var line = console.ReadLine(console.Interactive ? Prompt + " " : "", Complete);
                if (line is null)
                {
                    return Leave(discard: false) ?? ExitCodes.Success;
                }

                var code = Execute(line);
                if (leaving)
                {
                    leaving = false;
                    if (Leave(discard: code == -1) is { } exit)
                    {
                        return exit;
                    }

                    continue;
                }

                if (code != ExitCodes.Success && !console.Interactive)
                {
                    return code;
                }
            }
        }

        // Leaving: with unsaved changes a person is asked to save, save as or discard; a script is refused (exit 2) unless
        // it said exit --discard. Null when the shell goes on.
        private int? Leave(bool discard)
        {
            if (session.Unsaved == 0 || discard)
            {
                return ExitCodes.Success;
            }

            if (!console.Interactive)
            {
                console.Error.WriteLine($"{session.Unsaved} change(s) not saved: save, save as <file>, or exit --discard.");
                return ExitCodes.Usage;
            }

            while (true)
            {
                var answer = console.ReadLine($"{session.Unsaved} change(s) to {name} are not saved. [S]ave, Save [A]s, [D]iscard or [C]ancel? ", null);
                switch (answer?.Trim().ToUpperInvariant())
                {
                    case null:
                        return ExitCodes.Usage;
                    case "S" or "SAVE":
                        return Execute("save") == ExitCodes.Success ? ExitCodes.Success : null;
                    case "A" or "SAVE AS":
                        var file = console.ReadLine("Save as: ", null);
                        if (string.IsNullOrWhiteSpace(file))
                        {
                            return null;
                        }

                        return Execute("save as " + ShellWords.Quote(file.Trim())) == ExitCodes.Success ? ExitCodes.Success : null;
                    case "D" or "DISCARD":
                        return ExitCodes.Success;
                    case "C" or "CANCEL" or "":
                        return null;
                }
            }
        }

        /// <summary>Runs one command line; returns its exit code (docs/cli.md §5.5).</summary>
        public int Execute(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            var words = ShellWords.Split(line).Select(w => w.Text).ToList();
            if (words.Count == 0 || words[0].StartsWith('#'))
            {
                return ExitCodes.Success;
            }

            var command = words[0].ToLowerInvariant();
            running = command;
            var args = words.Skip(1).ToList();
            try
            {
                return command switch
                {
                    "cd" or "chdir" => Cd(args),
                    "dir" or "ls" => Read(args, 0, 1, [], [], (paths, target, _) => paths.Ls(target, json)),
                    "type" or "cat" => Read(args, 1, 1, ["--hex", "--rsrc"], ["--max-bytes"], (paths, target, o) => paths.Cat(target, o.Flag("--hex"), raw: false,
                        o.Flag("--rsrc") ? MacFork.Resource : MacFork.Data, o.Long("--max-bytes", 16L << 20), json)),
                    "info" or "stat" => Read(args, 1, 1, [], [], (paths, target, _) => paths.Stat(target, json)),
                    "res" => Res(args),
                    "find" => Read(args, 0, 1, [], ["--name", "--type", "--creator", "--kind", "--resource-type", "--contains", "--limit"],
                        (paths, target, o) => paths.Find(target, Query(o), (int)o.Long("--limit", 1000), json)),
                    "copy" or "cp" => Copy(args, null),
                    "get" => Copy(args, true),
                    "put" => Copy(args, false),
                    "del" or "rm" or "erase" => Change(args, 1, ["-r", "--recursive"], [], (o, path) =>
                        (_, tree, rest) => MacEdits.Rm(tree, rest, o.Flag("-r") || o.Flag("--recursive"))),
                    "md" or "mkdir" => Change(args, 1, [], [], (_, _) => (_, tree, rest) => MacEdits.Mkdir(tree, rest)),
                    "ren" or "rename" => Change(args, 2, [], [], (o, _) => (kind, tree, rest) => MacEdits.Rename(kind, tree, rest, o.Positional[1])),
                    "lock" => Change(args, 1, [], [], (_, _) => (_, tree, rest) => MacEdits.Lock(tree, rest, true)),
                    "unlock" => Change(args, 1, [], [], (_, _) => (_, tree, rest) => MacEdits.Lock(tree, rest, false)),
                    "bless" => Change(args, 1, [], [], (_, _) => (_, tree, rest) => MacEdits.Bless(tree, rest)),
                    "move" or "mv" => Change(args, 2, [], [], (o, _) => (_, tree, rest) => MacEdits.Mv(tree, rest, Resolve(o.Positional[1]))),
                    "set" => Change(args, 1, [], ["--type", "--creator", "--flags"], (o, _) =>
                        (kind, tree, rest) => MacEdits.Set(kind, tree, rest, o.Value("--type"), o.Value("--creator"), o.Value("--flags"))),
                    "save" => Save(args),
                    "exit" or "quit" or "bye" => Exit(args),
                    "help" or "?" => Show(Help),
                    _ => throw new ShellUsage($"There is no command {words[0]}; help lists them."),
                };
            }
            catch (PathNotFound e)
            {
                return Fail(command, e.Message, ExitCodes.NotFound);
            }
            catch (FileNotFoundException e)
            {
                return Fail(command, e.Message, ExitCodes.NotFound);
            }
            catch (DirectoryNotFoundException e)
            {
                return Fail(command, e.Message, ExitCodes.NotFound);
            }
            catch (Exception e) when (e is ShellUsage or WriteRefused or InvalidOperationException or InvalidDataException or ArgumentException
                or EndOfStreamException or NotSupportedException)
            {
                return Fail(command, e.Message, ExitCodes.Usage);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Fail(command, e.Message, ExitCodes.IoError);
            }
        }

        // A failed command: its reason on standard error, or as a JSON object with --json.
        private int Fail(string command, string message, int code)
        {
            if (json)
            {
                console.Out.WriteLine(MacPathJson.Document(w =>
                {
                    w.WriteString("command", command);
                    w.WriteString("error", message.Trim());
                    w.WriteNumber("code", code);
                }, indented: false));
            }
            else
            {
                console.Error.WriteLine($"{command}: {message.Trim()}");
            }

            return code;
        }

        private int Show(string text)
        {
            console.Out.WriteLine(text);
            return ExitCodes.Success;
        }

        /// <summary>
        /// A path typed in the shell, as a path inside the input (docs/cli.md §5.2): relative to where the shell is; ':' or
        /// '/' alone or first, or the input's own name first, starts from the input; '..' goes up and '.' stays.
        /// </summary>
        public string Resolve(string typed)
        {
            ArgumentNullException.ThrowIfNull(typed);
            var from = current;
            var rest = typed;
            if (typed.Length > 0 && typed[0] is ':' or '/')
            {
                (from, rest) = ("", typed[1..]);
            }
            else if (MacPaths.Split(typed) is [var first, ..] && MacPaths.NamesEqual(first, name)
                && (typed.Length == first.Length || typed[first.Length] is ':' or '/'))
            {
                (from, rest) = ("", typed[first.Length..]);
            }

            var names = MacPaths.Split(from).ToList();
            foreach (var part in MacPaths.Split(rest))
            {
                if (part == "..")
                {
                    if (names.Count > 0)
                    {
                        names.RemoveAt(names.Count - 1);
                    }
                }
                else if (part != ".")
                {
                    names.Add(part);
                }
            }

            return string.Join(":", names.Select(MacPaths.Escape));
        }

        // The entry a typed path names, or not found.
        private MacPathEntry Existing(string typed) => session.Tree.Resolve(Resolve(typed)) ?? throw new PathNotFound($"{typed}: names nothing.");

        // An entry's path inside the input, as the tree names it (its own case).
        private string Inside(MacPathEntry entry) => MacPathJson.Inside(session.Tree, entry.Path);

        private int Cd(List<string> args)
        {
            var o = ShellOptions.Parse(args, [], [], 0, 1);
            if (o.Positional.Count == 1)
            {
                var entry = Existing(o.Positional[0]);
                if (entry.Kind == MacPathKind.Resource)
                {
                    throw new ShellUsage($"{o.Positional[0]} is a resource; cd goes into folders, containers, files' forks and types.");
                }

                current = Inside(entry);
            }

            if (json)
            {
                return Show(MacPathJson.Document(w =>
                {
                    w.WriteString("input", session.Input);
                    w.WriteString("path", current);
                }, indented: false));
            }

            return o.Positional.Count == 0 ? Show(current.Length == 0 ? name : name + ":" + current) : ExitCodes.Success;
        }

        // A read command through the CLI's path commands, on the session's tree.
        private int Read(List<string> args, int min, int max, string[] flags, string[] values, Func<PathCommands, string, ShellOptions, int> run)
        {
            var o = ShellOptions.Parse(args, flags, values, min, max);
            var target = o.Positional.Count > 0 ? Resolve(o.Positional[0]) : current;
            var error = new StringWriter();
            var paths = new PathCommands(console.Out, error, Stream.Null, options, readOptions, strict: false, quiet: false, session.Tree, session.Input);
            var code = run(paths, target, o);
            var problems = error.ToString().Trim();
            if (code != ExitCodes.Success)
            {
                return Fail(running, problems.Length > 0 ? problems : "failed.", code);
            }

            if (problems.Length > 0)
            {
                console.Error.WriteLine(problems);
            }

            return code;
        }

        private static MacFindQuery Query(ShellOptions o)
        {
            FourCC? Code(string option) => o.Value(option) switch
            {
                null => null,
                var text when FourCC.TryParse(text, out var code) => code,
                var text => throw new ShellUsage($"{option} '{text}' is not a four-character code."),
            };

            return new MacFindQuery
            {
                Name = o.Value("--name"),
                Type = Code("--type"),
                Creator = Code("--creator"),
                Kind = o.Value("--kind") switch
                {
                    null => null,
                    "folder" => MacPathKind.Folder,
                    "file" => MacPathKind.File,
                    "container" => MacPathKind.Container,
                    var other => throw new ShellUsage($"--kind is folder, file or container, not {other}."),
                },
                ResourceType = Code("--resource-type"),
                Contains = o.Value("--contains") is { } text ? MacRoman.Encode(text) : null,
            };
        }

        // res: a file's resources, type by type.
        private int Res(List<string> args)
        {
            var o = ShellOptions.Parse(args, [], [], 0, 1);
            var file = o.Positional.Count > 0 ? Existing(o.Positional[0]) : session.Tree.Resolve(current)!;
            var fork = file.Kind switch
            {
                MacPathKind.ResourceFork or MacPathKind.ResourceType => file,
                MacPathKind.File or MacPathKind.Container => session.Tree.Children(file).FirstOrDefault(c => c.Kind == MacPathKind.ResourceFork),
                _ => throw new ShellUsage($"{Inside(file)} is a {MacCommands.KindName(file.Kind)}; res lists a file's resources."),
            };
            var types = fork is null ? [] : fork.Kind == MacPathKind.ResourceType ? [fork] : session.Tree.Children(fork);
            var resources = types.SelectMany(t => MacCommands.List(session.Tree, t)).ToList();
            if (json)
            {
                return Show(MacPathJson.Document(w =>
                {
                    MacPathJson.Where(w, session.Input, session.Tree, file.Path);
                    MacPathJson.Entries(w, "resources", session.Tree, resources);
                }, indented: false));
            }

            if (resources.Count == 0)
            {
                return Show("No resources.");
            }

            foreach (var r in resources)
            {
                var size = r.DataSize?.ToString(CultureInfo.InvariantCulture) ?? "";
                console.Out.WriteLine($"'{r.ResourceType}' {r.ResourceId,6} {size,8}  {r.ResourceName}".TrimEnd());
            }

            return ExitCodes.Success;
        }

        // copy: out to the host when the source is in the input (get), in from the host when it is a host file (put).
        private int Copy(List<string> args, bool? outward)
        {
            var o = ShellOptions.Parse(args, ["--overwrite"], ["--as", "--name", "--type", "--creator"], 2, 2);
            var (source, destination) = (o.Positional[0], o.Positional[1]);
            var inside = outward ?? (session.Tree.Resolve(Resolve(source)) is not null || !File.Exists(source));
            if (inside)
            {
                var format = o.Value("--as") switch
                {
                    null or "appledouble" => MacGetFormat.AppleDouble,
                    "basilisk" => MacGetFormat.Basilisk,
                    "macbinary" => MacGetFormat.MacBinary,
                    "raw" => MacGetFormat.Raw,
                    var other => throw new ShellUsage($"--as is appledouble, basilisk, macbinary or raw, not {other}."),
                };
                Existing(source);
                return Read([source], 1, 1, [], [], (paths, target, _) => paths.Get(target, Path.GetFullPath(destination), format, o.Flag("--overwrite"), enter: false, json));
            }

            var file = HostImport.Read(Path.GetFullPath(source), options);
            return Apply(destination, (_, tree, rest) => MacEdits.Put(tree, rest, file, o.Value("--name"), o.Value("--type"), o.Value("--creator")));
        }

        // A change: checked, made in the session, and printed as the write commands print theirs.
        private int Change(List<string> args, int count, string[] flags, string[] values,
            Func<ShellOptions, string, Func<InputEditKind, MacPathTree, string, Action<InputEditSession>>> plan)
        {
            var o = ShellOptions.Parse(args, flags, values, count, count);
            return Apply(o.Positional[0], plan(o, o.Positional[0]));
        }

        private int Apply(string typed, Func<InputEditKind, MacPathTree, string, Action<InputEditSession>> plan)
        {
            var made = session.Apply(plan, Resolve(typed), dryRun: false);

            // The shell stays where it was, or goes up to what is left of it (a folder deleted or renamed).
            while (current.Length > 0 && session.Tree.Resolve(current) is null)
            {
                current = string.Join(":", MacPaths.Split(current).SkipLast(1).Select(MacPaths.Escape));
            }

            if (json)
            {
                return Show(MacPathJson.Document(w =>
                {
                    MacPathJson.Changes(w, session.Input, false, [], made);
                    w.WriteNumber("unsaved", session.Unsaved);
                }, indented: false));
            }

            foreach (var planned in made)
            {
                console.Out.WriteLine(planned.Detail.Length > 0 ? $"{planned.Action} {planned.Path} ({planned.Detail})" : $"{planned.Action} {planned.Path}");
            }

            return ExitCodes.Success;
        }

        // save: over the input (keeping .orig); save as <file>: a new file.
        private int Save(List<string> args)
        {
            var o = ShellOptions.Parse(args, [], [], 0, 2);
            var changes = session.Changes.ToList();
            IReadOnlyList<string> written;
            if (o.Positional.Count == 0)
            {
                session.SaveInPlace();
                written = [session.Input];
            }
            else if (o.Positional.Count == 2 && o.Positional[0].Equals("as", StringComparison.OrdinalIgnoreCase))
            {
                written = session.SaveAs(Path.GetFullPath(o.Positional[1]));
            }
            else
            {
                throw new ShellUsage("save, or save as <file>.");
            }

            if (json)
            {
                return Show(MacPathJson.Document(w =>
                {
                    MacPathJson.Changes(w, session.Input, false, written, changes);
                    w.WriteNumber("unsaved", session.Unsaved);
                }, indented: false));
            }

            foreach (var file in written)
            {
                console.Out.WriteLine($"Wrote {file}");
            }

            return ExitCodes.Success;
        }

        // exit [--discard]: leaving is decided by the loop (-1 when told to discard).
        private int Exit(List<string> args)
        {
            var o = ShellOptions.Parse(args, ["--discard"], [], 0, 0);
            leaving = true;
            return o.Flag("--discard") ? -1 : ExitCodes.Success;
        }

        /// <summary>
        /// Tab completion (docs/cli.md §5.3): a command name first, then the names in the folder the word before the
        /// cursor is in, quoted when they hold spaces; a folder, container, fork or type gets a ':' to go on.
        /// </summary>
        public Completion Complete(string line, int cursor)
        {
            ArgumentNullException.ThrowIfNull(line);
            var before = line[..cursor];
            var words = ShellWords.Scan(before, out var open);
            var atSpace = !open && (before.Length == 0 || char.IsWhiteSpace(before[^1]));
            var start = atSpace || words.Count == 0 ? cursor : words[^1].Start;
            var fragment = atSpace || words.Count == 0 ? "" : words[^1].Text;
            var first = atSpace ? words.Count == 0 : words.Count <= 1;
            if (first)
            {
                var commands = CommandNames.Where(c => c.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)).ToList();
                return Replace(line, cursor, start, commands.Count == 1 ? commands[0] + " " : Common(commands, fragment), commands, fragment);
            }

            var cut = LastSeparator(fragment);
            var folder = fragment[..(cut + 1)];
            var prefix = string.Concat(MacPaths.Split(fragment[(cut + 1)..]));
            if (session.Tree.Resolve(Resolve(folder)) is not { } entry)
            {
                return new Completion(line, cursor, []);
            }

            var matches = session.Tree.Children(entry).Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            var names = matches.Select(m => m.Name).ToList();
            if (matches.Count == 1)
            {
                var goesOn = matches[0].Kind is MacPathKind.Folder or MacPathKind.Container or MacPathKind.ResourceFork or MacPathKind.ResourceType;
                var word = ShellWords.Quote(folder + MacPaths.Escape(matches[0].Name) + (goesOn ? ":" : ""));
                return Replace(line, cursor, start, goesOn ? word : word + " ", names, fragment);
            }

            var common = Common(names, prefix);
            return Replace(line, cursor, start, common is not null ? ShellWords.Quote(folder + MacPaths.Escape(common)) : null, names, fragment);
        }

        // The line with the word replaced (null: unchanged), and the candidates.
        private static Completion Replace(string line, int cursor, int start, string? word, IReadOnlyList<string> candidates, string fragment)
        {
            if (word is null || word == fragment)
            {
                return new Completion(line, cursor, candidates);
            }

            return new Completion(line[..start] + word + line[cursor..], start + word.Length, candidates);
        }

        // The longest start the names share (in the first name's case), or null when no longer than what was typed.
        private static string? Common(IReadOnlyList<string> names, string typed)
        {
            if (names.Count == 0)
            {
                return null;
            }

            var length = names[0].Length;
            foreach (var other in names.Skip(1))
            {
                var i = 0;
                while (i < length && i < other.Length && char.ToUpperInvariant(names[0][i]) == char.ToUpperInvariant(other[i]))
                {
                    i++;
                }

                length = i;
            }

            return length > typed.Length ? names[0][..length] : null;
        }

        // The last ':' or '/' that separates names (not escaped, not in a type's quotes); -1 when there is none.
        private static int LastSeparator(string text)
        {
            var last = -1;
            var inSingle = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                }
                else if (c == '\'' && (inSingle || i == 0 || text[i - 1] is ':' or '/'))
                {
                    inSingle = !inSingle;
                }
                else if (c is ':' or '/' && !inSingle)
                {
                    last = i;
                }
            }

            return last;
        }
    }
}
