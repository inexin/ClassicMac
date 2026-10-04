using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassicMac.App.Views;

/// <summary>
/// The app's icon in a small slot (the title bar's 16 DIP): the hand-drawn pixel size for the display scaling (16 at
/// 100%, 20 at 125%, 24 at 150%, 32 at 200%; design/icon/README.md), drawn one device pixel per icon pixel from a
/// snapped origin, never scaled.
/// </summary>
internal sealed class AppIconView : PixelControl
{
    /// <summary>The icon's sizes in Assets/icon (16–32 drawn pixel by pixel, 40 and up rendered from the vector master).</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32, 40, 48, 64];

    private static readonly Dictionary<int, Bitmap> Cache = [];

    /// <summary>The slot's size in DIPs at 100% scaling.</summary>
    public static readonly StyledProperty<double> SlotProperty = AvaloniaProperty.Register<AppIconView, double>(nameof(Slot), 16);

    static AppIconView()
    {
        AffectsMeasure<AppIconView>(SlotProperty);
        AffectsRender<AppIconView>(SlotProperty);
    }

    public double Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    /// <summary>The bitmap drawn at the current scaling.</summary>
    public Bitmap? Bitmap => Load(PixelsFor(Slot, RenderScaling));

    /// <summary>The largest size no bigger than the slot in device pixels (at least the smallest).</summary>
    public static int PixelsFor(double slot, double scaling)
    {
        var target = (int)Math.Round(slot * scaling);
        return Sizes.LastOrDefault(s => s <= target, Sizes[0]);
    }

    private static Bitmap? Load(int size)
    {
        if (!Cache.TryGetValue(size, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ClassicMac/Assets/icon/classicmac-{size}.png"));
            Cache[size] = bitmap = new Bitmap(stream);
        }

        return bitmap;
    }

    // One device pixel per icon pixel: the icon's pixels over the scaling, in DIPs.
    protected override Size MeasureOverride(Size availableSize)
    {
        var pixels = PixelsFor(Slot, RenderScaling) / RenderScaling;
        return new Size(pixels, pixels);
    }

    public override void Render(DrawingContext context)
    {
        if (Bitmap is not { } bitmap)
        {
            return;
        }

        using var snap = PushSnap(context);
        var size = bitmap.PixelSize.Width / RenderScaling;
        context.DrawImage(bitmap, new Rect(0, 0, size, size));
    }
}
