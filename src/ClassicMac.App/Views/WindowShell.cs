using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The shell (S1, S7): the window's part of the shell commands, the Window menu and the theme.
internal sealed class WindowShell(Window window, MenuItem windowMenu) : IShell
{
    /// <summary>The About box while it is open (tests close it).</summary>
    internal Window? About { get; private set; }

    /// <summary>Makes this the model's shell, and follows its theme and inputs.</summary>
    public void Bind(MainViewModel model)
    {
        model.ShellActions.Shell = this;
        if (model.ShellActions.Theme != AppTheme.System)
        {
            ApplyTheme(model.ShellActions.Theme);
        }

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                BuildWindowMenu(model);
            }
        };
        model.ShellActions.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellActions.Theme))
            {
                ApplyTheme(model.ShellActions.Theme);
            }
        };
        model.Roots.CollectionChanged += (_, _) => BuildWindowMenu(model);
        BuildWindowMenu(model);
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    // Window ▸ Minimize, Zoom, then one item per open input (checked: the one holding the selection).
    private void BuildWindowMenu(MainViewModel model)
    {
        windowMenu.Items.Clear();
        windowMenu.Items.Add(new MenuItem { Header = "_Minimize", Command = model.ShellActions.MinimizeCommand });
        windowMenu.Items.Add(new MenuItem { Header = "_Zoom", Command = model.ShellActions.ZoomWindowCommand });
        if (model.Roots.Count == 0)
        {
            return;
        }

        windowMenu.Items.Add(new Separator());
        foreach (var input in model.Roots)
        {
            windowMenu.Items.Add(new MenuItem
            {
                Header = input.BaseTitle,
                Command = model.ShellActions.ShowInputCommand,
                CommandParameter = input,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "inputs",
                IsChecked = model.ShellActions.IsSelectedInput(input),
            });
        }
    }

    public void OpenUri(Uri uri) => _ = window.Launcher.LaunchUriAsync(uri);

    public async Task ShowAboutAsync(AboutInfo about)
    {
        About = AboutBox.Create(about, OpenUri);
        try
        {
            await About.ShowDialog(window);
        }
        finally
        {
            About = null;
        }
    }

    public void Minimize() => window.WindowState = WindowState.Minimized;

    public Task CopyTextAsync(string text) => window.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    public void ToggleZoom() => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
