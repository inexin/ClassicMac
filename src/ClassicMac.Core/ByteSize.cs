using System;
using System.Globalization;

namespace ClassicMac.Core;

/// <summary>Sizes as people type them: plain bytes, or a whole number with KiB, MiB or GiB (also K, M, G), any case.</summary>
public static class ByteSize
{
    private static readonly (string Suffix, long Value)[] Units =
    [
        ("KiB", 1L << 10), ("MiB", 1L << 20), ("GiB", 1L << 30), ("K", 1L << 10), ("M", 1L << 20), ("G", 1L << 30),
    ];

    /// <summary>Reads a size; false for anything not a whole, positive number of bytes or units.</summary>
    public static bool TryParse(string text, out long size)
    {
        ArgumentNullException.ThrowIfNull(text);
        size = 0;
        var s = text.Trim();
        long unit = 1;
        foreach (var (suffix, value) in Units)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length].TrimEnd();
                unit = value;
                break;
            }
        }

        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > long.MaxValue / unit)
        {
            return false;
        }

        size = number * unit;
        return size > 0;
    }

    /// <summary>A size in its largest whole unit (<c>800K</c>, <c>20M</c>), or in bytes; <see cref="TryParse"/> reads it back.</summary>
    public static string Format(long size)
    {
        foreach (var (suffix, value) in new[] { ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10) })
        {
            if (size != 0 && size % value == 0)
            {
                return (size / value).ToString(CultureInfo.InvariantCulture) + suffix;
            }
        }

        return size.ToString(CultureInfo.InvariantCulture);
    }
}
