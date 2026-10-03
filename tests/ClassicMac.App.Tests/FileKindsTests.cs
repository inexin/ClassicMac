using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Tests;

namespace ClassicMac.App.Tests;

// Kinds as the Finder names them (finder.md §2.3, §5), from a volume's applications and System, then the built-in
// table: in the inspector's kind line, the Details tab and the tree's tooltip.
public sealed class FileKindsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-kinds").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A 'kind' resource (finder.md §1.4).
    private static byte[] Kind(string signature, short region, params (string Type, string Kind)[] entries)
    {
        var bytes = new List<byte>([.. MacRoman.Encode(signature), (byte)(region >> 8), (byte)region, 0, 0, 0, (byte)entries.Length]);
        foreach (var (type, kind) in entries)
        {
            bytes.AddRange(MacRoman.Encode(type));
            bytes.Add((byte)MacRoman.Encode(kind).Length);
            bytes.AddRange(MacRoman.Encode(kind));
            if (bytes.Count % 2 != 0)
            {
                bytes.Add(0);
            }
        }

        return [.. bytes];
    }

    private static byte[] Fork(params (string Type, short Id, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, data) in resources)
        {
            fork.Add(new Resource(FourCC.FromString(type), id, data));
        }

        return fork.ToArray();
    }

    // Applications: SimpleText (a 'kind' naming TEXT, and its name) and Teach (a bundle, no 'kind'); the System Folder's
    // System Resources with the standard kinds; documents for each, one whose application is not here, and one unknown.
    private async Task<(MainViewModel Model, InputNode Input)> Open(MainViewModel? model = null)
    {
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var apps = HfsBuilder.Root;                                          // one catalog node: few folders
        disk.File(apps, "SimpleText", [], Fork(("kind", 128, Kind("ttxt", 0, ("apnm", "SimpleText"), ("TEXT", "SimpleText text document")))),
            type: "APPL", creator: "ttxt");
        disk.File(apps, "Teach", [], Fork(("BNDL", 128, [.. "TCH "u8, 0, 0, 0, 0, .. "FREF"u8, 0, 0, 0, 0, 0, 128]), ("FREF", 128, [.. "TEXT"u8, 0, 0, 0])),
            type: "APPL", creator: "TCH ");
        var system = HfsBuilder.Root;
        disk.File(system, "System Resources", [], Fork(("kind", -16550, Kind("istd", 0, ("PICT", "PICT document")))),
            type: "zsyr", creator: "MACS");
        var docs = disk.Folder(HfsBuilder.Root, "Docs");
        disk.File(docs, "Read Me", [1], [], type: "TEXT", creator: "ttxt");
        disk.File(docs, "Help", [1], [], type: "TEXT", creator: "hbwr");
        disk.File(docs, "Lesson", [1], [], type: "TEXT", creator: "TCH ");
        disk.File(docs, "Photo", [1], [], type: "PICT", creator: "WXYZ");
        disk.File(docs, "Mystery", [1], [], type: "ZZZZ", creator: "WXYZ");
        var path = Path.Combine(folder, "kinds.img");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, disk.Build("Kinds"));
        }
        model ??= new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        return (model, input);
    }

    private static NodeViewModel Node(InputNode input, params string[] path)
    {
        NodeViewModel at = input;
        foreach (var name in path)
        {
            at = at.Children.First(c => c.Title == name);
        }

        return at;
    }

    [Theory]
    [InlineData("Read Me", "SimpleText text document", "from SimpleText’s 'kind' 128")]
    [InlineData("Lesson", "Teach document", "from Teach, by its file name")]
    [InlineData("Help", "Apple Help page", "built-in")]
    [InlineData("Photo", "PICT document", "from the System’s 'kind' -16550")]
    [InlineData("Mystery", "document", "built-in")]
    public async Task Documents_are_named_from_the_volume_then_the_table(string name, string kind, string source)
    {
        var (model, input) = await Open();
        var file = (FileNode)Node(input, "Docs", name);
        var found = FileKinds.Of(file);
        Assert.Equal(kind, found.Text);
        Assert.Equal(source, FileKinds.Source(found));

        model.Selected = file;
        Assert.Equal($"{char.ToUpperInvariant(kind[0])}{kind[1..]} in Docs", model.Header!.Kind);
        var rows = model.Details.Groups.Single(g => g.Title == "File").Rows;
        Assert.Equal((kind, source), (rows.Single(r => r.Label == "Kind").Value, rows.Single(r => r.Label == "Kind from").Value));
        Assert.Equal($"{kind}\n{source}", file.KindTip!.ToString());
    }

    [Fact]
    public async Task Applications_folders_and_system_files_have_the_Finders_kinds()
    {
        var (model, input) = await Open();
        Assert.Equal("application program", FileKinds.Of((FileNode)Node(input, "SimpleText")).Text);
        Assert.Equal("system file", FileKinds.Of((FileNode)Node(input, "System Resources")).Text);
        model.Selected = Node(input, "Teach");
        Assert.Equal("Application program in kinds.img", model.Header!.Kind);
    }

    [Fact]
    public async Task One_resolver_serves_the_volume_and_the_tooltip_waits_until_shown()
    {
        var (_, input) = await Open();
        var readMe = (FileNode)Node(input, "Docs", "Read Me");
        var tip = readMe.KindTip;
        Assert.False(FileKinds.HasResolver(input));                       // nothing resolved until the tip is shown
        _ = tip!.ToString();
        Assert.True(FileKinds.HasResolver(input));
        Assert.Same(FileKinds.ResolverFor(readMe), FileKinds.ResolverFor((FileNode)Node(input, "Docs", "Help")));
    }

    // ---- A type and creator database the user supplies (View ▸ Type/Creator Database…, finder.md §2.6) ----

    private sealed class Picker(string? path) : IFilePicker
    {
        public string? Title { get; private set; }

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(path is null ? [] : [path]);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
        {
            Title = title;
            return Task.FromResult(path);
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) => Task.FromResult<string?>(null);
    }

    private string Database()
    {
        var path = Path.Combine(folder, "tcdb.xlsx");
        File.WriteAllBytes(path, XlsxBuilder.Xlsx(
        [
            ["File Name", "Type", "Creator", "Comments", "Category"],
            ["WidgetÑwidget file", "ZZZZ", "WXYZ", "Widget", "Widget"],
            ["SimpleTextÑtheir words", "TEXT", "ttxt", "SimpleText", "Text File"],
        ]));
        return path;
    }

    [Fact]
    public async Task A_chosen_database_names_what_the_volume_and_the_table_do_not_and_is_kept()
    {
        var store = new MemorySettingsStore();
        var (model, input) = await Open(new MainViewModel(store));
        var mystery = (FileNode)Node(input, "Docs", "Mystery");
        model.Selected = mystery;
        Assert.Equal("document", model.Details.Groups.Single(g => g.Title == "File").Rows.Single(r => r.Label == "Kind").Value);
        var path = Database();
        var picker = new Picker(path);
        model.FilePicker = picker;

        await model.ChooseTypeCreatorDatabaseCommand.ExecuteAsync(null);
        Assert.Equal("Type/Creator Database", picker.Title);
        Assert.Equal(path, store.Settings.TypeCreatorDatabase);
        Assert.Equal("Type/Creator database: 2 kinds from “tcdb.xlsx”.", model.Status);
        var rows = model.Details.Groups.Single(g => g.Title == "File").Rows;     // the selection's details follow
        Assert.Equal(("Widget widget file", "TCDB (your copy)"), (rows.Single(r => r.Label == "Kind").Value, rows.Single(r => r.Label == "Kind from").Value));
        Assert.Equal("Widget widget file in Docs", model.Header!.Kind);
        Assert.Equal("SimpleText text document", FileKinds.Of((FileNode)Node(input, "Docs", "Read Me")).Text);   // the volume first
        Assert.True(model.HasTypeCreatorDatabase);

        // The next session reads it from the settings.
        var again = new MainViewModel(store);
        await again.TypeCreatorDatabaseLoading;
        var (_, reopened) = await Open(again);
        Assert.Equal("Widget widget file", FileKinds.Of((FileNode)Node(reopened, "Docs", "Mystery")).Text);

        model.ForgetTypeCreatorDatabaseCommand.Execute(null);
        Assert.Null(store.Settings.TypeCreatorDatabase);
        Assert.False(model.HasTypeCreatorDatabase);
        Assert.Equal("document", FileKinds.Of(mystery).Text);
    }

    [Fact]
    public async Task A_file_that_is_no_database_is_reported_and_not_kept()
    {
        var store = new MemorySettingsStore();
        var model = new MainViewModel(store);
        var path = Path.Combine(folder, "notes.xlsx");
        File.WriteAllText(path, "not a spreadsheet");
        model.FilePicker = new Picker(path);
        await model.ChooseTypeCreatorDatabaseCommand.ExecuteAsync(null);
        Assert.StartsWith("“notes.xlsx” could not be read as a type and creator database:", model.Status, StringComparison.Ordinal);
        Assert.Null(store.Settings.TypeCreatorDatabase);
        Assert.False(model.HasTypeCreatorDatabase);

        model.FilePicker = new Picker(null);                                   // cancelled: nothing changes
        await model.ChooseTypeCreatorDatabaseCommand.ExecuteAsync(null);
        Assert.Null(store.Settings.TypeCreatorDatabase);
    }

    [Fact]
    public async Task A_kept_database_that_is_gone_is_reported_and_kept_for_later()
    {
        var gone = Path.Combine(folder, "gone.xlsx");
        var store = new MemorySettingsStore(new AppSettings(TypeCreatorDatabase: gone));
        var model = new MainViewModel(store);
        await model.TypeCreatorDatabaseLoading;
        Assert.False(model.HasTypeCreatorDatabase);
        Assert.StartsWith("“gone.xlsx” could not be read as a type and creator database:", model.Status, StringComparison.Ordinal);
        Assert.Equal(gone, store.Settings.TypeCreatorDatabase);                // a drive not there today
    }
}
