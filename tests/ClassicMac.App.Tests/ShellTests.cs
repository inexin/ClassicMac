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
        Assert.False(model.ZoomInCommand.CanExecute(null));             // the input: no zoomable preview yet
        model.Selected = Resource(input, "ICN#");
        await model.PreviewTask;
        Assert.Equal(2, model.Zoom);                                      // small icons open enlarged
        Assert.True(model.IsZoomable);
        Assert.True(model.ZoomInCommand.CanExecute(null));
        model.ZoomInCommand.Execute(null);
        Assert.Equal(4, model.Zoom);
        model.ZoomInCommand.Execute(null);
        Assert.Equal(8, model.Zoom);
        Assert.False(model.ZoomInCommand.CanExecute(null));
        model.ZoomOutCommand.Execute(null);
        model.ZoomOutCommand.Execute(null);
        Assert.Equal(2, model.Zoom);
        model.ActualSizeCommand.Execute(null);
        Assert.Equal(1, model.Zoom);
        Assert.False(model.ZoomOutCommand.CanExecute(null));
        Assert.False(model.ActualSizeCommand.CanExecute(null));

        model.Selected = Resource(input, "TEXT");
        await model.PreviewTask;
        Assert.False(model.ZoomInCommand.CanExecute(null));
        Assert.False(model.ZoomOutCommand.CanExecute(null));
        Assert.False(model.ActualSizeCommand.CanExecute(null));
        Assert.False(model.IsZoomable);
    }

    [Fact]
    public void Screen_depths_have_labels_and_one_is_chosen()
    {
        var model = new MainViewModel();
        Assert.Equal(["1-bit", "2-bit", "4-bit", "8-bit (256)", "16-bit", "32-bit"], model.ScreenDepthChoices.Select(c => c.Label));
        Assert.Equal(32, model.SelectedDepthChoice.Depth);
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.SetScreenDepthCommand.Execute(8);
        Assert.Equal(8, model.ScreenDepth);
        Assert.Equal("8-bit (256)", model.SelectedDepthChoice.Label);
        Assert.Contains(nameof(MainViewModel.SelectedDepthChoice), changed);
        model.SelectedDepthChoice = model.ScreenDepthChoices[0];
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
        Assert.Equal(store.Settings.Theme, model.Theme);
        model.SetThemeCommand.Execute(theme);
        Assert.Equal(theme, model.Theme);
        Assert.Equal((theme == AppTheme.System, theme == AppTheme.Light, theme == AppTheme.Dark), (model.IsSystemTheme, model.IsLightTheme, model.IsDarkTheme));
        Assert.Equal(new AppSettings(GroupNoName: false, Theme: theme), store.Settings);  // the other settings kept

        model.TreeDisplay.HideInvisible = false;                                         // and the theme kept by them
        Assert.Equal(new AppSettings(GroupNoName: false, HideInvisible: false, Theme: theme), store.Settings);
    }

    [Fact]
    public void Theme_changes_are_announced()
    {
        var model = new MainViewModel();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.Theme = AppTheme.Dark;
        Assert.Equal([nameof(MainViewModel.Theme), nameof(MainViewModel.IsSystemTheme), nameof(MainViewModel.IsLightTheme), nameof(MainViewModel.IsDarkTheme)],
            changed.Where(n => n is not null && n.Contains("Theme", StringComparison.Ordinal)));
    }

    [Fact]
    public void Window_commands_go_to_the_window()
    {
        var shell = new FakeShell();
        var model = new MainViewModel { Shell = shell };
        model.MinimizeCommand.Execute(null);
        model.ZoomWindowCommand.Execute(null);
        model.ZoomWindowCommand.Execute(null);
        Assert.Equal((1, 2), (shell.Minimized, shell.Zoomed));
        new MainViewModel().MinimizeCommand.Execute(null);                // no window: nothing happens
    }

    [Fact]
    public async Task Show_input_selects_an_open_input_and_opens_it()
    {
        var model = new MainViewModel();
        var first = (await model.OpenAsync(Fork("One.rsrc")))!;
        var second = (await model.OpenAsync(Fork("Two.rsrc")))!;
        Assert.Same(second, model.Selected);
        first.IsExpanded = false;
        Assert.True(model.ShowInputCommand.CanExecute(first));
        Assert.False(model.ShowInputCommand.CanExecute(null));
        await model.ShowInputCommand.ExecuteAsync(first);
        Assert.Same(first, model.Selected);
        Assert.True(first.IsExpanded);
        Assert.True(model.IsSelectedInput(first));
        Assert.False(model.IsSelectedInput(second));
    }

    [Fact]
    public async Task Help_opens_the_readme_and_the_issue_form_and_about_shows_the_about_box()
    {
        var shell = new FakeShell();
        var model = new MainViewModel { Shell = shell };
        model.OpenHelpCommand.Execute(null);
        model.ReportProblemCommand.Execute(null);
        Assert.Equal([new Uri("https://github.com/inexin/ClassicMac#readme"), new Uri("https://github.com/inexin/ClassicMac/issues/new")], shell.Opened);
        await model.AboutCommand.ExecuteAsync(null);
        Assert.Same(AboutInfo.Current, Assert.Single(shell.Abouts));
        new MainViewModel().OpenHelpCommand.Execute(null);                // no window: nothing happens
        await new MainViewModel().AboutCommand.ExecuteAsync(null);
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
        Assert.Equal(new Uri("https://github.com/inexin/ClassicMac"), about.Repository);
    }

    [Fact]
    public async Task The_title_names_the_selected_input_and_marks_unsaved_edits()
    {
        var model = new MainViewModel();
        Assert.Null(model.TitleFile);
        Assert.Equal("ClassicMac", model.WindowTitle);
        var (_, input) = await Open(model);
        Assert.Equal("Icons.rsrc", model.TitleFile);
        Assert.False(model.TitleUnsaved);
        Assert.Equal("Icons.rsrc — ClassicMac", model.WindowTitle);

        model.Selected = Resource(input, "STR ");
        await model.PreviewTask;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await model.DeleteResourceCommand.ExecuteAsync(null);
        Assert.True(model.TitleUnsaved);
        Assert.Equal("Icons.rsrc • — ClassicMac", model.WindowTitle);
        Assert.Contains(nameof(MainViewModel.TitleUnsaved), changed);
        Assert.Contains(nameof(MainViewModel.WindowTitle), changed);
        await model.UndoCommand.ExecuteAsync(null);
        Assert.False(model.TitleUnsaved);

        model.Selected = null;
        Assert.Equal("ClassicMac", model.WindowTitle);
    }
}
