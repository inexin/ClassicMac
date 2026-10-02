using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App
{
    internal sealed partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            // Fluent's accent and its six shades (focus rings, check boxes, sliders) from CmAccent, per theme variant.
            var fluent = Styles.OfType<FluentTheme>().Single();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                if (Resources.TryGetResource("CmAccentColor", variant, out var accent) && accent is Color color)
                {
                    fluent.Palettes[variant] = new ColorPaletteResources { Accent = color };
                }
            }
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var model = new MainViewModel(new JsonSettingsStore(JsonSettingsStore.DefaultPath));
                desktop.MainWindow = new MainWindow { DataContext = model };
                foreach (var path in desktop.Args ?? [])
                {
                    _ = model.OpenAsync(path);
                }
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
