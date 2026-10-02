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
        Assert.Equal(("Prefs", "Document in inspect.img"), (header.Name, header.Kind));
        Assert.Equal(["Type / creator", "Total size", "Resources"], header.Facts.Select(f => f.Label));
        Assert.Equal(new InspectorFact("Type / creator", "TEXT · ttxt", true), Facts(header)["Type / creator"]);
        Assert.Equal("not read", Facts(header)["Resources"].Value);
        await prefs.EnsureLoadedAsync();
        model.Selected = input;
        model.Selected = prefs;
        Assert.Equal("3", Facts(model.Header!)["Resources"].Value);
        Assert.Equal($"{4 + prefs.File.ResourceFork.Length:N0} bytes", Facts(model.Header!)["Total size"].Value);

        model.Selected = Child<FileNode>(input, "App");
        Assert.Equal("Application in inspect.img", model.Header!.Kind);
        Assert.Equal("none", Facts(model.Header)["Resources"].Value);

        var docs = Child<FolderNode>(input, "Docs");
        model.Selected = docs;
        Assert.Equal(("Docs", "Folder in inspect.img"), (model.Header!.Name, model.Header.Kind));
        Assert.Equal("2", Facts(model.Header)["Items"].Value);
        model.Selected = Child<FileNode>(docs, "One");
        Assert.Equal("Document in Docs", model.Header!.Kind);

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
        Assert.False(model.IsEditingForm);
        Assert.True(model.EditFormCommand.CanExecute(null));
        Assert.False(model.CancelFormCommand.CanExecute(null));
        model.SelectedTab = 0;

        model.EditFormCommand.Execute(null);

        Assert.True(model.IsEditingForm);
        Assert.Equal(1, model.SelectedTab);
        Assert.False(model.EditFormCommand.CanExecute(null));
        Assert.True(model.CancelFormCommand.CanExecute(null));
        var form = Assert.IsType<StringListForm>(model.Form);
        form.Strings[0].Text = "uno";
        Assert.True(model.HasDraft);

        model.CancelFormCommand.Execute(null);

        Assert.False(model.IsEditingForm);
        Assert.False(model.HasDraft);
        Assert.Equal("one", Assert.IsType<StringListForm>(model.Form).Strings[0].Text);
        Assert.False(input.IsUnsaved);
    }

    [Fact]
    public async Task Apply_ends_editing()
    {
        var (model, input, _) = await OpenForms(Forks());
        model.EditFormCommand.Execute(null);
        Assert.IsType<StringListForm>(model.Form).Strings[0].Text = "uno";

        model.ApplyFormCommand.Execute(null);

        Assert.False(model.IsEditingForm);
        Assert.True(input.IsUnsaved);
    }

    [Fact]
    public async Task Another_selection_ends_editing_and_nodes_without_a_form_cannot_edit()
    {
        var (model, input, _) = await OpenForms(Forks());
        model.EditFormCommand.Execute(null);
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>(); // the preview reports from its own thread
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.Selected = Child<ResourceTypeNode>(input, "'ZZZZ' (1)").Children[0];

        Assert.False(model.IsEditingForm);
        Assert.Contains(nameof(MainViewModel.IsEditingForm), changed);
        Assert.False(model.EditFormCommand.CanExecute(null));
        model.EditFormCommand.Execute(null);
        Assert.False(model.IsEditingForm);
    }
}
