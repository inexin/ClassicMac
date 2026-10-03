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
            var rows = window.FindControl<BrowseTree>("Tree")!.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.InRange(rows.Count, 10, 200);
            var last = rows.Single(r => ReferenceEquals(r.DataContext, many.Children[^1]));
            var tree = window.FindControl<BrowseTree>("Tree")!;
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

    // A folder of 100 files, an open folder of 5,000, then 200 more files: scrolling past the open folder to the end and
    // back, the scroll extent never changes (every row is one fixed height, made or not) and the first row on screen
    // comes back to the same node at the same place.
    internal static string Mixed(string folder)
    {
        var disk = new HfsBuilder { CatalogLeaves = 1400 };
        var top = disk.Folder(HfsBuilder.Root, "Top");
        for (var i = 0; i < 100; i++)
        {
            disk.File(top, $"A {i:D3}", [], []);
        }

        var big = disk.Folder(top, "Big");
        for (var i = 0; i < Files; i++)
        {
            disk.File(big, $"File {i:D4}", [], []);
        }

        for (var i = 0; i < 200; i++)
        {
            disk.File(top, $"Z {i:D3}", [], []);
        }

        var path = Path.Combine(folder, "mixed.img");
        File.WriteAllBytes(path, disk.Build("Mixed"));
        return path;
    }
    [Fact]
    public void Scrolling_down_and_back_keeps_the_extent_and_the_rows() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-treescroll").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Headless.Pump(model.OpenAsync(Mixed(folder)));
            var top = model.Roots[0].Children.OfType<FolderNode>().Single();
            top.IsExpanded = true;
            Render(window);
            var many = top.Children.OfType<FolderNode>().Single();
            many.IsExpanded = true;
            Render(window);
            var tree = window.FindControl<Control>("Tree")!;
            var scroller = tree.GetVisualDescendants().OfType<ScrollViewer>().First();

            (NodeViewModel Node, double Top) FirstRow()
            {
                var rows = tree.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("tree-row") && p.IsEffectivelyVisible)
                    .Select(p => (Node: (NodeViewModel)p.DataContext!, Top: p.TranslatePoint(default, scroller)!.Value.Y))
                    .Where(r => r.Top > -1).OrderBy(r => r.Top).ToList();
                return rows[0];
            }

            scroller.Offset = new Vector(0, 3000);
            Render(window);
            var extent = scroller.Extent.Height;
            Assert.Equal(22.0 * (2 + 100 + 1 + Files + 200), extent);                  // the volume, Top, A…, Big, its files, Z…
            var start = FirstRow();
            var extents = new List<double>();
            for (var y = 3000.0; y <= scroller.Extent.Height - scroller.Viewport.Height; y += 9370)
            {
                scroller.Offset = new Vector(0, y);
                Render(window);
                extents.Add(scroller.Extent.Height);
            }

            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Render(window);
            extents.Add(scroller.Extent.Height);
            Assert.Same(model.TreeRows[^1], tree.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("tree-row"))
                .OrderBy(p => p.TranslatePoint(default, scroller)!.Value.Y).Last().DataContext);
            for (var y = scroller.Offset.Y; y > 0; y -= 8110)
            {
                scroller.Offset = new Vector(0, y);
                Render(window);
                extents.Add(scroller.Extent.Height);
            }

            scroller.Offset = new Vector(0, 3000);
            Render(window);
            Assert.All(extents, e => Assert.Equal(extent, e));
            Assert.Equal(3000, scroller.Offset.Y);
            Assert.Equal(start, FirstRow());
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The tree's keys and its expander: Right opens a row, then goes to its first child; Left goes to the parent, then
    // closes it; + and − open and close; * opens every row below; a press on the expander opens or closes without
    // selecting, Alt+press every row below; a double click opens or closes. Home and End are the list's.
    [Fact]
    public void Keys_and_the_expander_open_and_close_rows() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-treekeys").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Headless.Pump(model.OpenAsync(Mixed(folder)));
            Render(window);
            var volume = model.Roots[0];
            var top = volume.Children.OfType<FolderNode>().Single();
            var big = top.Children.OfType<FolderNode>().Single();
            var tree = window.FindControl<BrowseTree>("Tree")!;
            void Key(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPressQwerty(key, modifiers);
                window.KeyReleaseQwerty(key, modifiers);
                Render(window);
            }

            model.Selected = top;
            Render(window);
            tree.ContainerFromItem(top)!.Focus();
            Assert.False(top.IsExpanded);
            Key(PhysicalKey.ArrowRight);
            Assert.True(top.IsExpanded);
            Key(PhysicalKey.ArrowRight);
            Assert.Same(top.Children[0], model.Selected);
            Key(PhysicalKey.ArrowLeft);
            Assert.Same(top, model.Selected);
            Key(PhysicalKey.ArrowLeft);
            Assert.False(top.IsExpanded);
            Key(PhysicalKey.NumPadAdd);
            Assert.True(top.IsExpanded);
            Key(PhysicalKey.NumPadSubtract);
            Assert.False(top.IsExpanded);
            Key(PhysicalKey.NumPadMultiply);
            Assert.True(top.IsExpanded && big.IsExpanded);
            Key(PhysicalKey.End);
            Assert.Same(model.TreeRows[^1], model.Selected);
            Key(PhysicalKey.Home);
            Assert.Same(volume, model.Selected);

            // The expander: a press on Top's closes it, Top not selected; Alt+press opens it and every row below.
            top.IsExpanded = false;
            big.IsExpanded = false;
            Render(window);
            Control Expander(NodeViewModel node) =>
                tree.ContainerFromItem(node)!.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tree-expander"));
            void Press(Control control, RawInputModifiers modifiers)
            {
                var at = control.TranslatePoint(new Point(6, 6), window)!.Value;
                window.MouseDown(at, MouseButton.Left, modifiers);
                window.MouseUp(at, MouseButton.Left, modifiers);
                Render(window);
            }

            Press(Expander(top), RawInputModifiers.Alt);
            Assert.True(top.IsExpanded && big.IsExpanded);
            Assert.Same(volume, model.Selected);
            Press(Expander(top), RawInputModifiers.None);
            Assert.False(top.IsExpanded);
            Assert.True(big.IsExpanded);                                              // the rows below keep their state
            Assert.Same(volume, model.Selected);

            // A double click on a row opens or closes it.
            var row = tree.ContainerFromItem(top)!;
            var middle = row.TranslatePoint(new Point(120, 10), window)!.Value;
            window.MouseDown(middle, MouseButton.Left);
            window.MouseUp(middle, MouseButton.Left);
            window.MouseDown(middle, MouseButton.Left);
            window.MouseUp(middle, MouseButton.Left);
            Render(window);
            Assert.True(top.IsExpanded);
            Assert.Same(top, model.Selected);
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
            var row = window.FindControl<BrowseTree>("Tree")!.GetVisualDescendants().OfType<ListBoxItem>().Single(r => ReferenceEquals(r.DataContext, model.Selected));
            var tree = window.FindControl<BrowseTree>("Tree")!;
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
            Assert.Same(selected, window.FindControl<BrowseTree>("Tree")!.SelectedItem);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
