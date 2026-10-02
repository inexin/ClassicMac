using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

using static Headless;

// The tree pane at a narrow width: rows fit the viewport (no sideways scrolling), long names trim with an ellipsis
// while the "not read" chip and the meta stay whole, and the meta keeps clear of the vertical scroll bar.
public sealed class TreePaneTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-tree-pane").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A disk with 24 files (more than the pane shows) and a MacBinary file with a long name, not read when it opens.
    private string Disk()
    {
        var inner = new MacFile { Name = MacString.FromMacRoman("Inner"), DataFork = ForkData.FromBytes(new byte[] { 1 }) };
        var disk = new HfsBuilder { CatalogLeaves = 9 };
        disk.File(HfsBuilder.Root, "A long archive name here.bin", Files.Containers.MacBinaryWriter.ToArray(inner), []);
        for (var i = 0; i < 24; i++)
        {
            disk.File(HfsBuilder.Root, $"Document {i:00}", [2], [], type: "rohd", creator: "ddsk");
        }
        var path = Path.Combine(folder, "Narrow.img");
        File.WriteAllBytes(path, disk.Build("Narrow"));
        return path;
    }

    private static Rect BoundsIn(Visual visual, Visual relativeTo) =>
        new(visual.TranslatePoint(default, relativeTo)!.Value, visual.Bounds.Size);

    [Fact]
    public void Rows_fit_a_narrow_pane_with_the_meta_clear_of_the_scroll_bar() => OnUiThread(() =>
    {
        var model = new MainViewModel();
        model.TreeDisplay.ShowDetails = true;                                // the details column on: the meta must fit too
        var window = new MainWindow { DataContext = model };
        var baselines = new List<string>();
        window.Show();
        Pump(model.OpenAsync(Disk()));
        Pump(model.PreviewTask);
        var body = window.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 3);
        body.ColumnDefinitions[0].Width = new GridLength(240);
        Dispatcher.UIThread.RunJobs();

        Baselines.Check(window, "tree-narrow", baselines, Baselines.Variant.Light, Baselines.Variant.Scaled150);
        var tree = window.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "Tree");
        var scrollBar = tree.GetVisualDescendants().OfType<ScrollBar>().Single(s => s.Orientation == Orientation.Vertical);
        Assert.True(scrollBar.IsVisible);
        var scrollLeft = BoundsIn(scrollBar, tree).Left;
        Assert.DoesNotContain(tree.GetVisualDescendants().OfType<ScrollBar>(), s => s.Orientation == Orientation.Horizontal && s.IsVisible);
        var rows = tree.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("tree-row")).ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var bounds = BoundsIn(row, tree);
            Assert.True(bounds.Right <= scrollLeft + 0.5, $"{(row.DataContext as NodeViewModel)?.Name}: row ends at {bounds.Right}, the scroll bar starts at {scrollLeft}");
            var meta = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("meta"));
            if (meta.IsVisible)
            {
                Assert.True(BoundsIn(meta, tree).Right <= scrollLeft, $"meta ends at {BoundsIn(meta, tree).Right}");
                Assert.True(meta.DesiredSize.Width - meta.Margin.Left - meta.Margin.Right <= meta.Bounds.Width + 0.5, $"meta cut: {meta.Text}");
            }
        }

        // The long name trims; its chip and meta stay whole, the chip just left of the meta.
        var archive = rows.Single(r => r.DataContext is ContainerFileNode);
        var name = archive.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("name"));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, name.TextTrimming);
        var whole = new TextBlock { Text = ((NodeViewModel)archive.DataContext!).Name, FontSize = name.FontSize, FontFamily = name.FontFamily };
        whole.Measure(Size.Infinity);
        Assert.True(name.Bounds.Width < whole.DesiredSize.Width, "the name is trimmed");
        var chip = archive.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("not-read"));
        var archiveMeta = archive.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("meta"));
        Assert.True(BoundsIn(chip, tree).Right <= BoundsIn(archiveMeta, tree).Left + 0.5);
        Assert.True(BoundsIn(name, tree).Right <= BoundsIn(chip, tree).Left + 0.5);
        Assert.Equal(chip.DesiredSize.Width, chip.Bounds.Width + chip.Margin.Left, 0.5);
        window.Close();
        Baselines.Verify(baselines);
    });
}
