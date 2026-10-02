using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

// The dialog item list form (design/boards/dialog-item-list.md, E3): the read-only table, the selection it shares with
// the preview, and the preview following text and bounds on each change, drawn in the 'DLOG' that uses the list.
public sealed class DialogItemsFormTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-ditl").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Find: the OK button, "Find what:", an edit text, the note icon and a user item (disabled).
    internal static IReadOnlyList<DialogItem> Find() =>
    [
        new(new MacRect(74, 230, 94, 290), 4, true, "OK", null, ReadOnlyMemory<byte>.Empty),
        new(new MacRect(13, 70, 29, 150), 8, false, "Find what:", null, ReadOnlyMemory<byte>.Empty),
        new(new MacRect(13, 155, 29, 285), 16, true, "", null, ReadOnlyMemory<byte>.Empty),
        new(new MacRect(10, 20, 42, 52), 32, false, null, 1, ReadOnlyMemory<byte>.Empty),
        new(new MacRect(50, 20, 60, 60), 0, false, null, null, ReadOnlyMemory<byte>.Empty),
    ];

    internal static WindowTemplate FindDialog() => new(new MacRect(40, 40, 146, 340), 1, true, false, 0, "Find", 128, null);

    private static DialogItemsForm Form() => new(new Resource(FourCC.FromString("DITL"), 128, InterfaceWriter.WriteDialogItems(Find())), Find());

    [Fact]
    public void The_table_reads_each_item()
    {
        var form = Form();
        Assert.True(form.HasReadOnlyView);
        Assert.Equal("Item 1 is the default button. Esc cancels, Ctrl+Enter applies.", form.EditHint);
        var rows = form.Items;
        Assert.Equal([1, 2, 3, 4, 5], rows.Select(r => r.Number));
        Assert.Equal(("Button", "“OK”", "74, 230, 94, 290", "Yes", false), (rows[0].KindName, rows[0].Content, rows[0].BoundsText, rows[0].EnabledText, rows[0].IsDimmed));
        Assert.Equal(("Static text", "“Find what:”", "No", true), (rows[1].KindName, rows[1].Content, rows[1].EnabledText, rows[1].IsDimmed));
        Assert.Equal("“”", rows[2].Content);
        Assert.Equal(("Icon", "1"), (rows[3].KindName, rows[3].Content));
        Assert.Equal(("User item", "—", true), (rows[4].KindName, rows[4].Content, rows[4].HasNoContent));
        Assert.False(rows[0].HasNoContent);

        var changed = new List<string?>();
        rows[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rows[0].Right = 300;
        rows[0].Text = "Find";
        rows[0].Enabled = false;
        Assert.Equal(("74, 230, 94, 300", "“Find”", "No"), (rows[0].BoundsText, rows[0].Content, rows[0].EnabledText));
        Assert.Contains(nameof(DialogItemRow.BoundsText), changed);
        Assert.Contains(nameof(DialogItemRow.Content), changed);
        Assert.Contains(nameof(DialogItemRow.IsDimmed), changed);
        rows[0].Kind = DialogItemRow.Kinds.Single(k => k.Type == 64);      // a picture: its resource ID shows
        Assert.Equal(("Picture", "0"), (rows[0].KindName, rows[0].Content));
    }

    [Fact]
    public void Rows_and_the_preview_select_each_other_without_editing_the_values()
    {
        var form = Form();
        var edited = 0;
        form.Edited += (_, _) => edited++;
        Assert.Null(form.SelectedItem);
        Assert.Equal(-1, form.PreviewIndex);

        form.SelectRow(form.Items[2]);                                     // a double-click on a row, or a click
        Assert.Same(form.Items[2], form.SelectedItem);
        Assert.True(form.Items[2].IsSelected);
        Assert.Equal(2, form.PreviewIndex);
        form.PreviewIndex = 0;                                             // a click in the preview
        Assert.Same(form.Items[0], form.SelectedItem);
        Assert.False(form.Items[2].IsSelected);
        form.PreviewIndex = 9;                                             // outside: none
        Assert.Null(form.SelectedItem);
        form.SelectRow("not a row");
        Assert.Null(form.SelectedItem);
        Assert.Equal(0, edited);

        form.SelectRow(form.Items[4]);
        form.RemoveCommand.Execute(form.Items[4]);                         // the selected row removed: none selected
        Assert.Null(form.SelectedItem);
        Assert.Equal([1, 2, 3, 4], form.Items.Select(r => r.Number));
        form.AddCommand.Execute(null);                                     // a new item is selected
        Assert.Same(form.Items[^1], form.SelectedItem);
        Assert.Equal(5, form.Items[^1].Number);
        form.MoveUpCommand.Execute(form.Items[^1]);
        Assert.Equal(4, form.SelectedItem!.Number);
        Assert.Equal(3, form.PreviewIndex);
        Assert.True(edited > 0);
    }

    private async Task<(MainViewModel Model, ResourceNode Node)> Open(bool withDialog)
    {
        var path = Path.Combine(folder, "Find.rsrc");
        (string, short, string?, byte[])[] resources = withDialog
            ? [("DITL", 128, "Find", InterfaceWriter.WriteDialogItems(Find())), ("DLOG", 128, null, InterfaceWriter.WriteWindow(FindDialog(), true))]
            : [("DITL", 128, "Find", InterfaceWriter.WriteDialogItems(Find()))];
        File.WriteAllBytes(path, PreviewTests.Fork(resources));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        var node = (ResourceNode)input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "DITL").Children[0];
        model.Selected = node;
        await model.PreviewTask;
        return (model, node);
    }

    [Fact]
    public async Task The_preview_is_the_dialog_that_uses_the_list_and_follows_each_change()
    {
        var (model, node) = await Open(withDialog: true);
        var form = Assert.IsType<DialogItemsForm>(model.Form);
        Assert.Equal("'DLOG' 128", form.UsedBy);
        var preview = Assert.IsType<DialogPreview>(model.FormDialog);
        Assert.Equal(("Find", 300, 106), (preview.Drawing.Title, preview.Drawing.Width, preview.Drawing.Height));
        Assert.Equal("Drawn from 'DLOG' 128 · 300 × 106", model.FormDialogNote);
        Assert.True(preview.ContentLeft > 0 && preview.ContentTop > 0);     // the window's frame around the content

        model.EditFormCommand.Execute(null);
        form.Items[0].Right = 300;                                         // one keystroke in a bounds field
        var moved = model.FormDialog!;
        Assert.NotSame(preview, moved);
        Assert.Equal(300, moved.Drawing.Items[0].Item.Bounds.Right);
        form.Items[1].Text = "Find:";
        Assert.Equal("Find:", model.FormDialog!.Drawing.Items[1].Item.Text);
        Assert.Equal("Find", model.FormDialog.Drawing.Title);              // still in the dialog's window
        Assert.False(model.ShowsHostDialog);                               // the form shows its own preview panel
        _ = node;
    }

    [Fact]
    public async Task A_list_no_dialog_uses_is_drawn_on_its_own()
    {
        var (model, _) = await Open(withDialog: false);
        var form = Assert.IsType<DialogItemsForm>(model.Form);
        Assert.Null(form.UsedBy);
        Assert.Equal(2, model.FormDialog!.Drawing.Definition);             // a plain box around the items
        Assert.Equal($"Drawn on its own · {model.FormDialog.Drawing.Width} × {model.FormDialog.Drawing.Height}", model.FormDialogNote);
    }

    [Fact]
    public async Task The_header_counts_the_items_and_names_their_dialog()
    {
        var (model, _) = await Open(withDialog: true);
        var facts = model.Header!.Facts;
        Assert.Contains(facts, f => f is { Label: "Items", Value: "5" });
        Assert.Contains(facts, f => f is { Label: "Used by", Value: "'DLOG' 128", IsMono: true });
    }
}
