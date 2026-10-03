using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources.Cli.Shell;

namespace ClassicMac.Resources.Cli.Tests;

// classicmac shell (docs/cli.md §5): commands run from a script, a person's session through a fake console, the line
// editor through a fake terminal, and the words and completion rules.
public sealed class ShellTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-shell").FullName;
    private readonly string disk;

    public ShellTests() => disk = WritableDisk.Build(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Runs a script through the command line: exit code, output lines, error text.
    private static (int Code, string[] Lines, string Error) Script(string input, string script, params string[] options)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error) { ShellInput = new StringReader(script) }.Run(["shell", input, .. options]);
        return (code, output.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n'), error.ToString());
    }

    private sealed class Person(params string[] lines) : IShellConsole
    {
        private readonly Queue<string> answers = new(lines);

        public List<string> Prompts { get; } = [];

        public bool Interactive => true;

        public TextWriter Out { get; } = new StringWriter();

        public TextWriter Error { get; } = new StringWriter();

        public string? ReadLine(string prompt, Func<string, int, Completion>? complete)
        {
            Prompts.Add(prompt);
            return answers.TryDequeue(out var line) ? line : null;
        }
    }

    private MacShell Shell(IShellConsole console, PathSession session, bool json = false) =>
        new(console, session, ContainerReadOptions.Default, ReadOptions.Default, json);

    private PathSession Open() => PathSession.Open("t", disk, ContainerReadOptions.Default, ReadOptions.Default);

    private static IReadOnlyList<MacFolder> Folders(string image) => HfsReader.Instance.ReadFolders(ForkData.FromFile(image), new ContainerContext());

    private static IReadOnlyList<MacFile> Files(string image) => HfsReader.Instance.Read(ForkData.FromFile(image), new ContainerContext());

    [Fact]
    public void A_script_goes_into_folders_and_lists_reads_and_describes()
    {
        var (code, lines, error) = Script(disk, "cd Docs\ndir\ncd ..\ntype \"Read Me\"\ninfo Docs:Letter\ncd\nres Docs:Letter\n");
        Assert.True(code == 0, error);
        Assert.Matches(@"^file\s+.*Letter$", lines[0]);                            // dir in Docs: Letter
        Assert.Equal("hello", lines[1]);                                          // type
        Assert.Contains(lines, l => l == $"Path: {Path.GetFullPath(disk)}:Docs:Letter");   // info names the input, not a working copy
        Assert.Contains("disk.img", lines);                                       // cd alone: where it is
        Assert.Matches(@"^'STR '\s+128\s+2$", lines[^1]);                         // res
    }

    [Fact]
    public void Paths_are_relative_absolute_or_up()
    {
        using var session = Open();
        var shell = Shell(new Person(), session);
        Assert.Equal("disk.img>", shell.Prompt);
        shell.Execute("cd docs");                                                  // HFS names ignore case; the shell keeps the volume's
        Assert.Equal(("Docs", "disk.img:Docs>"), (shell.Current, shell.Prompt));
        Assert.Equal("Docs:Letter", shell.Resolve("Letter"));
        Assert.Equal("Read Me", shell.Resolve("../Read Me"));
        Assert.Equal("Read Me", shell.Resolve(":Read Me"));
        Assert.Equal("Read Me", shell.Resolve("/Read Me"));
        Assert.Equal("Read Me", shell.Resolve("disk.img:Read Me"));
        Assert.Equal("", shell.Resolve(":"));
        Assert.Equal("", shell.Resolve("../.."));
        Assert.Equal("Docs", shell.Resolve("."));
        shell.Execute("cd Letter:#rsrc:'STR '");
        Assert.Equal("disk.img:Docs:Letter:#rsrc:'STR '>", shell.Prompt);
        Assert.Equal(ExitCodes.Usage, shell.Execute("cd 128"));                   // a resource is not gone into
        Assert.Equal(ExitCodes.NotFound, shell.Execute("cd Missing"));
        shell.Execute("cd :");
        Assert.Equal("", shell.Current);
    }

    [Fact]
    public void Move_puts_an_item_in_another_folder_named_from_where_the_shell_is()
    {
        var output = Path.Combine(folder, "moved.img");
        var (code, lines, error) = Script(disk, $"cd Docs\nmove Letter ..\nmove \"..:Read Me\" .\nsave as \"{output}\"\nexit\n");

        Assert.True(code == 0, error);
        Assert.Equal("move Docs:Letter (to the volume's top level)", lines[0]);
        Assert.Equal("move Read Me (to Docs)", lines[1]);
        Assert.Equal(["Docs:Read Me", "Letter"], Files(output).Select(f => f.MacPath).Order());
    }

    [Fact]
    public void Changes_stay_in_the_session_until_saved_as()
    {
        var before = File.ReadAllBytes(disk);
        var output = Path.Combine(folder, "out.img");
        var source = Path.Combine(folder, "note.txt");
        File.WriteAllText(source, "Hi");
        var (code, lines, error) = Script(disk,
            $"md Docs:Old\ncd Docs\nren Letter \"Old Letter\"\ncopy \"{source}\" Old --type TEXT\nset Old:note.txt --creator ttxt\ndir Old\nsave as \"{output}\"\nexit\n");
        Assert.True(code == 0, error);
        Assert.Equal("mkdir Docs:Old (a new folder)", lines[0]);
        Assert.Equal("rename Docs:Letter (to Old Letter)", lines[1]);
        Assert.StartsWith("add Docs:Old:note.txt", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("set Docs:Old:note.txt", lines[3], StringComparison.Ordinal);
        Assert.Matches(@"^file\s+TEXT ttxt.*note\.txt$", lines[4]);              // reads see the changes
        Assert.Equal($"Wrote {output}", lines[5]);
        Assert.Equal(before, File.ReadAllBytes(disk));
        var note = Files(output).Single(f => f.MacPath == "Docs:Old:note.txt");
        Assert.Equal((FourCC.FromString("TEXT"), FourCC.FromString("ttxt")), (note.FinderInfo.Type, note.FinderInfo.Creator));
        Assert.Contains(Files(output), f => f.MacPath == "Docs:Old Letter");
    }

    [Fact]
    public void Save_writes_over_the_input_keeping_the_original()
    {
        var before = File.ReadAllBytes(disk);
        var (code, lines, error) = Script(disk, "del Docs -r\nsave\ndir\n");
        Assert.True(code == 0, error);
        Assert.Equal("delete Docs (with everything in it)", lines[0]);
        Assert.Equal($"Wrote {Path.GetFullPath(disk)}", lines[1]);
        Assert.DoesNotContain(Folders(disk), f => f.MacPath == "Docs");
        Assert.Equal(before, File.ReadAllBytes(disk + ".orig"));
        Assert.Single(lines.Skip(2));                                            // only Read Me left
    }

    [Fact]
    public void A_script_stops_at_the_first_error_with_its_exit_code()
    {
        var (code, lines, error) = Script(disk, "dir\ncd Missing\ndir\n");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Equal(2, lines.Length);                                           // the first dir only
        Assert.Contains("cd:", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, Script(disk, "frobnicate\n").Code);
        Assert.Equal(ExitCodes.Usage, Script(disk, "del\n").Code);               // an argument missing
        Assert.Equal(ExitCodes.Usage, Script(disk, "dir --bogus\n").Code);
        Assert.Equal(ExitCodes.Usage, Script(disk, "del Docs\n").Code);          // not empty
        Assert.Equal(0, Script(disk, "# a comment\n\nhelp\n").Code);
        Assert.Equal(ExitCodes.Usage, Script(disk, "save to x\n").Code);
        Assert.Equal(ExitCodes.Usage, Script(disk, "res Docs\n").Code);           // a folder has no resources
        Assert.Equal(ExitCodes.Usage, Script(disk, "type Docs\n").Code);          // a folder is not typed
        Assert.Equal(ExitCodes.Usage, Script(disk, "find --kind disk\n").Code);
    }

    [Fact]
    public void Help_hex_and_files_without_resources()
    {
        var (code, lines, _) = Script(disk, "help\ntype \"Read Me\" --hex\nres \"Read Me\"\n");
        Assert.Equal(0, code);
        Assert.Contains(lines, l => l.StartsWith("cd [path]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("68 65 6C 6C 6F", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("No resources.", lines[^1]);
    }

    [Fact]
    public void Leaving_a_script_with_unsaved_changes_is_refused_unless_discarded()
    {
        var (code, _, error) = Script(disk, "md New\n");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("not saved", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, Script(disk, "md New\nexit\n").Code);
        Assert.Equal(0, Script(disk, "md New\nexit --discard\n").Code);
        Assert.DoesNotContain(Folders(disk), f => f.MacPath == "New");
    }

    [Fact]
    public void Json_prints_one_object_per_command()
    {
        var (code, lines, error) = Script(disk, "cd Docs\ndir\nmd Sub\ntype Letter:#rsrc:'STR ':128\nfind : --name R*\ncd Nope\n", "--json");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Equal(6, lines.Length);
        var json = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal((Path.GetFullPath(disk), "Docs"), (json[0].GetProperty("input").GetString(), json[0].GetProperty("path").GetString()));
        Assert.Equal("Docs:Letter", json[1].GetProperty("entries")[0].GetProperty("path").GetString());
        Assert.Equal(Path.GetFullPath(disk), json[1].GetProperty("input").GetString());     // the input, not a working copy
        Assert.Equal((1, "mkdir"), (json[2].GetProperty("unsaved").GetInt32(), json[2].GetProperty("changes")[0].GetProperty("action").GetString()));
        Assert.Equal(("text", "a"), (json[3].GetProperty("encoding").GetString(), json[3].GetProperty("text").GetString()));
        Assert.Equal("Read Me", json[4].GetProperty("matches")[0].GetProperty("path").GetString());
        Assert.Equal(("cd", 5), (json[5].GetProperty("command").GetString(), json[5].GetProperty("code").GetInt32()));
    }

    [Fact]
    public void Copy_goes_out_to_the_host_and_in_from_it()
    {
        var target = Path.Combine(folder, "out");
        Directory.CreateDirectory(target);
        var (code, lines, error) = Script(disk, $"copy \"Read Me\" \"{target}\" --as raw\nget Docs:Letter \"{target}\" --as macbinary\nexit\n");
        Assert.True(code == 0, error);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "Read Me")));
        Assert.Equal(2, lines.Length);
        Assert.Equal(ExitCodes.NotFound, Script(disk, $"put \"{Path.Combine(folder, "none")}\" Docs\n").Code);
    }

    [Fact]
    public void The_input_path_can_start_inside_it()
    {
        var (code, lines, _) = Script(disk + ":Docs", "cd\n");
        Assert.Equal((0, "disk.img:Docs"), (code, lines[0]));
        Assert.Equal(ExitCodes.NotFound, Script(disk + ":Nope", "cd\n").Code);
        Assert.Equal(ExitCodes.NotFound, Script(Path.Combine(folder, "none.img"), "cd\n").Code);
    }

    [Fact]
    public void A_person_leaving_with_unsaved_changes_is_asked()
    {
        using (var session = Open())
        {
            var person = new Person("md A", "exit", "c", "exit", "d");               // cancel, then discard
            Assert.Equal(0, Shell(person, session).Run());
            Assert.Equal(2, person.Prompts.Count(p => p.Contains("not saved", StringComparison.Ordinal)));
            Assert.Equal("disk.img> ", person.Prompts[0]);
        }

        Assert.DoesNotContain(Folders(disk), f => f.MacPath == "A");

        var output = Path.Combine(folder, "asked.img");
        using (var session = Open())
        {
            var person = new Person("md B", "exit", "a", output);
            Assert.Equal(0, Shell(person, session).Run());
            Assert.Contains(person.Prompts, p => p == "Save as: ");
        }

        Assert.Contains(Folders(output), f => f.MacPath == "B");

        using (var session = Open())
        {
            Assert.Equal(0, Shell(new Person("md C", "exit", "s"), session).Run());
        }

        Assert.Contains(Folders(disk), f => f.MacPath == "C");

        using (var session = Open())
        {
            Assert.Equal(ExitCodes.Usage, Shell(new Person("md D"), session).Run());        // the input ends while asking
        }
    }

    [Fact]
    public void Tab_completes_commands_and_names_quoting_spaces()
    {
        using var session = Open();
        var shell = Shell(new Person(), session);
        Assert.Equal(("dir ", 4), Line(shell.Complete("di", 2)));
        Assert.Equal(["dir", "del"], shell.Complete("d", 1).Candidates);
        Assert.Equal(("cd \"Read Me\" ", 13), Line(shell.Complete("cd r", 4)));
        Assert.Equal(("cd Docs:", 8), Line(shell.Complete("cd D", 4)));
        Assert.Equal(("cd Docs:Letter ", 15), Line(shell.Complete("cd Docs:L", 9)));
        Assert.Equal(("type Docs:Letter:#rsrc:", 23), Line(shell.Complete("type Docs:Letter:#", 18)));
        Assert.Equal(("type Docs:Letter:#rsrc:'STR ':", 30), Line(shell.Complete("type Docs:Letter:#rsrc:", 23)));
        Assert.Equal(("type \"Read Me\" ", 15), Line(shell.Complete("type \"Rea", 9)));
        var both = shell.Complete("dir ", 4);                                    // several: shown, line unchanged
        Assert.Equal(("dir ", 4), (both.Line, both.Cursor));
        Assert.Equal(["Read Me", "Docs"], both.Candidates);
        Assert.Empty(shell.Complete("dir Nope:x", 10).Candidates);
        Assert.Equal(("cd Docs: --x", 8), Line(shell.Complete("cd D --x", 4)));  // the rest of the line stays
    }

    private static (string, int) Line(Completion completion) => (completion.Line, completion.Cursor);

    [Fact]
    public void Words_split_on_spaces_with_quotes_and_resource_types()
    {
        Assert.Equal(["cd", "Read Me"], ShellWords.Split("cd \"Read Me\"").Select(w => w.Text));
        Assert.Equal(["type", "File:#rsrc:'STR ':128"], ShellWords.Split("type File:#rsrc:'STR ':128").Select(w => w.Text));
        Assert.Equal(["cd", "Bob's", "Disk"], ShellWords.Split("cd Bob's Disk").Select(w => w.Text));     // an apostrophe in a name is not a quote
        Assert.Equal(["cd", "A B", "C\"D"], ShellWords.Split("cd A\\ B C\\\"D").Select(w => w.Text));
        Assert.Equal(["copy", "C:\\Disks\\a.img"], ShellWords.Split("copy C:\\Disks\\a.img").Select(w => w.Text));
        Assert.Equal(["cd", "System Fo"], ShellWords.Split("  cd  \"System F\"o ").Select(w => w.Text));
        Assert.Equal([0, 3], ShellWords.Split("cd \"x y\"").Select(w => w.Start));
        ShellWords.Scan("cd \"Sys", out var open);
        Assert.True(open);
        Assert.Equal("\"Read Me\"", ShellWords.Quote("Read Me"));
        Assert.Equal("File:#rsrc:'STR '", ShellWords.Quote("File:#rsrc:'STR '"));
        Assert.Equal("Docs", ShellWords.Quote("Docs"));
    }

    // A terminal whose keys are given and whose screen is the last line shown.
    private sealed class Keys(params ConsoleKeyInfo[] keys) : IShellTerminal
    {
        private readonly Queue<ConsoleKeyInfo> queue = new(keys);

        public string Shown { get; private set; } = "";

        public int Cursor { get; private set; }

        public List<string> Written { get; } = [];

        public ConsoleKeyInfo ReadKey() => queue.TryDequeue(out var key) ? key : Key(ConsoleKey.D, control: true);

        public void Show(string prompt, string line, int cursor) => (Shown, Cursor) = (prompt + line, cursor);

        public void EndLine()
        {
        }

        public void WriteLine(string text) => Written.Add(text);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, bool control = false) => new('\0', key, false, false, control);

    private static ConsoleKeyInfo[] Type(string text) => [.. text.Select(c => new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false))];

    private static readonly Func<string, int, Completion> NoCompletion = (line, cursor) => new Completion(line, cursor, []);

    [Fact]
    public void The_line_editor_edits_anywhere_in_the_line()
    {
        var terminal = new Keys([.. Type("dr"), Key(ConsoleKey.LeftArrow), .. Type("i"), Key(ConsoleKey.End), .. Type(" x"), Key(ConsoleKey.Backspace),
            Key(ConsoleKey.Home), Key(ConsoleKey.Delete), .. Type("D"), Key(ConsoleKey.Enter)]);
        var editor = new LineEditor(terminal);
        Assert.Equal("Dir ", editor.ReadLine("> ", NoCompletion));
        Assert.Equal("> Dir ", terminal.Shown);

        var cleared = new Keys([.. Type("abc"), Key(ConsoleKey.Escape), .. Type("ok"), Key(ConsoleKey.Enter)]);
        Assert.Equal("ok", new LineEditor(cleared).ReadLine("", NoCompletion));
        Assert.Null(new LineEditor(new Keys(Key(ConsoleKey.D, control: true))).ReadLine("", NoCompletion));
        Assert.Equal("a", new LineEditor(new Keys([.. Type("a"), Key(ConsoleKey.D, control: true), Key(ConsoleKey.Enter)])).ReadLine("", NoCompletion));   // not empty: goes on
    }

    [Fact]
    public void Up_and_down_recall_the_lines_typed_before()
    {
        var terminal = new Keys([.. Type("one"), Key(ConsoleKey.Enter), .. Type("two"), Key(ConsoleKey.Enter), .. Type("two"), Key(ConsoleKey.Enter),
            .. Type("dr"), Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter),
            .. Type("x"), Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)]);
        var editor = new LineEditor(terminal);
        Assert.Equal(["one", "two", "two", "two", "x"], Enumerable.Range(0, 5).Select(_ => editor.ReadLine("", NoCompletion)));
        Assert.Equal(["one", "two", "x"], editor.History);                       // the same line twice is kept once

        var asked = new LineEditor(new Keys([.. Type("y"), Key(ConsoleKey.Enter)]));
        Assert.Equal("y", asked.ReadLine("Save as: ", null));
        Assert.Empty(asked.History);                                             // answers are not commands
    }

    [Fact]
    public void Tab_completes_through_the_editor_and_lists_several()
    {
        using var session = Open();
        var shell = Shell(new Person(), session);
        var terminal = new Keys([.. Type("cd r"), Key(ConsoleKey.Tab), Key(ConsoleKey.Enter), .. Type("dir "), Key(ConsoleKey.Tab), Key(ConsoleKey.Enter)]);
        var editor = new LineEditor(terminal);
        Assert.Equal("cd \"Read Me\" ", editor.ReadLine("", shell.Complete));
        Assert.Equal("dir ", editor.ReadLine("", shell.Complete));
        Assert.Equal(["Read Me  Docs"], terminal.Written);
    }

    [Fact]
    public void The_shell_command_is_on_the_command_line()
    {
        var output = new StringWriter();
        Assert.Equal(0, new CommandLine(output, new StringWriter()).Run(["shell", "--help"]));
        Assert.Contains("--script", output.ToString(), StringComparison.Ordinal);
        var script = Path.Combine(folder, "commands.txt");
        File.WriteAllText(script, "cd Docs\ncd\n");
        var result = new StringWriter();
        Assert.Equal(0, new CommandLine(result, new StringWriter()).Run(["shell", disk, "--script", script]));
        Assert.Contains("disk.img:Docs", result.ToString(), StringComparison.Ordinal);
    }
}
