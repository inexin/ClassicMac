using System;
using System.Globalization;

namespace ClassicMac.Core;

/// <summary>
/// A classic Mac OS date: unsigned seconds since midnight, 1 January 1904, in the local time of the Mac that wrote
/// it (the time zone is not recorded). Covers 1904 to 2040.
/// </summary>
/// <param name="Seconds">Seconds since the 1904 epoch.</param>
public readonly record struct MacDate(uint Seconds)
{
    private static readonly DateTime Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The date as a <see cref="DateTime"/> of unspecified kind, since the Mac's time zone is unknown.</summary>
    public DateTime ToDateTime() => Epoch.AddSeconds(Seconds);

    /// <summary>Converts a date in 1904–2040 to Mac seconds, ignoring its kind and dropping fractions of a second.</summary>
    public static MacDate FromDateTime(DateTime date)
    {
        var seconds = (date.Ticks - Epoch.Ticks) / TimeSpan.TicksPerSecond;
        if (date.Ticks < Epoch.Ticks || seconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "A Mac date covers 1904 to 2040.");
        }

        return new MacDate((uint)seconds);
    }

    /// <inheritdoc/>
    public override string ToString() => ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
