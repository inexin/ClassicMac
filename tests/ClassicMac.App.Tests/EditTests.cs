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
public sealed class EditTests : EditTestsBase
{
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

        Assert.True(ClassicMac.App.Behaviors.HexKeys.Handle(editor, Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.None));
        Assert.False(ClassicMac.App.Behaviors.HexKeys.Handle(editor, Avalonia.Input.Key.S, Avalonia.Input.KeyModifiers.None));
        Assert.False(ClassicMac.App.Behaviors.HexKeys.Handle(editor, Avalonia.Input.Key.A, Avalonia.Input.KeyModifiers.Control));
        Assert.Equal(2, new HexEditor(new byte[16]).Lines.Count);             // a line to append on
    }

    [Fact]
    public async Task Bytes_edited_in_the_hex_view_are_an_undoable_edit()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 129);
        Assert.True(model.EditActions.BeginHexEditCommand.CanExecute(null));
        model.EditActions.BeginHexEditCommand.Execute(null);
        Assert.False(model.EditActions.BeginHexEditCommand.CanExecute(null));
        Assert.True(model.EditActions.IsHexEditing);
        Assert.Same(model.EditActions.HexEdit!.Lines, model.HexLines);

        model.EditActions.HexEdit.MoveTo(1);
        model.EditActions.HexEdit.TypeDigit(4);
        model.EditActions.HexEdit.TypeDigit(1);                                           // 'A'
        model.EditActions.ApplyHexEditCommand.Execute(null);
        Assert.False(model.EditActions.IsHexEditing);
        Assert.Equal("Ai"u8.ToArray(), Resource(file, 129).Resource.GetData().ToArray());
        Assert.Equal("_Undo Edit 'STR ' 129", model.EditActions.UndoTitle);

        // Clicking another resource asks; Apply applies the edit, then selects it.
        dialogs.Draft = DraftChoice.Apply;
        model.Selected = Resource(file, 129);
        model.EditActions.BeginHexEditCommand.Execute(null);
        model.EditActions.HexEdit!.Delete();
        model.Selected = Resource(file, 128);
        Assert.False(model.EditActions.IsHexEditing);
        Assert.Equal(2, Resource(file, 129).Resource.Length);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);

        // Discard leaves the resource alone.
        model.EditActions.BeginHexEditCommand.Execute(null);
        model.EditActions.HexEdit!.Delete();
        model.EditActions.DiscardHexEditCommand.Execute(null);
        Assert.False(model.EditActions.IsHexEditing);
        Assert.Equal(6, Resource(file, 128).Resource.Length);
        model.EditActions.UndoCommand.Execute(null);
        model.EditActions.UndoCommand.Execute(null);
        Assert.Equal(3, Resource(file, 129).Resource.Length);
    }

    [Fact]
    public async Task Edits_undo_redo_and_save_back_into_the_file()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 128);
        Assert.True(model.EditActions.DuplicateResourceCommand.CanExecute(null));
        Assert.False(model.EditActions.SaveCommand.CanExecute(null));

        model.EditActions.DuplicateResourceCommand.Execute(null);
        Assert.Equal(130, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Equal("Prefs •", file.Title);
        Assert.Equal("_Undo Duplicate 'STR ' 128", model.EditActions.UndoTitle);

        dialogs.Info = i => i with { Id = 200, Name = "renamed" };
        await model.EditActions.GetInfoCommand.ExecuteAsync(null);
        Assert.Equal((200, "renamed"), ((int)((ResourceNode)model.Selected!).Resource.Id, ((ResourceNode)model.Selected).Resource.Name!.Value.ToMacRoman()));

        await EditBytes(model, [1, 2, 3]);
        model.EditActions.UndoCommand.Execute(null);
        model.EditActions.UndoCommand.Execute(null);
        model.EditActions.RedoCommand.Execute(null);                       // back to the renamed copy with its old data

        model.Selected = Resource(file, 129);
        model.EditActions.DeleteResourceCommand.Execute(null);

        await model.EditActions.SaveCommand.ExecuteAsync(null);
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
        await EditBytes(model, [1, 2, 3]);

        Assert.True(file.IsUnsaved);
        Assert.Equal(("129", true), (Resource(file, 129).Name, Resource(file, 129).IsUnsaved));
        Assert.Equal(("128 “greeting”", false), (Resource(file, 128).Name, Resource(file, 128).IsUnsaved));
        Assert.False(file.Children.OfType<ResourceTypeNode>().Single().IsUnsaved);

        // A new resource is unsaved; a rename marks the resource too.
        model.Selected = Resource(file, 128);
        model.EditActions.DuplicateResourceCommand.Execute(null);
        Assert.True(Resource(file, 130).IsUnsaved);
        model.Selected = Resource(file, 128);
        dialogs.Info = i => i with { Name = "renamed" };
        await model.EditActions.GetInfoCommand.ExecuteAsync(null);
        Assert.True(Resource(file, 128).IsUnsaved);

        // Undo back to the saved resource clears its mark; the others keep theirs.
        model.EditActions.UndoCommand.Execute(null);
        Assert.False(Resource(file, 128).IsUnsaved);
        Assert.True(Resource(file, 129).IsUnsaved);

        await model.EditActions.SaveCommand.ExecuteAsync(null);
        Assert.False(file.IsUnsaved);
        Assert.All(file.Children.OfType<ResourceTypeNode>().Single().Children, r => Assert.False(r.IsUnsaved));

        // After a save, the saved file is what edits compare with: undoing past it marks the resource again.
        model.EditActions.UndoCommand.Execute(null);
        Assert.Null(file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().FirstOrDefault(r => r.Resource.Id == 130));
        model.EditActions.UndoCommand.Execute(null);
        Assert.True(Resource(file, 129).IsUnsaved);
    }

    [Fact]
    public async Task Clashing_ids_are_refused_and_new_resources_added()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        dialogs.Info = i => i with { Id = 129 };
        await model.EditActions.GetInfoCommand.ExecuteAsync(null);
        Assert.Equal("The file already has a 'STR ' 129.", model.Status);
        Assert.Equal("Prefs", file.Title);

        dialogs.Info = i => i with { Type = "TEXT", Name = "notes" };
        await model.EditActions.NewResourceCommand.ExecuteAsync(null);
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

        model.ImageReader = new FixedImage(image);
        picker.Open = Path.Combine(folder, "art.png");

        // A picture, at the next free ID, named.
        model.Selected = file;
        dialogs.Import = c => c with { Name = "art" };
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        var pict = ((ResourceNode)model.Selected!).Resource;
        Assert.Equal(("PICT", (short)128, "art"), (pict.Type.ToString(), pict.Id, pict.Name?.ToMacRoman()));
        Assert.Contains(ImportActions.IconFamily, dialogs.ImportTypes);

        // An icon family: six resources in one edit; again with the ICN# selected, its data replaced after asking.
        dialogs.Import = c => c with { Type = ImportActions.IconFamily, Id = 200 };
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        var fork = file.Editing!.Session.Fork;
        Assert.All(new[] { "ICN#", "icl4", "icl8", "ics#", "ics4", "ics8" }, t => Assert.NotNull(fork.Find(FourCC.FromString(t), 200)));
        Assert.Equal("_Undo Import art.png", model.EditActions.UndoTitle);
        dialogs.Confirm = false;
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        Assert.Equal("_Undo Import art.png", model.EditActions.UndoTitle);                          // declined: nothing done
        model.EditActions.UndoCommand.Execute(null);
        Assert.Null(fork.Find(FourCC.FromString("icl8"), 200));

        // A WAV file becomes a 'snd '; a file that is not one is refused.
        picker.Open = Path.Combine(folder, "beep.wav");
        File.WriteAllBytes(picker.Open, [.. "RIFF"u8, 36, 0, 0, 0, .. "WAVEfmt "u8, 16, 0, 0, 0, 1, 0, 1, 0, 0x11, 0x2B, 0, 0, 0x11, 0x2B, 0, 0, 1, 0, 8, 0,
            .. "data"u8, 4, 0, 0, 0, 128, 200, 128, 50]);
        dialogs.Import = c => c;
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        Assert.Equal(["snd "], dialogs.ImportTypes);
        var snd = ((ResourceNode)model.Selected!).Resource;
        Assert.Equal("snd ", snd.Type.ToString());
        Assert.Equal(new byte[] { 128, 200, 128, 50 }, snd.GetData()[^4..].ToArray());
        File.WriteAllBytes(picker.Open, [1, 2, 3]);
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        Assert.StartsWith("“beep.wav” could not be imported", model.Status);
    }

    // A mono 8-bit 11025 Hz WAV of the samples.
    private static byte[] Wav(params byte[] samples) =>
        [.. "RIFF"u8, (byte)(36 + samples.Length), 0, 0, 0, .. "WAVEfmt "u8, 16, 0, 0, 0, 1, 0, 1, 0, 0x11, 0x2B, 0, 0, 0x11, 0x2B, 0, 0, 1, 0, 8, 0,
            .. "data"u8, (byte)samples.Length, 0, 0, 0, .. samples];

    // The sound header's actions (boards/sound.md): Save as WAV… and Replace from WAV…, for a 'snd ' only.
    [Fact]
    public async Task A_sound_saves_as_WAV_and_is_replaced_from_one()
    {
        var (model, file, dialogs, picker, _) = await Open();
        model.Selected = file.Children.OfType<ResourceTypeNode>().First().Children[0];   // a 'STR '
        await model.PreviewTask;
        Assert.False(model.SoundHeaderActions.IsSoundResource);
        Assert.False(model.SoundHeaderActions.SaveAsWavCommand.CanExecute(null));
        Assert.False(model.SoundHeaderActions.ReplaceFromWavCommand.CanExecute(null));

        picker.Open = Path.Combine(folder, "beep.wav");
        File.WriteAllBytes(picker.Open, Wav(128, 200, 128, 50));
        model.Selected = file;
        dialogs.Import = c => c;
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        await model.PreviewTask;
        var snd = ((ResourceNode)model.Selected!).Resource;
        Assert.True(model.SoundHeaderActions.IsSoundResource);
        Assert.True(model.SoundHeaderActions.SaveAsWavCommand.CanExecute(null));
        Assert.True(model.SoundHeaderActions.ReplaceFromWavCommand.CanExecute(null));

        // Save as WAV…: the decoded sound, as extract writes it.
        File.Delete(picker.Open);
        await model.SoundHeaderActions.SaveAsWavCommand.ExecuteAsync(null);
        var saved = Assert.Single(Directory.GetFiles(folder, "*.wav"));
        var wav = File.ReadAllBytes(saved);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.StartsWith("Saved ", model.Status);
        File.Delete(saved);

        // Replace from WAV…: the resource's data, as one undoable edit.
        var before = snd.GetData().ToArray();
        picker.Open = Path.Combine(folder, "tone.wav");
        File.WriteAllBytes(picker.Open, Wav(10, 20, 30, 40, 50));
        await model.SoundHeaderActions.ReplaceFromWavCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, snd.GetData()[^5..].ToArray());
        Assert.Equal("_Undo Replace from tone.wav", model.EditActions.UndoTitle);
        Assert.Same(snd, ((ResourceNode)model.Selected!).Resource);
        model.EditActions.UndoCommand.Execute(null);
        Assert.Equal(before, snd.GetData().ToArray());

        model.Selected = file.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "snd ").Children[0];  // undo selects the file
        // A file that is not a WAV is refused.
        File.WriteAllBytes(picker.Open, [1, 2, 3]);
        await model.SoundHeaderActions.ReplaceFromWavCommand.ExecuteAsync(null);
        Assert.StartsWith("“tone.wav” could not be read", model.Status);
        Assert.Equal(before, snd.GetData().ToArray());
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
        Assert.Null(model.Forms.Form);                                                             // no template open yet

        // A template in another open file is used, as ResEdit uses templates in any open file.
        await (await model.OpenAsync(templates))!.EnsureLoadedAsync();
        model.Selected = null;
        model.Selected = Node();
        var form = Assert.IsType<TemplateForm>(model.Forms.Form);
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
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.Equal(new byte[] { 0, 16, 0, 1, 0, 0, 0xAB, 0xCD }, Node().Resource.GetData().ToArray());

        var edited = Assert.IsType<TemplateForm>(model.Forms.Form);
        ((TemplateScalarRow)edited.Fields[0]).Text = "70000";
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.Contains("does not fit", model.Status);
        model.EditActions.UndoCommand.Execute(null);
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
        Assert.IsType<StringForm>(model.Forms.Form);
        Assert.True(model.Forms.HasTemplateChoice);

        model.Forms.UseTemplate = true;
        Assert.IsType<TemplateForm>(model.Forms.Form);
        model.Forms.UseTemplate = false;
        Assert.IsType<StringForm>(model.Forms.Form);
    }

    [Fact]
    public async Task Closing_with_unsaved_edits_asks_and_can_save()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 129);
        model.EditActions.DeleteResourceCommand.Execute(null);
        Assert.True(model.EditActions.HasUnsavedChanges);

        dialogs.Choice = SaveChanges.Cancel;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Single(model.Roots);

        dialogs.Choice = SaveChanges.Save;
        model.Selected = file;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Empty(model.Roots);
        Assert.Equal(["Prefs.bin"], dialogs.Asked.Distinct());                    // the input, and the file in it with the count
        Assert.Equal(["1 resource in Prefs was edited."], dialogs.Edited.Distinct());
        Assert.Null(Saved(path).Find(Str, 129));
    }

    [Theory]
    [InlineData(1, "Prefs", "1 resource in Prefs was edited.")]
    [InlineData(3, "Finder", "3 resources in Finder were edited.")]
    [InlineData(2, null, "2 resources were edited.")]
    [InlineData(0, "Finder", "Resources in Finder were edited.")]
    [InlineData(0, null, "Resources were edited.")]
    public void The_save_question_counts_the_edited_resources(int count, string? file, string expected) =>
        Assert.Equal(expected, EditActions.EditedSummary(count, file));

    [Fact]
    public async Task The_save_question_counts_new_changed_and_deleted_resources()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        model.EditActions.DuplicateResourceCommand.Execute(null);                  // new (130)
        model.Selected = Resource(file, 129);
        model.EditActions.DeleteResourceCommand.Execute(null);                     // deleted
        dialogs.Info = i => i with { Name = "renamed" };
        model.Selected = Resource(file, 128);
        await model.EditActions.GetInfoCommand.ExecuteAsync(null);                 // changed
        Assert.Equal(3, file.Editing!.UnsavedCount);
        dialogs.Choice = SaveChanges.Cancel;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Equal("3 resources in Prefs were edited.", Assert.Single(dialogs.Edited));
    }

    [Fact]
    public async Task Get_info_shows_the_icon_the_kind_and_the_size_and_new_resource_does_not()
    {
        var (model, file, dialogs, _, _) = await Open();
        var node = Resource(file, 128);
        model.Selected = node;
        await model.EditActions.GetInfoCommand.ExecuteAsync(null);
        var subject = Assert.Single(dialogs.Subjects)!;
        Assert.Equal((InspectorHeader.For(node)!.Name, "String in Prefs · 6 bytes"), (subject.Name, subject.Line));
        Assert.Equal(NodeImages.LargeIcon(node), subject.IconPng);

        await model.EditActions.NewResourceCommand.ExecuteAsync(null);
        Assert.Null(dialogs.Subjects[1]);
    }

    [Fact]
    public async Task Import_shows_the_source_and_draws_what_each_choice_makes()
    {
        var (model, file, dialogs, picker, _) = await Open();
        var image = new RgbaBitmap(40, 20);
        Array.Fill(image.Pixels, (byte)255);
        model.ImageReader = new FixedImage(image);
        picker.Open = Path.Combine(folder, "art.png");
        model.Selected = file;
        dialogs.Import = _ => null;
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        var source = dialogs.Source!;
        Assert.Equal("40 × 20 · 24-bit", source.Details);                // opaque
        var pict = Assert.Single(source.Preview("PICT"));
        Assert.Equal((40, 20, "PICT"), (pict.Width, pict.Height, pict.Caption));
        Assert.Equal((32, 32, "ICN#"), Assert.Single(source.Preview("ICN#")) is var icn ? (icn.Width, icn.Height, icn.Caption) : default);
        Assert.Equal(["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8"], source.Preview(ImportActions.IconFamily).Select(i => i.Caption));
        Assert.Empty(source.Preview("snd "));                             // not one an image makes

        image.Pixels[3] = 0;                                              // a transparent pixel
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        Assert.Equal("40 × 20 · 32-bit with alpha", dialogs.Source!.Details);

        picker.Open = Path.Combine(folder, "beep.wav");
        File.WriteAllBytes(picker.Open, [.. "RIFF"u8, 36, 0, 0, 0, .. "WAVEfmt "u8, 16, 0, 0, 0, 1, 0, 1, 0, 0x11, 0x2B, 0, 0, 0x11, 0x2B, 0, 0, 1, 0, 8, 0,
            .. "data"u8, 4, 0, 0, 0, 128, 200, 128, 50]);
        await model.ImportActions.ImportCommand.ExecuteAsync(null);
        Assert.StartsWith("11025 Hz, mono, 8-bit", dialogs.Source!.Details);
        Assert.Empty(dialogs.Source.Preview("snd "));
    }

    [Fact]
    public void An_import_source_with_nothing_drawn()
    {
        Assert.Equal("", ImportSource.None.Details);
        Assert.Empty(ImportSource.None.Preview("PICT"));
    }

    [Fact]
    public async Task Save_as_writes_a_new_container_and_revert_rereads()
    {
        var (model, file, _, _, path) = await Open();
        model.Selected = Resource(file, 129);
        model.EditActions.DeleteResourceCommand.Execute(null);
        await model.EditActions.SaveAsCommand.ExecuteAsync(SaveAsFormat.BinHex);
        var hqx = Path.Combine(folder, "Prefs.hqx");
        var copy = BinHexReader.Instance.Read(ForkData.FromFile(hqx), new ContainerContext())[0];
        Assert.Single(ResourceFork.Read(copy.ResourceFork.ToArray()).Resources);
        Assert.Equal([7], copy.DataFork.ToArray());

        model.Selected = file;
        await model.EditActions.RevertCommand.ExecuteAsync(null);
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

        var dialogs = new Dialogs();
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        var input = (await model.OpenAsync(sourcePath))!;
        var folderNode = Assert.IsType<FolderNode>(input.Children.Single(n => n.Title == "Folder"));
        var fileNode = Assert.IsType<FileNode>(folderNode.Children.Single(n => n.Title == "Prefs"));
        await fileNode.EnsureLoadedAsync();
        model.Selected = Resource(fileNode, 128);
        await EditBytes(model, [3, (byte)'b', (byte)'y', (byte)'e']);

        await model.EditActions.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);

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
        Assert.True(model.VolumeActions.NewFolderCommand.CanExecute(null));
        Assert.False(model.VolumeActions.DeleteItemCommand.CanExecute(null));          // the volume itself

        dialogs.FolderName = "Docs";
        await model.VolumeActions.NewFolderCommand.ExecuteAsync(null);
        var docs = Assert.IsType<FolderNode>(model.Selected);
        Assert.Equal("Docs", docs.Title);
        Assert.Contains(docs, input.Children);
        Assert.True(model.EditActions.HasUnsavedChanges);
        Assert.EndsWith("•", input.Title);

        dialogs.NewFile = c => c with { Name = "Notes", Type = "TEXT", Creator = "ttxt" };
        await model.VolumeActions.NewFileCommand.ExecuteAsync(null);
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
        await model.VolumeActions.ImportFileCommand.ExecuteAsync(null);
        Assert.Equal(new NewFileChoice("Imported", "APPL", "abcd"), offered);
        var imported = Assert.IsType<FileNode>(model.Selected);
        Assert.Same(input, imported.Parent);

        model.Selected = input.Children.Single(n => n.Title == "Other");
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.DoesNotContain(input.Children, n => n.Title == "Other");

        Assert.Equal(original, File.ReadAllBytes(path));                 // nothing written until Save As
        await model.EditActions.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);
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
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.Contains(folderNode, input.Children);
        Assert.False(model.EditActions.HasUnsavedChanges);

        dialogs.Confirm = true;
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.DoesNotContain(folderNode, input.Children);
        Assert.Same(input, model.Selected);

        // A new file's resources are edited like any other's, and saved into the image with it.
        dialogs.NewFile = c => c with { Name = "Fresh" };
        await model.VolumeActions.NewFileCommand.ExecuteAsync(null);
        var fresh = Assert.IsType<FileNode>(model.Selected);
        dialogs.Info = i => i with { Type = "STR ", Id = 300 };
        await model.EditActions.NewResourceCommand.ExecuteAsync(null);
        model.Selected = input;
        await model.EditActions.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);
        var saved = HfsReader.Instance.Read(ForkData.FromFile(Path.Combine(folder, "Volume-edited.hfs")), new ContainerContext());
        Assert.Equal(["Fresh", "Other"], saved.Select(f => f.MacPath).Order(StringComparer.Ordinal));
        Assert.NotNull(ResourceFork.Read(saved.Single(f => f.MacPath == "Fresh").ResourceFork.ToArray()).Find(Str, 300));
        _ = fresh;
    }

    // The Volume menu and Save As ▸ HFS Volume Image on a volume inside a Disk Copy 4.2 image and a partitioned disk: the
    // volume node takes new folders and deletions, and Save As writes the image in its own format.
    [Theory]
    [InlineData("diskcopy")]
    [InlineData("partitioned")]
    public async Task A_volume_inside_a_disk_image_is_edited_and_saved_in_its_format(string kind)
    {
        var volume = HfsWriter.Format(800 * 1024, "Floppy");
        volume = HfsWriter.CreateFile(ForkData.FromBytes(volume), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        var path = Path.Combine(folder, kind == "diskcopy" ? "Floppy.image" : "Disk.img");
        File.WriteAllBytes(path, kind == "diskcopy" ? Fixtures.DiskCopy42("Floppy", volume)
            : Fixtures.PartitionMap(("Driver", "Apple_Driver43", new byte[1024]), ("Floppy", "Apple_HFS", volume)));
        var original = File.ReadAllBytes(path);
        var dialogs = new Dialogs();
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        Assert.True(input.IsWritableHfs);
        var disk = input.Children.OfType<ContainerFileNode>().Single();

        model.Selected = disk;
        Assert.True(model.VolumeActions.NewFolderCommand.CanExecute(null));
        dialogs.FolderName = "Docs";
        await model.VolumeActions.NewFolderCommand.ExecuteAsync(null);
        Assert.IsType<FolderNode>(model.Selected);
        model.Selected = disk.Children.Single(n => n.Title == "Read Me");
        dialogs.Confirm = true;
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.True(model.EditActions.HasUnsavedChanges);
        Assert.Equal(original, File.ReadAllBytes(path));                 // nothing written until Save As

        model.Selected = input;
        await model.EditActions.SaveAsCommand.ExecuteAsync(SaveAsFormat.HfsImage);
        var saved = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-edited" + Path.GetExtension(path));
        var session = ClassicMac.Files.Editing.InputEditSession.Open(saved);
        Assert.Equal(ClassicMac.Files.Editing.InputEditKind.HfsVolume, session.Kind);
        Assert.NotNull(session.Region);                                  // still a Disk Copy image or a partitioned disk
        var folders = HfsReader.Instance.ReadFolders(ForkData.FromBytes(session.Volume), new ContainerContext());
        Assert.Contains(folders, f => f.MacPath == "Docs");
        Assert.Empty(HfsReader.Instance.Read(ForkData.FromBytes(session.Volume), new ContainerContext()));
    }

    [Fact]
    public async Task Volume_commands_refuse_bad_names_and_other_containers()
    {
        var (model, input, dialogs, _, _, _) = await OpenVolume();
        model.Selected = input;
        dialogs.FolderName = "Bad:Name";
        await model.VolumeActions.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("Could not", model.Status);
        Assert.False(model.EditActions.HasUnsavedChanges);

        dialogs.FolderName = "Folder";                                     // already there
        await model.VolumeActions.NewFolderCommand.ExecuteAsync(null);
        Assert.Contains("Could not", model.Status);
        Assert.False(model.EditActions.HasUnsavedChanges);

        dialogs.NewFile = c => c with { Type = "TOOLONG" };
        await model.VolumeActions.NewFileCommand.ExecuteAsync(null);
        Assert.Contains("four Mac OS Roman", model.Status);

        // A MacBinary file is no volume.
        var (other, file, _, _, _) = await Open();
        other.Selected = file;
        Assert.False(other.VolumeActions.NewFileCommand.CanExecute(null));
        Assert.False(other.VolumeActions.NewFolderCommand.CanExecute(null));
        Assert.False(other.VolumeActions.DeleteItemCommand.CanExecute(null));
        Assert.False(other.EditActions.SaveAsCommand.CanExecute(SaveAsFormat.HfsImage));
    }

    [Fact]
    public async Task Forms_apply_as_undoable_edits()
    {
        var (model, file, _, _, path) = await Open();
        model.Selected = Resource(file, 128);
        var form = Assert.IsType<StringForm>(model.Forms.Form);
        Assert.Equal("hello", form.Text);

        form.Text = "hello, world";
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.Equal("hello, world"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
        Assert.Equal("hello, world", Assert.IsType<StringForm>(model.Forms.Form).Text);   // the form reads the new data

        Assert.IsType<StringForm>(model.Forms.Form).Text = "日本";               // not Mac OS Roman: refused
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.Contains("Mac OS Roman", model.Status);

        model.EditActions.UndoCommand.Execute(null);
        model.Selected = Resource(file, 128);
        Assert.Equal("hello", Assert.IsType<StringForm>(model.Forms.Form).Text);
        Assert.False(model.EditActions.HasUnsavedChanges);
        await Task.CompletedTask;
        _ = path;
    }

    // An image reader that returns one image whatever the path.
    private sealed class FixedImage(RgbaBitmap image) : IImageReader
    {
        public RgbaBitmap Read(string path) => image;
    }
}
