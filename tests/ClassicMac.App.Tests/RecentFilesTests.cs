using ClassicMac.App.Services;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The empty state (design/boards/empty-state.md, S5): shown while nothing is open, with the recent files, which persist,
// open on click, can be cleared, and show (then drop) files no longer found.
public sealed class RecentFilesTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-recent").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A raw resource fork file (opens as one input).
    private string Fork(string name)
    {
        var path = Path.Combine(folder, name);
        var fork = new ResourceFork();
        fork.Add(new Resource(Core.FourCC.FromString("STR "), 128, new byte[] { 1, (byte)'a' }));
        File.WriteAllBytes(path, fork.ToArray());
        return path;
    }

    [Fact]
    public async Task The_empty_state_shows_while_nothing_is_open()
    {
        var model = new MainViewModel();
        var changes = new System.Collections.Concurrent.ConcurrentQueue<string?>();  // background preview tasks raise on the thread pool
        model.EmptyState.PropertyChanged += (_, e) => changes.Enqueue(e.PropertyName);
        Assert.True(model.EmptyState.IsEmpty);
        var input = (await model.OpenAsync(Fork("A.rsrc")))!;
        await model.PreviewTask;
        Assert.False(model.EmptyState.IsEmpty);
        Assert.Contains(nameof(EmptyState.IsEmpty), changes);
        model.Selected = input;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.True(model.EmptyState.IsEmpty);
    }

    [Fact]
    public async Task Opened_files_go_to_the_top_of_the_list_which_keeps_ten_and_persists()
    {
        var store = new MemorySettingsStore();
        var model = new MainViewModel(store);
        Assert.Empty(model.EmptyState.RecentFiles);
        Assert.False(model.EmptyState.HasRecentFiles);
        var paths = Enumerable.Range(0, 12).Select(i => Fork($"F{i}.rsrc")).ToList();
        foreach (var path in paths)
        {
            await model.OpenAsync(path);
        }

        await model.OpenAsync(paths[5]);                                   // again: moves to the top, no duplicate

        Assert.True(model.EmptyState.HasRecentFiles);
        Assert.Equal([paths[5], paths[11], paths[10], paths[9], paths[8], paths[7], paths[6], paths[4], paths[3], paths[2]],
            model.EmptyState.RecentFiles.Select(r => r.Path));
        Assert.Equal(model.EmptyState.RecentFiles.Select(r => r.Path), store.Settings.RecentFiles);
        var first = model.EmptyState.RecentFiles[0];
        Assert.Equal(("F5.rsrc", folder, true, TreeIconKind.Document), (first.Name, first.Folder, first.Exists, first.IconKind));

        // A new session reads the list back.
        var again = new MainViewModel(store);
        Assert.Equal(model.EmptyState.RecentFiles.Select(r => r.Path), again.EmptyState.RecentFiles.Select(r => r.Path));
        Assert.True(again.EmptyState.RecentFiles.All(r => r.Exists));
    }

    [Fact]
    public async Task Unreadable_files_are_not_added()
    {
        var model = new MainViewModel();
        await model.OpenAsync(Path.Combine(folder, "missing.img"));
        Assert.Empty(model.EmptyState.RecentFiles);
    }

    [Fact]
    public async Task A_recent_file_opens_on_click()
    {
        var path = Fork("A.rsrc");
        var model = new MainViewModel(new MemorySettingsStore(new AppSettings(RecentFiles: [path])));
        await model.EmptyState.OpenRecentCommand.ExecuteAsync(model.EmptyState.RecentFiles[0]);
        Assert.Equal(path, Assert.Single(model.Roots).Path);
        Assert.Same(model.Roots[0], model.Selected);
    }

    [Fact]
    public async Task Missing_files_show_as_not_found_and_are_removed_on_click()
    {
        var kept = Fork("Kept.rsrc");
        var gone = Path.Combine(folder, "Gone.img");
        var store = new MemorySettingsStore(new AppSettings(RecentFiles: [gone, kept]));
        var model = new MainViewModel(store);
        Assert.Equal([false, true], model.EmptyState.RecentFiles.Select(r => r.Exists));

        await model.EmptyState.OpenRecentCommand.ExecuteAsync(model.EmptyState.RecentFiles[0]);
        Assert.Empty(model.Roots);
        Assert.Equal([kept], model.EmptyState.RecentFiles.Select(r => r.Path));
        Assert.Equal([kept], store.Settings.RecentFiles);
        Assert.Contains("Gone.img", model.Status);
        Assert.Contains("not found", model.Status);

        // A file that went missing while the app ran shows once the empty state comes back.
        var input = (await model.OpenAsync(kept))!;
        File.Delete(kept);
        model.Selected = input;
        await model.CloseCommand.ExecuteAsync(null);
        Assert.False(model.EmptyState.RecentFiles[0].Exists);
    }

    [Fact]
    public void The_list_can_be_cleared()
    {
        var store = new MemorySettingsStore(new AppSettings(RecentFiles: [Fork("A.rsrc")]));
        var model = new MainViewModel(store);
        model.EmptyState.ClearRecentCommand.Execute(null);
        Assert.Empty(model.EmptyState.RecentFiles);
        Assert.False(model.EmptyState.HasRecentFiles);
        Assert.Empty(store.Settings.RecentFiles);
    }

    [Fact]
    public void Changing_a_display_option_keeps_the_list()
    {
        var path = Fork("A.rsrc");
        var store = new MemorySettingsStore(new AppSettings(RecentFiles: [path]));
        var model = new MainViewModel(store);
        model.TreeDisplay.GroupNoName = false;
        Assert.Equal(new AppSettings(GroupNoName: false, RecentFiles: [path]), store.Settings);
    }

    [Theory]
    [InlineData("Disk.img", TreeIconKind.HardDisk)]
    [InlineData("Mac OS 9.hfv", TreeIconKind.HardDisk)]
    [InlineData("game.dsk", TreeIconKind.HardDisk)]
    [InlineData("Install.dmg", TreeIconKind.HardDisk)]
    [InlineData("tools.sit", TreeIconKind.Parcel)]
    [InlineData("tools.SEA", TreeIconKind.Parcel)]
    [InlineData("old.cpt", TreeIconKind.Parcel)]
    [InlineData("files.zip", TreeIconKind.Parcel)]
    [InlineData("Prefs.bin", TreeIconKind.Document)]
    [InlineData("Read Me", TreeIconKind.Document)]
    public void Recent_files_show_an_icon_for_their_kind(string name, TreeIconKind kind) =>
        Assert.Equal(kind, new RecentFile(Path.Combine("x", name), true).IconKind);

    [Fact]
    public void Settings_compare_their_recent_files_by_value()
    {
        Assert.Equal(new AppSettings(RecentFiles: ["a", "b"]), new AppSettings(RecentFiles: ["a", "b"]));
        Assert.Equal(new AppSettings(RecentFiles: ["a"]).GetHashCode(), new AppSettings(RecentFiles: ["a"]).GetHashCode());
        Assert.NotEqual(new AppSettings(RecentFiles: ["a"]), new AppSettings(RecentFiles: ["b"]));
        Assert.Equal(new AppSettings(), new AppSettings(RecentFiles: []));
        Assert.Empty(new AppSettings().RecentFiles);
        Assert.NotEqual(new AppSettings(), new AppSettings(HideInvisible: false));
    }

    [Fact]
    public void Recent_files_are_kept_as_json()
    {
        var path = Path.Combine(folder, "settings", "settings.json");
        new JsonSettingsStore(path).Save(new AppSettings(RecentFiles: [@"C:\Mac\Disk.img"]));
        Assert.Equal([@"C:\Mac\Disk.img"], new JsonSettingsStore(path).Load().RecentFiles);
        File.WriteAllText(path, """{ "groupNoName": false }""");                // written before the list existed
        Assert.Equal(new AppSettings(GroupNoName: false), new JsonSettingsStore(path).Load());
    }

    [Fact]
    public void A_drag_over_the_window_marks_the_drop_zone()
    {
        var model = new MainViewModel();
        Assert.False(model.EmptyState.IsDropTarget);
        model.EmptyState.IsDropTarget = true;
        Assert.True(model.EmptyState.IsDropTarget);
    }

    [Fact]
    public async Task With_nothing_open_the_diagnostics_are_collapsed_and_the_status_is_Ready()
    {
        var model = new MainViewModel();
        var panel = model.DiagnosticsPanel;
        Assert.Equal("Ready", model.Status);
        Assert.False(panel.IsExpanded);
        Assert.Equal("Nothing opened yet", panel.Placeholder);

        var input = (await model.OpenAsync(Fork("A.rsrc")))!;               // something open: the panel opens
        Assert.True(panel.IsExpanded);
        Assert.Null(panel.Placeholder);
        Assert.NotEqual("Ready", model.Status);

        model.Selected = input;
        await model.CloseCommand.ExecuteAsync(null);                         // all closed: back as it was
        Assert.False(panel.IsExpanded);
        Assert.Equal("Nothing opened yet", panel.Placeholder);
        Assert.Equal("Ready", model.Status);
    }

    [Fact]
    public async Task A_panel_the_user_opened_or_closed_stays_so()
    {
        var model = new MainViewModel();
        var panel = model.DiagnosticsPanel;
        panel.IsExpanded = true;                                             // opened while empty: kept open
        var input = (await model.OpenAsync(Fork("A.rsrc")))!;
        Assert.True(panel.IsExpanded);
        panel.IsExpanded = false;                                            // closed with a file open
        model.Selected = input;
        await model.CloseCommand.ExecuteAsync(null);
        await model.OpenAsync(Fork("B.rsrc"));
        Assert.False(panel.IsExpanded);                                      // the user's choice stands
    }

    [Fact]
    public async Task A_diagnostic_while_nothing_is_open_opens_the_panel()
    {
        var model = new MainViewModel();
        await model.OpenAsync(Path.Combine(folder, "missing.img"));          // unreadable: reported, nothing opened
        Assert.True(model.EmptyState.IsEmpty);
        Assert.True(model.DiagnosticsPanel.IsExpanded);
        Assert.Null(model.DiagnosticsPanel.Placeholder);
    }
}
