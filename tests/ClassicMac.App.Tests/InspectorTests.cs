using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The inspector's header (design/boards/main-window.md, S3): the selection's name, kind and owner, facts and actions;
// forms edit in place in the Preview tab (the Edit tab is gone).
public sealed class InspectorTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-inspector").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static T Child<T>(NodeViewModel node, string title) where T : NodeViewModel =>
        node.Children.OfType<T>().Single(c => c.Title == title);

    // A disk: "Prefs" (TEXT/ttxt) with STR# 128 "Names" and 129 and a 'ZZZZ' 1; an application; a folder with two files.
    private string Disk()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR#"), 128, new byte[] { 0, 1, 2, (byte)'h', (byte)'i' }) { Name = MacString.FromMacRoman("Names"), Attributes = ResourceAttributes.Purgeable | ResourceAttributes.Locked });
        fork.Add(new Resource(FourCC.FromString("STR#"), 129, new byte[] { 0, 0 }));
        fork.Add(new Resource(FourCC.FromString("ZZZZ"), 1, new byte[] { 1, 2, 3 }));
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Prefs", [1, 2, 3, 4], fork.ToArray());
        disk.File(HfsBuilder.Root, "App", [], [], type: "APPL", creator: "ABCD");
        var docs = disk.Folder(HfsBuilder.Root, "Docs");
        disk.File(docs, "One", [1], []);
        disk.File(docs, "Two", [2], []);
        var path = Path.Combine(folder, "inspect.img");
        File.WriteAllBytes(path, disk.Build("Inspect"));
        return path;
    }

    private static Dictionary<string, InspectorFact> Facts(InspectorHeader header) => header.Facts.ToDictionary(f => f.Label);

    [Fact]
    public async Task A_resource_shows_its_kind_owner_and_facts()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var prefs = Child<FileNode>(input, "Prefs");
        await prefs.EnsureLoadedAsync();
        var names = Child<ResourceTypeNode>(prefs, "'STR#' (2)").Children[0];

        model.Selected = names;

        var header = model.Header!;
        Assert.Same(names, header.Node);
        Assert.Equal("128 “Names”", header.Name);
        Assert.Equal("String list in Prefs", header.Kind);
        var facts = Facts(header);
        Assert.Equal(["Type", "ID", "Size", "Attributes"], header.Facts.Select(f => f.Label));
        Assert.Equal(new InspectorFact("Type", "'STR#'", true), facts["Type"]);
        Assert.Equal(new InspectorFact("ID", "128", false), facts["ID"]);
        Assert.Equal("5 bytes", facts["Size"].Value);
        Assert.Equal("Locked, Purgeable", facts["Attributes"].Value);

        var other = Child<ResourceTypeNode>(prefs, "'ZZZZ' (1)").Children[0];
        model.Selected = other;
        Assert.Equal("'ZZZZ' resource in Prefs", model.Header!.Kind);
        Assert.Equal("none", Facts(model.Header)["Attributes"].Value);
    }

    [Fact]
    public async Task A_resource_type_shows_its_count_and_size()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var prefs = Child<FileNode>(input, "Prefs");
        await prefs.EnsureLoadedAsync();

        model.Selected = Child<ResourceTypeNode>(prefs, "'STR#' (2)");

        var header = model.Header!;
        Assert.Equal("'STR#'", header.Name);
        Assert.Equal("String lists in Prefs", header.Kind);
        Assert.Equal(["Type", "Resources", "Size"], header.Facts.Select(f => f.Label));
        Assert.Equal("2", Facts(header)["Resources"].Value);
        Assert.Equal("7 bytes", Facts(header)["Size"].Value);
    }

    [Fact]
    public async Task Files_folders_and_inputs_show_theirs()
    {
        var model = new MainViewModel();
        var path = Disk();
        var input = (await model.OpenAsync(path))!;
        var prefs = Child<FileNode>(input, "Prefs");

        model.Selected = prefs;
        var header = model.Header!;
        Assert.Equal(("Prefs", "SimpleText text document in inspect.img"), (header.Name, header.Kind));   // SimpleText is not on the disk: the table
        Assert.Equal(["Type / creator", "Total size", "Resources"], header.Facts.Select(f => f.Label));
        Assert.Equal(new InspectorFact("Type / creator", "TEXT · ttxt", true), Facts(header)["Type / creator"]);
        Assert.Equal("not read", Facts(header)["Resources"].Value);
        await prefs.EnsureLoadedAsync();
        model.Selected = input;
        model.Selected = prefs;
        Assert.Equal("3", Facts(model.Header!)["Resources"].Value);
        Assert.Equal($"{4 + prefs.File.ResourceFork.Length:N0} bytes", Facts(model.Header!)["Total size"].Value);

        model.Selected = Child<FileNode>(input, "App");
        Assert.Equal("Application program in inspect.img", model.Header!.Kind);
        Assert.Equal("none", Facts(model.Header)["Resources"].Value);

        var docs = Child<FolderNode>(input, "Docs");
        model.Selected = docs;
        Assert.Equal(("Docs", "Folder in inspect.img"), (model.Header!.Name, model.Header.Kind));
        Assert.Equal("2", Facts(model.Header)["Items"].Value);
        model.Selected = Child<FileNode>(docs, "One");
        Assert.Equal("SimpleText text document in Docs", model.Header!.Kind);

        model.Selected = input;
        Assert.Equal(("inspect.img", "HFS volume"), (model.Header!.Name, model.Header.Kind));
        Assert.Equal(["Files", "Size"], model.Header.Facts.Select(f => f.Label));
        Assert.Equal("4", Facts(model.Header)["Files"].Value);
        Assert.Equal(NodeViewModel.FormatSize(new FileInfo(path).Length), Facts(model.Header)["Size"].Value);

        model.Selected = null;
        Assert.Null(model.Header);
    }

    [Fact]
    public async Task The_header_follows_the_selection()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.Selected = Child<FileNode>(input, "App");

        Assert.Contains(nameof(MainViewModel.Header), changed);
        Assert.Equal("App", model.Header!.Name);
    }

    [Fact]
    public async Task Export_in_the_header_does_what_fits_the_selection()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var prefs = Child<FileNode>(input, "Prefs");
        await prefs.EnsureLoadedAsync();
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.Selected = Child<ResourceTypeNode>(prefs, "'STR#' (2)").Children[0];
        Assert.Same(model.SaveResourceAsCommand, model.HeaderExportCommand);
        Assert.Contains(nameof(MainViewModel.HeaderExportCommand), changed);
        model.Selected = prefs;
        Assert.Same(model.ExportResourcesCommand, model.HeaderExportCommand);
        model.Selected = Child<ResourceTypeNode>(prefs, "'STR#' (2)");
        Assert.Same(model.ExportResourcesCommand, model.HeaderExportCommand);
        model.Selected = Child<FolderNode>(input, "Docs");
        Assert.Same(model.ExtractAllCommand, model.HeaderExportCommand);
        model.Selected = input;
        Assert.Same(model.ExtractAllCommand, model.HeaderExportCommand);
    }

    // A plain resource file: STR# 128 has a form.
    private string Forks()
    {
        var path = Path.Combine(folder, "Forms.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("STR#", 128, null, [0, 2, 3, .. "one"u8, 3, .. "two"u8]), ("ZZZZ", 1, null, [1])));
        return path;
    }

    private static async Task<(MainViewModel Model, InputNode Input, ResourceNode Strings)> OpenForms(string path)
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        var strings = Child<ResourceTypeNode>(input, "'STR#' (1)").Children.OfType<ResourceNode>().Single();
        model.Selected = strings;
        await model.PreviewTask;
        return (model, input, strings);
    }

    [Fact]
    public async Task Edit_opens_the_form_in_place_and_Cancel_drops_the_draft()
    {
        var (model, input, strings) = await OpenForms(Forks());
        Assert.False(model.FormEditing.IsEditingForm);
        Assert.True(model.FormEditing.EditFormCommand.CanExecute(null));
        Assert.False(model.FormEditing.CancelFormCommand.CanExecute(null));
        model.SelectedTab = 0;

        model.FormEditing.EditFormCommand.Execute(null);

        Assert.True(model.FormEditing.IsEditingForm);
        Assert.Equal(1, model.SelectedTab);
        Assert.False(model.FormEditing.EditFormCommand.CanExecute(null));
        Assert.True(model.FormEditing.CancelFormCommand.CanExecute(null));
        var form = Assert.IsType<StringListForm>(model.Form);
        form.Strings[0].Text = "uno";
        Assert.True(model.Drafts.HasDraft);

        model.FormEditing.CancelFormCommand.Execute(null);

        Assert.False(model.FormEditing.IsEditingForm);
        Assert.False(model.Drafts.HasDraft);
        Assert.Equal("one", Assert.IsType<StringListForm>(model.Form).Strings[0].Text);
        Assert.False(input.IsUnsaved);
    }

    [Fact]
    public async Task Apply_ends_editing()
    {
        var (model, input, _) = await OpenForms(Forks());
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.IsType<StringListForm>(model.Form).Strings[0].Text = "uno";

        model.ApplyFormCommand.Execute(null);

        Assert.False(model.FormEditing.IsEditingForm);
        Assert.True(input.IsUnsaved);
    }

    [Fact]
    public async Task Another_selection_ends_editing_and_nodes_without_a_form_cannot_edit()
    {
        var (model, input, _) = await OpenForms(Forks());
        model.FormEditing.EditFormCommand.Execute(null);
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>(); // the preview reports from its own thread
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);
        model.FormEditing.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.Selected = Child<ResourceTypeNode>(input, "'ZZZZ' (1)").Children[0];

        Assert.False(model.FormEditing.IsEditingForm);
        Assert.Contains(nameof(FormEditing.IsEditingForm), changed);
        Assert.False(model.FormEditing.EditFormCommand.CanExecute(null));
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.False(model.FormEditing.IsEditingForm);
    }

    // An ICN# 128 (black, full mask) with an icl8 128 (black: colour 255) and an ics# 128 (black left half); an ics# 129 alone; a cicn;
    // an icns with an ICN# member; a CURS; a TEXT; and a file with a custom icon (ICN# −16455).
    private string Icons()
    {
        byte[] icn = [.. Enumerable.Repeat((byte)0xFF, 256)];
        byte[] ics = [.. Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0xFF, 0 }), .. Enumerable.Repeat((byte)0xFF, 32)];
        byte[] icns = [.. "icns"u8, 0, 0, 1, 0x10, .. "ICN#"u8, 0, 0, 1, 8, .. icn];
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Icons", [], PreviewTests.Fork(("ICN#", 128, null, icn), ("icl8", 128, null, [.. Enumerable.Repeat((byte)0xFF, 1024)]), ("ics#", 128, null, ics),
            ("ics#", 129, null, ics), ("icns", 130, null, icns), ("CURS", 128, null, [.. Enumerable.Repeat((byte)0xFF, 64), 0, 0, 0, 0]),
            ("TEXT", 128, null, [1])));
        disk.File(HfsBuilder.Root, "Custom", [], PreviewTests.Fork(("ICN#", -16455, null, icn)),
            info: new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt"), Flags = FinderFlags.HasCustomIcon });
        disk.File(HfsBuilder.Root, "Plain", [1], []);
        var path = Path.Combine(folder, "icons.img");
        File.WriteAllBytes(path, disk.Build("Icons"));
        return path;
    }

    private static ResourceNode Resource(FileNode file, string type, short id) =>
        file.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    [Fact]
    public async Task Icon_families_list_their_members()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Icons()))!;
        var icons = Child<FileNode>(input, "Icons");
        await icons.EnsureLoadedAsync();

        model.Selected = Resource(icons, "ics#", 128);
        Assert.Equal(["Type", "ID", "Members", "Size", "Attributes"], model.Header!.Facts.Select(f => f.Label));
        Assert.Equal(new InspectorFact("Members", "ICN# · icl8 · ics#", true), Facts(model.Header)["Members"]);
        model.Selected = Resource(icons, "ics#", 129);
        Assert.Equal("ics#", Facts(model.Header!)["Members"].Value);
        model.Selected = Resource(icons, "icns", 130);
        Assert.Equal("ICN#", Facts(model.Header!)["Members"].Value);
        model.Selected = Resource(icons, "TEXT", 128);
        Assert.DoesNotContain("Members", Facts(model.Header!).Keys);
        model.Selected = Resource(icons, "CURS", 128);
        Assert.DoesNotContain("Members", Facts(model.Header!).Keys);
    }

    private static (int Width, int Height, uint TopLeft, uint TopRight) Png(byte[] png)
    {
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        static uint At(SkiaSharp.SKBitmap b, int x, int y) => (uint)b.GetPixel(x, y);
        return (bitmap.Width, bitmap.Height, At(bitmap, 1, 1), At(bitmap, bitmap.Width - 2, 1));
    }

    private const uint Black = 0xFF000000;

    [Fact]
    public async Task The_header_icon_is_the_large_icon()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Icons()))!;
        var icons = Child<FileNode>(input, "Icons");
        await icons.EnsureLoadedAsync();

        async Task<(int Width, int Height, uint TopLeft, uint TopRight)?> Select(NodeViewModel node)
        {
            var previous = model.HeaderIconPng;
            model.Selected = node;
            // The previous one goes at once (the new one may already be there: it loads on another thread).
            Assert.True(model.HeaderIconPng is null || !ReferenceEquals(previous, model.HeaderIconPng));
            await model.HeaderIconTask;
            return model.HeaderIconPng is { } png ? Png(png) : null;
        }

        // The family's 32-pixel member (all black), not its small one (left half), whichever member is selected.
        Assert.Equal((32, 32, Black, Black), await Select(Resource(icons, "ics#", 128)));
        Assert.Equal((32, 32, Black, Black), await Select(Resource(icons, "ICN#", 128)));
        // A family without a large member: its small icon at 16.
        var small = await Select(Resource(icons, "ics#", 129));
        Assert.Equal((16, 16, Black), (small!.Value.Width, small.Value.Height, small.Value.TopLeft));
        Assert.NotEqual(Black, small.Value.TopRight);
        Assert.Equal(32, (await Select(Resource(icons, "icns", 130)))!.Value.Width);
        Assert.Equal(16, (await Select(Resource(icons, "CURS", 128)))!.Value.Width);
        Assert.Null(await Select(Resource(icons, "TEXT", 128)));

        // A file's Finder icon at 32; none without one (the kind icon shows); folders and inputs none.
        Assert.Equal((32, 32, Black, Black), await Select(Child<FileNode>(input, "Custom")));
        Assert.Null(await Select(Child<FileNode>(input, "Plain")));
        Assert.Null(await Select(input));
    }

    // A picture's header icon is its thumbnail, fitted into 32 by nearest neighbour; a small one stays 1:1.
    [Theory]
    [InlineData(64, 32, 32, 16)]
    [InlineData(20, 10, 20, 10)]
    public async Task A_picture_s_header_icon_is_its_thumbnail(int width, int height, int iconWidth, int iconHeight)
    {
        var picture = new ClassicMac.Graphics.RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                picture[x, y] = new ClassicMac.Graphics.RgbaColor(0, 0, 0);
            }
        }

        using var pict = new MemoryStream();
        ClassicMac.Graphics.Pict.PictWriter.Write(pict, picture);
        var path = Path.Combine(folder, "Picture.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("PICT", 128, null, pict.ToArray()), ("PICT", 129, null, [0, 1, 2])));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        var pictures = input.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().ToList();

        model.Selected = pictures[0];
        await model.HeaderIconTask;
        Assert.Equal((iconWidth, iconHeight, Black, Black), Png(model.HeaderIconPng!));

        // A picture that does not decode has none (the kind icon shows).
        model.Selected = pictures[1];
        await model.HeaderIconTask;
        Assert.Null(model.HeaderIconPng);
    }

    [Theory]
    [InlineData(64, 32, 32, 16)]
    [InlineData(32, 32, 32, 32)]
    [InlineData(16, 8, 16, 8)]
    public void Pictures_are_fitted_by_nearest_neighbour(int width, int height, int fittedWidth, int fittedHeight)
    {
        using var fitted = SkiaSharp.SKBitmap.Decode(NodeViewModel.Fit(new ClassicMac.Graphics.RgbaBitmap(width, height), 32));
        Assert.Equal((fittedWidth, fittedHeight), (fitted.Width, fitted.Height));
    }
}
