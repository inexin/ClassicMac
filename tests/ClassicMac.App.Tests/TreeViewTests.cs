using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.Tests;

// The browse tree's rows drawn (design/boards/browse-tree.md): pixel icons 1:1, icons resolved only for rows on
// screen, and the row states.
public class TreeViewTests
{
    private static void OnUiThread(Action test) => Headless.OnUiThread(test);

    // A row's name, held in runs (before, matched letters, after) for the search highlight.
    private static string NameOf(TextBlock name) => string.Concat(name.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    private static void Pump(Task task) => Headless.Pump(task);

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, key)!;

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    // The row of a node: its TreeViewItem's header panel.
    private static Panel Row(Window window, NodeViewModel node) =>
        window.GetVisualDescendants().OfType<TreeViewItem>().Single(i => ReferenceEquals(i.DataContext, node))
            .GetVisualDescendants().OfType<Panel>().First(p => p.Classes.Contains("tree-row"));

    private static T Part<T>(Panel row, string @class) where T : Control =>
        row.GetVisualDescendants().OfType<T>().First(c => c.Classes.Contains(@class));

    private static (uint[] Pixels, int Width) Frame(TopLevel window)
    {
        var frame = window.CaptureRenderedFrame()!;
        using var png = new MemoryStream();
#pragma warning disable CS0618
        frame.Save(png);
#pragma warning restore CS0618
        var decoded = Baselines.Decode(png.ToArray());
        return (decoded.Pixels, decoded.Size.Width);
    }

