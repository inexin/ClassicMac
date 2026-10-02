using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

using static Headless;

// The empty state (S5) and the tree's filter and type-ahead (S6) in the window, headless.
public sealed class SearchWindowTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-search-window").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static IEnumerable<string> Texts(Visual visual) =>
        visual.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Run>().Select(r => r.Text) ?? []));

    [Fact]
    public void The_empty_state_shows_the_drop_zone_and_recent_files() => OnUiThread(() =>
    {
        var kept = Path.Combine(folder, "Kept.rsrc");
        File.WriteAllBytes(kept, new Resources.ResourceFork().ToArray());
        var gone = Path.Combine(folder, "Old Disk.img");
        var model = new MainViewModel(new MemorySettingsStore(new AppSettings(RecentFiles: [kept, gone])));
        var window = new MainWindow { DataContext = model };
        var baselines = new List<string>();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var empty = Named<Border>(window, "EmptyState");
        Assert.True(empty.IsVisible);
        Assert.Contains("Drop a Mac file, disk image or archive here", Texts(empty));
        Assert.Contains("Kept.rsrc", Texts(empty));
        Assert.Contains("Not found", Texts(empty));
        // The diagnostics collapsed with "Nothing opened yet"; the status bar says "Ready".
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Nothing opened yet" && t.IsEffectivelyVisible);
        Assert.Contains(Named<Border>(window, "StatusBar").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ready" && t.IsEffectivelyVisible);

        // The baseline shows fixed paths (the temporary folder's name changes from run to run).
        model.RecentFiles.Insert(0, new RecentFile(@"C:\Mac\Mac OS 9.hfv", true));
        model.RecentFiles.Insert(1, new RecentFile(@"C:\Mac\Archives\Tools.sit", true));
        var shown = model.RecentFiles.Skip(2).ToList();
        foreach (var file in shown)
        {
            model.RecentFiles.Remove(file);
        }
        model.RecentFiles.Add(new RecentFile(@"D:\Old\Games.img", false));
        Dispatcher.UIThread.RunJobs();
        Baselines.Check(window, "empty-state", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        model.RecentFiles.Clear();
        foreach (var file in shown)
        {
            model.RecentFiles.Add(file);
        }
        Dispatcher.UIThread.RunJobs();

        // A drag over the window marks the drop zone.
        var zone = Named<Border>(window, "DropZone");
        model.IsDropTarget = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("active", zone.Classes);
        model.IsDropTarget = false;

        // A click on a recent file opens it; the empty state goes.
        var rows = empty.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("recent")).ToList();
        Assert.Equal(2, rows.Count);
        // The open runs in the background: wait for it, then for the preview it starts.
        Pump(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<RecentFile?>)rows[0].Command!).ExecuteAsync((RecentFile?)rows[0].CommandParameter));
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(model.Roots);
        Assert.False(empty.IsVisible);
        window.Close();
        Baselines.Verify(baselines);
    });

    [Fact]
    public void Clear_list_empties_the_recent_files() => OnUiThread(() =>
    {
        var model = new MainViewModel(new MemorySettingsStore(new AppSettings(RecentFiles: [Path.Combine(folder, "A.img")])));
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var clear = Named<Border>(window, "EmptyState").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Clear list");
        clear.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(model.RecentFiles);
        Assert.False(Named<Control>(window, "RecentList").IsVisible);
        window.Close();
    });

    // Volume.img from TreeSearchTests' shape: Games (Read Me, Realmz) and Manual.
    private MainViewModel Opened(MainWindow window)
    {
        var disk = new Files.Tests.HfsBuilder();
        var games = disk.Folder(Files.Tests.HfsBuilder.Root, "Games");
        disk.File(games, "Realmz", [1], [], type: "APPL", creator: "RLMZ");
        disk.File(games, "Read Me", "hello"u8.ToArray(), []);
        disk.File(Files.Tests.HfsBuilder.Root, "Manual", [2], []);
        var path = Path.Combine(folder, "Volume.img");
        File.WriteAllBytes(path, disk.Build("Volume"));
        var model = (MainViewModel)window.DataContext!;
        window.Show();
        Pump(model.OpenAsync(path));
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        return model;
    }

    [Fact]
    public void Ctrl_F_filters_the_tree_and_Esc_clears_it() => OnUiThread(() =>
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        var model = Opened(window);
        var filter = Named<TextBox>(window, "TreeFilter");
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs();
        Assert.True(filter.IsFocused);
        window.KeyTextInput("rea");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("rea", model.FilterText);
        var tree = Named<TreeView>(window, "Tree");
        var manual = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(i => i.DataContext is FileNode { Name: "Manual" });
        Assert.False(manual.IsVisible);
        var readMe = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(i => i.DataContext is FileNode { Name: "Read Me" });
        Assert.True(readMe.IsVisible);
        Assert.Contains(readMe.GetVisualDescendants().OfType<TextBlock>().SelectMany(t => t.Inlines?.OfType<Run>() ?? []),
            r => r.Text == "Rea" && r.Classes.Contains("match") && r.Classes.Contains("on"));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", model.FilterText);
        Assert.True(manual.IsVisible);
        window.Close();
    });

    [Fact]
    public void Typing_in_the_tree_opens_the_type_ahead_pill() => OnUiThread(() =>
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        var baselines = new List<string>();
        var model = Opened(window);
        var tree = Named<TreeView>(window, "Tree");
        tree.GetVisualDescendants().OfType<TreeViewItem>().First().Focus();
        Dispatcher.UIThread.RunJobs();
        var pill = Named<Border>(window, "TypeAheadPill");
        Assert.False(pill.IsVisible);

        window.KeyTextInput("re");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("re", model.TypeAheadText);
        Assert.True(pill.IsVisible);
        Assert.Contains("1 of 2 loaded matches", Texts(pill));
        Assert.IsType<FileNode>(model.Selected);
        Assert.Equal("Read Me", model.Selected!.Name);
        Baselines.Check(window, "type-ahead", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);

        window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Realmz", model.Selected!.Name);
        window.KeyPress(Key.F3, RawInputModifiers.Shift, PhysicalKey.F3, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Read Me", model.Selected!.Name);
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("r", model.TypeAheadText);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.IsTypeAheadOpen);
        Assert.False(pill.IsVisible);
        window.Close();
        Baselines.Verify(baselines);
    });
}
