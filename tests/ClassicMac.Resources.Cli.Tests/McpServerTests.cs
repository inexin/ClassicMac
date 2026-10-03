using System.IO.Pipelines;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources.Cli.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClassicMac.Resources.Cli.Tests;

// classicmac mcp (docs/cli.md §4): the server driven in-process by the SDK's client over a pipe.
public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-mcp").FullName;
    private readonly Pipe toServer = new();
    private readonly Pipe toClient = new();
    private readonly CancellationTokenSource stop = new();
    private MacMcpServer tools = null!;
    private McpServer server = null!;
    private Task running = Task.CompletedTask;
    private McpClient client = null!;
    private string disk = "";

    public async ValueTask InitializeAsync()
    {
        disk = WritableDisk.Build(folder);
        tools = new MacMcpServer(ContainerReadOptions.Default, ReadOptions.Default);
        server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), tools.Options());
        running = server.RunAsync(stop.Token);
        client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        await stop.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }

        await server.DisposeAsync();
        tools.Dispose();
        stop.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    // A tool's JSON result; asserts whether it is an error.
    private async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments, bool error = false)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True((result.IsError ?? false) == error, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string> Open(string? path = null) =>
        (await Call("open", new() { ["path"] = path ?? disk })).GetProperty("session").GetString()!;

    private static string[] Names(JsonElement json, string list) =>
        [.. json.GetProperty(list).EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];

    private static IReadOnlyList<MacFile> Files(string image) => HfsReader.Instance.Read(ForkData.FromFile(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(string image) => HfsReader.Instance.ReadFolders(ForkData.FromFile(image), new ContainerContext());

    [Fact]
    public async Task The_tools_are_listed_with_schemas_and_hints()
    {
        var listed = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["open", "close", "list", "stat", "read", "search", "extract", "put", "mkdir", "rm", "rename", "set", "res_add", "res_rm", "save_as"],
            listed.Select(t => t.Name));
        foreach (var tool in listed)
        {
            Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(tool.Description));
        }

        var hints = listed.ToDictionary(t => t.Name, t => t.ProtocolTool.Annotations!);
        Assert.True(hints["list"].ReadOnlyHint);
        Assert.True(hints["read"].ReadOnlyHint);
        Assert.False(hints["mkdir"].ReadOnlyHint);
        Assert.False(hints["mkdir"].DestructiveHint);          // a change stays in the session until saved
        Assert.True(hints["save_as"].DestructiveHint);
        Assert.Equal(["session", "path"], listed.Single(t => t.Name == "read").JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("classicmac", client.ServerInfo.Name);
        Assert.Contains("save_as", client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_gives_a_session_that_list_stat_and_read_use()
    {
        var opened = await Call("open", new() { ["path"] = disk });
        var session = opened.GetProperty("session").GetString()!;
        Assert.Equal((Path.GetFullPath(disk), "", "volume"),
            (opened.GetProperty("input").GetString(), opened.GetProperty("path").GetString(), opened.GetProperty("editable").GetString()));

        var list = await Call("list", new() { ["session"] = session });
        Assert.Equal((Path.GetFullPath(disk), ""), (list.GetProperty("input").GetString(), list.GetProperty("path").GetString()));
        Assert.Equal(["Read Me", "Docs"], Names(list, "entries"));                     // the volume's order
        Assert.Equal(2, list.GetProperty("count").GetInt32());
        Assert.False(list.GetProperty("truncated").GetBoolean());
        Assert.False(list.TryGetProperty("more", out _));

        var stat = await Call("stat", new() { ["session"] = session, ["path"] = "Docs:Letter" });
        Assert.Equal(("Docs:Letter", "file", 4), (stat.GetProperty("path").GetString(), stat.GetProperty("kind").GetString(), stat.GetProperty("dataSize").GetInt32()));
        Assert.True(stat.TryGetProperty("chain", out _));

        var text = await Call("read", new() { ["session"] = session, ["path"] = "Read Me" });
        Assert.Equal(("text", "hello", 5), (text.GetProperty("encoding").GetString(), text.GetProperty("text").GetString(), text.GetProperty("size").GetInt32()));
        var resource = await Call("read", new() { ["session"] = session, ["path"] = "Docs/Letter/#rsrc/'STR '/128" });
        Assert.Equal(("text", "a"), (resource.GetProperty("encoding").GetString(), resource.GetProperty("text").GetString()));     // decoded, as cat does
        var hex = await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter", ["hex"] = true });
        Assert.Equal(("hex", "64617461", 0), (hex.GetProperty("encoding").GetString(), hex.GetProperty("hex").GetString(), hex.GetProperty("offset").GetInt32()));
        var fork = await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter", ["fork"] = "rsrc" });
        Assert.Equal("hex", fork.GetProperty("encoding").GetString());
    }

    [Fact]
    public async Task Long_results_come_in_pages_with_a_more_cursor()
    {
        var session = await Open();
        var first = await Call("list", new() { ["session"] = session, ["limit"] = 1 });
        Assert.Equal(["Read Me"], Names(first, "entries"));
        Assert.True(first.GetProperty("truncated").GetBoolean());
        var second = await Call("list", new() { ["session"] = session, ["limit"] = 1, ["cursor"] = first.GetProperty("more").GetString() });
        Assert.Equal(["Docs"], Names(second, "entries"));
        Assert.False(second.TryGetProperty("more", out _));

        var page = await Call("read", new() { ["session"] = session, ["path"] = "Read Me", ["max_bytes"] = 2 });
        Assert.Equal(("he", "2", 5), (page.GetProperty("text").GetString(), page.GetProperty("more").GetString(), page.GetProperty("length").GetInt32()));
        var rest = await Call("read", new() { ["session"] = session, ["path"] = "Read Me", ["max_bytes"] = 10, ["cursor"] = "2" });
        Assert.Equal(("llo", 2), (rest.GetProperty("text").GetString(), rest.GetProperty("offset").GetInt32()));
        Assert.False(rest.TryGetProperty("more", out _));

        var bytes = await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter", ["hex"] = true, ["max_bytes"] = 3 });
        Assert.Equal(("646174", "3"), (bytes.GetProperty("hex").GetString(), bytes.GetProperty("more").GetString()));

        var bad = await Call("list", new() { ["session"] = session, ["cursor"] = "x" }, error: true);
        Assert.Equal("badArguments", bad.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_and_extract_read_through_the_session()
    {
        var session = await Open();
        var found = await Call("search", new() { ["session"] = session, ["name"] = "L*" });
        Assert.Equal(["Docs:Letter"], Names(found, "matches"));
        Assert.Equal(["Docs:Letter"], Names(await Call("search", new() { ["session"] = session, ["resource_type"] = "STR " }), "matches"));
        Assert.Equal(["Read Me"], Names(await Call("search", new() { ["session"] = session, ["contains"] = "hell" }), "matches"));
        var paged = await Call("search", new() { ["session"] = session, ["kind"] = "file", ["limit"] = 1 });
        Assert.Equal("1", paged.GetProperty("more").GetString());

        var target = Path.Combine(folder, "out");
        var extracted = await Call("extract", new() { ["session"] = session, ["path"] = "Read Me", ["directory"] = target, ["format"] = "raw" });
        var written = extracted.GetProperty("written")[0].GetString()!;
        Assert.Equal("hello", File.ReadAllText(written));
    }

    [Fact]
    public async Task Changes_stay_in_the_session_until_saved_as_a_new_file()
    {
        var session = await Open();
        var before = File.ReadAllBytes(disk);

        var dry = await Call("mkdir", new() { ["session"] = session, ["path"] = "Docs:New", ["dry_run"] = true });
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(("mkdir", "Docs:New"), (dry.GetProperty("changes")[0].GetProperty("action").GetString(), dry.GetProperty("changes")[0].GetProperty("path").GetString()));
        Assert.Equal(0, dry.GetProperty("unsaved").GetInt32());
        Assert.Equal(["Docs:Letter"], Names(await Call("list", new() { ["session"] = session, ["path"] = "Docs" }), "entries"));

        var made = await Call("mkdir", new() { ["session"] = session, ["path"] = "Docs:New" });
        Assert.Equal((false, 1, 0), (made.GetProperty("dryRun").GetBoolean(), made.GetProperty("unsaved").GetInt32(), made.GetProperty("written").GetArrayLength()));
        Assert.Equal(["Docs:Letter", "Docs:New"], Names(await Call("list", new() { ["session"] = session, ["path"] = "Docs" }), "entries").Order());   // reads see it
        await Call("rename", new() { ["session"] = session, ["path"] = "Read Me", ["name"] = "Note" });
        await Call("set", new() { ["session"] = session, ["path"] = "Note", ["type"] = "TEXT", ["creator"] = "ttxt", ["flags"] = "Invisible" });
        Assert.Equal(before, File.ReadAllBytes(disk));

        var refused = await Call("close", new() { ["session"] = session }, error: true);
        Assert.Equal("refused", refused.GetProperty("code").GetString());

        var output = Path.Combine(folder, "saved.img");
        var saved = await Call("save_as", new() { ["session"] = session, ["destination"] = output });
        Assert.Equal([output], saved.GetProperty("written").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["mkdir", "rename", "set"], saved.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("action").GetString()));
        Assert.Equal(0, saved.GetProperty("unsaved").GetInt32());
        Assert.Contains(Folders(output), f => f.MacPath == "Docs:New");
        var note = Files(output).Single(f => f.MacPath == "Note");
        Assert.Equal((FourCC.FromString("TEXT"), FourCC.FromString("ttxt"), FinderFlags.IsInvisible),
            (note.FinderInfo.Type, note.FinderInfo.Creator, note.FinderInfo.Flags & FinderFlags.IsInvisible));
        Assert.Equal(before, File.ReadAllBytes(disk));

        Assert.True((await Call("close", new() { ["session"] = session })).GetProperty("closed").GetBoolean());
        Assert.Empty(tools.Sessions);
    }

    [Fact]
    public async Task Saving_in_place_needs_the_flag_and_keeps_the_original()
    {
        var session = await Open();
        var before = File.ReadAllBytes(disk);
        await Call("rm", new() { ["session"] = session, ["path"] = "Docs", ["recursive"] = true });

        Assert.Equal("badArguments", (await Call("save_as", new() { ["session"] = session }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("save_as", new() { ["session"] = session, ["destination"] = Path.Combine(folder, "x.img"), ["in_place"] = true }, error: true))
            .GetProperty("code").GetString());
        Assert.Equal("refused", (await Call("save_as", new() { ["session"] = session, ["destination"] = disk }, error: true)).GetProperty("code").GetString());   // never over the input

        var saved = await Call("save_as", new() { ["session"] = session, ["in_place"] = true });
        Assert.Equal([Path.GetFullPath(disk)], saved.GetProperty("written").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(Folders(disk), f => f.MacPath == "Docs");
        Assert.Equal(before, File.ReadAllBytes(disk + ".orig"));
        Assert.Equal(["Read Me"], Names(await Call("list", new() { ["session"] = session }), "entries"));
        await Call("close", new() { ["session"] = session });
    }

    [Fact]
    public async Task Resources_and_host_files_are_added_and_removed()
    {
        var session = await Open();
        var added = await Call("res_add", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':129", ["data_hex"] = "02 4869", ["name"] = "Two" });
        Assert.Equal(("res-set", "Docs:Letter"), (added.GetProperty("changes")[0].GetProperty("action").GetString(), added.GetProperty("changes")[0].GetProperty("path").GetString()));
        Assert.Equal("Hi", (await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':129" })).GetProperty("text").GetString());
        Assert.Equal("refused", (await Call("res_add", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':129", ["data_hex"] = "00" }, error: true))
            .GetProperty("code").GetString());                                                                                  // exists, no replace
        await Call("res_add", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':129", ["data_hex"] = "0141", ["replace"] = true });
        await Call("res_rm", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':128" });

        await Call("res_add", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:SIZE:-1", ["data_hex"] = "5800 00100000 00080000" });
        var size = await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:SIZE:-1" });
        Assert.Equal(("json", 0x100000), (size.GetProperty("encoding").GetString(), size.GetProperty("json").GetProperty("preferredSize").GetInt32()));
        var sizeText = await Call("read", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:SIZE:-1", ["max_bytes"] = 10 });   // too long for one page: its text
        Assert.Equal(("json", 10, "10"), (sizeText.GetProperty("encoding").GetString(), sizeText.GetProperty("text").GetString()!.Length, sizeText.GetProperty("more").GetString()));
        Assert.StartsWith("{", sizeText.GetProperty("text").GetString(), StringComparison.Ordinal);

        var source = Path.Combine(folder, "note.txt");
        File.WriteAllText(source, "Hi");
        await Call("put", new() { ["session"] = session, ["source"] = source, ["path"] = "Docs", ["type"] = "TEXT" });
        Assert.Equal("Hi", (await Call("read", new() { ["session"] = session, ["path"] = "Docs:note.txt" })).GetProperty("text").GetString());

        var output = Path.Combine(folder, "res.img");
        await Call("save_as", new() { ["session"] = session, ["destination"] = output });
        var fork = ResourceFork.Read(Files(output).Single(f => f.MacPath == "Docs:Letter").ResourceFork.ToArray());
        Assert.Null(fork.Find(WritableDisk.Str, 128));
        Assert.Equal(new byte[] { 1, (byte)'A' }, fork.Find(WritableDisk.Str, 129)!.GetData().ToArray());
        Assert.Contains(Files(output), f => f.MacPath == "Docs:note.txt");
    }

    [Fact]
    public async Task Errors_say_what_is_wrong_with_a_code()
    {
        Assert.Equal("notFound", (await Call("open", new() { ["path"] = Path.Combine(folder, "nothing.img") }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("list", new() { ["session"] = "99" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("open", new() { }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("open", new() { ["path"] = 3 }, error: true)).GetProperty("code").GetString());
        var session = await Open();
        var missing = await Call("stat", new() { ["session"] = session, ["path"] = "Missing" }, error: true);
        Assert.Equal("notFound", missing.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(missing.GetProperty("error").GetString()));
        Assert.Equal("notFound", (await Call("rm", new() { ["session"] = session, ["path"] = "Missing" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("refused", (await Call("rm", new() { ["session"] = session, ["path"] = "Docs" }, error: true)).GetProperty("code").GetString());   // not empty
        Assert.Equal("refused", (await Call("read", new() { ["session"] = session, ["path"] = "Docs" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("read", new() { ["session"] = session, ["path"] = "Read Me", ["fork"] = "both" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("refused", (await Call("set", new() { ["session"] = session, ["path"] = "Read Me" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("notFound", (await Call("put", new() { ["session"] = session, ["source"] = Path.Combine(folder, "none.txt"), ["path"] = "Docs" }, error: true))
            .GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("res_add", new() { ["session"] = session, ["path"] = "Docs:Letter:#rsrc:'STR ':1" }, error: true)).GetProperty("code").GetString());
        Assert.Equal("badArguments", (await Call("nonsense", new() { }, error: true)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_input_ClassicMac_does_not_write_is_read_but_not_changed()
    {
        var zip = Path.Combine(folder, "a.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Read.txt").Open());
            writer.Write("zipped");
        }

        var opened = await Call("open", new() { ["path"] = zip });
        var session = opened.GetProperty("session").GetString()!;
        Assert.Equal("no", opened.GetProperty("editable").GetString());
        Assert.Equal("zipped", (await Call("read", new() { ["session"] = session, ["path"] = "Read.txt" })).GetProperty("text").GetString());
        Assert.Equal("refused", (await Call("rm", new() { ["session"] = session, ["path"] = "Read.txt" }, error: true)).GetProperty("code").GetString());
        await Call("close", new() { ["session"] = session });
    }

    [Fact]
    public async Task Closing_with_discard_drops_unsaved_changes_and_the_working_copies()
    {
        var session = await Open();
        await Call("mkdir", new() { ["session"] = session, ["path"] = "New" });
        var closed = await Call("close", new() { ["session"] = session, ["discard"] = true });
        Assert.True(closed.GetProperty("closed").GetBoolean());
        Assert.DoesNotContain(Folders(disk), f => f.MacPath == "New");
        Assert.Equal("badArguments", (await Call("list", new() { ["session"] = session }, error: true)).GetProperty("code").GetString());
    }

    [Fact]
    public void The_mcp_command_is_on_the_command_line()
    {
        var output = new StringWriter();
        var code = new CommandLine(output, new StringWriter()).Run(["mcp", "--help"]);
        Assert.Equal(0, code);
        Assert.Contains("MCP", output.ToString(), StringComparison.Ordinal);
    }
}
