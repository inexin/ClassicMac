using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Controls;

/// <summary>
/// Resize's size slider (design/boards/volume-tools.md §4): a 6 px track, logarithmic from <see cref="Minimum"/> to
/// <see cref="Maximum"/> (<see cref="SizeScale"/>), striped in the warning colour up to <see cref="WarningEnd"/> (the sizes
/// that need Defragment first); a 2 px tick at the <see cref="Current"/> size, the stretch from it to the thumb tinted in
/// the thumb's colour at 45%; snap points as 5 px ticks with mono labels under them (dropped where they would overlap);
/// an 18 px round thumb ringed in the accent, or the note's warning or error colour. A drag snaps (Shift drags freely)
/// and lands on a snap point within 6 px; the keys of <see cref="SizeScale.Step"/> move it.
/// </summary>
public sealed class SizeSlider : Control
{
    private const double Thumb = 18, Track = 6, Tick = 5, LabelGap = 3, SnapDistance = 6;

    public static readonly StyledProperty<long> MinimumProperty = AvaloniaProperty.Register<SizeSlider, long>(nameof(Minimum), 1);

    public static readonly StyledProperty<long> MaximumProperty = AvaloniaProperty.Register<SizeSlider, long>(nameof(Maximum), 1);

    public static readonly StyledProperty<long> ValueProperty = AvaloniaProperty.Register<SizeSlider, long>(nameof(Value), 1);

    public static readonly StyledProperty<long> CurrentProperty = AvaloniaProperty.Register<SizeSlider, long>(nameof(Current), 1);

    public static readonly StyledProperty<long> WarningEndProperty = AvaloniaProperty.Register<SizeSlider, long>(nameof(WarningEnd));

    public static readonly StyledProperty<NoteSeverity> SeverityProperty = AvaloniaProperty.Register<SizeSlider, NoteSeverity>(nameof(Severity));

