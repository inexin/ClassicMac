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
            Assert.False(window.FindControl<Border>("HexEditLine")!.IsEffectivelyVisible);
            Assert.Contains("zero", Cell(window, 4).Classes);

            Pump(model.EditActions.EditHexCommand.ExecuteAsync(null));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("HexInspector")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Border>("HexFooter")!.IsEffectivelyVisible);
            // The host's look (E1): its footer, and the 3 px accent line over the values while editing.
            Assert.Contains("host-footer", window.FindControl<Border>("HexFooter")!.Classes);
            var line = window.FindControl<Border>("HexEditLine")!;
            Assert.True(line.IsEffectivelyVisible);
            Assert.Equal((3.0, Token("CmAccent")), (line.Bounds.Height, ((ISolidColorBrush)line.Background!).Color));
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
            Assert.Equal(3, model.EditActions.HexEdit!.Cursor);
            model.EditActions.GoToText = "0x5";
            model.EditActions.GoToCommand.Execute(null);
            Assert.Equal(5, model.EditActions.HexEdit.Cursor);

            // The footer's switch is the editor's mode.
            var mode = window.FindControl<ListBox>("HexMode")!;
            mode.SelectedIndex = 1;
            Assert.True(model.EditActions.HexEdit.InsertMode);
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

            // The inspector's text stays clear of its overlay scroll bar (seen cut on a 'SIZE' byte's bit names).
            var scroll = window.FindControl<ScrollViewer>("HexInspectorScroll")!;
            Assert.All(inspector.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                t => Assert.True(t.TranslatePoint(new Point(t.Bounds.Width, 0), scroll)!.Value.X <= scroll.Bounds.Width - 8, $"“{t.Text}” runs under the scroll bar"));
            // and the widest reading, a 13-character UInt32 ($FF616263 = 4,284,572,259), fits its column.
            var widest = Cell(window, 2).TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(widest, MouseButton.Left);
            window.MouseUp(widest, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.All(inspector.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.TextWrapping == TextWrapping.NoWrap),
                t => Assert.True(t.TextLayout.WidthIncludingTrailingWhitespace <= t.Bounds.Width + 0.5, $"“{t.Text}” is cut"));
            foreach (var reading in inspector.GetVisualDescendants().OfType<Grid>().Where(g => g.DataContext is HexReading))
            {
                var label = (TextBlock)reading.Children[0];
                Assert.True(label.TextLayout.WidthIncludingTrailingWhitespace + 6 <= reading.ColumnDefinitions[0].ActualWidth, $"“{label.Text}” touches its value");
            }

            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // Find in the window: Ctrl+F in the Hex tab goes to the Find box; a match far down is scrolled to and highlighted.
    [Fact]
    public void Find_scrolls_to_and_highlights_the_match() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-hexfindview").FullName;
        try
        {
            var path = Path.Combine(folder, "Find.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 1, null, [.. new byte[4000], .. "needle"u8, 0, 0])));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            var at = Cell(window, 0).TranslatePoint(new Point(4, 4), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
            Dispatcher.UIThread.RunJobs();
            var box = window.FindControl<TextBox>("FindBox")!;
            Assert.True(box.IsFocused);

            model.HexFind.FindModeIndex = 1;
            box.Text = "needle";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("1 of 1", model.HexFind.FindStatus);
            Assert.Contains("match", Cell(window, 4000).Classes);
            Assert.Equal(Token("CmMatch"), ((ISolidColorBrush)Cell(window, 4001).Background!).Color);
            Assert.True(window.FindControl<TextBlock>("FindStatusText")!.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The byte inspector fits under the Find bar at the default size (1200 × 780, diagnostics open): its last row, Binary,
    // is inside the panel.
    [Fact]
    public void The_inspector_s_last_row_is_in_view_at_the_default_size() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-hexinspector").FullName;
        try
        {
            var path = Path.Combine(folder, "Fit.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 1, null, [.. "Hello, hex editing works across lines"u8])));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Assert.Equal((1200, 780), ((int)window.Width, (int)window.Height));
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            model.EditActions.BeginHexEditCommand.Execute(null);
            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();

            Assert.True(model.DiagnosticsPanel.IsExpanded);
            var inspector = window.FindControl<Border>("HexInspector")!;
            var last = inspector.GetVisualDescendants().OfType<TextBlock>().Last(t => t.Text == "Binary");
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), inspector)!.Value.Y;
            Assert.True(bottom <= inspector.Bounds.Height - inspector.Padding.Bottom,
                $"Binary ends at {bottom}, the panel at {inspector.Bounds.Height}");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
