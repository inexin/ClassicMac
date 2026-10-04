using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

internal static class ShellConverters
{
    /// <summary>Whether all the values are equal (a radio menu item for the current choice).</summary>
    public static FuncMultiValueConverter<object?, bool> AreEqual { get; } = new(values =>
    {
        object? first = null;
        var any = false;
        foreach (var value in values)
        {
            if (!any)
            {
                first = value;
                any = true;
            }
            else if (!Equals(first, value))
            {
                return false;
            }
        }

        return any;
    });
}

/// <summary>
/// The About box (design/boards/main-window.md, S7; frame as in boards/dialogs.md): the app's icon, name, version,
/// what it does, a link to the repository, the licence and the third-party notices, and one OK button.
/// </summary>
internal static class AboutBox
{
    public static Window Create(AboutInfo about, Action<Uri> open)
    {
        var window = new Window
        {
            Title = $"About {about.Name}",
            Width = 640,
            Height = 560,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        // The app's icon: the 128 PNG in the compact 64 DIP layout (design/icon/README.md), sharp at 200%.
        using var iconStream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://ClassicMac/Assets/icon/classicmac-128.png"));
        var icon = new Image
        {
            Name = "AppIcon",
            Source = new Avalonia.Media.Imaging.Bitmap(iconStream),
            Width = 64,
            Height = 64,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapInterpolationMode(icon, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        var link = new Button { Content = about.Repository.ToString(), Classes = { "link" }, Name = "RepositoryLink" };
        link.Click += (_, _) => open(about.Repository);
        var heading = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = about.Name, FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Version {about.Version}", Classes = { "muted" }, Name = "Version" },
            },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, heading } };
        var licenceLine = about.Licence.Split('\n', 2)[0].Trim();
        var notices = new SelectableTextBlock
        {
            Name = "Notices",
            Text = about.Licence.Trim() + "\n\n" + about.Notices.Trim(),
            TextWrapping = TextWrapping.NoWrap,
            Classes = { "mono" },
        };
        var scroller = new ScrollViewer
        {
            Content = notices,
            Padding = new Thickness(10, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Classes = { "sidebar" },
        };
        var body = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var top = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                header,
                new TextBlock { Text = about.Description, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{licenceLine}; the libraries, data and fonts it uses are listed below.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
                link,
                new TextBlock { Text = "LICENCE AND THIRD-PARTY NOTICES", Classes = { "caption" } },
            },
        };
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);
        var card = new Border { Child = scroller, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("CmCardBorder"));
        body.Children.Add(card);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
        ok.Click += (_, _) => window.Close();
        var footer = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Classes = { "sidebar", "dialog-footer" },
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
        };
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("CmDivider"));
        DockPanel.SetDock(footer, Dock.Bottom);
        window.Content = new DockPanel { Classes = { "pane" }, Children = { footer, body } };
        return window;
    }
}
