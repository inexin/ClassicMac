using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// Unapplied edits (a form's values or the hex view's bytes): moving the selection, undoing, closing or quitting asks to
// apply, discard or cancel first.
public sealed partial class EditTests
{
    [Fact]
    public async Task A_form_draft_keeps_the_selection_until_answered_and_cancel_stays()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        await model.PreviewTask;                                            // its preview done, so only the refusal could change it
        var form = Assert.IsType<StringForm>(model.Form);
        var details = model.Details;
        Assert.False(model.HasDraft);
        form.Text = "edited";
        Assert.True(model.HasDraft);

        dialogs.Pending = new TaskCompletionSource<DraftChoice>();
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        model.Selected = Resource(file, 129);
        // Not answered yet: nothing moved or rebuilt.
        Assert.Equal([("'STR ' 128", (string?)null)], dialogs.DraftAsked);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Same(form, model.Form);
        Assert.Same(details, model.Details);
        Assert.DoesNotContain(nameof(MainViewModel.Form), changes);
        Assert.DoesNotContain(nameof(MainViewModel.Preview), changes);
        Assert.Contains(nameof(MainViewModel.Selected), changes);           // the tree is told to show the old node again
        model.Selected = Resource(file, 129);                               // asked once
        Assert.Single(dialogs.DraftAsked);

        dialogs.Pending.SetResult(DraftChoice.Cancel);
        await model.DraftTask;
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Same(form, model.Form);
        Assert.Equal("edited", form.Text);
        Assert.True(model.HasDraft);
        Assert.Equal("hello"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
    }

    [Fact]
    public async Task Discard_drops_the_draft_then_moves()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Discard;
        model.Selected = Resource(file, 129);
        await model.DraftTask;
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Equal("hi", Assert.IsType<StringForm>(model.Form).Text);
        Assert.False(model.HasDraft);
        Assert.Equal("hello"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
        Assert.False(model.HasUnsavedChanges);
    }

    [Fact]
    public async Task Apply_makes_one_undoable_edit_then_moves()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Apply;
        var target = Resource(file, 129);
        model.Selected = target;
        await model.DraftTask;
        Assert.Equal("edited"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
        // The type's nodes were rebuilt by the edit: the clicked resource is selected in the new ones.
        Assert.Same(Resource(file, 129), model.Selected);
        Assert.Equal("hi", Assert.IsType<StringForm>(model.Form).Text);
        Assert.Equal("_Undo Edit 'STR ' 128 \"greeting\"", model.UndoTitle);
        Assert.True(model.HasUnsavedChanges);

        // Applied but unsaved: moving on does not ask.
        model.Selected = Resource(file, 128);
        Assert.Single(dialogs.DraftAsked);
        Assert.Equal("edited", Assert.IsType<StringForm>(model.Form).Text);
    }

