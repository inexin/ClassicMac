using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

// The inspector's header, its tabs and the status bar in the window (design/boards/main-window.md, S3, S4).
public sealed class InspectorViewTests
{
    private static void Pump(Task task) => Headless.Pump(task);

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Token(string key) =>
        ColorOf((IBrush)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, key)!);

    private static string Forks(string folder)
    {
        var path = Path.Combine(folder, "Forms.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("STR#", 128, "Names", [0, 2, 3, .. "one"u8, 3, .. "two"u8]), ("ZZZZ", 1, null, [1])));
        return path;
    }

    [Fact]
    public void The_header_shows_the_selection_and_the_Edit_tab_is_gone() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-inspector-view").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var header = window.FindControl<Border>("InspectorHeader")!;
            Assert.False(header.IsVisible); // nothing selected

            var open = model.OpenAsync(Forks(folder));
            Pump(open);
            var input = open.Result!;
            Pump(input.EnsureLoadedAsync());
            var strings = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#").Children[0];
            model.Selected = strings;
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();

            Assert.True(header.IsVisible);
            var texts = header.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.Equal("128 “Names”", texts.Single(t => t.Classes.Contains("heading")).Text);
            Assert.Equal("String list in Forms.rsrc", texts.Single(t => t.Classes.Contains("kind")).Text);
            Assert.Equal(["Type", "ID", "Size", "Attributes"], texts.Where(t => t.Classes.Contains("label")).Select(t => t.Text));
            var type = texts.Single(t => t.Classes.Contains("value") && t.Text == "'STR#'");
            Assert.Equal("IBM Plex Mono", type.FontFamily.Name.Split('#')[^1]);
            Assert.Same(strings, header.GetVisualDescendants().OfType<TreeIcon>().Single().Node);
            // No large icon of its own: the kind icon shows in the tile.
            Assert.True(header.GetVisualDescendants().OfType<TreeIcon>().Single().IsEffectivelyVisible);
            Assert.False(header.GetVisualDescendants().OfType<PixelImage>().Single().IsEffectivelyVisible);
            model.HeaderIconPng = ClassicMac.Resources.Decoders.Images.PngEncoder.Instance.Encode(32, 32, new byte[32 * 32 * 4]);
            Dispatcher.UIThread.RunJobs();
            var large = header.GetVisualDescendants().OfType<PixelImage>().Single();
            Assert.True(large.IsEffectivelyVisible);
            Assert.Equal((32, 1.0), (large.Source!.PixelSize.Width, large.Zoom));
            Assert.False(header.GetVisualDescendants().OfType<TreeIcon>().Single().IsEffectivelyVisible);
            // The toolbar's Export… does what the header's does.
            var toolbarExport = window.FindControl<Border>("Toolbar")!.GetVisualDescendants().OfType<Button>()
                .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Export");
            Assert.Same(model.HeaderExportCommand, toolbarExport.Command);
            var buttons = header.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Same(model.SaveResourceAsCommand, buttons.Single(b => (string?)b.Content == "Export…").Command);
            var edit = buttons.Single(b => (string?)b.Content == "Edit");
            Assert.True(edit.IsEffectivelyVisible);

            // Details, Preview and (for resources without a preview) Hex; no Edit tab.
            var tabs = window.GetLogicalDescendants().OfType<TabItem>().Select(t => (string?)t.Header).ToList();
            Assert.Equal(["Details", "Preview", "Hex"], tabs);

            // Edit opens the form in the Preview tab; the header shows "Editing".
            edit.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, model.SelectedTab);
            Assert.True(window.FindControl<DockPanel>("FormHost")!.IsEffectivelyVisible);
            Assert.False(edit.IsEffectivelyVisible);
            Assert.True(header.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editing-badge")).IsEffectivelyVisible);
            var cancel = window.FindControl<DockPanel>("FormHost")!.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Cancel");
            cancel.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<DockPanel>("EditingFooter")!.IsEffectivelyVisible);   // read only again
            Assert.True(window.FindControl<StackPanel>("ReadOnlyFooter")!.IsEffectivelyVisible);

            // A resource without a form has no Edit button.
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "ZZZZ").Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Assert.False(edit.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void The_status_bar_shows_the_summary_and_progress() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-status-view").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(Forks(folder)));
            model.DiagnosticsPanel.Add(new DiagnosticEntry(new ClassicMac.Core.Diagnostic(ClassicMac.Core.DiagnosticSeverity.Error, "x.y", "m"), "s", model.Roots[0]));
            Dispatcher.UIThread.RunJobs();

            var bar = window.FindControl<Border>("StatusBar")!;
            Assert.Equal(26, bar.Bounds.Height, 6);
            var shown = bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            Assert.Equal("Forms.rsrc · resource fork · 1 file", shown[0].Text);
            var errors = shown.Single(t => t.Text == " · 1 error");
            Assert.Equal(Token("CmError"), ColorOf(errors.Foreground));
            Assert.Contains(shown, t => t.Text == model.Status);
            Assert.False(window.FindControl<StackPanel>("Progress")!.IsEffectivelyVisible);

            var progress = model.BeginProgress("Extracting Forms…", 10);
            progress.Apply(4);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<StackPanel>("Progress")!.IsEffectivelyVisible);
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible), t => t.Text == model.Status);
            var progressBar = bar.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.Equal((4.0, 10.0, 120.0, 6.0), (progressBar.Value, progressBar.Maximum, progressBar.Bounds.Width, progressBar.Bounds.Height));
            Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "4 of 10" && t.IsEffectivelyVisible);
            progress.Finish("Done.");
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<StackPanel>("Progress")!.IsEffectivelyVisible);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
