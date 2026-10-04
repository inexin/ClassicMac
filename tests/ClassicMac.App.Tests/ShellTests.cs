using ClassicMac.App.ViewModels;
using ClassicMac.Core;

namespace ClassicMac.App.Tests;

// The window's shell (design/boards/main-window.md): the title (S1), the toolbar's zoom and depth (S2), and the View,
// Window and Help menus with the About box (S7).
public sealed class ShellTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-shell").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class FakeShell : IShell
    {
        public List<Uri> Opened { get; } = [];

        public List<AboutInfo> Abouts { get; } = [];

        public int Minimized { get; private set; }

        public int Zoomed { get; private set; }

        public void OpenUri(Uri uri) => Opened.Add(uri);

        public Task ShowAboutAsync(AboutInfo about)
        {
            Abouts.Add(about);
            return Task.CompletedTask;
        }

        public void Minimize() => Minimized++;

        public void ToggleZoom() => Zoomed++;

        public Task CopyTextAsync(string text) => Task.CompletedTask;
    }

    private string Fork(string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, PreviewTests.Fork(("ICN#", 128, null, new byte[256]), ("TEXT", 128, null, "hello"u8.ToArray()), ("STR ", 128, null, [2, (byte)'h', (byte)'i'])));
        return path;
    }

    private static NodeViewModel Resource(InputNode input, string type) =>
        input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];

    private async Task<(MainViewModel Model, InputNode Input)> Open(MainViewModel? model = null)
    {
        model ??= new MainViewModel();
        var input = (await model.OpenAsync(Fork("Icons.rsrc")))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    [Fact]
    public async Task Zoom_in_out_and_actual_size_step_through_the_zooms_of_a_zoomable_preview()
    {
        var (model, input) = await Open();
        Assert.False(model.ShellActions.ZoomInCommand.CanExecute(null));             // the input: no zoomable preview yet
        model.Selected = Resource(input, "ICN#");
        await model.PreviewTask;
        Assert.Equal(4, model.Zoom);                                      // small icons open enlarged
        Assert.True(model.ShellActions.IsZoomable);
        Assert.True(model.ShellActions.ZoomInCommand.CanExecute(null));
        model.ShellActions.ZoomInCommand.Execute(null);
        Assert.Equal(8, model.Zoom);
        Assert.False(model.ShellActions.ZoomInCommand.CanExecute(null));
        model.ShellActions.ZoomOutCommand.Execute(null);
        model.ShellActions.ZoomOutCommand.Execute(null);
        Assert.Equal(2, model.Zoom);
        model.ShellActions.ActualSizeCommand.Execute(null);
        Assert.Equal(1, model.Zoom);
        Assert.False(model.ShellActions.ZoomOutCommand.CanExecute(null));
        Assert.False(model.ShellActions.ActualSizeCommand.CanExecute(null));

        model.Selected = Resource(input, "TEXT");
        await model.PreviewTask;
        Assert.False(model.ShellActions.ZoomInCommand.CanExecute(null));
        Assert.False(model.ShellActions.ZoomOutCommand.CanExecute(null));
        Assert.False(model.ShellActions.ActualSizeCommand.CanExecute(null));
        Assert.False(model.ShellActions.IsZoomable);
    }

    [Fact]
    public void Screen_depths_have_labels_and_one_is_chosen()
    {
        var model = new MainViewModel();
        Assert.Equal(["1-bit", "2-bit", "4-bit", "8-bit (256)", "16-bit", "32-bit"], model.ShellActions.ScreenDepthChoices.Select(c => c.Label));
        Assert.Equal(32, model.ShellActions.SelectedDepthChoice.Depth);
        var changed = new List<string?>();
        model.ShellActions.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.ShellActions.SetScreenDepthCommand.Execute(8);
        Assert.Equal(8, model.ScreenDepth);
        Assert.Equal("8-bit (256)", model.ShellActions.SelectedDepthChoice.Label);
        Assert.Contains(nameof(ShellActions.SelectedDepthChoice), changed);
        model.ShellActions.SelectedDepthChoice = model.ShellActions.ScreenDepthChoices[0];
        Assert.Equal(1, model.ScreenDepth);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.System)]
    public void The_theme_is_chosen_from_the_view_menu_and_remembered(AppTheme theme)
    {
        var store = new MemorySettingsStore(new AppSettings(GroupNoName: false, Theme: theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark));
        var model = new MainViewModel(store);
        Assert.Equal(store.Settings.Theme, model.ShellActions.Theme);
        model.ShellActions.SetThemeCommand.Execute(theme);
        Assert.Equal(theme, model.ShellActions.Theme);
        Assert.Equal((theme == AppTheme.System, theme == AppTheme.Light, theme == AppTheme.Dark), (model.ShellActions.IsSystemTheme, model.ShellActions.IsLightTheme, model.ShellActions.IsDarkTheme));
        Assert.Equal(new AppSettings(GroupNoName: false, Theme: theme), store.Settings);  // the other settings kept

        model.TreeDisplay.HideInvisible = false;                                         // and the theme kept by them
        Assert.Equal(new AppSettings(GroupNoName: false, HideInvisible: false, Theme: theme), store.Settings);
    }

    [Fact]
    public void Theme_changes_are_announced()
    {
        var model = new MainViewModel();
        var changed = new List<string?>();
        model.ShellActions.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.ShellActions.Theme = AppTheme.Dark;
        Assert.Equal([nameof(ShellActions.Theme), nameof(ShellActions.IsSystemTheme), nameof(ShellActions.IsLightTheme), nameof(ShellActions.IsDarkTheme)],
            changed.Where(n => n is not null && n.Contains("Theme", StringComparison.Ordinal)));
    }

    [Fact]
    public void Window_commands_go_to_the_window()
    {
        var shell = new FakeShell();
        var model = new MainViewModel();
        model.ShellActions.Shell = shell;
        model.ShellActions.MinimizeCommand.Execute(null);
        model.ShellActions.ZoomWindowCommand.Execute(null);
        model.ShellActions.ZoomWindowCommand.Execute(null);
        Assert.Equal((1, 2), (shell.Minimized, shell.Zoomed));
        new MainViewModel().ShellActions.MinimizeCommand.Execute(null);                // no window: nothing happens
    }

    [Fact]
    public async Task Show_input_selects_an_open_input_and_opens_it()
    {
        var model = new MainViewModel();
        var first = (await model.OpenAsync(Fork("One.rsrc")))!;
        var second = (await model.OpenAsync(Fork("Two.rsrc")))!;
        Assert.Same(second, model.Selected);
        first.IsExpanded = false;
        Assert.True(model.ShellActions.ShowInputCommand.CanExecute(first));
        Assert.False(model.ShellActions.ShowInputCommand.CanExecute(null));
        await model.ShellActions.ShowInputCommand.ExecuteAsync(first);
        Assert.Same(first, model.Selected);
        Assert.True(first.IsExpanded);
        Assert.True(model.ShellActions.IsSelectedInput(first));
        Assert.False(model.ShellActions.IsSelectedInput(second));
    }

    [Fact]
    public async Task Help_opens_the_readme_and_the_issue_form_and_about_shows_the_about_box()
    {
        var shell = new FakeShell();
        var model = new MainViewModel();
        model.ShellActions.Shell = shell;
        model.ShellActions.OpenHelpCommand.Execute(null);
        model.ShellActions.ReportProblemCommand.Execute(null);
        Assert.Equal([new Uri("https://github.com/inexin/ClassicMac#readme"), new Uri("https://github.com/inexin/ClassicMac/issues/new")], shell.Opened);
        await model.ShellActions.AboutCommand.ExecuteAsync(null);
        Assert.Same(AboutInfo.Current, Assert.Single(shell.Abouts));
        new MainViewModel().ShellActions.OpenHelpCommand.Execute(null);                // no window: nothing happens
        await new MainViewModel().ShellActions.AboutCommand.ExecuteAsync(null);
    }

    [Fact]
    public void About_names_the_version_the_licence_and_the_notices()
    {
        var about = AboutInfo.Current;
        Assert.Equal("ClassicMac", about.Name);
        Assert.Matches(@"^\d+\.\d+\.\d+", about.Version);
        Assert.Contains("classic Mac OS", about.Description, StringComparison.Ordinal);
        Assert.StartsWith("MIT License", about.Licence, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted", about.Licence, StringComparison.Ordinal);
        Assert.StartsWith("# Third-party notices", about.Notices, StringComparison.Ordinal);
        Assert.Contains("IBM Plex", about.Notices, StringComparison.Ordinal);
        Assert.Contains("SIL Open Font License", about.Notices, StringComparison.Ordinal);
        Assert.Contains("Type/Creator Database (TCDB) by Ilan Szekely, 1996–2003, https://www.lacikam.co.il/tcdb/", about.Notices, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://github.com/inexin/ClassicMac"), about.Repository);
    }

    [Fact]
    public async Task The_title_names_the_selected_input_and_marks_unsaved_edits()
    {
        var model = new MainViewModel();
        Assert.Null(model.ShellActions.TitleFile);
        Assert.Equal("ClassicMac", model.ShellActions.WindowTitle);
        var (_, input) = await Open(model);
        Assert.Equal("Icons.rsrc", model.ShellActions.TitleFile);
        Assert.False(model.ShellActions.TitleUnsaved);
        Assert.Equal("Icons.rsrc — ClassicMac", model.ShellActions.WindowTitle);

        model.Selected = Resource(input, "STR ");
        await model.PreviewTask;
        await model.InspectorActions.HeaderIconTask;
        // The selection's preview and header icon finish on the thread pool and raise PropertyChanged there: a
        // concurrent queue takes them, and the new selection's tasks are awaited before the names are looked at.
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.ShellActions.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);
        await model.DeleteResourceCommand.ExecuteAsync(null);
        await model.PreviewTask;
        await model.InspectorActions.HeaderIconTask;
        Assert.True(model.ShellActions.TitleUnsaved);
        Assert.Equal("Icons.rsrc • — ClassicMac", model.ShellActions.WindowTitle);
        Assert.Contains(nameof(ShellActions.TitleUnsaved), changed.ToArray());
        Assert.Contains(nameof(ShellActions.WindowTitle), changed.ToArray());
        await model.UndoCommand.ExecuteAsync(null);
        await model.PreviewTask;
        Assert.False(model.ShellActions.TitleUnsaved);

        model.Selected = null;
        Assert.Equal("ClassicMac", model.ShellActions.WindowTitle);
    }
}
