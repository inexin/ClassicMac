using System;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    // DOS directory times as File Exchange reads them (confirmed in SheepShaver on a FAT12 disk): local time like Mac
    // dates, even seconds only, and years from 2032 on wrap back 128 years, because File Exchange stores Mac years
    // 1904–1979 as DOS 2032–2107 (DOS 2040 reads as 1912).
    internal static class DosTime
    {
        // A DOS date and time field pair (date: year − 1980, month, day; time: hours, minutes, seconds / 2), or null
        // for a zero or impossible date.
        public static MacDate? FromFields(ushort date, ushort time)
        {
            if (date == 0) return null;
            int year = 1980 + (date >> 9), month = (date >> 5) & 15, day = date & 31;
            int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
                return null;
            return FromLocal(new DateTime(year, month, day, hour, minute, second));
        }

        // A host time for a file from a DOS disk, read the same way.
        public static MacDate? FromLocal(DateTime local)
        {
            local = local.AddTicks(-(local.Ticks % (2 * TimeSpan.TicksPerSecond)));
            if (local.Year >= 2032) local = local.AddYears(-128);
            try
            {
                return MacDate.FromDateTime(local);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
