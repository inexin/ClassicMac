using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

// The read-then-edit host (design/boards/read-then-edit.md, E1): every form opens read only, Edit (or a row's
// double-click) starts editing, Cancel drops the draft, Apply makes one undoable edit; the footer, the preview title and
// the error follow.
public sealed class FormHostTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-host").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private async Task<(MainViewModel Model, InputNode Input)> Open()
    {
        var path = Path.Combine(folder, "Forms.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("MENU", 129, null, InterfaceWriter.WriteMenu(MenuFormTests.File())),
            ("STR#", 128, null, [0, 1, 3, .. "one"u8]),
            ("CNTL", 128, null, InterfaceWriter.WriteControl(new ControlTemplate(new MacRect(10, 10, 30, 90), 0, true, 1, 0, 0, 0, "OK"))),
            ("TMPL", 128, "Rsrc", EditTests.Tmpl(("Flag", "BOOL"))), ("Rsrc", 128, null, [1, 0])));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    private static async Task Select(MainViewModel model, InputNode input, string type)
    {
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
        await model.PreviewTask;
    }

    [Fact]
    public async Task A_form_opens_read_only_and_a_form_with_a_read_only_view_shows_it()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        var menu = Assert.IsType<MenuForm>(model.Forms.Form);
        Assert.False(model.FormEditing.IsEditingForm);
        Assert.False(menu.IsEditing);
        Assert.True(model.FormEditing.ShowsForm);                                      // the menu's table, not the plain preview
        Assert.Equal("Preview", model.FormEditing.FormPreviewTitle);
        Assert.Equal("Read only. Press Edit or double-click a row to change it.", model.FormEditing.FormReadOnlyNote);

        await Select(model, input, "STR#");
        Assert.True(model.FormEditing.ShowsForm);                                      // moved (E5): its numbered list

        await Select(model, input, "CNTL");
        Assert.True(model.FormEditing.ShowsForm);                                      // moved (E4): its property cards

        await Select(model, input, "Rsrc");
        Assert.True(model.FormEditing.ShowsForm);                                      // moved (E6): its fields and lists

        // A form without a read-only view (every form has one now; the host still shows the preview until Edit).
        model.Forms.Form = new NoReadOnlyView(model.Forms.Form!.Resource);
        Assert.False(model.Forms.Form!.HasReadOnlyView);
        Assert.False(model.FormEditing.ShowsForm);
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.True(model.FormEditing.ShowsForm);
        Assert.True(model.Forms.Form!.IsEditing);
        Assert.Equal("Esc cancels, Ctrl+Enter applies.", model.FormEditing.FormHint);   // the default hint
    }

    [Fact]
    public async Task Double_clicking_a_row_edits_with_that_row_selected()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        var menu = Assert.IsType<MenuForm>(model.Forms.Form);
        model.SelectedTab = 0;
        model.FormEditing.EditFormCommand.Execute(menu.Items[2]);
        Assert.True(model.FormEditing.IsEditingForm);
        Assert.True(menu.IsEditing);
        Assert.Same(menu.Items[2], menu.SelectedItem);
        Assert.Equal(1, model.SelectedTab);
        Assert.Equal(menu.EditHint, model.FormEditing.FormHint);
        Assert.Equal("Live preview · unapplied changes", model.FormEditing.FormPreviewTitle);
    }

    [Fact]
    public async Task Cancel_drops_the_draft_and_returns_to_read_only()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.IsType<MenuForm>(model.Forms.Form).Title = "Fichier";
        Assert.True(model.Drafts.HasDraft);
        model.FormEditing.CancelFormCommand.Execute(null);
        Assert.False(model.FormEditing.IsEditingForm);
        Assert.False(model.Drafts.HasDraft);
        var fresh = Assert.IsType<MenuForm>(model.Forms.Form);
        Assert.Equal("File", fresh.Title);
        Assert.False(fresh.IsEditing);
        Assert.Null(model.FormEditing.LastApplied);
    }

    [Fact]
    public async Task Apply_makes_one_undoable_edit_returns_to_read_only_and_says_how_to_undo()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.FormEditing.EditFormCommand.Execute(null);
        var menu = Assert.IsType<MenuForm>(model.Forms.Form);
        menu.Title = "Fichier";
        menu.Items[0].Italic = true;
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.False(model.FormEditing.IsEditingForm);
        Assert.False(model.Forms.Form!.IsEditing);
        Assert.Equal("Fichier", Assert.IsType<MenuForm>(model.Forms.Form).Title);
        Assert.Equal("Applied · Undo Edit 'MENU' 129 (Ctrl+Z)", model.FormEditing.LastApplied);

        await model.UndoCommand.ExecuteAsync(null);                         // one edit: one undo brings it all back
        await Select(model, input, "MENU");
        Assert.Equal("File", Assert.IsType<MenuForm>(model.Forms.Form).Title);
        Assert.Equal(1, Assert.IsType<MenuForm>(model.Forms.Form).Items[0].Face);
        Assert.Null(model.FormEditing.LastApplied);                                    // another selection clears it
    }

    [Fact]
    public async Task Apply_is_disabled_while_the_values_have_an_error()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.FormEditing.EditFormCommand.Execute(null);
        var menu = Assert.IsType<MenuForm>(model.Forms.Form);
        Assert.True(model.Forms.ApplyFormCommand.CanExecute(null));
        menu.Items[3].Key = "S";
        Assert.Equal(menu.Error, model.FormLivePreview.FormError);
        Assert.False(model.Forms.ApplyFormCommand.CanExecute(null));
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.True(model.FormEditing.IsEditingForm);                                  // nothing applied
        menu.Items[3].Key = "";
        Assert.Null(model.FormLivePreview.FormError);
        Assert.True(model.Forms.ApplyFormCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_cannot_start_twice_or_without_a_form_and_cancel_needs_editing()
    {
        var (model, input) = await Open();
        Assert.False(model.FormEditing.EditFormCommand.CanExecute(null));             // the input: no form
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.False(model.FormEditing.IsEditingForm);
        await Select(model, input, "MENU");
        Assert.False(model.FormEditing.CancelFormCommand.CanExecute(null));
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.False(model.FormEditing.EditFormCommand.CanExecute(null));
        Assert.True(model.FormEditing.CancelFormCommand.CanExecute(null));
    }

    [Fact]
    public async Task Leaving_with_a_draft_still_asks_through_the_host()
    {
        var path = Path.Combine(folder, "Forms.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("MENU", 129, null, InterfaceWriter.WriteMenu(MenuFormTests.File())),
            ("STR#", 128, null, [0, 1, 3, .. "one"u8])));
        var dialogs = new EditTests.Dialogs { Draft = DraftChoice.Cancel };
        var model = new MainViewModel { EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        await Select(model, input, "MENU");
        model.FormEditing.EditFormCommand.Execute(null);
        Assert.IsType<MenuForm>(model.Forms.Form).Title = "Fichier";
        var menuNode = model.Selected;
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#").Children[0];
        await model.DraftTask;
        Assert.Single(dialogs.DraftAsked);
        Assert.Same(menuNode, model.Selected);                             // cancelled: still editing the menu
        Assert.True(model.FormEditing.IsEditingForm);

        dialogs.Draft = DraftChoice.Apply;
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#").Children[0];
        await model.DraftTask;
        Assert.IsType<StringListForm>(model.Forms.Form);
        Assert.False(model.FormEditing.IsEditingForm);
        Assert.True(input.IsUnsaved);
    }

    private sealed class NoReadOnlyView(ClassicMac.Resources.Resource resource) : ResourceForm(resource)
    {
        public override byte[] BuildData() => Resource.GetData().ToArray();
    }
}
