using System;
using System.Collections.Generic;

namespace ClassicMac.App.Controls;

/// <summary>The keys Resize's slider answers (design/boards/volume-tools.md §4).</summary>
public enum SizeKey
{
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
}

/// <summary>
/// Resize's slider scale (volume-tools.md §4): logarithmic from the smallest to the largest size, so a floppy and a 2 GB
/// volume both get room; a drag snaps to whole K below 1 MB and to 0.1 MB above (to 512-byte blocks when free); arrow keys
/// step 64K below 1 MB and 1 MB above, Page Up and Down go to the next snap point, Home and End to the ends.
/// </summary>
public readonly record struct SizeScale(long Minimum, long Maximum)
{
    private const long K = 1024, M = K * K, Block = 512;

    /// <summary>Where a size lies on the track, from 0 to 1.</summary>
    public double Position(long value) => Maximum <= Minimum || value <= Minimum ? 0
        : Math.Clamp(Math.Log((double)value / Minimum) / Math.Log((double)Maximum / Minimum), 0, 1);

    /// <summary>The size at a place on the track, snapped as a drag snaps (to 512-byte blocks when free) and kept in range.</summary>
    public long ValueAt(double position, bool free) =>
        Clamp(Snap((long)Math.Round(Minimum * Math.Pow((double)Maximum / Minimum, Math.Clamp(position, 0, 1))), free));

    /// <summary>A size snapped as a drag snaps it: whole 512-byte blocks when free, else whole K below 1 MB and 0.1 MB above.</summary>
    public static long Snap(long value, bool free)
    {
        if (free)
        {
            return RoundTo(value, Block);
        }

        if (value < M)
        {
            return RoundTo(value, K);
        }

        double tenth = M / 10.0;
        return RoundTo((long)Math.Round(Math.Round(value / tenth) * tenth), Block);
    }

    /// <summary>The size a key moves to from <paramref name="value"/>, with the snap points <paramref name="marks"/>.</summary>
    public long Step(long value, SizeKey key, IReadOnlyList<long> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        return Clamp(key switch
        {
            SizeKey.Up => value + (value < M ? 64 * K : M),
            SizeKey.Down => value - (value <= M ? 64 * K : M),
            SizeKey.PageUp => Next(value, marks, up: true),
            SizeKey.PageDown => Next(value, marks, up: false),
            SizeKey.Home => Minimum,
            _ => Maximum,
        });
    }

    private long Next(long value, IReadOnlyList<long> marks, bool up)
    {
        long next = up ? Maximum : Minimum;
        foreach (var mark in marks)
        {
            if (up ? mark > value && mark < next : mark < value && mark > next)
            {
                next = mark;
            }
        }

        return next;
    }

    private long Clamp(long value) => Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));

    private static long RoundTo(long value, long unit) => Math.Max(unit, (long)Math.Round((double)value / unit) * unit);
}
