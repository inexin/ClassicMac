using Avalonia;
using Avalonia.Media;

namespace ClassicMac.App;

/// <summary>
/// The app's bundled fonts (design/TOKENS.md "Type"): IBM Plex Sans and IBM Plex Mono, under the SIL Open Font
/// License, in Assets/Fonts, so the app looks the same on Windows, macOS and Linux. The XAML resources
/// <c>CmFontSans</c> and <c>CmFontMono</c> name the same families.
/// </summary>
internal static class AppFonts
{
    private const string Folder = "avares://ClassicMac/Assets/Fonts";

    public const string SansName = Folder + "#IBM Plex Sans";

    public const string MonoName = Folder + "#IBM Plex Mono";

    public static FontFamily Sans { get; } = new(SansName);

    public static FontFamily Mono { get; } = new(MonoName);

    /// <summary>Makes Plex Sans the default font (in place of Avalonia's Inter).</summary>
    public static AppBuilder WithAppFonts(this AppBuilder builder) =>
        builder.With(new FontManagerOptions { DefaultFamilyName = SansName });
}
