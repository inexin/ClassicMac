using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App
{
    internal sealed partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var model = new MainViewModel();
                desktop.MainWindow = new MainWindow { DataContext = model };
                foreach (var path in desktop.Args ?? []) _ = model.OpenAsync(path);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
