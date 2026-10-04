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
}
