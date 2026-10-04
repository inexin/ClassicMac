using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

/// <summary>
/// A font family's sample line (design/boards/font-family.md, P6): the glyphs QuickDraw drew, black on white at the
/// zoom (each Mac pixel a whole number of device pixels), with the selected kerning pair on CmMatchSoft behind them.
/// </summary>
internal sealed class FontSampleView : PixelControl
{
    public static readonly StyledProperty<FontSample?> SampleProperty = AvaloniaProperty.Register<FontSampleView, FontSample?>(nameof(Sample));

    public static readonly StyledProperty<FontSampleSpan?> HighlightProperty = AvaloniaProperty.Register<FontSampleView, FontSampleSpan?>(nameof(Highlight));

    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<FontSampleView, double>(nameof(Zoom), 1);

    private Bitmap? bitmap;

    static FontSampleView()
    {
        AffectsMeasure<FontSampleView>(SampleProperty, ZoomProperty);
        AffectsRender<FontSampleView>(SampleProperty, HighlightProperty, ZoomProperty);
    }

    public FontSample? Sample
    {
        get => GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    public FontSampleSpan? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SampleProperty)
        {
            bitmap?.Dispose();
            bitmap = Sample is { } sample ? new Bitmap(new MemoryStream(sample.Png)) : null;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => Sample is { } sample
        ? PixelScaling.Size(new Size(sample.Width, sample.Height), Zoom, RenderScaling)
        : default;

    public override void Render(DrawingContext context)
    {
        if (Sample is not { } sample || bitmap is null)
        {
            return;
        }

        var scale = PixelScaling.Scale(Zoom, RenderScaling);
        using var snap = PushSnap(context);
        context.FillRectangle(Brushes.White, new Rect(0, 0, sample.Width * scale, sample.Height * scale));
        if (Highlight is { } span && this.TryFindResource("CmMatchSoft", ActualThemeVariant, out var brush) && brush is IBrush soft)
        {
            context.FillRectangle(soft, new Rect(span.X * scale, 0, span.Width * scale, sample.Height * scale));
        }

        context.DrawImage(bitmap, new Rect(0, 0, sample.Width * scale, sample.Height * scale));
    }
}
