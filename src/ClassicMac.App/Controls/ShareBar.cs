using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClassicMac.App.Controls;

/// <summary>
/// A thin rounded bar showing a share from 0 to 1 (a fork's part of a file's size in Details): the track, and the
/// share filled from the left. It draws itself from its own size in one pass, unlike a ProgressBar, whose template
/// sizes its indicator in a later layout pass.
/// </summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ShareBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Fill));

    static ShareBar() => AffectsRender<ShareBar>(ValueProperty, TrackProperty, FillProperty);

    /// <summary>The share, from 0 to 1 (clamped).</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        double radius = bounds.Height / 2;
        context.DrawRectangle(Track, null, bounds, radius, radius);
        double share = Math.Clamp(double.IsNaN(Value) ? 0 : Value, 0, 1);
        if (share > 0 && Fill is not null)
        {
            // The share clipped to the track's rounded shape, so a small one keeps the track's left end.
            using (context.PushClip(new RoundedRect(bounds, radius)))
            {
                context.DrawRectangle(Fill, null, new Rect(0, 0, bounds.Width * share, bounds.Height));
            }
        }
    }
}