    [Fact]
    public async Task Values_changed_back_or_untouched_are_no_draft()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        var form = Assert.IsType<StringForm>(model.Form);
        form.Text = "edited";
        form.Text = "hello";
        Assert.False(model.HasDraft);
        model.Selected = Resource(file, 129);
        Assert.Empty(dialogs.DraftAsked);
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);

        // The Apply button applies the draft: the form is clean again.
        Assert.IsType<StringForm>(model.Form).Text = "ho";
        model.ApplyFormCommand.Execute(null);
        Assert.False(model.HasDraft);
        model.Selected = file;
        Assert.Empty(dialogs.DraftAsked);
    }

    [Fact]
    public async Task A_draft_with_an_error_names_it_and_cannot_be_applied()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "日本";               // not Mac OS Roman
        dialogs.Draft = DraftChoice.Apply;                                  // the dialog offers no Apply: treated as Cancel
        model.Selected = Resource(file, 129);
        await model.DraftTask;
        var (what, error) = Assert.Single(dialogs.DraftAsked);
        Assert.Equal("'STR ' 128", what);
        Assert.Contains("Mac OS Roman", error);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.False(model.HasUnsavedChanges);

        dialogs.Draft = DraftChoice.Discard;
        model.Selected = Resource(file, 129);
        await model.DraftTask;
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
    }

    [Fact]
    public async Task Interface_forms_are_drafts_while_their_data_differs()
    {
        var path = Path.Combine(folder, "Wind.rsrc");
        var wind = FourCC.FromString("WIND");
        var fork = new ResourceFork();
        // Bounds 40,40,200,300, document window, visible, close box, refCon 0, title "Untitled".
        fork.Add(new Resource(wind, 128, (byte[])[0, 40, 0, 40, 0, 200, 1, 44, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 8, .. "Untitled"u8]));
        fork.Add(new Resource(wind, 129, (byte[])[0, 40, 0, 40, 0, 200, 1, 44, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, (byte)'X']));
        File.WriteAllBytes(path, fork.ToArray());
        var dialogs = new Dialogs { Draft = DraftChoice.Cancel };
        var model = new MainViewModel { EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        NodeViewModel Node(int i) => input.Children.OfType<ResourceTypeNode>().Single().Children[i];
        model.Selected = Node(0);
        var form = Assert.IsType<WindowForm>(model.Form);
        form.Title = "Other";
        Assert.True(model.HasDraft);
        form.Title = "日本";                                                 // not Mac OS Roman: the form's error is the draft's
        Assert.NotNull(model.FormError);
        model.Selected = Node(1);
        Assert.Equal(model.FormError, Assert.Single(dialogs.DraftAsked).Error);
        form.Title = "Untitled";
        Assert.False(model.HasDraft);
        model.Selected = Node(1);
        Assert.Same(Node(1), model.Selected);
    }

    [Fact]
    public async Task Hex_edits_ask_before_the_selection_moves()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 129);
        model.BeginHexEditCommand.Execute(null);
        model.HexEdit!.Delete();
        Assert.True(model.HasDraft);

        dialogs.Draft = DraftChoice.Cancel;
        model.Selected = Resource(file, 128);
        await model.DraftTask;
        Assert.Equal(("'STR ' 129", (string?)null), Assert.Single(dialogs.DraftAsked));
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.True(model.IsHexEditing);

        dialogs.Draft = DraftChoice.Discard;
        model.Selected = Resource(file, 128);
        await model.DraftTask;
        Assert.False(model.IsHexEditing);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Equal(3, Resource(file, 129).Resource.Length);
        Assert.False(model.HasUnsavedChanges);

        // Hex editing with no bytes changed is no draft.
        model.BeginHexEditCommand.Execute(null);
        model.Selected = Resource(file, 129);
        Assert.Equal(2, dialogs.DraftAsked.Count);
        Assert.False(model.IsHexEditing);
    }

    [Fact]
    public async Task Selecting_a_diagnostic_asks_first()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Cancel;
        model.SelectedDiagnostic = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "a warning"), "Prefs", Resource(file, 129));
        await model.DraftTask;
        Assert.Single(dialogs.DraftAsked);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);

        dialogs.Draft = DraftChoice.Discard;
        model.SelectedDiagnostic = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "another warning"), "Prefs", Resource(file, 129));
        await model.DraftTask;
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
    }

    // "Show item" on a diagnostic's row (design/boards/diagnostics.md): selects its node through the same question,
    // opens its ancestors and asks the view to show it; nothing is shown when the user cancels.
    [Fact]
    public async Task Show_item_selects_the_diagnostic_s_node_and_shows_it()
    {
        var (model, file, dialogs, _, _) = await Open();
        var shown = new List<NodeViewModel>();
        model.ItemShown += shown.Add;
        var target = Resource(file, 129);
        var entry = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "a warning"), "Prefs", target);
        Assert.False(model.ShowItemCommand.CanExecute(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "input.unreadable", "gone"), "x", null)));
        Assert.True(model.ShowItemCommand.CanExecute(entry));
        Assert.True(entry.HasNode);

        model.Selected = Resource(file, 128);
        target.Parent!.IsExpanded = false;
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Cancel;
        await model.ShowItemCommand.ExecuteAsync(entry);
        Assert.Single(dialogs.DraftAsked);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Empty(shown);

        dialogs.Draft = DraftChoice.Discard;
        await model.ShowItemCommand.ExecuteAsync(entry);
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.True(model.Selected!.Parent!.IsExpanded);
        Assert.Same(model.Selected, Assert.Single(shown));

        // With no draft it shows at once, even the node already selected.
        await model.ShowItemCommand.ExecuteAsync(entry);
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public async Task Opening_a_file_asks_before_selecting_it()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Cancel;
        var other = Path.Combine(folder, "Other.rsrc");
        File.WriteAllBytes(other, new ResourceFork().ToArray());
        await model.OpenAsync(other);
        await model.DraftTask;
        Assert.Equal(2, model.Roots.Count);                                 // opened, but the draft stays selected
        Assert.Single(dialogs.DraftAsked);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.True(model.HasDraft);
    }

    [Fact]
    public async Task Undo_and_resource_commands_ask_before_acting()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 129);
        model.DuplicateResourceCommand.Execute(null);                       // 'STR ' 130
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";

        dialogs.Draft = DraftChoice.Cancel;
        await model.UndoCommand.ExecuteAsync(null);
        await model.DeleteResourceCommand.ExecuteAsync(null);
        await model.DuplicateResourceCommand.ExecuteAsync(null);
        Assert.Equal(3, dialogs.DraftAsked.Count);
        Assert.NotNull(file.Resources!.Fork!.Find(Str, 130));               // nothing undone, deleted or duplicated
        Assert.NotNull(file.Resources.Fork.Find(Str, 128));
        Assert.Equal(3, file.Resources.Fork.Resources.Count());
        Assert.True(model.HasDraft);

        dialogs.Draft = DraftChoice.Apply;
        await model.UndoCommand.ExecuteAsync(null);                         // the draft applied, then undone
        Assert.Equal("hello"u8.ToArray(), file.Resources.Fork.Find(Str, 128)!.GetData().ToArray());
        Assert.NotNull(file.Resources.Fork.Find(Str, 130));
        Assert.Equal("_Redo Edit 'STR ' 128 \"greeting\"", model.RedoTitle);

        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        dialogs.Draft = DraftChoice.Discard;
        await model.DeleteResourceCommand.ExecuteAsync(null);
        Assert.Null(file.Resources.Fork.Find(Str, 128));
        Assert.Equal("_Undo Delete 'STR ' 128", model.UndoTitle);
    }

    [Fact]
    public async Task Closing_asks_about_the_draft_then_to_save()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";

        dialogs.Draft = DraftChoice.Cancel;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Single(model.Roots);
        Assert.Equal(["draft 'STR ' 128"], dialogs.Log);                   // cancelled: no save question

        dialogs.Draft = DraftChoice.Apply;
        dialogs.Choice = SaveChanges.Cancel;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.Equal(["draft 'STR ' 128", "draft 'STR ' 128", "save Prefs"], dialogs.Log);
        Assert.Single(model.Roots);
        Assert.Equal("edited"u8.ToArray(), file.Resources!.Fork!.Find(Str, 128)!.GetData().ToArray());
        Assert.False(model.HasDraft);
    }

    [Fact]
    public async Task Quitting_asks_about_the_draft_first()
    {
        var (model, file, dialogs, _, _) = await Open();
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        Assert.False(model.HasUnsavedChanges);
        Assert.True(model.HasDraft);

        dialogs.Draft = DraftChoice.Cancel;
        Assert.False(await model.ConfirmQuitAsync());
        dialogs.Draft = DraftChoice.Discard;
        Assert.True(await model.ConfirmQuitAsync());
        Assert.Equal(["draft 'STR ' 128", "draft 'STR ' 128"], dialogs.Log);  // discarded: nothing to save
    }

    [Fact]
    public async Task Without_dialogs_a_draft_is_discarded()
    {
        var (model, file, _, _, _) = await Open();
        model.EditDialogs = null;
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        model.Selected = Resource(file, 129);
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.Equal("hello"u8.ToArray(), Resource(file, 128).Resource.GetData().ToArray());
    }

    [Fact]
    public async Task Switching_to_the_template_asks_about_the_draft()
    {
        var path = Path.Combine(folder, "Both.rsrc");
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 2, (byte)'h', (byte)'i' }));
        fork.Add(new Resource(FourCC.FromString("TMPL"), 1000, Tmpl(("Text", "PSTR"))) { Name = MacString.FromMacRoman("STR ") });
        File.WriteAllBytes(path, fork.ToArray());
        var dialogs = new Dialogs();
        var model = new MainViewModel { EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        ResourceNode Node() => (ResourceNode)input.Children.OfType<ResourceTypeNode>().Single(t => t.Type == Str).Children[0];
        model.Selected = Node();
        var form = Assert.IsType<StringForm>(model.Form);
        form.Text = "edited";

        dialogs.Pending = new TaskCompletionSource<DraftChoice>();
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        model.UseTemplate = true;
        Assert.False(model.UseTemplate);                                    // not until answered
        Assert.Same(form, model.Form);
        Assert.Contains(nameof(MainViewModel.UseTemplate), changes);        // the check box is told to show it unchecked
        dialogs.Pending.SetResult(DraftChoice.Cancel);
        await model.DraftTask;
        Assert.False(model.UseTemplate);
        Assert.Same(form, model.Form);
        Assert.Equal("edited", form.Text);
        dialogs.Pending = null;

        dialogs.Draft = DraftChoice.Discard;
        model.UseTemplate = true;
        await model.DraftTask;
        Assert.True(model.UseTemplate);
        Assert.Equal("hi", Assert.IsType<TemplateScalarRow>(Assert.IsType<TemplateForm>(model.Form).Fields[0]).Text);
        Assert.Equal(2, dialogs.DraftAsked.Count);

        ((TemplateScalarRow)((TemplateForm)model.Form!).Fields[0]).Text = "yo";
        dialogs.Draft = DraftChoice.Apply;
        model.UseTemplate = false;
        await model.DraftTask;
        Assert.False(model.UseTemplate);
        Assert.Equal("yo", Assert.IsType<StringForm>(model.Form).Text);
        Assert.Equal("yo"u8.ToArray(), Node().Resource.GetData().ToArray());

        model.UseTemplate = true;                                           // no draft: no question
        Assert.Equal(3, dialogs.DraftAsked.Count);
        Assert.True(model.UseTemplate);
    }

    [Fact]
    public async Task Saving_asks_about_the_draft_and_applies_it_first()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 128);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.IsType<StringForm>(model.Form).Text = "edited";
        Assert.True(model.SaveCommand.CanExecute(null));                    // a draft alone can be saved

        dialogs.Draft = DraftChoice.Cancel;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(path + ".orig"));                          // not saved
        Assert.True(model.HasDraft);

        dialogs.Draft = DraftChoice.Apply;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal("edited"u8.ToArray(), Saved(path).Find(Str, 128)!.GetData().ToArray());
        Assert.False(model.HasDraft);
        Assert.False(model.HasUnsavedChanges);
        Assert.Equal(2, dialogs.DraftAsked.Count);
    }

    [Fact]
    public async Task Saving_with_the_draft_discarded_saves_the_applied_edits_only()
    {
        var (model, file, dialogs, _, path) = await Open();
        model.Selected = Resource(file, 129);
        await model.DeleteResourceCommand.ExecuteAsync(null);
        model.Selected = Resource(file, 128);
        Assert.IsType<StringForm>(model.Form).Text = "edited";

        dialogs.Draft = DraftChoice.Discard;
        await model.SaveCommand.ExecuteAsync(null);
        var saved = Saved(path);
        Assert.Null(saved.Find(Str, 129));
        Assert.Equal("hello"u8.ToArray(), saved.Find(Str, 128)!.GetData().ToArray());
        Assert.Equal("hello", Assert.IsType<StringForm>(model.Form).Text);
        Assert.False(model.HasDraft);
    }

    // Save follows edits made deep in a form or in the hex view: each one re-evaluates it, and it can then save.
    private static Func<int> Watch(System.Windows.Input.ICommand command)
    {
        var count = 0;
        command.CanExecuteChanged += (_, _) => count++;
        return () => count;
    }

    [Fact]
    public async Task Save_follows_edits_to_string_list_items()
    {
        var path = Path.Combine(folder, "List.rsrc");
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR#"), 128, new byte[] { 0, 1, 1, (byte)'a' }));
        File.WriteAllBytes(path, fork.ToArray());
        var model = new MainViewModel { EditDialogs = new Dialogs() };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single().Children[0];
        var form = Assert.IsType<StringListForm>(model.Form);
        var edited = 0;
        form.Edited += (_, _) => edited++;
        var changes = Watch(model.SaveCommand);
        Assert.False(model.SaveCommand.CanExecute(null));

        form.Strings[0].Text = "b";                                         // an item's text
        Assert.Equal((1, 1), (edited, changes()));
        Assert.True(model.SaveCommand.CanExecute(null));
        form.Strings[0].Text = "a";
        Assert.False(model.SaveCommand.CanExecute(null));

        form.AddCommand.Execute(null);                                      // a new item, then its text
        Assert.True(model.SaveCommand.CanExecute(null));
        var before = changes();
        form.Strings[1].Text = "c";
        Assert.True(changes() > before);
        form.RemoveCommand.Execute(form.Strings[1]);
        Assert.False(model.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_follows_edits_to_template_rows()
    {
        var path = Path.Combine(folder, "Data.rsrc");
        var rsrc = FourCC.FromString("Rsrc");
        var fork = new ResourceFork();
        fork.Add(new Resource(rsrc, 128, new byte[] { 0, 7, 0, 0, 0, 2 }));     // id, ZCNT 0 (one item), the item
        fork.Add(new Resource(FourCC.FromString("TMPL"), 1000, Tmpl(("ID", "DWRD"), ("Count", "ZCNT"), ("*****", "LSTC"), ("Value", "HWRD"), ("*****", "LSTE")))
            { Name = MacString.FromMacRoman("Rsrc") });
        File.WriteAllBytes(path, fork.ToArray());
        var model = new MainViewModel { EditDialogs = new Dialogs() };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type == rsrc).Children[0];
        var form = Assert.IsType<TemplateForm>(model.Form);
        var changes = Watch(model.SaveCommand);
        var list = (TemplateListRow)form.Fields[2];

        ((TemplateScalarRow)list.Items[0].Fields[0]).Text = "$0003";         // a field inside a list item
        Assert.True(changes() > 0);
        Assert.True(model.SaveCommand.CanExecute(null));
        ((TemplateScalarRow)list.Items[0].Fields[0]).Text = "$0002";
        Assert.False(model.SaveCommand.CanExecute(null));

        var before = changes();
        list.AddCommand.Execute(null);                                      // a new item, then its field
        Assert.True(changes() > before);
        Assert.True(model.SaveCommand.CanExecute(null));
        before = changes();
        ((TemplateScalarRow)list.Items[1].Fields[0]).Text = "$0009";
        Assert.True(changes() > before);
    }

    [Fact]
    public async Task Save_follows_edits_in_the_hex_view()
    {
        var (model, file, _, _, _) = await Open();
        model.Selected = Resource(file, 129);
        model.BeginHexEditCommand.Execute(null);
        var changes = Watch(model.SaveCommand);
        Assert.False(model.SaveCommand.CanExecute(null));
        model.HexEdit!.TypeDigit(4);
        Assert.True(changes() > 0);
        Assert.True(model.SaveCommand.CanExecute(null));
        Assert.True(model.HasDraft);
    }
}
