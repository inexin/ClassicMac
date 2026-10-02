using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    // The shell (S1, S7): the window's part of the shell commands, the Window menu and the theme.
    internal sealed partial class MainWindow
    {
        /// <summary>The About box while it is open (tests close it).</summary>
        internal Window? About { get; private set; }

        private void BindShell(MainViewModel model)
        {
            model.Shell = this;
            if (model.Theme != AppTheme.System)
            {
                ApplyTheme(model.Theme);
            }

            model.PropertyChanged += (_, e) => OnModelChanged(model, e);
            model.Roots.CollectionChanged += (_, _) => BuildWindowMenu(model);
            BuildWindowMenu(model);
        }

        private void OnModelChanged(MainViewModel model, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Theme))
            {
                ApplyTheme(model.Theme);
            }
            else if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                BuildWindowMenu(model);
            }
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
            WindowMenu.Items.Clear();
            WindowMenu.Items.Add(new MenuItem { Header = "_Minimize", Command = model.MinimizeCommand });
            WindowMenu.Items.Add(new MenuItem { Header = "_Zoom", Command = model.ZoomWindowCommand });
            if (model.Roots.Count == 0)
            {
                return;
            }

            WindowMenu.Items.Add(new Separator());
            foreach (var input in model.Roots)
            {
                WindowMenu.Items.Add(new MenuItem
                {
                    Header = input.BaseTitle,
                    Command = model.ShowInputCommand,
                    CommandParameter = input,
                    ToggleType = MenuItemToggleType.Radio,
                    GroupName = "inputs",
                    IsChecked = model.IsSelectedInput(input),
                });
            }
        }

        public void OpenUri(Uri uri) => _ = Launcher.LaunchUriAsync(uri);

        public async Task ShowAboutAsync(AboutInfo about)
        {
            About = AboutBox.Create(about, OpenUri);
            try
            {
                await About.ShowDialog(this);
            }
            finally
            {
                About = null;
            }
        }

        public void Minimize() => WindowState = WindowState.Minimized;

        public void ToggleZoom() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
