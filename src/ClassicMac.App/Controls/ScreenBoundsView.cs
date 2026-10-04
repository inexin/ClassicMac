using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassicMac.App.Controls;

/// <summary>
/// A window's bounds on a Mac screen at half scale (design/boards/window-alert.md, the window form's right panel): the
/// screen with its menu bar, the content rectangle in CmSelectionInactive edged in CmAccent, and the title bar above it
/// in CmAccent for windows that have one. It follows the form's values, so the draft while editing.
/// </summary>
internal sealed class ScreenBoundsView : Control
{
    private const double Scale = 0.5;
    private const int MenuBarHeight = 20, TitleBarHeight = 18;

    public static readonly StyledProperty<double> TopProperty = AvaloniaProperty.Register<ScreenBoundsView, double>(nameof(Top));
    public static readonly StyledProperty<double> LeftProperty = AvaloniaProperty.Register<ScreenBoundsView, double>(nameof(Left));
    public static readonly StyledProperty<double> BottomProperty = AvaloniaProperty.Register<ScreenBoundsView, double>(nameof(Bottom));
    public static readonly StyledProperty<double> RightProperty = AvaloniaProperty.Register<ScreenBoundsView, double>(nameof(Right));
    public static readonly StyledProperty<int> DefinitionProperty = AvaloniaProperty.Register<ScreenBoundsView, int>(nameof(Definition));
    public static readonly StyledProperty<int> ScreenWidthProperty = AvaloniaProperty.Register<ScreenBoundsView, int>(nameof(ScreenWidth), 512);
    public static readonly StyledProperty<int> ScreenHeightProperty = AvaloniaProperty.Register<ScreenBoundsView, int>(nameof(ScreenHeight), 342);

    static ScreenBoundsView()
    {
        AffectsRender<ScreenBoundsView>(TopProperty, LeftProperty, BottomProperty, RightProperty, DefinitionProperty, ScreenWidthProperty, ScreenHeightProperty);
        AffectsMeasure<ScreenBoundsView>(ScreenWidthProperty, ScreenHeightProperty);
    }

    public ScreenBoundsView() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public double Top { get => GetValue(TopProperty); set => SetValue(TopProperty, value); }

    public double Left { get => GetValue(LeftProperty); set => SetValue(LeftProperty, value); }

    public double Bottom { get => GetValue(BottomProperty); set => SetValue(BottomProperty, value); }

    public double Right { get => GetValue(RightProperty); set => SetValue(RightProperty, value); }

    public int Definition { get => GetValue(DefinitionProperty); set => SetValue(DefinitionProperty, value); }

    public int ScreenWidth { get => GetValue(ScreenWidthProperty); set => SetValue(ScreenWidthProperty, value); }

    public int ScreenHeight { get => GetValue(ScreenHeightProperty); set => SetValue(ScreenHeightProperty, value); }

    /// <summary>What is drawn, in DIPs: the screen, its menu bar, the window's content and its title bar (null for none).</summary>
    public readonly record struct Geometry(Rect Screen, Rect MenuBar, Rect Content, Rect? TitleBar);

    /// <summary>
    /// Whether a window definition draws a title bar above its content: the document windows, the movable modal and the
    /// rounded window do; the dialog box, plain box and alt dialog box do not.
    /// </summary>
    public static bool HasTitleBar(int definition) => definition is not (1 or 2 or 3);

    public static Geometry Layout(int screenWidth, int screenHeight, double top, double left, double bottom, double right, bool titleBar)
    {
        var content = new Rect(Math.Min(left, right) * Scale, Math.Min(top, bottom) * Scale,
            Math.Abs(right - left) * Scale, Math.Abs(bottom - top) * Scale);
        return new Geometry(new Rect(0, 0, screenWidth * Scale, screenHeight * Scale), new Rect(0, 0, screenWidth * Scale, MenuBarHeight * Scale), content,
            titleBar ? new Rect(content.X, content.Y - TitleBarHeight * Scale, content.Width, TitleBarHeight * Scale) : null);
    }

    protected override Size MeasureOverride(Size availableSize) => new(ScreenWidth * Scale, ScreenHeight * Scale);

    private IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        var g = Layout(ScreenWidth, ScreenHeight, Top, Left, Bottom, Right, HasTitleBar(Definition));
        using var clip = context.PushClip(g.Screen);
        context.DrawRectangle(Brush("CmControlBackground"), new Pen(Brush("CmBorder")), g.Screen.Deflate(0.5));
        context.DrawRectangle(Brush("CmChromeBackground"), null, g.MenuBar);
        context.DrawLine(new Pen(Brush("CmBorder")), g.MenuBar.BottomLeft, g.MenuBar.BottomRight);
        if (g.TitleBar is { } title)
        {
            context.DrawRectangle(Brush("CmAccent"), null, title);
        }

        context.DrawRectangle(Brush("CmSelectionInactive"), new Pen(Brush("CmAccent")), g.Content);
    }
}
