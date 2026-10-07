using System.Text;
using System.Text.Json;
using ClassicMac.Files.Tests;

namespace ClassicMac.Cli.Tests;

// The file commands on Mac paths (docs/cli.md §2): ls, stat, cat, find and get, as text and as --json.
public sealed class PathCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-pathcli").FullName;
    private readonly string disk;

    public PathCommandTests() => disk = MacPathFixtures.Disk(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error, byte[] Binary) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var binary = new MemoryStream();
        var code = new CommandLine(output, error, binary).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString(), binary.ToArray());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private string P(string mac) => disk + ":" + mac;

    [Fact]
    public void Ls_lists_entries_as_columns_or_JSON()
    {
        var (code, output, _, _) = Run("ls", P("System Folder"));
        Assert.Equal(0, code);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^file\s+FNDR MACS\s+6\s+\d+\s+\S+ \S+\s+Finder$", lines[0]);           // kind, type creator, data, rsrc, modified, name
        Assert.Matches(@"^file\s+TEXT ttxt\s+13\s+0\s+\S+ \S+\s+Read Me$", lines[1]);

        (code, output, _, _) = Run("ls", P("System Folder"), "--json");
        Assert.Equal(0, code);
        var json = Json(output);
        Assert.Equal((disk, "System Folder"), (json.GetProperty("input").GetString(), json.GetProperty("path").GetString()));
        Assert.Equal("System Folder:Finder", json.GetProperty("entries")[0].GetProperty("path").GetString());   // inside the input, as the write commands name paths
        var finder = json.GetProperty("entries")[0];
        Assert.Equal(("Finder", "file", "FNDR", "MACS", 6), (finder.GetProperty("name").GetString(), finder.GetProperty("kind").GetString(),
            finder.GetProperty("type").GetString(), finder.GetProperty("creator").GetString(), finder.GetProperty("dataSize").GetInt32()));
        Assert.Equal(["hasBundle", "invisible"], finder.GetProperty("flagNames").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0x6000, finder.GetProperty("flags").GetInt32());
        Assert.False(finder.TryGetProperty("format", out _));                              // facts that do not apply are left out

        // An empty folder, a container, and the dates of a file in an archive.
        Assert.Equal(0, Json(Run("ls", P("Empty"), "--json").Output).GetProperty("entries").GetArrayLength());
        var packed = Json(Run("ls", P("Archive.bin"), "--json").Output).GetProperty("entries")[0];
        Assert.Equal(("1999-01-24T05:20:00", "1999-01-24T05:21:00"), (packed.GetProperty("created").GetString(), packed.GetProperty("modified").GetString()));
        var root = Json(Run("ls", disk, "--json").Output).GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Inner.img");
        Assert.Equal(("container", "HFS volume"), (root.GetProperty("kind").GetString(), root.GetProperty("format").GetString()));
    }

    [Fact]
    public void Stat_shows_the_facts_the_kind_and_how_it_was_read()
    {
        var (code, output, _, _) = Run("stat", P("Inner.img:Deep:Note"));
        Assert.Equal(0, code);
        Assert.Contains("Kind: file", output);
        Assert.Contains("Type / creator: 'TEXT' / 'ttxt'", output);
        Assert.Contains("Finder kind: SimpleText text document", output);
        Assert.Contains("Read as: disk.img (host file) > disk.img (HFS volume) > Inner.img (HFS volume)", output);

        var json = Json(Run("stat", P("Inner.img:Deep:Note"), "--json").Output);
        Assert.Equal("SimpleText text document", json.GetProperty("kindName").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("kindSource").GetString()));
        Assert.Equal(["host file", "HFS volume", "HFS volume"], json.GetProperty("chain").EnumerateArray().Select(s => s.GetProperty("format").GetString()));
        var resource = Json(Run("stat", P("System Folder:Finder:#rsrc:'STR ':128"), "--json").Output);
        Assert.Equal(("resource", "STR ", 128, "Greeting"), (resource.GetProperty("kind").GetString(), resource.GetProperty("resourceType").GetString(),
            resource.GetProperty("resourceId").GetInt32(), resource.GetProperty("resourceName").GetString()));
    }

    [Fact]
    public void Stat_of_a_volume_shows_its_name_space_counts_and_locks()
    {
        var (code, output, _, _) = Run("stat", disk + ":");
        Assert.Equal(0, code);
        Assert.Contains("Volume: HFS \"Disk\"\n", output);
        Assert.Matches(@"\nSize: [\d,]+ bytes \([\d.]+ [KMG]iB\) in [\d,]+ blocks of [\d,]+ bytes\n", output);
        Assert.Matches(@"\nFree: [\d,]+ bytes \([\d.]+ [KMG]iB\), [\d,]+ blocks\n", output);
        Assert.Matches(@"\nFiles / folders: \d+ / \d+\n", output);
        Assert.Matches(@"\nFragmentation: \d+ of \d+ files in more than one extent; free space in [\d,]+ runs?, the largest [\d,]+ blocks\n", output);
        Assert.DoesNotContain("Volume locked", output);
        Assert.Contains("Volume: HFS \"Inner\"\n", Run("stat", P("Inner.img")).Output);              // a volume inside the volume

        var volume = Json(Run("stat", disk + ":", "--json").Output).GetProperty("volume");
        Assert.Equal(("HFS", "Disk"), (volume.GetProperty("format").GetString(), volume.GetProperty("name").GetString()));
        var blockSize = volume.GetProperty("blockSize").GetInt64();
        Assert.True(blockSize >= 512);
        Assert.Equal(volume.GetProperty("totalBlocks").GetInt64() * blockSize, volume.GetProperty("totalBytes").GetInt64());
        Assert.Equal(volume.GetProperty("freeBlocks").GetInt64() * blockSize, volume.GetProperty("freeBytes").GetInt64());
        Assert.True(volume.GetProperty("files").GetInt64() > 0);
        Assert.True(volume.GetProperty("folders").GetInt64() > 0);
        var fragmentation = volume.GetProperty("fragmentation");
        Assert.Equal(volume.GetProperty("files").GetInt64(), fragmentation.GetProperty("files").GetInt64());
        Assert.True(fragmentation.GetProperty("largestFreeRun").GetInt64() <= volume.GetProperty("freeBlocks").GetInt64());
        Assert.True(fragmentation.GetProperty("smallestSize").GetInt64() >= fragmentation.GetProperty("smallestSizeDefragmented").GetInt64());
        foreach (var name in new[] { "fragmentedFiles", "fragmentedForks", "mostExtents", "freeRuns" })
        {
            Assert.True(fragmentation.TryGetProperty(name, out _), name);
        }
        Assert.Equal((false, false), (volume.GetProperty("softwareLocked").GetBoolean(), volume.GetProperty("hardwareLocked").GetBoolean()));
        Assert.False(Json(Run("stat", P("Inner.img:Deep:Note"), "--json").Output).TryGetProperty("volume", out _));
    }

    [Fact]
    public void A_path_that_names_nothing_or_no_host_is_an_error()
    {
        var (code, _, error, _) = Run("stat", P("Nope"));
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("names nothing", error);
        (code, _, error, _) = Run("ls", Path.Combine(folder, "gone.img:x"));
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("no host file", error);
    }

    // --encoding (docs/cli.md §1): text in another Mac encoding, for files, decoded resources and find --contains.
    [Fact]
    public void Encoding_reads_text_in_another_Mac_script()
    {
        byte[] nihongo = [0x93, 0xFA, 0x96, 0x7B, 0x8C, 0xEA];                             // 日本語 in Mac OS Japanese
        var japanese = new HfsBuilder();
        japanese.File(HfsBuilder.Root, "Note", [.. nihongo, 0x0D], MacPathFixtures.Fork(("STR ", 128, null, [6, .. nihongo])));
        var path = Path.Combine(folder, "japanese.img");
        File.WriteAllBytes(path, japanese.Build("Disk"));

        Assert.Equal("日本語\n", Run("cat", path + ":Note", "--encoding", "japanese").Output);
        Assert.Equal("日本語\n", Run("--encoding", "x-mac-japanese", "cat", path + ":Note:#rsrc:'STR ':128").Output);
        Assert.NotEqual("日本語\n", Run("cat", path + ":Note").Output);                        // Mac OS Roman by default
        Assert.Contains("Note", Run("find", path + ":", "--contains", "本語", "--encoding", "japanese").Output);

        // Names too: a file named 日本 in Mac OS Japanese is listed and found by that name.
        var named = new HfsBuilder();
        named.File(HfsBuilder.Root, ClassicMac.Core.MacRoman.Decode([0x93, 0xFA, 0x96, 0x7B]), "hi"u8.ToArray(), []);
        var namedPath = Path.Combine(folder, "named.img");
        File.WriteAllBytes(namedPath, named.Build("Disk"));
        Assert.Contains("日本", Run("ls", namedPath + ":", "--encoding", "japanese").Output);
        Assert.Equal("hi\n", Run("cat", namedPath + ":日本", "--encoding", "japanese").Output);

        var bad = Run("cat", path + ":Note", "--encoding", "ebcdic");
        Assert.Equal(ExitCodes.Usage, bad.Code);
        Assert.Contains("japanese", bad.Error);                                             // the names it takes
    }

    // derez (docs/cli.md §2.8, docs/formats/output/rez.md): a resource fork as MPW DeRez writes it, or the portable subset.
    [Fact]
    public void Derez_writes_a_resource_fork_as_Rez_source()
    {
        var (code, _, _, binary) = Run("derez", P("System Folder:Finder"));
        Assert.Equal(0, code);
        var text = ClassicMac.Core.MacRoman.Decode(binary);
        Assert.StartsWith("data 'STR ' (128, \"Greeting\") {\r\t$\"0248 69\"", text, StringComparison.Ordinal);
        Assert.Contains("data 'TEXT' (128) {\r\t$\"7465 7874\"", text, StringComparison.Ordinal);

        var file = Path.Combine(folder, "Finder.r");
        Assert.Equal(0, Run("derez", P("System Folder:Finder"), "--portable", "-o", file).Code);
        Assert.Contains("data 'STR ' (128, \"Greeting\") {\n", File.ReadAllText(file), StringComparison.Ordinal);
    }

    // With CLASSICMAC_MPW_DISK set to an HFS disk holding MPW DeRez's outputs (X.derez beside the file X.out whose
    // resource fork it shows; crafted, crafted_e with -e): each output is byte for byte what derez writes. The disk is
    // made with Apple's tools and is not committed.
    [Fact]
    public void Derez_matches_MPW_DeRez()
    {
        var mpw = Environment.GetEnvironmentVariable("CLASSICMAC_MPW_DISK");
        Assert.SkipWhen(string.IsNullOrEmpty(mpw) || !File.Exists(mpw), "CLASSICMAC_MPW_DISK is not set.");
        var listing = Json(Run("ls", mpw + ":tests", "--json").Output);
        var names = listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
        int compared = 0;
        foreach (var derez in names.Where(n => n.EndsWith(".derez", StringComparison.Ordinal)))
        {
            var stem = derez[..^".derez".Length];
            var (source, option) = stem switch
            {
                "crafted" or "crafted_m" => ("crafted", null),
                "crafted_e" => ("crafted", "-e"),
                _ => (stem + ".out", (string?)null),
            };
            string[] args = option is null ? ["derez", mpw + ":tests:" + source] : ["derez", mpw + ":tests:" + source, option];
            var expected = Run("cat", mpw + ":tests:" + derez, "--raw").Binary;
            Assert.True(expected.SequenceEqual(Run(args).Binary), $"{derez} differs");
            compared++;
        }

        Assert.True(compared > 0);
    }

    // rez (docs/cli.md §2.9): source compiled into a resource fork; read and include take files beside it.
    [Fact]
    public void Rez_compiles_source_into_a_resource_fork()
    {
        var source = Path.Combine(folder, "app.r");
        File.WriteAllText(source, "data 'STR ' (128, \"Hello\", purgeable) { $\"05\" \"Hello\" };\nread 'TEXT' (1) \"note.txt\";\n");
        File.WriteAllText(Path.Combine(folder, "note.txt"), "a note");
        var output = Path.Combine(folder, "app.rsrc");

        var (code, text, error, _) = Run("rez", source, "-o", output);

        Assert.True(code == 0, error);
        Assert.Contains("2 resources to", text);
        var fork = ClassicMac.Resources.ResourceFork.Read(File.ReadAllBytes(output));
        var str = fork.Find(ClassicMac.Core.FourCC.FromString("STR "), 128)!;
        Assert.Equal(("Hello", ClassicMac.Resources.ResourceAttributes.Purgeable), (str.Name!.Value.ToMacRoman(), str.Attributes));
        Assert.Equal("a note"u8.ToArray(), fork.Find(ClassicMac.Core.FourCC.FromString("TEXT"), 1)!.GetData().ToArray());

        File.WriteAllText(source, "data 'TEST' (1, changed) { };");
        var failed = Run("rez", source, "-o", Path.Combine(folder, "none.rsrc"));
        Assert.Equal(1, failed.Code);
        Assert.Contains("rez.syntax", failed.Error);
        Assert.False(File.Exists(Path.Combine(folder, "none.rsrc")));
    }

    // With CLASSICMAC_MPW_DISK set: every X.r on the disk that MPW's Rez compiled (X.out) compiles to the same resources;
    // those it refused are refused; typed resources (type and resource statements) are not compiled. The changed bit,
    // which MPW's Rez keeps from an include and ClassicMac's fork writer clears as the Resource Manager does, is left out.
    [Fact]
    public void Rez_compiles_what_MPWs_Rez_compiles()
    {
        var mpw = Environment.GetEnvironmentVariable("CLASSICMAC_MPW_DISK");
        Assert.SkipWhen(string.IsNullOrEmpty(mpw) || !File.Exists(mpw), "CLASSICMAC_MPW_DISK is not set.");
        var listing = Json(Run("ls", mpw + ":tests", "--json").Output);
        var names = listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToHashSet();
        string[] typed = ["f02.r", "f03.r", "k03.r"];
        int compared = 0;
        foreach (var source in names.Where(n => n.EndsWith(".r", StringComparison.Ordinal) && !typed.Contains(n)).Order(StringComparer.Ordinal))
        {
            var output = Path.Combine(folder, source + ".rsrc");
            var (code, _, error, _) = Run("rez", mpw + ":tests:" + source, "-o", output);
            var mpwOutput = source[..^2] + ".out";
            if (!names.Contains(mpwOutput))
            {
                Assert.True(code != 0, $"{source}: MPW's Rez refused it, ClassicMac's did not");
                continue;
            }

            Assert.True(code == 0, $"{source}: {error}");
            var expected = ClassicMac.Resources.ResourceFork.Read(Run("cat", mpw + ":tests:" + mpwOutput, "--raw", "--fork", "rsrc").Binary);
            var actual = ClassicMac.Resources.ResourceFork.Read(File.ReadAllBytes(output));
            static string Key(ClassicMac.Resources.Resource r) =>
                $"{r.Type.Value:X8} {r.Id} {(r.Name is { } n ? Convert.ToHexString(n.Bytes) : "-")} {(int)r.Attributes & ~2:X2} {Convert.ToHexString(r.GetData().Span)}";
            Assert.True(expected.Resources.Select(Key).Order().SequenceEqual(actual.Resources.Select(Key).Order()),
                $"{source}: MPW {string.Join(", ", expected.Resources.Select(Key))}; ClassicMac {string.Join(", ", actual.Resources.Select(Key))}");
            compared++;
        }

        Assert.True(compared > 30, $"only {compared} compared");
    }

    [Fact]
    public void Cat_shows_text_hex_raw_bytes_or_a_decoded_resource()
    {
        var (code, output, _, _) = Run("cat", P("System Folder:Read Me"));
        Assert.Equal((0, "Hello\nWorld é\n"), (code, output));                            // Mac OS Roman to UTF-8, CR to LF
        Assert.StartsWith("00000000  66 69 6E 64 65 72", Run("cat", P("System Folder:Finder"), "--hex").Output);
        var raw = Run("cat", P("System Folder:Finder"), "--raw");
        Assert.Equal(("", "finder"), (raw.Output, Encoding.ASCII.GetString(raw.Binary)));
        Assert.True(Run("cat", P("System Folder:Finder"), "--raw", "--fork", "rsrc").Binary.Length > 100);

        // A resource: decoded (a string list as JSON, a string as text).
        Assert.Equal("Hi\n", Run("cat", P("System Folder:Finder:#rsrc:'STR ':128")).Output);
        var json = Json(Run("cat", P("System Folder:Finder:#rsrc:'STR ':128"), "--json").Output);
        Assert.Equal(("text", "Hi"), (json.GetProperty("encoding").GetString(), json.GetProperty("text").GetString()));
        var hex = Json(Run("cat", P("System Folder:Finder"), "--hex", "--json").Output);
        Assert.Equal(("hex", "66696E646572", 6), (hex.GetProperty("encoding").GetString(), hex.GetProperty("hex").GetString(), hex.GetProperty("size").GetInt32()));

        // The size limit.
        var (limited, text, error, _) = Run("cat", P("System Folder:Read Me"), "--max-bytes", "5");
        Assert.Equal((0, "Hello\n"), (limited, text));
        Assert.Contains("first 5 of 13 bytes", error);
        Assert.True(Json(Run("cat", P("System Folder:Read Me"), "--max-bytes", "5", "--json").Output).GetProperty("truncated").GetBoolean());

        // Nothing to show: a folder.
        (code, _, error, _) = Run("cat", P("System Folder"));
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("folder", error);
    }

    [Fact]
    public void Find_lists_paths_or_JSON()
    {
        var (code, output, _, _) = Run("find", disk, "--type", "TEXT");
        Assert.Equal(0, code);
        Assert.Equal([P(@"A\/B"), P("Inner.img:Deep:Note"), P("System Folder:Read Me")], output.TrimEnd('\n').Split('\n').Order(StringComparer.Ordinal));
        Assert.Equal([P("System Folder:Read Me")], Run("find", disk, "--contains", "World").Output.TrimEnd('\n').Split('\n'));
        Assert.Equal([P("System Folder:Finder")], Run("find", disk, "--contains-hex", "66 69 6e").Output.TrimEnd('\n').Split('\n'));
        Assert.Equal([P("Inner.img:Deep:Note")], Run("find", disk, "--name", "n*", "--kind", "file").Output.TrimEnd('\n').Split('\n'));
        Assert.Equal(2, Run("find", disk, "--resource-type", "STR ").Output.TrimEnd('\n').Split('\n').Length);
        Assert.Empty(Run("find", disk, "--name", "Note", "--max-depth", "0").Output);
        var json = Json(Run("find", disk, "--name", "*", "--limit", "2", "--json").Output);
        Assert.Equal((2, true), (json.GetProperty("matches").GetArrayLength(), json.GetProperty("truncated").GetBoolean()));
        Assert.Equal(ExitCodes.Usage, Run("find", disk, "--type", "TOOLONG").Code);
    }

    [Fact]
    public void Get_writes_files_to_the_host()
    {
        var output = Path.Combine(folder, "out");
        var (code, text, _, _) = Run("get", P("System Folder:Finder"), "-o", output);
        Assert.Equal(0, code);
        Assert.Equal([Path.Combine(output, "Finder"), Path.Combine(output, "._Finder")], text.TrimEnd('\n').Split('\n'));
        var json = Json(Run("get", P("System Folder:Finder"), "-o", output, "--as", "macbinary", "--json").Output);
        Assert.Equal(Path.Combine(output, "Finder.bin"), json.GetProperty("written")[0].GetString());
        Assert.Equal(ExitCodes.IoError, Run("get", P("System Folder:Finder"), "-o", output, "--as", "macbinary").Code);
        Assert.Equal(0, Run("get", P("System Folder:Finder"), "-o", output, "--as", "macbinary", "--overwrite").Code);
        Assert.Equal(0, Run("get", P("System Folder:Finder:#rsrc:'STR ':128"), "-o", output).Code);
        Assert.True(File.Exists(Path.Combine(output, "STR_128.bin")));
        Assert.Equal(0, Run("get", P("Inner.img"), "-o", output, "--enter").Code);
        Assert.True(File.Exists(Path.Combine(output, "Inner.img", "Deep", "Note")));
    }

    [Fact]
    public void Stat_shows_where_an_alias_points_and_whether_it_resolves()
    {
        var aliases = AliasFixtures.Disk(folder);
        var (code, output, _, _) = Run("stat", aliases + ":Moved alias");
        Assert.Equal(0, code);
        Assert.Contains("Original: Aliases: Old: Note\n", output);
        Assert.Contains($"Resolves: yes, by its file ID: {aliases}:Docs:Note\n", output);
        Assert.Contains("Resolves: no. The original is not on Aliases any more.", Run("stat", aliases + ":Gone alias").Output);
        var json = Json(Run("stat", aliases + ":Stuff alias", "--json").Output).GetProperty("alias");
        Assert.Equal(("Aliases: Stuff", true, "by its folder ID", "Stuff"),
            (json.GetProperty("storedPath").GetString(), json.GetProperty("found").GetBoolean(), json.GetProperty("how").GetString(), json.GetProperty("target").GetString()));
        Assert.Equal("found", json.GetProperty("state").GetString());
        var gone = Json(Run("stat", aliases + ":Gone alias", "--json").Output).GetProperty("alias");
        Assert.False(gone.TryGetProperty("target", out _));
        Assert.Equal(("missing", "The original is not on Aliases any more."), (gone.GetProperty("state").GetString(), gone.GetProperty("explanation").GetString()));
        Assert.False(Json(Run("stat", aliases + ":Docs:Note", "--json").Output).TryGetProperty("alias", out _));
    }

    [Fact]
    public void Ls_cat_and_get_follow_aliases_with_follow()
    {
        var aliases = AliasFixtures.Disk(folder);
        Assert.Equal("Hello\n", Run("cat", aliases + ":Chain alias", "--follow").Output);   // an alias of an alias
        Assert.Equal("", Run("cat", aliases + ":Note alias").Output.Trim());                 // without: the alias's empty data fork
        Assert.EndsWith("Thing\n", Run("ls", aliases + ":Stuff alias", "--follow").Output);
        var output = Path.Combine(folder, "out");
        Assert.Equal(0, Run("get", aliases + ":Moved alias", "-o", output, "--follow").Code);
        Assert.Equal("Hello", File.ReadAllText(Path.Combine(output, "Note")));
        var (code, _, error, _) = Run("cat", aliases + ":Gone alias", "--follow");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("an alias whose original is not found (Aliases: Old: Gone)", error);
    }

    // HFS Plus symbolic links: stat shows the target and where it leads; --follow follows them as it follows aliases.
    [Fact]
    public void Stat_shows_a_symbolic_link_and_follow_follows_it()
    {
        var builder = new HfsPlusBuilder();
        uint etc = builder.Folder(builder.Folder(HfsPlusBuilder.Root, "private"), "etc");
        builder.File(etc, "hosts", "127.0.0.1"u8.ToArray(), []);
        builder.Symlink(HfsPlusBuilder.Root, "etc", "private/etc");
        builder.Symlink(HfsPlusBuilder.Root, "Hosts", "/etc/hosts");
        builder.Symlink(HfsPlusBuilder.Root, "Gone", "private/nowhere");
        var image = Path.Combine(folder, "links.img");
        File.WriteAllBytes(image, builder.Build("Links"));

        Assert.Equal("127.0.0.1\n", Run("cat", image + ":Hosts", "--follow").Output);
        Assert.EndsWith("hosts\n", Run("ls", image + ":etc", "--follow").Output);
        Assert.Equal("127.0.0.1\n", Run("cat", image + ":etc:hosts", "--follow").Output);       // through a link
        var stat = Run("stat", image + ":Hosts").Output;
        Assert.Contains("Symbolic link: /etc/hosts", stat);
        Assert.Contains("Leads to: ", stat);
        Assert.Contains("private:etc:hosts", stat);
        var json = Json(Run("stat", image + ":Gone", "--json").Output).GetProperty("symbolicLink");
        Assert.Equal(("private/nowhere", false), (json.GetProperty("target").GetString(), json.GetProperty("found").GetBoolean()));
        var (code, _, error, _) = Run("cat", image + ":Gone", "--follow");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("a symbolic link whose target is not found (private/nowhere)", error);
    }
}
