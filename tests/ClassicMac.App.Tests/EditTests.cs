using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
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