    // A kind's icon at 100%, 125%, 150% and 200%: one device pixel per Mac pixel up to 150%, centred in the 16-DIP slot;
    // two at 200%. Transparent pixels show the window behind.
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(1.25, 1)]
    [InlineData(1.5, 1)]
    [InlineData(2.0, 2)]
    public void Kind_icons_are_drawn_one_to_one(double scaling, int k) => OnUiThread(() =>
    {
        var icon = new TreeIcon { Node = new FolderNode(null!, "F"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 100, Height = 60, Background = Brushes.Red, Content = new Border { Padding = new Thickness(10.3, 7.7, 0, 0), Child = icon } };
        window.Show();
        window.SetRenderScaling(scaling);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new Size(16, 16), icon.Bounds.Size);

        var (pixels, width) = Frame(window);
        var art = TreeIcons.Pixels(TreeIconKind.Folder);
        // The first opaque art pixel is (2, 2); find where it landed.
        var first = Array.FindIndex(pixels, p => p != 0xFFFF0000);
        var (x0, y0) = (first % width - 2 * k, first / width - 2 * k);
        for (var y = 0; y < 16 * k; y++)
        {
            for (var x = 0; x < 16 * k; x++)
            {
                var a = art[y / k * 16 + x / k];
                var expected = a >> 24 == 0 ? 0xFFFF0000 : a;
                Assert.True(pixels[(y0 + y) * width + x0 + x] == expected, $"({x}, {y}) at {scaling:P0}: {pixels[(y0 + y) * width + x0 + x]:X8}, expected {expected:X8}");
            }
        }
        // Centred in the slot: as many device pixels left as right, give or take one.
        var slot = (int)Math.Round(16 * scaling);
        var left = (int)Math.Round(icon.TranslatePoint(default, window)!.Value.X * scaling);
        Assert.InRange(x0 - left, (slot - 16 * k) / 2 - 1, (slot - 16 * k) / 2 + 1);
        window.Close();
    });

    // The node's own icon, once it has one, replaces the kind's.
    [Fact]
    public void A_node_s_own_icon_replaces_the_kind_icon() => OnUiThread(() =>
    {
        var node = new FolderNode(null!, "F");
        var icon = new TreeIcon { Node = node };
        var window = new Window { Width = 40, Height = 40, Background = Brushes.Red, Content = icon };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var green = Enumerable.Range(0, 256).SelectMany(_ => new byte[] { 0, 255, 0, 255 }).ToArray();
        node.IconPng = PngEncoder.Instance.Encode(16, 16, green);
        Dispatcher.UIThread.RunJobs();
        var (pixels, _) = Frame(window);
        Assert.Equal(256, pixels.Count(p => p == 0xFF00FF00));
        window.Close();
    });

    [Fact]
    public void Every_kind_has_16_by_16_art_with_a_black_outline()
    {
        foreach (var kind in Enum.GetValues<TreeIconKind>().Where(k => k != TreeIconKind.Loading))
        {
            var pixels = TreeIcons.Pixels(kind);
            Assert.Equal(256, pixels.Length);
            Assert.Contains(0xFF000000u, pixels);
            Assert.Contains(pixels, p => p >> 24 == 0);
        }
        Assert.False(TreeIcons.Art.ContainsKey(TreeIconKind.Loading));
    }

    // Lets layout, rendering and the background icon work finish.
    private static void Settle(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Thread.Sleep(20);
        }
    }

    // A folder of many files expanded: only the rows on screen resolve their Finder icons.
    [Fact]
    public void Only_rows_on_screen_resolve_their_icons() => OnUiThread(() =>
    {
        const int Files = 5000;
        var folder = Directory.CreateTempSubdirectory("classicmac-many-").FullName;
        try
        {
            var path = System.IO.Path.Combine(folder, "many.tar");
            using (var stream = File.Create(path))
            using (var tar = new System.Formats.Tar.TarWriter(stream, System.Formats.Tar.TarEntryFormat.Pax))
            {
                for (var i = 0; i < Files; i++)
                {
                    tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"Many/File {i:D4}") { DataStream = new MemoryStream([1]) });
                }
            }

            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(path));
            var node = model.Roots[0].Children.OfType<FolderNode>().Single();
            node.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            var files = node.Children.OfType<FileNode>().ToList();
            Assert.Equal(Files, files.Count);
            Settle(window);

            // The rows on screen asked (the tree is about 25 rows high); none of the thousands below did.
            var resolved = NodeViewModel.ResolvedIcons(files[0]);
            Assert.InRange(resolved, 10, 40);
            Assert.All(files.Skip(60), f => Assert.Null(f.IconPng));

            // Scrolled to the end: the rows now on screen ask too, and only they.
            var scroller = window.GetVisualDescendants().OfType<TreeView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Settle(window);
            Assert.InRange(NodeViewModel.ResolvedIcons(files[0]) - resolved, 10, 40);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The states (T5): the unsaved mark in CmAccent SemiBold, the "not read" chip, the meta in CmTextMuted; in the
    // selected row of the focused tree, meta and mark in CmSelectionText, but not in the rows nested under it.
    [Fact]
    public void Rows_show_their_states() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-rowstates-").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(TreeRowTests.Disk(folder)));
            var input = model.Roots[0];
            var containers = input.Children.OfType<FolderNode>().Single();
            containers.IsExpanded = true;
            var app = input.Children.OfType<FileNode>().Single(f => f.Title == "App");
            app.Title = app.BaseTitle + " •";
            Dispatcher.UIThread.RunJobs();

            var appRow = Row(window, app);
            var mark = Part<TextBlock>(appRow, "unsaved");
            Assert.True(mark.IsVisible);
            Assert.Equal(FontWeight.SemiBold, mark.FontWeight);
            Assert.Equal(ColorOf(Brush("CmAccent")), ColorOf(mark.Foreground));
            Assert.Equal("App", NameOf(Part<TextBlock>(appRow, "name")));
            Assert.Equal(ColorOf(Brush("CmTextMuted")), ColorOf(Part<TextBlock>(appRow, "meta").Foreground));
            Assert.Equal("APPL · ABCD", Part<TextBlock>(appRow, "meta").Text);

            var zip = containers.Children.OfType<ContainerFileNode>().Single(c => c.Title.StartsWith("Files.zip", StringComparison.Ordinal));
            // CmRowTree high, CmTreeIndent per level.
            double X(NodeViewModel n) => Row(window, n).TranslatePoint(default, window)!.Value.X;
            Assert.Equal(16, X(zip) - X(containers), 6);
            Assert.Equal(16, X(containers) - X(input), 6);
            Assert.Equal(22, window.GetVisualDescendants().OfType<TreeViewItem>().Single(i => ReferenceEquals(i.DataContext, app))
                .GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot").Bounds.Height, 6);
            Assert.True(Part<Panel>(Row(window, zip), "not-read").IsVisible);
            Assert.False(Part<Panel>(appRow, "not-read").IsVisible);
            Assert.False(Part<Rectangle>(appRow, "drag-outline").IsVisible);
            app.IsDragSource = true;
            Assert.True(Part<Rectangle>(appRow, "drag-outline").IsVisible);
            app.IsDragSource = false;

            // Selected in the focused tree: its own meta and mark turn CmSelectionText; the nested rows' meta stays muted.
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            TreeViewItem Item(NodeViewModel n) => window.GetVisualDescendants().OfType<TreeViewItem>().Single(i => ReferenceEquals(i.DataContext, n));
            model.Selected = app;
            Item(app).Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(tree.IsKeyboardFocusWithin, "tree focused");
            Assert.Equal(ColorOf(Brush("CmSelectionText")), ColorOf(Part<TextBlock>(appRow, "meta").Foreground));
            Assert.Equal(ColorOf(Brush("CmSelectionText")), ColorOf(mark.Foreground));
            model.Selected = input;
            Item(input).Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ColorOf(Brush("CmSelectionText")), ColorOf(Part<TextBlock>(Row(window, input), "meta").Foreground));
            Assert.Equal(ColorOf(Brush("CmTextMuted")), ColorOf(Part<TextBlock>(appRow, "meta").Foreground));
            Assert.Equal(ColorOf(Brush("CmAccent")), ColorOf(mark.Foreground));
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The placeholder of a node not loaded yet: a spinner in place of the icon and "Loading…" in muted italics.
    [Fact]
    public void The_loading_placeholder_shows_a_spinner() => OnUiThread(() =>
    {
        var parent = new FolderNode(null!, "Parent");
        var loading = new LoadingNode(parent);
        parent.Children.Add(loading);
        parent.IsExpanded = true;
        var main = new MainWindow();
        var tree = new TreeView { Classes = { "browse" }, ItemTemplate = main.FindControl<TreeView>("Tree")!.ItemTemplate, ItemsSource = new[] { parent } };
        tree.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<TreeViewItem>(x))
        {
            Setters = { new Avalonia.Styling.Setter(TreeViewItem.IsExpandedProperty, true) },
        });
        var window = new Window { Width = 300, Height = 200, Content = tree };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var row = Row(window, loading);
        Assert.True(Part<Panel>(row, "spinner").IsVisible);
        Assert.False(row.GetVisualDescendants().OfType<TreeIcon>().Single().IsVisible);
        var name = Part<TextBlock>(row, "name");
        Assert.Equal(("Loading…", FontStyle.Italic), (NameOf(name), name.FontStyle));
        Assert.Equal(ColorOf(Brush("CmTextMuted")), ColorOf(name.Foreground));
        Assert.False(Part<TextBlock>(row, "meta").IsVisible); // no counts
        Assert.False(Part<Panel>(Row(window, parent), "spinner").IsVisible);
        window.Close();
    });
}
