using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;
using ClassicMac.Graphics;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// Editing resources in the app: the Resource commands, undo and redo, saving and closing.
public sealed partial class EditTests : IDisposable
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
            Log.Add("save " + fileName);
            return Task.FromResult(Choice);
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

        // The unapplied-draft question: every ask is logged (with the save questions, in order); Pending, when set, is
        // the answer still to come.
        public DraftChoice Draft { get; set; } = DraftChoice.Discard;
        public TaskCompletionSource<DraftChoice>? Pending { get; set; }
        public List<(string What, string? Error)> DraftAsked { get; } = [];
        public List<string> Log { get; } = [];

        public Task<DraftChoice> AskApplyDraftAsync(string what, string? error)
        {
            DraftAsked.Add((what, error));
            Log.Add("draft " + what);
            return Pending?.Task ?? Task.FromResult(Draft);
        }

        public Task<byte[]?> EditHexAsync(string title, byte[] data) => Task.FromResult(Hex);

        public Func<ImportChoice, ImportChoice?> Import { get; set; } = c => c;
        public IReadOnlyList<string> ImportTypes { get; private set; } = [];

        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial)
        {
            ImportTypes = types;
            return Task.FromResult(Import(initial));
        }

        public Func<NewFileChoice, NewFileChoice?> NewFile { get; set; } = c => c;
        public string? FolderName { get; set; } = "New Folder";

        public Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial) => Task.FromResult(NewFile(initial));

        public Task<string?> NewFolderAsync(string initial) => Task.FromResult(FolderName);
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
    public void The_hex_editor_overwrites_inserts_and_deletes()
    {
        var editor = new HexEditor(new byte[] { 0x01, 0x02, 0x03 });
        editor.TypeDigit(0xA);
        Assert.Equal(new byte[] { 0xA1, 2, 3 }, editor.ToArray());          // first digit: high nibble
        editor.TypeDigit(0xB);
        Assert.Equal(new byte[] { 0xAB, 2, 3 }, editor.ToArray());
        Assert.Equal(1, editor.Cursor);
        editor.ToggleInsert();
        editor.TypeDigit(0xC);
        editor.TypeDigit(0xD);
        Assert.Equal(new byte[] { 0xAB, 0xCD, 2, 3 }, editor.ToArray());
        editor.MoveTo(99);                                                    // kept at the end
        Assert.Equal(4, editor.Cursor);
        editor.InsertMode = false;
        editor.TypeDigit(1);
        editor.TypeDigit(2);                                                  // at the end: appends
        Assert.Equal(new byte[] { 0xAB, 0xCD, 2, 3, 0x12 }, editor.ToArray());
        editor.Backspace();
        editor.MoveTo(0);
        editor.Delete();
        Assert.Equal(new byte[] { 0xCD, 2, 3 }, editor.ToArray());
        Assert.True(editor.IsModified);
        Assert.Equal("00000000", editor.Lines[0].Offset);
        Assert.Equal(("", "CD", " 02 03"), (editor.Lines[0].Before, editor.Lines[0].At, editor.Lines[0].After));

        Assert.True(editor.OnKey(Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.None));
        Assert.False(editor.OnKey(Avalonia.Input.Key.S, Avalonia.Input.KeyModifiers.None));
        Assert.False(editor.OnKey(Avalonia.Input.Key.A, Avalonia.Input.KeyModifiers.Control));
        Assert.Equal(2, new HexEditor(new byte[16]).Lines.Count);             // a line to append on
    }

    [Fact]
    public async Task Bytes_edited_in_the_hex_view_are_an_undoable_edit()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 129);
        Assert.True(model.BeginHexEditCommand.CanExecute(null));
        model.BeginHexEditCommand.Execute(null);
        Assert.False(model.BeginHexEditCommand.CanExecute(null));
        Assert.True(model.IsHexEditing);
        Assert.Same(model.HexEdit!.Lines, model.HexLines);

        model.HexEdit.MoveTo(1);
        model.HexEdit.TypeDigit(4);
        model.HexEdit.TypeDigit(1);                                           // 'A'
        model.ApplyHexEditCommand.Execute(null);
        Assert.False(model.IsHexEditing);
        Assert.Equal("Ai"u8.ToArray(), Resource(file, 129).Resource.GetData().ToArray());
        Assert.Equal("_Undo Edit 'STR ' 129", model.UndoTitle);

        // Clicking another resource asks; Apply applies the edit, then selects it.
        dialogs.Draft = DraftChoice.Apply;
        model.Selected = Resource(file, 129);
        model.BeginHexEditCommand.Execute(null);
        model.HexEdit!.Delete();
        model.Selected = Resource(file, 128);
        Assert.False(model.IsHexEditing);
        Assert.Equal(2, Resource(file, 129).Resource.Length);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);

        // Discard leaves the resource alone.
        model.BeginHexEditCommand.Execute(null);
        model.HexEdit!.Delete();
        model.DiscardHexEditCommand.Execute(null);
        Assert.False(model.IsHexEditing);
        Assert.Equal(6, Resource(file, 128).Resource.Length);
        model.UndoCommand.Execute(null);
        model.UndoCommand.Execute(null);
        Assert.Equal(3, Resource(file, 129).Resource.Length);
    }

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

    // T6 (design/boards/browse-tree.md): each resource that differs from the file as saved carries the unsaved mark, not
    // only its file; it clears on Save, or when undo brings the resource back.
    [Fact]
    public async Task Each_edited_resource_carries_the_unsaved_mark()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 129);
        dialogs.Hex = [1, 2, 3];
        await model.EditHexCommand.ExecuteAsync(null);

        Assert.True(file.IsUnsaved);
        Assert.Equal(("129", true), (Resource(file, 129).Name, Resource(file, 129).IsUnsaved));
        Assert.Equal(("128 “greeting”", false), (Resource(file, 128).Name, Resource(file, 128).IsUnsaved));
        Assert.False(file.Children.OfType<ResourceTypeNode>().Single().IsUnsaved);

        // A new resource is unsaved; a rename marks the resource too.
        model.Selected = Resource(file, 128);
        model.DuplicateResourceCommand.Execute(null);
        Assert.True(Resource(file, 130).IsUnsaved);
        model.Selected = Resource(file, 128);
        dialogs.Info = i => i with { Name = "renamed" };
        await model.GetInfoCommand.ExecuteAsync(null);
        Assert.True(Resource(file, 128).IsUnsaved);

        // Undo back to the saved resource clears its mark; the others keep theirs.
        model.UndoCommand.Execute(null);
        Assert.False(Resource(file, 128).IsUnsaved);
        Assert.True(Resource(file, 129).IsUnsaved);

        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(file.IsUnsaved);
        Assert.All(file.Children.OfType<ResourceTypeNode>().Single().Children, r => Assert.False(r.IsUnsaved));

        // After a save, the saved file is what edits compare with: undoing past it marks the resource again.
        model.UndoCommand.Execute(null);
        Assert.Null(file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().FirstOrDefault(r => r.Resource.Id == 130));
        model.UndoCommand.Execute(null);
        Assert.True(Resource(file, 129).IsUnsaved);
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
        for (int i = 0; i < 32 * 16 * 4; i += 4)
        {
            (image.Pixels[i], image.Pixels[i + 3]) = (200, 255);   // red top half
        }

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
    public async Task A_resource_with_a_form_can_be_edited_through_its_template()
    {
        var path = Path.Combine(folder, "Both.rsrc");
        var str = FourCC.FromString("STR ");
        var fork = new ResourceFork();
        fork.Add(new Resource(str, 128, new byte[] { 2, (byte)'h', (byte)'i' }));
        fork.Add(new Resource(FourCC.FromString("TMPL"), 1000, Tmpl(("Text", "PSTR"))) { Name = MacString.FromMacRoman("STR ") });
        File.WriteAllBytes(path, fork.ToArray());

        var model = new MainViewModel { EditDialogs = new Dialogs() };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type == str).Children[0];
        Assert.IsType<StringForm>(model.Form);
        Assert.True(model.HasTemplateChoice);

        model.UseTemplate = true;
        Assert.IsType<TemplateForm>(model.Form);
        model.UseTemplate = false;
        Assert.IsType<StringForm>(model.Form);
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
    public async Task Save_as_hfs_image_preserves_the_volume_and_other_forks()
    {
        var resourceFork = new ResourceFork();
        resourceFork.Add(new Resource(Str, 128, new byte[] { 2, (byte)'h', (byte)'i' }));
        var disk = new HfsBuilder();
        var folderId = disk.Folder(HfsBuilder.Root, "Folder");
        disk.File(folderId, "Prefs", "data fork"u8.ToArray(), resourceFork.ToArray());
        disk.File(HfsBuilder.Root, "Other", "other file"u8.ToArray(), []);
        var sourcePath = Path.Combine(folder, "Volume.hfs");
        var original = disk.Build("Volume");
        File.WriteAllBytes(sourcePath, original);

        var dialogs = new Dialogs { Hex = [3, (byte)'b', (byte)'y', (byte)'e'] };
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        var input = (await model.OpenAsync(sourcePath))!;
        var folderNode = Assert.IsType<FolderNode>(input.Children.Single(n => n.Title == "Folder"));
        var fileNode = Assert.IsType<FileNode>(folderNode.Children.Single(n => n.Title == "Prefs"));
        await fileNode.EnsureLoadedAsync();
        model.Selected = Resource(fileNode, 128);
        await model.EditHexCommand.ExecuteAsync(null);

        await model.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);

        var savedPath = Path.Combine(folder, "Volume-edited.hfs");
        Assert.True(File.Exists(savedPath), model.Status);
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        var savedFiles = HfsReader.Instance.Read(ForkData.FromFile(savedPath), new ContainerContext());
        var savedPrefs = Assert.Single(savedFiles, f => f.MacPath == "Folder:Prefs");
        Assert.Equal("data fork"u8.ToArray(), savedPrefs.DataFork.ToArray());
        Assert.Equal([3, (byte)'b', (byte)'y', (byte)'e'],
            ResourceFork.Read(savedPrefs.ResourceFork.ToArray()).Find(Str, 128)!.GetData().ToArray());
        Assert.Equal("other file"u8.ToArray(), Assert.Single(savedFiles, f => f.MacPath == "Other").DataFork.ToArray());
    }

    // A plain HFS image with Folder:Prefs (data and a resource fork) and Other, with room to grow.
    private async Task<(MainViewModel Model, InputNode Input, Dialogs Dialogs, Picker Picker, string Path, byte[] Original)> OpenVolume()
    {
        var resourceFork = new ResourceFork();
        resourceFork.Add(new Resource(Str, 128, new byte[] { 2, (byte)'h', (byte)'i' }));
        var disk = new HfsBuilder();
        var folderId = disk.Folder(HfsBuilder.Root, "Folder");
        disk.File(folderId, "Prefs", "data fork"u8.ToArray(), resourceFork.ToArray());
        disk.File(HfsBuilder.Root, "Other", "other file"u8.ToArray(), []);
        var image = WithFreeSpace(disk.Build("Volume"));
        var path = Path.Combine(folder, "Volume.hfs");
        File.WriteAllBytes(path, image);
        var dialogs = new Dialogs();
        var picker = new Picker(folder);
        var model = new MainViewModel { FilePicker = picker, EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        return (model, input, dialogs, picker, path, image);
    }

    internal static byte[] WithFreeSpace(byte[] image)
    {
        const int allocationBlocks = 1600;
        int oldBlocks = image[2 * HfsBuilder.Block + 0x12] << 8 | image[2 * HfsBuilder.Block + 0x13];
        int oldFree = image[2 * HfsBuilder.Block + 0x22] << 8 | image[2 * HfsBuilder.Block + 0x23];
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), allocationBlocks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22), checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }

    [Fact]
    public async Task Volume_commands_create_import_and_delete_files_and_folders_until_save_as()
    {
        var (model, input, dialogs, picker, path, original) = await OpenVolume();
        model.Selected = input;
        Assert.True(model.NewFolderCommand.CanExecute(null));
        Assert.False(model.DeleteItemCommand.CanExecute(null));          // the volume itself

        dialogs.FolderName = "Docs";
        await model.NewFolderCommand.ExecuteAsync(null);
        var docs = Assert.IsType<FolderNode>(model.Selected);
        Assert.Equal("Docs", docs.Title);
        Assert.Contains(docs, input.Children);
        Assert.True(model.HasUnsavedChanges);
        Assert.EndsWith("•", input.Title);

        dialogs.NewFile = c => c with { Name = "Notes", Type = "TEXT", Creator = "ttxt" };
        await model.NewFileCommand.ExecuteAsync(null);
        var notes = Assert.IsType<FileNode>(model.Selected);
        Assert.Same(docs, notes.Parent);

        // Import a MacBinary file into the root: its name, type, creator and both forks.
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 200, "\u0003new"u8.ToArray()));
        var host = new MacFile
        {
            Name = MacString.FromMacRoman("Imported"),
            DataFork = ForkData.FromBytes("imported data"u8.ToArray()),
            ResourceFork = ForkData.FromBytes(fork.ToArray()),
            FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("APPL"), Creator = FourCC.FromString("abcd") },
        };
        picker.Open = Path.Combine(folder, "Imported.bin");
        File.WriteAllBytes(picker.Open, MacBinaryWriter.ToArray(host));
        model.Selected = input.Children.Single(n => n.Title == "Other");     // a file: its folder (the root) gets the new one
        NewFileChoice? offered = null;
        dialogs.NewFile = c => offered = c;
        await model.ImportFileCommand.ExecuteAsync(null);
        Assert.Equal(new NewFileChoice("Imported", "APPL", "abcd"), offered);
        var imported = Assert.IsType<FileNode>(model.Selected);
        Assert.Same(input, imported.Parent);

        model.Selected = input.Children.Single(n => n.Title == "Other");
        await model.DeleteItemCommand.ExecuteAsync(null);
        Assert.DoesNotContain(input.Children, n => n.Title == "Other");

        Assert.Equal(original, File.ReadAllBytes(path));                 // nothing written until Save As
        await model.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);
        var saved = HfsReader.Instance.Read(ForkData.FromFile(Path.Combine(folder, "Volume-edited.hfs")), new ContainerContext());
        Assert.Equal(["Docs:Notes", "Folder:Prefs", "Imported"], saved.Select(f => f.MacPath).Order(StringComparer.Ordinal));
        var savedImport = saved.Single(f => f.MacPath == "Imported");
        Assert.Equal("imported data"u8.ToArray(), savedImport.DataFork.ToArray());
        Assert.Equal(fork.ToArray(), savedImport.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("APPL"), savedImport.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("TEXT"), saved.Single(f => f.MacPath == "Docs:Notes").FinderInfo.Type);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Deleting_a_folder_deletes_its_contents_and_fork_edits_save_with_volume_changes()
    {
        var (model, input, dialogs, _, _, _) = await OpenVolume();
        var folderNode = input.Children.Single(n => n.Title == "Folder");

        dialogs.Confirm = false;                                           // asked first: no
        model.Selected = folderNode;
        await model.DeleteItemCommand.ExecuteAsync(null);
        Assert.Contains(folderNode, input.Children);
        Assert.False(model.HasUnsavedChanges);

        dialogs.Confirm = true;
        await model.DeleteItemCommand.ExecuteAsync(null);
        Assert.DoesNotContain(folderNode, input.Children);
        Assert.Same(input, model.Selected);

        // A new file's resources are edited like any other's, and saved into the image with it.
        dialogs.NewFile = c => c with { Name = "Fresh" };
        await model.NewFileCommand.ExecuteAsync(null);
        var fresh = Assert.IsType<FileNode>(model.Selected);
        dialogs.Info = i => i with { Type = "STR ", Id = 300 };
        await model.NewResourceCommand.ExecuteAsync(null);
        model.Selected = input;
        await model.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);
        var saved = HfsReader.Instance.Read(ForkData.FromFile(Path.Combine(folder, "Volume-edited.hfs")), new ContainerContext());
        Assert.Equal(["Fresh", "Other"], saved.Select(f => f.MacPath).Order(StringComparer.Ordinal));
        Assert.NotNull(ResourceFork.Read(saved.Single(f => f.MacPath == "Fresh").ResourceFork.ToArray()).Find(Str, 300));
        _ = fresh;
    }

    [Fact]
    public async Task Volume_commands_refuse_bad_names_and_other_containers()
    {
        var (model, input, dialogs, _, _, _) = await OpenVolume();
        model.Selected = input;
        dialogs.FolderName = "Bad:Name";
        await model.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("Could not", model.Status);
        Assert.False(model.HasUnsavedChanges);

        dialogs.FolderName = "Folder";                                     // already there
        await model.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("Could not", model.Status);
        Assert.False(model.HasUnsavedChanges);

        dialogs.NewFile = c => c with { Type = "TOOLONG" };
        await model.NewFileCommand.ExecuteAsync(null);
        Assert.Contains("four Mac OS Roman", model.Status);

        // A MacBinary file is no volume.
        var (other, file, _, _, _) = await Open();
        other.Selected = file;
        Assert.False(other.NewFileCommand.CanExecute(null));
        Assert.False(other.NewFolderCommand.CanExecute(null));
        Assert.False(other.DeleteItemCommand.CanExecute(null));
        Assert.False(other.SaveAsCommand.CanExecute(SaveAsFormat.HfsImage));
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
