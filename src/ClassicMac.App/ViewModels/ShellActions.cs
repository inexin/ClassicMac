using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>What the window does for the shell's commands: links, the About box and its own state. Tests replace it.</summary>
public interface IShell
{
    /// <summary>Opens a web page in the system's browser.</summary>
    void OpenUri(Uri uri);

    Task ShowAboutAsync(AboutInfo about);

    void Minimize();

    /// <summary>Maximizes the window, or restores it when maximized (Window ▸ Zoom).</summary>
    void ToggleZoom();

    /// <summary>Puts plain text on the clipboard (Details ▸ Copy all).</summary>
    Task CopyTextAsync(string text);
}

/// <summary>A screen depth for the toolbar's Depth select and View ▸ Screen Depth.</summary>
public sealed record ScreenDepthChoice(int Depth, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the About box shows (design/boards/main-window.md, S7).</summary>
public sealed record AboutInfo(string Name, string Version, string Description, string Licence, string Notices, Uri Repository)
{
    public static Uri RepositoryUri { get; } = new("https://github.com/inexin/ClassicMac");

    /// <summary>This build's: the version from the assembly, the licence and notices embedded from the repository.</summary>
    public static AboutInfo Current { get; } = Read();

    private static AboutInfo Read()
    {
        var assembly = typeof(AboutInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = version[..plus];                                   // without the source revision
        }

        return new AboutInfo("ClassicMac", version, "Reads, converts and edits classic Mac OS files, disk images and resource forks.",
            Text(assembly, "LICENSE"), Text(assembly, "THIRD-PARTY-NOTICES.md"), RepositoryUri);
    }

    private static string Text(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

// The shell's commands (S1, S2, S7): the title, zoom and depth, the theme, the Window and Help menus.
public sealed partial class ShellActions(IAppSelection appSelection, IAppServices appServices, IAppView appView) : ObservableObject
{
    private IShell? Shell => appServices.Shell;

    // ---- Title (S1) ----

    /// <summary>The selected input's name for the title bar, or null when nothing is open.</summary>
    public string? TitleFile => appSelection.Selected?.Input.BaseTitle;

    /// <summary>Whether the selected input has unsaved edits (the title's " •").</summary>
    public bool TitleUnsaved => appSelection.Selected?.Input is { } input && (input.HasVolumeChanges || EditActions.EditedFiles(input).Any(e => e.State.Session.IsDirty));

    /// <summary>The window's title: "Mac OS 9.hfv • — ClassicMac".</summary>
    public string WindowTitle => TitleFile is { } file ? $"{file}{(TitleUnsaved ? " •" : "")} — ClassicMac" : "ClassicMac";

    internal void NotifyTitle()
    {
        OnPropertyChanged(nameof(TitleFile));
        OnPropertyChanged(nameof(TitleUnsaved));
        OnPropertyChanged(nameof(WindowTitle));
    }

    // ---- Zoom and depth (S2, View menu) ----

    /// <summary>Whether the preview can be zoomed (images, folders, dialogs, menus): the toolbar's Zoom and Depth.</summary>
    public bool IsZoomable => appView.Preview.IsZoomable;

    // MainViewModel's zoom, preview or screen depth changed: the zoom commands and the depth choice follow.
    internal void ViewChanged()
    {
        OnPropertyChanged(nameof(IsZoomable));
        OnPropertyChanged(nameof(SelectedDepthChoice));
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        ActualSizeCommand.NotifyCanExecuteChanged();
    }

    private bool CanZoomIn() => IsZoomable && appView.Zoom < appView.Zooms[^1];

    private bool CanZoomOut() => IsZoomable && appView.Zoom > appView.Zooms[0];

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => appView.Zoom = appView.Zooms.First(z => z > appView.Zoom);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => appView.Zoom = appView.Zooms.Last(z => z < appView.Zoom);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ActualSize() => appView.Zoom = appView.Zooms[0];

    public IReadOnlyList<ScreenDepthChoice> ScreenDepthChoices { get; } =
        [new(1, "1-bit"), new(2, "2-bit"), new(4, "4-bit"), new(8, "8-bit (256)"), new(16, "16-bit"), new(32, "32-bit")];

    /// <summary>The chosen screen depth, as the Depth select shows it.</summary>
    public ScreenDepthChoice SelectedDepthChoice
    {
        get => ScreenDepthChoices.FirstOrDefault(c => c.Depth == appView.ScreenDepth) ?? ScreenDepthChoices[^1];
        set
        {
            if (value is not null)
            {
                appView.ScreenDepth = value.Depth;
            }
        }
    }

    [RelayCommand]
    private void SetScreenDepth(int depth) => appView.ScreenDepth = depth;

    // ---- Theme (View ▸ Theme) ----

    /// <summary>The theme, kept between sessions; the window applies it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
    private AppTheme theme = appServices.Settings.Load().Theme;

    public bool IsSystemTheme => Theme == AppTheme.System;

    public bool IsLightTheme => Theme == AppTheme.Light;

    public bool IsDarkTheme => Theme == AppTheme.Dark;

    [RelayCommand]
    private void SetTheme(AppTheme value) => Theme = value;

    partial void OnThemeChanged(AppTheme value) => appServices.Settings.Save(appServices.Settings.Load() with { Theme = value });

    // ---- Window menu ----

    [RelayCommand]
    private void Minimize() => Shell?.Minimize();

    [RelayCommand]
    private void ZoomWindow() => Shell?.ToggleZoom();

    /// <summary>Whether <paramref name="input"/> holds the selection (its Window menu item is checked).</summary>
    public bool IsSelectedInput(InputNode input) => ReferenceEquals(appSelection.Selected?.Input, input);

    private static bool CanShowInput(InputNode? input) => input is not null;

    /// <summary>Window ▸ an open input: the tree switches to it (asking about unapplied edits first).</summary>
    [RelayCommand(CanExecute = nameof(CanShowInput))]
    private async Task ShowInput(InputNode? input)
    {
        if (input is null)
        {
            return;
        }

        input.IsExpanded = true;
        appSelection.Selected = input;
        await appSelection.DraftTask;
    }

    // ---- Help menu ----

    [RelayCommand]
    private void OpenHelp() => Shell?.OpenUri(new Uri(AboutInfo.RepositoryUri + "#readme"));

    [RelayCommand]
    private void ReportProblem() => Shell?.OpenUri(new Uri(AboutInfo.RepositoryUri + "/issues/new"));

    [RelayCommand]
    private Task About() => Shell?.ShowAboutAsync(AboutInfo.Current) ?? Task.CompletedTask;
}
