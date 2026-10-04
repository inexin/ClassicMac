using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

using static Headless;

// The dialog item list form in the window (E3): the read-only table, rows and the preview selecting each other, and
// bounds typed into a row moving the item in the preview on each keystroke.
public sealed class DialogItemsWindowTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-ditl-window").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private (MainWindow Window, MainViewModel Model, DialogItemsForm Form) Open()
    {
        var path = Path.Combine(folder, "Find.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("DITL", 128, "Find", InterfaceWriter.WriteDialogItems(DialogItemsFormTests.Find())),
            ("DLOG", 128, null, InterfaceWriter.WriteWindow(DialogItemsFormTests.FindDialog(), true))));
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Pump(model.OpenAsync(path));
        Pump(model.Roots[0].EnsureLoadedAsync());
        var type = model.Roots[0].Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "DITL");
        type.IsExpanded = true;
        model.Selected = type.Children[0];
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        return (window, model, Assert.IsType<DialogItemsForm>(model.Form));
    }

    private static List<Border> Rows(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ditl-row") && b.IsEffectivelyVisible).ToList();

    private static void Click(Window window, Visual target, Point at, int clicks = 1)
    {
        var point = target.TranslatePoint(at, window)!.Value;
        for (var i = 0; i < clicks; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void Rows_and_the_preview_select_each_other() => OnUiThread(() =>
    {
        var (window, model, form) = Open();
        var baselines = new List<string>();
        Assert.True(model.FormEditing.ShowsForm);
        Assert.False(model.FormEditing.IsEditingForm);
        var rows = Rows(window);
        Assert.Equal(5, rows.Count);
        var cells = rows[0].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(["1", "Button", "“OK”", "74, 230, 94, 290", "Yes"], cells);
        var preview = window.GetVisualDescendants().OfType<DialogView>().Single(v => v.Name == "ItemListPreview");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Drawn from 'DLOG' 128 · 300 × 106");

        Click(window, rows[2], new Point(40, rows[2].Bounds.Height / 2));     // a row: its item outlined in the preview
        Assert.Same(form.Items[2], form.SelectedItem);
        Assert.Equal(2, preview.SelectedIndex);
        Assert.Contains("selected", rows[2].Classes);
        Baselines.Check(window, "ditl-read-only", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);

        // A click on the OK button in the preview selects its row.
        var dialog = model.FormDialog!;
        var scale = preview.Bounds.Width / (dialog.PixelWidth + 24);
        var ok = dialog.Drawing.Items[0].Item.Bounds;
        Click(window, preview, new Point((12 + dialog.ContentLeft + ok.Left + 5) * scale, (12 + dialog.ContentTop + ok.Top + 5) * scale));
        Assert.Same(form.Items[0], form.SelectedItem);
        Assert.Contains("selected", Rows(window)[0].Classes);
        window.Close();
        Baselines.Verify(baselines);
    });

    [Fact]
    public void Bounds_typed_into_a_row_move_the_item_on_each_keystroke() => OnUiThread(() =>
    {
        var (window, model, form) = Open();
        var baselines = new List<string>();
        var rows = Rows(window);
        Click(window, rows[0], new Point(40, rows[0].Bounds.Height / 2), clicks: 2);   // a double-click edits at that row
        Assert.True(model.FormEditing.IsEditingForm);
        Assert.Same(form.Items[0], form.SelectedItem);
        var row = Rows(window)[0];
        var right = row.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RightBox");
        right.Focus();
        right.SelectAll();
        window.KeyTextInput("3");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, model.FormDialog!.Drawing.Items[0].Item.Bounds.Right);        // the first keystroke
        window.KeyTextInput("00");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, model.FormDialog!.Drawing.Items[0].Item.Bounds.Right);

        var text = row.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ItemText");
        text.Focus();
        text.SelectAll();
        window.KeyTextInput("Go");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Go", model.FormDialog!.Drawing.Items[0].Item.Text);
        Assert.Equal("Live preview · unapplied changes", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ItemListPreviewTitle").Text);
        Baselines.Check(window, "ditl-editing", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);

        right.Focus();
        right.SelectAll();
        window.KeyTextInput("x");                                                   // not a number: the error, no move
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(model.FormError);
        Assert.Equal(300, model.FormDialog!.Drawing.Items[0].Item.Bounds.Right);
        window.Close();
        Baselines.Verify(baselines);
    });
}
