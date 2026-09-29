using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Graphics;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// Editing resources in the app: the Resource commands, undo and redo, saving and closing.
public sealed class EditTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-edit").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class Picker(string folder) : IFilePicker
    {
        public string? Open { get; set; }

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(Open is null ? [] : [Open]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) =>
            Task.FromResult<string?>(Path.Combine(folder, suggestedName));
    }

    private sealed class Dialogs : IEditDialogs
    {
        public Func<ResourceInfo, ResourceInfo?> Info { get; set; } = i => i;
        public SaveChanges Choice { get; set; } = SaveChanges.Discard;
        public bool Confirm { get; set; } = true;
        public byte[]? Hex { get; set; }
        public List<string> Asked { get; } = [];

        public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew) => Task.FromResult(Info(initial));

        public Task<SaveChanges> AskSaveChangesAsync(string fileName)
        {
            Asked.Add(fileName);
            return Task.FromResult(Choice);
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

        public Task<byte[]?> EditHexAsync(string title, byte[] data) => Task.FromResult(Hex);

        public Func<ImportChoice, ImportChoice?> Import { get; set; } = c => c;
        public IReadOnlyList<string> ImportTypes { get; private set; } = [];

        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial)
        {
            ImportTypes = types;
            return Task.FromResult(Import(initial));
        }
    }

    private static readonly FourCC Str = FourCC.FromString("STR ");

    // A MacBinary file holding "Prefs" with STR 128 "hello" and STR 129.
    private string MacBinary()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, "\u0005hello"u8.ToArray()) { Name = MacString.FromMacRoman("greeting") });
        fork.Add(new Resource(Str, 129, "\u0002hi"u8.ToArray()));
        var file = new MacFile { Name = MacString.FromMacRoman("Prefs"), DataFork = ForkData.FromBytes(new byte[] { 7 }), ResourceFork = ForkData.FromBytes(fork.ToArray()) };
        var path = Path.Combine(folder, "Prefs.bin");
        File.WriteAllBytes(path, MacBinaryWriter.ToArray(file));
        return path;
    }

    private async Task<(MainViewModel Model, FileNode File, Dialogs Dialogs, Picker Picker, string Path)> Open()
    {
        var path = MacBinary();
        var dialogs = new Dialogs();
        var picker = new Picker(folder);
        var model = new MainViewModel { FilePicker = picker, EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single();
        await file.EnsureLoadedAsync();
        return (model, file, dialogs, picker, path);
    }

    private static ResourceNode Resource(FileNode file, short id) =>
        file.Children.OfType<ResourceTypeNode>().Single(t => t.Type == Str).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    private static ResourceFork Saved(string path) =>
        ResourceFork.Read(MacBinaryReader.III.Read(ForkData.FromFile(path), new ContainerContext())[0].ResourceFork.ToArray());

    [Fact]
    public async Task Edits_undo_redo_and_save_back_into_the_file()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 128);
        Assert.True(model.DuplicateResourceCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));

        model.DuplicateResourceCommand.Execute(null);
        Assert.Equal(130, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Equal("Prefs •", file.Title);
        Assert.Equal("_Undo Duplicate 'STR ' 128", model.UndoTitle);

        dialogs.Info = i => i with { Id = 200, Name = "renamed" };
        await model.GetInfoCommand.ExecuteAsync(null);
        Assert.Equal((200, "renamed"), ((int)((ResourceNode)model.Selected!).Resource.Id, ((ResourceNode)model.Selected).Resource.Name!.Value.ToMacRoman()));

        dialogs.Hex = [1, 2, 3];
        await model.EditHexCommand.ExecuteAsync(null);
        model.UndoCommand.Execute(null);
        model.UndoCommand.Execute(null);
        model.RedoCommand.Execute(null);                       // back to the renamed copy with its old data

        model.Selected = Resource(file, 129);
        model.DeleteResourceCommand.Execute(null);

        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Prefs", file.Title);
        Assert.True(File.Exists(path + ".orig"));
        var saved = Saved(path);
        Assert.Equal([128, 200], saved.Resources.Select(r => (int)r.Id).Order());
        Assert.Equal("renamed", saved.Find(Str, 200)!.Name!.Value.ToMacRoman());
        Assert.Equal(saved.Find(Str, 128)!.GetData().ToArray(), saved.Find(Str, 200)!.GetData().ToArray());
    }

    [Fact]
    public async Task Clashing_ids_are_refused_and_new_resources_added()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        dialogs.Info = i => i with { Id = 129 };
        await model.GetInfoCommand.ExecuteAsync(null);
        Assert.Equal("The file already has a 'STR ' 129.", model.Status);
        Assert.Equal("Prefs", file.Title);

        dialogs.Info = i => i with { Type = "TEXT", Name = "notes" };
        await model.NewResourceCommand.ExecuteAsync(null);
        var added = (ResourceNode)model.Selected!;
        Assert.Equal(("TEXT", 130, 0), (added.Resource.Type.ToString(), (int)added.Resource.Id, added.Resource.Length));
    }

    [Fact]
    public async Task Images_and_sounds_are_imported_as_undoable_edits()
    {
        var (model, file, dialogs, picker, _) = await Open();
        var image = new RgbaBitmap(32, 32);
        for (int i = 0; i < 32 * 16 * 4; i += 4) (image.Pixels[i], image.Pixels[i + 3]) = (200, 255);   // red top half
        model.LoadImage = _ => image;
        picker.Open = Path.Combine(folder, "art.png");

        // A picture, at the next free ID, named.
        model.Selected = file;
        dialogs.Import = c => c with { Name = "art" };
        await model.ImportCommand.ExecuteAsync(null);
        var pict = ((ResourceNode)model.Selected!).Resource;
        Assert.Equal(("PICT", (short)128, "art"), (pict.Type.ToString(), pict.Id, pict.Name?.ToMacRoman()));
        Assert.Contains(MainViewModel.IconFamily, dialogs.ImportTypes);

        // An icon family: six resources in one edit; again with the ICN# selected, its data replaced after asking.
        dialogs.Import = c => c with { Type = MainViewModel.IconFamily, Id = 200 };
        await model.ImportCommand.ExecuteAsync(null);
        var fork = file.Editing!.Session.Fork;
        Assert.All(new[] { "ICN#", "icl4", "icl8", "ics#", "ics4", "ics8" }, t => Assert.NotNull(fork.Find(FourCC.FromString(t), 200)));
        Assert.Equal("_Undo Import art.png", model.UndoTitle);
        dialogs.Confirm = false;
        await model.ImportCommand.ExecuteAsync(null);
        Assert.Equal("_Undo Import art.png", model.UndoTitle);                          // declined: nothing done
        model.UndoCommand.Execute(null);
        Assert.Null(fork.Find(FourCC.FromString("icl8"), 200));

        // A WAV file becomes a 'snd '; a file that is not one is refused.
        picker.Open = Path.Combine(folder, "beep.wav");
        File.WriteAllBytes(picker.Open, [.. "RIFF"u8, 36, 0, 0, 0, .. "WAVEfmt "u8, 16, 0, 0, 0, 1, 0, 1, 0, 0x11, 0x2B, 0, 0, 0x11, 0x2B, 0, 0, 1, 0, 8, 0,
            .. "data"u8, 4, 0, 0, 0, 128, 200, 128, 50]);
        dialogs.Import = c => c;
        await model.ImportCommand.ExecuteAsync(null);
        Assert.Equal(["snd "], dialogs.ImportTypes);
        var snd = ((ResourceNode)model.Selected!).Resource;
        Assert.Equal("snd ", snd.Type.ToString());
        Assert.Equal(new byte[] { 128, 200, 128, 50 }, snd.GetData()[^4..].ToArray());
        File.WriteAllBytes(picker.Open, [1, 2, 3]);
        await model.ImportCommand.ExecuteAsync(null);
        Assert.StartsWith("“beep.wav” could not be imported", model.Status);
    }

    // A TMPL's data from (label, type) pairs.
    internal static byte[] Tmpl(params (string Label, string Type)[] fields) =>
        [.. fields.SelectMany(f => (byte[])[(byte)f.Label.Length, .. MacRoman.Encode(f.Label), .. MacRoman.Encode(f.Type)])];

    [Fact]
    public async Task Resources_without_a_form_are_edited_through_a_template()
    {
        var data = Path.Combine(folder, "Data.rsrc");
        var templates = Path.Combine(folder, "Templates.rsrc");
        var rsrc = FourCC.FromString("Rsrc");
        var fork = new ResourceFork();
        fork.Add(new Resource(rsrc, 128, new byte[] { 0, 7, 0, 1, 0, 2, 0, 0 }));                          // id, ZCNT 1 (two items), the items
        File.WriteAllBytes(data, fork.ToArray());
        var tmpl = new ResourceFork();
        tmpl.Add(new Resource(FourCC.FromString("TMPL"), 1000, Tmpl(("ID", "DWRD"), ("Count", "ZCNT"), ("*****", "LSTC"), ("Value", "HWRD"), ("*****", "LSTE")))
            { Name = MacString.FromMacRoman("Rsrc") });
        File.WriteAllBytes(templates, tmpl.ToArray());

        var model = new MainViewModel { EditDialogs = new Dialogs() };
        var input = (await model.OpenAsync(data))!;
        await input.EnsureLoadedAsync();
        ResourceNode Node() => (ResourceNode)input.Children.OfType<ResourceTypeNode>().Single(t => t.Type == rsrc).Children[0];
        model.Selected = Node();
        Assert.Null(model.Form);                                                             // no template open yet

        // A template in another open file is used, as ResEdit uses templates in any open file.
        await (await model.OpenAsync(templates))!.EnsureLoadedAsync();
        model.Selected = null;
        model.Selected = Node();
        var form = Assert.IsType<TemplateForm>(model.Form);
        Assert.Contains("TMPL 1000", form.Source);
        var id = Assert.IsType<TemplateScalarRow>(form.Fields[0]);
        var count = Assert.IsType<TemplateScalarRow>(form.Fields[1]);
        var list = Assert.IsType<TemplateListRow>(form.Fields[2]);
        Assert.Equal(("7", "1", 2), (id.Text, count.Text, list.Items.Count));
        Assert.Equal("$0002", ((TemplateScalarRow)list.Items[0].Fields[0]).Text);

        id.Text = "$10";
        list.AddCommand.Execute(null);
        Assert.Equal("2", count.Text);                                                       // kept in step
        ((TemplateScalarRow)list.Items[2].Fields[0]).Text = "$ABCD";
        list.RemoveCommand.Execute(list.Items[0]);
        model.ApplyFormCommand.Execute(null);
        Assert.Equal(new byte[] { 0, 16, 0, 1, 0, 0, 0xAB, 0xCD }, Node().Resource.GetData().ToArray());

        var edited = Assert.IsType<TemplateForm>(model.Form);
        ((TemplateScalarRow)edited.Fields[0]).Text = "70000";
        model.ApplyFormCommand.Execute(null);
        Assert.Contains("does not fit", model.Status);
        model.UndoCommand.Execute(null);
        Assert.Equal(8, Node().Resource.Length);
    }

    [Fact]
    public async Task Closing_with_unsaved_edits_asks_and_can_save()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 129);
        model.DeleteResourceCommand.Execute(null);
        Assert.True(model.HasUnsavedChanges);

        dialogs.Choice = SaveChanges.Cancel;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Single(model.Roots);

        dialogs.Choice = SaveChanges.Save;
        model.Selected = file;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Empty(model.Roots);
        Assert.Equal(["Prefs"], dialogs.Asked.Distinct());
        Assert.Null(Saved(path).Find(Str, 129));
    }

    [Fact]
    public async Task Save_as_writes_a_new_container_and_revert_rereads()
    {
        var (model, file, _, _, path) = await Open();
        model.Selected = Resource(file, 129);
        model.DeleteResourceCommand.Execute(null);
        await model.SaveAsCommand.ExecuteAsync(SaveAsFormat.BinHex);
        var hqx = Path.Combine(folder, "Prefs.hqx");
        var copy = BinHexReader.Instance.Read(ForkData.FromFile(hqx), new ContainerContext())[0];
        Assert.Single(ResourceFork.Read(copy.ResourceFork.ToArray()).Resources);
        Assert.Equal([7], copy.DataFork.ToArray());

        model.Selected = file;
        await model.RevertCommand.ExecuteAsync(null);
        var reopened = model.Roots.Single().Children.OfType<FileNode>().Single();
        await reopened.EnsureLoadedAsync();
        Assert.Equal(2, reopened.Resources!.Fork!.Resources.Count);
        Assert.False(File.Exists(path + ".orig"));
    }

    [Fact]
    public async Task Forms_apply_as_undoable_edits()
    {
        var (model, file, _, _, path) = await Open();
        model.Selected = Resource(file, 128);
        var form = Assert.IsType<StringForm>(model.Form);
        Assert.Equal("hello", form.Text);

        form.Text = "hello, world";
        model.ApplyFormCommand.Execute(null);
        Assert.Equal("hello, world"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
        Assert.Equal("hello, world", Assert.IsType<StringForm>(model.Form).Text);   // the form reads the new data

        Assert.IsType<StringForm>(model.Form).Text = "日本";               // not Mac OS Roman: refused
        model.ApplyFormCommand.Execute(null);
        Assert.Contains("Mac OS Roman", model.Status);

        model.UndoCommand.Execute(null);
        model.Selected = Resource(file, 128);
        Assert.Equal("hello", Assert.IsType<StringForm>(model.Form).Text);
        Assert.False(model.HasUnsavedChanges);
        await Task.CompletedTask;
        _ = path;
    }
}
