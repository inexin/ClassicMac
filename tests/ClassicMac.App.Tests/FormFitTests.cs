using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

using static Headless;

// Found in a look at real files (Mac OS 9's System): text that did not fit its field in the forms, drawn in the window.
public sealed class FormFitTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-fit").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private (MainWindow Window, MainViewModel Model, InputNode Input) Open()
    {
        IReadOnlyList<DialogItem> items =
        [
            .. DialogItemsFormTests.Find(),
            new(new MacRect(70, 20, 86, 160), 6, true, "Whole word", null, ReadOnlyMemory<byte>.Empty),   // a radio button
        ];
        byte[] alert = [0, 158, 0, 153, 0, 252, 2, 69, 0xAE, 0x2E, 0x55, 0x55, 0x28, 0x0A];               // ID -20946
        byte[] size = [0x58, 0x80, 0, 0x10, 0, 0, 0, 0x08, 0, 0];
        var cursor = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 1)).Concat(new byte[] { 0, 8, 0, 8 }).ToArray();
        var path = Path.Combine(folder, "Forms.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("DITL", 128, null, InterfaceWriter.WriteDialogItems(items)), ("ALRT", 128, null, alert),
            ("SIZE", -1, null, size), ("CURS", 128, null, cursor)));
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model, Width = 1400, Height = 900 };
        window.Show();
        Pump(model.OpenAsync(path));
        var input = model.Roots[0];
        Pump(input.EnsureLoadedAsync());
        return (window, model, input);
    }

    private static void Select(MainViewModel model, InputNode input, string type, bool edit)
    {
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
        Pump(model.PreviewTask);
        if (edit)
        {
            model.FormEditing.EditFormCommand.Execute(null);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static double Left(Visual control, Visual relativeTo) => control.TranslatePoint(default, relativeTo)!.Value.X;

    // A text presenter or text block whose text is wider than the room it has.
    private static bool Fits(Control text) => text switch
    {
        TextBlock block => block.TextLayout.WidthIncludingTrailingWhitespace <= block.Bounds.Width + 0.5,
        TextPresenter presenter => presenter.TextLayout.WidthIncludingTrailingWhitespace <= presenter.Bounds.Width + 0.5,
        _ => true,
    };

    [Fact]
    public void Item_kinds_fit_their_select_when_editing_an_item_list() => OnUiThread(() =>
    {
        var (window, model, input) = Open();
        Select(model, input, "DITL", edit: true);
        var kinds = window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.SelectedItem is ItemKind).ToList();
        Assert.Equal(6, kinds.Count);
        Assert.All(kinds, c => Assert.True(c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).All(Fits),
            $"'{((ItemKind)c.SelectedItem!).Name}' is cut"));
        window.Close();
    });

    [Fact]
    public void An_alerts_bounds_and_item_list_fit_their_fields_and_the_card() => OnUiThread(() =>
    {
        var (window, model, input) = Open();
        Select(model, input, "ALRT", edit: true);
        var cards = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AlertCards");
        var fields = cards.GetVisualDescendants().OfType<NumericUpDown>().Where(n => n.IsEffectivelyVisible).ToList();
        Assert.Equal(5, fields.Count);                                     // top, left, bottom, right, the item list
        Assert.All(fields, f => Assert.True(f.GetVisualDescendants().OfType<TextPresenter>().Single().TextLayout.WidthIncludingTrailingWhitespace
            <= f.GetVisualDescendants().OfType<ScrollViewer>().Single().Viewport.Width + 0.5, $"{f.Value} is cut"));
        var card = cards.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
        Assert.All(fields, f => Assert.True(Left(f, card) + f.Bounds.Width <= card.Bounds.Width, $"{f.Value} runs out of the card"));
        window.Close();
    });

    [Fact]
    public void Template_labels_keep_a_gap_before_their_values() => OnUiThread(() =>
    {
        var (window, model, input) = Open();
        Select(model, input, "SIZE", edit: false);
        var rows = window.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("template-field") && g.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var label = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("label"));
            var value = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("value") && t.IsEffectivelyVisible);
            Assert.True(Left(label, row) + label.Bounds.Width + 12 <= Left(value, row), $"“{label.Text}” touches its value");
        }

        window.Close();
    });

    [Fact]
    public void A_long_template_value_wraps_before_its_type_code() => OnUiThread(() =>
    {
        var (window, model, input) = Open();
        foreach (var edit in new[] { false, true })
        {
            Select(model, input, "CURS", edit);
            var rows = window.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("template-field") && g.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var code = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("type-code"));
                Control value = edit
                    ? row.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible)
                    : row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("value") && t.IsEffectivelyVisible);
                Assert.True(Left(value, row) + value.Bounds.Width <= Left(code, row), $"{code.Text}: the value runs under the code");
            }

            model.FormEditing.CancelFormCommand.Execute(null);
        }

        window.Close();
    });
}
