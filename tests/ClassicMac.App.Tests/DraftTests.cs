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
        await model.SelectionTask;
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
        await model.SelectionTask;
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
        await model.SelectionTask;
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
        await model.SelectionTask;
        var (what, error) = Assert.Single(dialogs.DraftAsked);
        Assert.Equal("'STR ' 128", what);
        Assert.Contains("Mac OS Roman", error);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.False(model.HasUnsavedChanges);

        dialogs.Draft = DraftChoice.Discard;
        model.Selected = Resource(file, 129);
        await model.SelectionTask;
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
        await model.SelectionTask;
        Assert.Equal(("'STR ' 129", (string?)null), Assert.Single(dialogs.DraftAsked));
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
        Assert.True(model.IsHexEditing);

        dialogs.Draft = DraftChoice.Discard;
        model.Selected = Resource(file, 128);
        await model.SelectionTask;
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
        await model.SelectionTask;
        Assert.Single(dialogs.DraftAsked);
        Assert.Equal(128, ((ResourceNode)model.Selected!).Resource.Id);

        dialogs.Draft = DraftChoice.Discard;
        model.SelectedDiagnostic = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "another warning"), "Prefs", Resource(file, 129));
        await model.SelectionTask;
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
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
        await model.SelectionTask;
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
}
