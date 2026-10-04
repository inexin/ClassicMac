using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ClassicMac.App.Views;

/// <summary>
/// Pixel content at any display scaling (design/TOKENS.md): a Mac pixel is drawn as a whole number of device pixels,
/// <c>k = max(1, floor(zoom × scaling))</c>, so 1-pixel detail never blurs or doubles at 125% or 150%.
/// </summary>
internal static class PixelScaling
{
    // zoom × scaling a hair under a whole number (2 × 1.4999999999999998) is that number.
    private const double Epsilon = 1e-9;

    /// <summary>Device pixels per Mac pixel at a zoom and render scaling: <c>max(1, floor(zoom × scaling))</c>.</summary>
    public static int DevicePixels(double zoom, double renderScaling) =>
        Math.Max(1, (int)Math.Floor(zoom * renderScaling + Epsilon));

    /// <summary>
    /// DIPs per Mac pixel: <see cref="DevicePixels"/> / scaling. A picture shrunk below one device pixel per Mac
    /// pixel (a document picture wider than its column) keeps its zoom [ClassicMac: there is no whole number to
    /// draw it with].
    /// </summary>
    public static double Scale(double zoom, double renderScaling) =>
        zoom < 1 && zoom * renderScaling < 1 - Epsilon ? zoom : DevicePixels(zoom, renderScaling) / renderScaling;

    /// <summary>The size in DIPs of <paramref name="pixels"/> Mac pixels: <c>width × k / scaling</c>.</summary>
    public static Size Size(Size pixels, double zoom, double renderScaling)
    {
        var scale = Scale(zoom, renderScaling);
        return new Size(pixels.Width * scale, pixels.Height * scale);
    }

    /// <summary>A position in DIPs moved to the nearest whole device pixel.</summary>
    public static double Snap(double dip, double renderScaling) => Math.Round(dip * renderScaling) / renderScaling;
}

/// <summary>
/// A control that draws Mac pixels: it knows its window's render scaling (measuring and drawing again when it
/// changes), snaps its drawing to whole device pixels and draws bitmaps unsmoothed.
/// </summary>
internal abstract class PixelControl : Control
{
    private TopLevel? topLevel;

    protected PixelControl() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

    /// <summary>The window's render scaling (1 outside one).</summary>
    protected double RenderScaling => topLevel?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            topLevel.ScalingChanged += OnScalingChanged;
        }

        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (topLevel is not null)
        {
            topLevel.ScalingChanged -= OnScalingChanged;
        }

        topLevel = null;
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Moves drawing so this control's origin falls on a whole device pixel (layout rounding does this for
    /// bounds, but not for a fractional scroll offset). Dispose the result to undo.
    /// </summary>
    protected DrawingContext.PushedState PushSnap(DrawingContext context)
    {
        var scaling = RenderScaling;
        var origin = topLevel is not null ? this.TranslatePoint(default, topLevel) ?? default : default;
        return context.PushTransform(Matrix.CreateTranslation(
            PixelScaling.Snap(origin.X, scaling) - origin.X, PixelScaling.Snap(origin.Y, scaling) - origin.Y));
    }
}

/// <summary>A bitmap of Mac pixels at a zoom, each Mac pixel a whole number of device pixels (<see cref="PixelScaling"/>).</summary>
internal sealed class PixelImage : PixelControl
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<PixelImage, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<PixelImage, double>(nameof(Zoom), 1);

    static PixelImage()
    {
        AffectsMeasure<PixelImage>(SourceProperty, ZoomProperty);
        AffectsRender<PixelImage>(SourceProperty, ZoomProperty);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => Source is { } source
        ? PixelScaling.Size(new Size(source.PixelSize.Width, source.PixelSize.Height), Zoom, RenderScaling)
        : default;

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source)
        {
            return;
        }

        using var snap = PushSnap(context);
        context.DrawImage(source, new Rect(PixelScaling.Size(new Size(source.PixelSize.Width, source.PixelSize.Height), Zoom, RenderScaling)));
    }
}
