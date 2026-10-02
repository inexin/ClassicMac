using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

// The Hex tab in the window (design/boards/hex.md, E7): the grid, editing in place, the inspector and the footer.
public sealed class HexViewTests
{
    private static void Pump(Task task) => Headless.Pump(task);

    private static Color Token(string key) =>
        ((ISolidColorBrush)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, key)!).Color;

    private static Border Cell(Window window, long offset) =>
        window.FindControl<ListBox>("HexList")!.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("hex-cell") && b.DataContext is HexCell c && c.Offset == offset);

    [Fact]
    public void Edit_Hex_edits_in_the_Hex_tab() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-hexview").FullName;
        try
        {
            var path = Path.Combine(folder, "Hex.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 1, null, [0x55, 0x6E, 0x74, 0x69, 0x00, 0xFF])));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();

            // Read only: the grid, no inspector or footer; Edit Bytes offered.
            Assert.False(window.FindControl<Border>("HexInspector")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Border>("HexFooter")!.IsEffectivelyVisible);
            Assert.Contains("zero", Cell(window, 4).Classes);

            Pump(model.EditHexCommand.ExecuteAsync(null));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("HexInspector")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Border>("HexFooter")!.IsEffectivelyVisible);
            var inspector = window.FindControl<Border>("HexInspector")!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("At 0x0000", inspector);
            Assert.Contains("'Unti'", inspector);
            Assert.Contains("cursor", Cell(window, 0).Classes);
            Assert.Equal(Token("CmHexCursor"), ((ISolidColorBrush)Cell(window, 0).Background!).Color);

            // Typing changes a byte: it is tinted; a click moves the cursor; Go to too.
            var list = window.FindControl<ListBox>("HexList")!;
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D4 });
            list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D1 });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("changed", Cell(window, 0).Classes);
            Assert.Equal(Token("CmWarningTint"), ((ISolidColorBrush)Cell(window, 0).Background!).Color);
            Assert.Contains("cursor", Cell(window, 1).Classes);
            var cell = Cell(window, 3);
            var at = cell.TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, model.HexEdit!.Cursor);
            model.GoToText = "0x5";
            model.GoToCommand.Execute(null);
            Assert.Equal(5, model.HexEdit.Cursor);

            // The footer's switch is the editor's mode.
            var mode = window.FindControl<ListBox>("HexMode")!;
            mode.SelectedIndex = 1;
            Assert.True(model.HexEdit.InsertMode);
            Assert.Contains(window.FindControl<Border>("HexFooter")!.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text?.StartsWith("0x0005 = 255 · 1 byte changed", StringComparison.Ordinal) == true);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // Read only: a click selects a byte (the pair on CmSelectionInactive), the inspector reads it and names its field
    // (E8, through a TMPL), whose bytes are on CmRowHighlight; the Mac OS Roman column stays clear of the inspector.
    [Fact]
    public void A_click_selects_a_byte_and_the_inspector_explains_it() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-hexview").FullName;
        try
        {
            var path = Path.Combine(folder, "Data.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("Rsrc", 128, null, [0, 7, 0xFF, .. Enumerable.Range(0, 29).Select(i => (byte)('a' + i % 26))]),
                ("TMPL", 1000, "Rsrc", EditTests.Tmpl(("ID", "DWRD"), ("Flag", "DBYT")))));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "Rsrc").Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var inspector = window.FindControl<Border>("HexInspector")!;
            Assert.False(inspector.IsEffectivelyVisible);

            var at = Cell(window, 1).TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.True(inspector.IsEffectivelyVisible);
            var texts = inspector.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("At 0x0001", texts);
            Assert.Contains("ID", texts);
            Assert.Contains("= 7", texts);
            Assert.Contains("selected", Cell(window, 1).Classes);
            Assert.Equal(Token("CmSelectionInactive"), ((ISolidColorBrush)Cell(window, 1).Background!).Color);
            var character = window.FindControl<ListBox>("HexList")!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("hex-char") && b.DataContext is HexCell c && c.Offset == 1);
            Assert.Equal(Token("CmSelectionInactive"), ((ISolidColorBrush)character.Background!).Color);
            Assert.Contains("in-field", Cell(window, 0).Classes);
            Assert.Equal(Token("CmRowHighlight"), ((ISolidColorBrush)Cell(window, 0).Background!).Color);
            Assert.DoesNotContain("in-field", Cell(window, 2).Classes);

            // At the default 1200 wide, the last character of a full line ends left of the inspector.
            var last = window.FindControl<ListBox>("HexList")!.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("hex-char") && b.DataContext is HexCell c && c.Offset == 15);
            var right = last.TranslatePoint(new Point(last.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= inspector.TranslatePoint(default, window)!.Value.X, $"text column ends at {right}");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
