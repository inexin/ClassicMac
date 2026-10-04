using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClassicMac.Files;

namespace ClassicMac.App.Controls;

/// <summary>What an allocation map's segment shows (design/boards/volume-tools.md §6).</summary>
public enum AllocationMapKind
{
    /// <summary>Every block in use.</summary>
    Used,

    /// <summary>A block of a file in more than one piece (wins over the others).</summary>
    Split,

    /// <summary>Some blocks free.</summary>
    Partial,

    /// <summary>Every block free.</summary>
    Free,
}

/// <summary>One segment of an allocation map: its blocks, what it shows, and how many of its blocks are free.</summary>
public readonly record struct AllocationMapSegment(BlockRange Blocks, AllocationMapKind Kind, long Free);

/// <summary>
/// A volume's allocation map (design/boards/volume-tools.md §6): one strip sized to its width, one segment per 2 DIP,
/// each an equal share of the volume's blocks (one block per segment when the volume has fewer). Small things win: a
/// segment with any block of a file in pieces is Split, else one with any free block Partial (Free when all are),
/// else Used. Drawn as rectangles snapped to device pixels, neighbours of one kind as one; the block range under the
/// pointer is its tooltip, and the layout's summary its accessible name.
/// </summary>
public sealed class AllocationMap : Control
{
    /// <summary>The width of a segment, in DIP.</summary>
    public const double SegmentWidth = 2;

    public static readonly StyledProperty<VolumeLayout?> LayoutProperty = AvaloniaProperty.Register<AllocationMap, VolumeLayout?>(nameof(Layout));

    public static readonly StyledProperty<IBrush?> UsedProperty = AvaloniaProperty.Register<AllocationMap, IBrush?>(nameof(Used));

    public static readonly StyledProperty<IBrush?> SplitProperty = AvaloniaProperty.Register<AllocationMap, IBrush?>(nameof(Split));

    public static readonly StyledProperty<IBrush?> PartialProperty = AvaloniaProperty.Register<AllocationMap, IBrush?>(nameof(Partial));

    public static readonly StyledProperty<IBrush?> FreeProperty = AvaloniaProperty.Register<AllocationMap, IBrush?>(nameof(Free));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<AllocationMap, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = AvaloniaProperty.Register<AllocationMap, CornerRadius>(nameof(CornerRadius));

    static AllocationMap()
    {
        AffectsRender<AllocationMap>(LayoutProperty, UsedProperty, SplitProperty, PartialProperty, FreeProperty, BorderBrushProperty, CornerRadiusProperty);
        LayoutProperty.Changed.AddClassHandler<AllocationMap>((map, _) =>
            AutomationProperties.SetName(map, map.Layout is { } layout ? Summary(layout) : string.Empty));
    }

    public VolumeLayout? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IBrush? Used
    {
        get => GetValue(UsedProperty);
        set => SetValue(UsedProperty, value);
    }

    public IBrush? Split
    {
        get => GetValue(SplitProperty);
        set => SetValue(SplitProperty, value);
    }

    public IBrush? Partial
    {
        get => GetValue(PartialProperty);
        set => SetValue(PartialProperty, value);
    }

    public IBrush? Free
    {
        get => GetValue(FreeProperty);
        set => SetValue(FreeProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>The segments of a strip this wide: one per 2 DIP, each an equal share of the blocks, never less than one block.</summary>
    public static IReadOnlyList<AllocationMapSegment> Segments(VolumeLayout layout, double width)
    {
        ArgumentNullException.ThrowIfNull(layout);
        long blocks = layout.BlockCount;
        int count = (int)Math.Max(1, Math.Min(blocks, Math.Floor(width / SegmentWidth)));
        var segments = new List<AllocationMapSegment>(count);
        for (int i = 0; i < count; i++)
        {
            long start = blocks * i / count, end = blocks * (i + 1) / count;
            long free = layout.FreeRuns.Sum(r => Math.Max(0, Math.Min(r.End, end) - Math.Max(r.Start, start)));
            bool split = layout.SplitExtents.Any(e => e.Start < end && start < e.End);
            var kind = split ? AllocationMapKind.Split
                : free == end - start ? AllocationMapKind.Free
                : free > 0 ? AllocationMapKind.Partial
                : AllocationMapKind.Used;
            segments.Add(new AllocationMapSegment(new BlockRange(start, end - start), kind, free));
        }

        return segments;
    }

    /// <summary>A segment's tooltip: its block range and what it holds ("Blocks 1,024–1,045: 2 free, the rest used").</summary>
    public static string Describe(AllocationMapSegment segment)
    {
        var blocks = segment.Blocks;
        var range = blocks.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"Block {blocks.Start:N0}")
            : string.Create(CultureInfo.InvariantCulture, $"Blocks {blocks.Start:N0}–{blocks.End - 1:N0}");
        var holds = segment.Kind switch
        {
            AllocationMapKind.Split => "part of a file in pieces" + (segment.Free > 0 ? string.Create(CultureInfo.InvariantCulture, $", {segment.Free:N0} free") : ""),
            AllocationMapKind.Free => "free",
            AllocationMapKind.Partial => string.Create(CultureInfo.InvariantCulture, $"{segment.Free:N0} free, the rest used"),
            _ => "used",
        };
        return $"{range}: {holds}";
    }

    /// <summary>The map's accessible name: the blocks, the files in pieces and the free runs, in words.</summary>
    public static string Summary(VolumeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int runs = layout.FreeRuns.Count;
        var free = runs == 0 ? "no free space"
            : string.Create(CultureInfo.InvariantCulture, $"free space in {runs:N0} {(runs == 1 ? "run" : "runs")}, the largest {layout.LargestFreeRun:N0} blocks");
        return string.Create(CultureInfo.InvariantCulture,
            $"{layout.BlockCount:N0} blocks; {layout.SplitFiles:N0} {(layout.SplitFiles == 1 ? "file" : "files")} in pieces; {free}");
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Layout is not { } layout || Bounds.Width <= 0)
        {
            return;
        }

        var segments = Segments(layout, Bounds.Width);
        int index = (int)Math.Clamp(e.GetPosition(this).X / Bounds.Width * segments.Count, 0, segments.Count - 1);
        ToolTip.SetTip(this, Describe(segments[index]));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double Snap(double x) => Math.Round(x * scale) / scale;
        using (context.PushClip(new RoundedRect(bounds, CornerRadius)))
        {
            context.DrawRectangle(Free, null, bounds);
            if (Layout is { } layout)
            {
                var segments = Segments(layout, bounds.Width);
                double width = bounds.Width / segments.Count;
                for (int i = 0; i < segments.Count;)
                {
                    // Neighbours of one kind as one rectangle, its edges on device pixels.
                    int j = i + 1;
                    while (j < segments.Count && segments[j].Kind == segments[i].Kind)
                    {
                        j++;
                    }

                    var brush = segments[i].Kind switch
                    {
                        AllocationMapKind.Split => Split,
                        AllocationMapKind.Partial => Partial,
                        AllocationMapKind.Free => Free,
                        _ => Used,
                    };
                    double left = Snap(i * width), right = Snap(j * width);
                    if (brush is not null && right > left)
                    {
                        context.DrawRectangle(brush, null, new Rect(left, 0, right - left, bounds.Height));
                    }

                    i = j;
                }
            }
        }

        if (BorderBrush is not null)
        {
            // A 1 px line inset in the strip.
            double half = 0.5 / scale;
            context.DrawRectangle(null, new Pen(BorderBrush, 1 / scale), bounds.Deflate(half), CornerRadius.TopLeft, CornerRadius.TopLeft);
        }
    }
}
