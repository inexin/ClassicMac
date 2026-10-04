using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.Services;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

using static Headless;

// Tree display ▸ Show details column (off by default): the rows' right-hand meta (type · creator, sizes, the input's
// format · size, the "No name" count) shows only when it is on; the choice is kept with the other display options.
public sealed class TreeDetailsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-details").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static IEnumerable<NodeViewModel> All(NodeViewModel node) => node.Children.SelectMany(c => All(c).Prepend(c));

    [Fact]
    public async Task The_details_column_is_off_by_default_and_follows_the_option()
    {
        var store = new MemorySettingsStore();
        var model = new MainViewModel(store);
        Assert.False(model.TreeDisplay.ShowDetails);
        var input = (await model.OpenAsync(TreeDisplayTests.Disk(folder)))!;
        var realmz = input.Children.OfType<FolderNode>().Single(f => f.Name == "Realmz");
        var app = realmz.Children.OfType<FileNode>().Single(f => f.Name == "Realmz");
        await app.EnsureLoadedAsync();
        var resource = app.Children.Single().Children.Single();
        var group = realmz.Children.OfType<NoNameGroupNode>().Single();
        NodeViewModel[] rows = [input, app, resource, group];
        Assert.All(rows, r => Assert.Null(r.Meta));

        var changed = new List<NodeViewModel>();
        foreach (var row in All(input).Prepend(input))
        {
            row.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NodeViewModel.Meta))
                {
                    changed.Add((NodeViewModel)s!);
                }
            };
        }
        model.TreeDisplay.ShowDetails = true;
        Assert.StartsWith("HFS volume · ", input.Meta);
        Assert.Equal("APPL · RLMZ", app.Meta);
        Assert.Equal("4 bytes", resource.Meta);
        Assert.Equal("4 files", group.Meta);
        Assert.All(rows, r => Assert.Contains(r, changed));
        Assert.True(store.Settings.ShowDetails);
        Assert.True(new MainViewModel(store).TreeDisplay.ShowDetails);           // kept for the next session

        model.TreeDisplay.ShowDetails = false;
        Assert.All(rows, r => Assert.Null(r.Meta));
        Assert.False(store.Settings.ShowDetails);
    }

    [Fact]
    public void Settings_compare_and_keep_the_option()
    {
        Assert.False(new AppSettings().ShowDetails);
        Assert.NotEqual(new AppSettings(), new AppSettings(ShowDetails: true));
        Assert.NotEqual(new AppSettings().GetHashCode(), new AppSettings(ShowDetails: true).GetHashCode());
        var path = Path.Combine(folder, "settings.json");
        new JsonSettingsStore(path).Save(new AppSettings(ShowDetails: true));
        Assert.True(new JsonSettingsStore(path).Load().ShowDetails);
    }

    [Fact]
    public void The_flyout_and_the_View_menu_switch_the_column() => OnUiThread(() =>
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var options = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TreeDisplayButton");
        options.Flyout!.ShowAt(options);
        Dispatcher.UIThread.RunJobs();
        var box = ((StackPanel)((Flyout)options.Flyout).Content!).Children.OfType<CheckBox>().Single(b => b.Content as string == "Show details column");
        Assert.False(box.IsChecked);
        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.TreeDisplay.ShowDetails);
        options.Flyout.Hide();

        var view = window.GetLogicalDescendantsOfType<MenuItem>().Single(m => m.Header as string == "_View");
        var item = view.Items.OfType<MenuItem>().Single(m => m.Header as string == "Show _Details Column");
        Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
        Assert.True(item.IsChecked);
        item.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.TreeDisplay.ShowDetails);
        window.Close();
    });
}

internal static class LogicalTreeHelpers
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
