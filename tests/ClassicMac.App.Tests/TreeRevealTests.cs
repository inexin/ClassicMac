using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.Controls;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// A selection made away from the tree (type-ahead, the Details tab's "In" link, Show item, a diagnostic's row) scrolls
// the tree to the selected row, however deep and far it is in the virtualized tree.
public sealed class TreeRevealTests : IDisposable
{
    private const int PerFolder = 400;

    private readonly string folder = Directory.CreateTempSubdirectory("cm-treereveal").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Big: Alpha (400 files), Bravo › Charlie (400 files), Bravo › Delta (400 files); every folder open.
    private (MainViewModel Model, MainWindow Window) Open()
    {
        var disk = new HfsBuilder { CatalogLeaves = 400 };
        var alpha = disk.Folder(HfsBuilder.Root, "Alpha");
        var bravo = disk.Folder(HfsBuilder.Root, "Bravo");
        var charlie = disk.Folder(bravo, "Charlie");
        var delta = disk.Folder(bravo, "Delta");
        foreach (var (id, name) in new[] { (alpha, "Alpha"), (charlie, "Charlie"), (delta, "Delta") })
        {
            for (var i = 0; i < PerFolder; i++)
            {
                disk.File(id, $"{name} {i:D4}", [], []);
            }
        }

        var path = Path.Combine(folder, "deep.img");
        File.WriteAllBytes(path, disk.Build("Big"));
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Headless.Pump(model.OpenAsync(path));
        Render(window);
        foreach (var node in Folders(model))
        {
            node.IsExpanded = true;
        }

        Render(window);
        return (model, window);
    }

    private static IEnumerable<NodeViewModel> Folders(MainViewModel model)
    {
        var stack = new Stack<NodeViewModel>([model.Roots[0]]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children.OfType<FolderNode>())
            {
                stack.Push(child);
            }
        }
    }

    private static NodeViewModel Find(MainViewModel model, string title) =>
        Folders(model).SelectMany(f => f.Children.Prepend(f)).First(n => n.Title == title);

    private static void Render(MainWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }

        Dispatcher.UIThread.RunJobs();
    }

    // The node's row exists, is selected and lies inside the tree's viewport.
    private static void AssertInView(MainWindow window, NodeViewModel node)
    {
        var row = window.Named<BrowseTree>("Tree")!.GetVisualDescendants().OfType<ListBoxItem>()
            .SingleOrDefault(r => ReferenceEquals(r.DataContext, node) && r.IsEffectivelyVisible);
        Assert.True(row is not null, $"no row for {node.Title}");
        var tree = window.Named<BrowseTree>("Tree")!;
        var top = row!.TranslatePoint(default, tree)!.Value.Y;
        var header = row.Bounds.Height;
        Assert.True(top >= -1 && top + header <= tree.Bounds.Height + 1, $"{node.Title}'s row at {top}, tree {tree.Bounds.Height}");
        Assert.True(row.IsSelected, $"{node.Title}'s row is not selected");
    }

    // The tree starts at its top with the volume selected.
    private static void ToTop(MainViewModel model, MainWindow window)
    {
        model.Selected = model.Roots[0];
        Render(window);
        AssertInView(window, model.Roots[0]);
    }

    [Fact]
    public void Type_ahead_scrolls_to_a_deep_row()
    {
        Headless.OnUiThread(() =>
        {
            var (model, window) = Open();
            ToTop(model, window);
            model.TreeSearch.TypeAhead("Delta 0350");
            Render(window);
            Assert.Equal("Delta 0350", model.Selected!.Title);
            AssertInView(window, model.Selected);
            window.Close();
        });
    }

    [Fact]
    public void The_In_link_scrolls_to_the_folder()
    {
        Headless.OnUiThread(() =>
        {
            var (model, window) = Open();
            var file = Find(model, "Delta 0399");
            model.Selected = file;
            Render(window);
            AssertInView(window, file);
            Headless.Pump(model.PreviewTask);
            Assert.NotNull(model.Details.InNode);
            Headless.Pump(model.DetailsActions.GoToInCommand.ExecuteAsync(null));
            Render(window);
            Assert.Equal("Delta", model.Selected!.Title);
            AssertInView(window, model.Selected);
            window.Close();
        });
    }

    [Fact]
    public void Show_item_scrolls_to_a_deep_row()
    {
        Headless.OnUiThread(() =>
        {
            var (model, window) = Open();
            ToTop(model, window);
            var file = Find(model, "Charlie 0300");
            Headless.Pump(model.ShowItemCommand.ExecuteAsync(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "Odd."), "Big", file)));
            Render(window);
            AssertInView(window, file);
            window.Close();
        });
    }

    [Fact]
    public void A_diagnostic_s_row_scrolls_to_its_node()
    {
        Headless.OnUiThread(() =>
        {
            var (model, window) = Open();
            ToTop(model, window);
            var file = Find(model, "Delta 0200");
            model.SelectedDiagnostic = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "Odd."), "Big", file);
            Render(window);
            Assert.Same(file, model.Selected);
            AssertInView(window, file);

            // And back up to the top.
            model.SelectedDiagnostic = new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test", "Odd."), "Big", Find(model, "Alpha 0003"));
            Render(window);
            AssertInView(window, model.Selected!);
            window.Close();
        });
    }
}
