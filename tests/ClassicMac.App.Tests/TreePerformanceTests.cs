using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// A folder of 5,000 files shows quickly: the tree makes rows only for what is on screen (virtualized), so opening it,
// scrolling to its end and collapsing it stay well inside a generous budget (before: about 20 s to show).
public sealed class TreePerformanceTests
{
    private const int Files = 5000;

    // Generous for a slow CI machine; the unvirtualized tree took about 20 s.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    internal static string ManyFiles(string folder)
    {
        var disk = new HfsBuilder { CatalogLeaves = 1300 };
        var many = disk.Folder(HfsBuilder.Root, "Many");
        for (var i = 0; i < Files; i++)
        {
            disk.File(many, $"File {i:D4}", [], []);
        }

        var path = Path.Combine(folder, "many.img");
        File.WriteAllBytes(path, disk.Build("Big"));
        return path;
    }

    private static void Render(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void A_folder_of_five_thousand_files_opens_scrolls_and_closes_quickly() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-treeperf").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Headless.Pump(model.OpenAsync(ManyFiles(folder)));
            Render(window);
            var many = model.Roots[0].Children.OfType<FolderNode>().Single();

            var clock = Stopwatch.StartNew();
            many.IsExpanded = true;
            Render(window);
            var open = clock.Elapsed;

            clock.Restart();
            model.Selected = many.Children[^1];
            window.ShowNode(many.Children[^1]);
            Render(window);
            var toEnd = clock.Elapsed;

            // Only the rows on screen exist, and the last file's row is on screen and selected.
            var rows = window.GetVisualDescendants().OfType<TreeViewItem>().ToList();
            Assert.InRange(rows.Count, 10, 200);
            var last = rows.Single(r => ReferenceEquals(r.DataContext, many.Children[^1]));
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            var top = last.TranslatePoint(default, tree)!.Value.Y;
            Assert.True(top >= 0 && top <= tree.Bounds.Height - last.Bounds.Height + 2, $"row at {top}, tree {tree.Bounds.Height}, row {last.Bounds.Height}");
            Assert.True(last.IsSelected);

            // Keyboard: Up from the last file goes to the one before.
            last.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Render(window);
            Assert.Same(many.Children[^2], model.Selected);

            clock.Restart();
            many.IsExpanded = false;
            Render(window);
            var close = clock.Elapsed;

            File.AppendAllText(Path.Combine(Path.GetTempPath(), "classicmac-treeperf.txt"),
                $"{DateTime.Now:O} open {open.TotalMilliseconds:F0} ms, to end {toEnd.TotalMilliseconds:F0} ms, close {close.TotalMilliseconds:F0} ms\n");
            Assert.True(open < Budget, $"opening took {open.TotalMilliseconds:F0} ms");
            Assert.True(toEnd < Budget, $"going to the last file took {toEnd.TotalMilliseconds:F0} ms");
            Assert.True(close < Budget, $"closing took {close.TotalMilliseconds:F0} ms");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // Type-ahead finds a file far down the folder: its row is made and scrolled into view.
    [Fact]
    public void Type_ahead_brings_a_far_row_into_view() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-treeperf").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Headless.Pump(model.OpenAsync(ManyFiles(folder)));
            var many = model.Roots[0].Children.OfType<FolderNode>().Single();
            many.IsExpanded = true;
            Render(window);

            model.TypeAhead("File 4321");
            Render(window);
            Render(window);

            Assert.Equal("File 4321", model.Selected!.Title);
            var row = window.GetVisualDescendants().OfType<TreeViewItem>().Single(r => ReferenceEquals(r.DataContext, model.Selected));
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            var top = row.TranslatePoint(default, tree)!.Value.Y;
            Assert.True(top >= 0 && top <= tree.Bounds.Height - row.Bounds.Height + 2, $"row at {top}, tree {tree.Bounds.Height}");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // An applied edit rebuilds the selected resource's rows: the tree keeps the view-model's new selection rather than
    // dropping it (a virtualized tree loses the old row's selection).
    [Fact]
    public void An_applied_edit_keeps_the_selection() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-treesel").FullName;
        try
        {
            var path = Path.Combine(folder, "Edit.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 128, null, [1, 2, 3])));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Headless.Pump(open);
            Headless.Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Headless.Pump(model.PreviewTask);
            Render(window);

            model.BeginHexEditCommand.Execute(null);
            model.HexEdit!.TypeDigit(9);
            model.HexEdit.TypeDigit(9);
            model.ApplyHexEditCommand.Execute(null);
            Render(window);
            Render(window);

            var selected = Assert.IsType<ResourceNode>(model.Selected);
            Assert.Equal(0x99, selected.Resource.GetData().Span[0]);
            Assert.Same(selected, window.GetVisualDescendants().OfType<TreeView>().Single().SelectedItem);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
