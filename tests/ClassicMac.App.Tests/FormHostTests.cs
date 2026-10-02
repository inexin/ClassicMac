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
            ("STR#", 128, null, [0, 1, 3, .. "one"u8])));
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
        var menu = Assert.IsType<MenuForm>(model.Form);
        Assert.False(model.IsEditingForm);
        Assert.False(menu.IsEditing);
        Assert.True(model.ShowsForm);                                      // the menu's table, not the plain preview
        Assert.Equal("Preview", model.FormPreviewTitle);
        Assert.Equal("Read only. Press Edit or double-click a row to change it.", model.FormReadOnlyNote);

        await Select(model, input, "STR#");
        Assert.False(model.ShowsForm);                                     // not moved yet: its preview until Edit
        model.EditFormCommand.Execute(null);
        Assert.True(model.ShowsForm);
        Assert.True(model.Form!.IsEditing);
        Assert.Equal("Esc cancels, Ctrl+Enter applies.", model.FormHint);   // the default hint
    }

    [Fact]
    public async Task Double_clicking_a_row_edits_with_that_row_selected()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        var menu = Assert.IsType<MenuForm>(model.Form);
        model.SelectedTab = 0;
        model.EditFormCommand.Execute(menu.Items[2]);
        Assert.True(model.IsEditingForm);
        Assert.True(menu.IsEditing);
        Assert.Same(menu.Items[2], menu.SelectedItem);
        Assert.Equal(1, model.SelectedTab);
        Assert.Equal(menu.EditHint, model.FormHint);
        Assert.Equal("Live preview · unapplied changes", model.FormPreviewTitle);
    }

    [Fact]
    public async Task Cancel_drops_the_draft_and_returns_to_read_only()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.EditFormCommand.Execute(null);
        Assert.IsType<MenuForm>(model.Form).Title = "Fichier";
        Assert.True(model.HasDraft);
        model.CancelFormCommand.Execute(null);
        Assert.False(model.IsEditingForm);
        Assert.False(model.HasDraft);
        var fresh = Assert.IsType<MenuForm>(model.Form);
        Assert.Equal("File", fresh.Title);
        Assert.False(fresh.IsEditing);
        Assert.Null(model.LastApplied);
    }

    [Fact]
    public async Task Apply_makes_one_undoable_edit_returns_to_read_only_and_says_how_to_undo()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.EditFormCommand.Execute(null);
        var menu = Assert.IsType<MenuForm>(model.Form);
        menu.Title = "Fichier";
        menu.Items[0].Italic = true;
        model.ApplyFormCommand.Execute(null);
        Assert.False(model.IsEditingForm);
        Assert.False(model.Form!.IsEditing);
        Assert.Equal("Fichier", Assert.IsType<MenuForm>(model.Form).Title);
        Assert.Equal("Applied · Undo Edit 'MENU' 129 (Ctrl+Z)", model.LastApplied);

        await model.UndoCommand.ExecuteAsync(null);                         // one edit: one undo brings it all back
        await Select(model, input, "MENU");
        Assert.Equal("File", Assert.IsType<MenuForm>(model.Form).Title);
        Assert.Equal(1, Assert.IsType<MenuForm>(model.Form).Items[0].Face);
        Assert.Null(model.LastApplied);                                    // another selection clears it
    }

    [Fact]
    public async Task Apply_is_disabled_while_the_values_have_an_error()
    {
        var (model, input) = await Open();
        await Select(model, input, "MENU");
        model.EditFormCommand.Execute(null);
        var menu = Assert.IsType<MenuForm>(model.Form);
        Assert.True(model.ApplyFormCommand.CanExecute(null));
        menu.Items[3].Key = "S";
        Assert.Equal(menu.Error, model.FormError);
        Assert.False(model.ApplyFormCommand.CanExecute(null));
        model.ApplyFormCommand.Execute(null);
        Assert.True(model.IsEditingForm);                                  // nothing applied
        menu.Items[3].Key = "";
        Assert.Null(model.FormError);
        Assert.True(model.ApplyFormCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_cannot_start_twice_or_without_a_form_and_cancel_needs_editing()
    {
        var (model, input) = await Open();
        Assert.False(model.EditFormCommand.CanExecute(null));             // the input: no form
        model.EditFormCommand.Execute(null);
        Assert.False(model.IsEditingForm);
        await Select(model, input, "MENU");
        Assert.False(model.CancelFormCommand.CanExecute(null));
        model.EditFormCommand.Execute(null);
        Assert.False(model.EditFormCommand.CanExecute(null));
        Assert.True(model.CancelFormCommand.CanExecute(null));
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
        model.EditFormCommand.Execute(null);
        Assert.IsType<MenuForm>(model.Form).Title = "Fichier";
        var menuNode = model.Selected;
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#").Children[0];
        await model.DraftTask;
        Assert.Single(dialogs.DraftAsked);
        Assert.Same(menuNode, model.Selected);                             // cancelled: still editing the menu
        Assert.True(model.IsEditingForm);

        dialogs.Draft = DraftChoice.Apply;
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#").Children[0];
        await model.DraftTask;
        Assert.IsType<StringListForm>(model.Form);
        Assert.False(model.IsEditingForm);
        Assert.True(input.IsUnsaved);
    }
}