    public static readonly StyledProperty<IReadOnlyList<SizeMark>> MarksProperty =
        AvaloniaProperty.Register<SizeSlider, IReadOnlyList<SizeMark>>(nameof(Marks), []);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(Accent));

    public static readonly StyledProperty<IBrush?> WarningProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(Warning));

    public static readonly StyledProperty<IBrush?> ErrorProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(Error));

    public static readonly StyledProperty<IBrush?> TickBrushProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(TickBrush));

    public static readonly StyledProperty<IBrush?> ThumbFillProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(ThumbFill));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<SizeSlider, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<FontFamily> LabelFontProperty =
        AvaloniaProperty.Register<SizeSlider, FontFamily>(nameof(LabelFont), FontFamily.Default);

    public static readonly StyledProperty<double> LabelSizeProperty = AvaloniaProperty.Register<SizeSlider, double>(nameof(LabelSize), 11);

    private bool dragging;

    static SizeSlider()
    {
        FocusableProperty.OverrideDefaultValue<SizeSlider>(true);
        AffectsRender<SizeSlider>(MinimumProperty, MaximumProperty, ValueProperty, CurrentProperty, WarningEndProperty, SeverityProperty,
            MarksProperty, TrackBrushProperty, AccentProperty, WarningProperty, ErrorProperty, TickBrushProperty, ThumbFillProperty,
            LabelBrushProperty, LabelFontProperty, LabelSizeProperty, IsFocusedProperty);
        AffectsMeasure<SizeSlider>(LabelSizeProperty, LabelFontProperty);
    }

    public long Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public long Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The size the thumb shows; a drag or a key sets it.</summary>
    public long Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public long Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>The end of the striped stretch: the sizes from <see cref="Minimum"/> up to it need Defragment first.</summary>
    public long WarningEnd
    {
        get => GetValue(WarningEndProperty);
        set => SetValue(WarningEndProperty, value);
    }

    public NoteSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public IReadOnlyList<SizeMark> Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? Warning
    {
        get => GetValue(WarningProperty);
        set => SetValue(WarningProperty, value);
    }

    public IBrush? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public IBrush? ThumbFill
    {
        get => GetValue(ThumbFillProperty);
        set => SetValue(ThumbFillProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public FontFamily LabelFont
    {
        get => GetValue(LabelFontProperty);
        set => SetValue(LabelFontProperty, value);
    }

    public double LabelSize
    {
        get => GetValue(LabelSizeProperty);
        set => SetValue(LabelSizeProperty, value);
    }

    private SizeScale Scale => new(Minimum, Maximum);

    private double TrackWidth => Math.Max(0, Bounds.Width - Thumb);

    /// <summary>The x of a size, the thumb's centre.</summary>
    public double XOf(long size) => Thumb / 2 + Scale.Position(size) * TrackWidth;

    /// <summary>The size a drag at <paramref name="x"/> gives: on a snap point within 6 px, else snapped (free with Shift).</summary>
    public long SizeAt(double x, bool free)
    {
        if (!free)
        {
            foreach (var mark in Marks)
            {
                if (mark.Size >= Minimum && mark.Size <= Maximum && Math.Abs(XOf(mark.Size) - x) <= SnapDistance)
                {
                    return mark.Size;
                }
            }
        }

        return Scale.ValueAt(TrackWidth <= 0 ? 0 : (x - Thumb / 2) / TrackWidth, free);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, Thumb + Tick + LabelGap + LabelSize * 1.4);

    protected override AutomationPeer OnCreateAutomationPeer() => new SliderPeer(this);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        dragging = true;
        e.Pointer.Capture(this);
        Focus();
        Value = SizeAt(e.GetPosition(this).X, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (dragging)
        {
            Value = SizeAt(x, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            return;
        }

        var near = Marks.FirstOrDefault(m => m.Size >= Minimum && m.Size <= Maximum && Math.Abs(XOf(m.Size) - x) <= SnapDistance);
        ToolTip.SetTip(this, near?.Tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        SizeKey? key = e.Key switch
        {
            Key.Right or Key.Up => SizeKey.Up,
            Key.Left or Key.Down => SizeKey.Down,
            Key.PageUp => SizeKey.PageUp,
            Key.PageDown => SizeKey.PageDown,
            Key.Home => SizeKey.Home,
            Key.End => SizeKey.End,
            _ => null,
        };
        if (key is { } step)
        {
            Value = Scale.Step(Value, step, [.. Marks.Select(m => m.Size)]);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= Thumb || Maximum <= Minimum)
        {
            return;
        }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double Snap(double x) => Math.Round(x * scale) / scale;
        double middle = Thumb / 2, top = middle - Track / 2;
        var track = new Rect(Thumb / 2, top, TrackWidth, Track);
        context.DrawRectangle(TrackBrush, null, track, Track / 2, Track / 2);

        // The sizes that need Defragment first: diagonal stripes in the warning colour.
        if (WarningEnd > Minimum && Warning is { } warning)
        {
            var stripes = new Rect(track.Left, top, XOf(Math.Min(WarningEnd, Maximum)) - track.Left, Track);
            using (context.PushClip(new RoundedRect(stripes, Track / 2)))
            {
                var pen = new Pen(warning, 2);
                for (double x = stripes.Left - Track; x < stripes.Right; x += 5)
                {
                    context.DrawLine(pen, new Point(x, stripes.Bottom), new Point(x + Track, stripes.Top));
                }
            }
        }

        var ring = Severity switch
        {
            NoteSeverity.Warning => Warning,
            NoteSeverity.Error => Error,
            _ => Accent,
        };
        double now = Snap(XOf(Current)), thumb = XOf(Math.Clamp(Value, Minimum, Maximum));
        if (ring is ISolidColorBrush solid && Math.Abs(thumb - now) > 0.5)
        {
            var tint = new SolidColorBrush(solid.Color, 0.45);
            context.DrawRectangle(tint, null, new Rect(Math.Min(now, thumb), top, Math.Abs(thumb - now), Track));
        }

        context.DrawRectangle(TickBrush, null, new Rect(now - 1, middle - Thumb / 3, 2, Thumb * 2 / 3));

        // Snap points: ticks under the track, labels under them where they fit.
        double ticksTop = Thumb, labelsTop = Thumb + Tick + LabelGap, lastRight = double.NegativeInfinity;
        var typeface = new Typeface(LabelFont);
        foreach (var mark in Marks.Where(m => m.Size >= Minimum && m.Size <= Maximum).OrderBy(m => m.Size))
        {
            double x = Snap(XOf(mark.Size));
            context.DrawRectangle(LabelBrush, null, new Rect(x - 0.5, ticksTop, 1, Tick));
            if (mark.Label is not { } label)
            {
                continue;
            }

            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelSize, LabelBrush);
            double left = Math.Clamp(x - text.Width / 2, 0, Bounds.Width - text.Width);
            if (left < lastRight + 6)
            {
                continue;
            }

            context.DrawText(text, new Point(left, labelsTop));
            lastRight = left + text.Width;
        }

        context.DrawEllipse(ThumbFill, new Pen(ring, 2), new Point(thumb, middle), Thumb / 2 - 1, Thumb / 2 - 1);
        if (IsFocused)
        {
            context.DrawEllipse(null, new Pen(Accent, 1), new Point(thumb, middle), Thumb / 2 + 2, Thumb / 2 + 2);
        }
    }

    // role=slider: its name (set by the view) carries the value text, "500 KB, needs Defragment".
    private sealed class SliderPeer(SizeSlider owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
