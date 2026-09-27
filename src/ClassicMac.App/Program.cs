using System;
using Avalonia;

namespace ClassicMac.App
{
    internal static class Program
    {
        // Paths on the command line are opened at start.
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Also used by the XAML previewer.
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
