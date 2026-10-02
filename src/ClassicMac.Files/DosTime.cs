using System;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    // DOS directory times as File Exchange reads them (confirmed in SheepShaver on a FAT12 disk): local time like Mac
    // dates, even seconds only, and years from 2032 on wrap back 128 years, because File Exchange stores Mac years
    // 1904–1979 as DOS 2032–2107 (DOS 2040 reads as 1912).
    internal static class DosTime
    {
        // A DOS date and time field pair (date: year − 1980, month, day; time: hours, minutes, seconds / 2), as File
        // Exchange converts it through the native Date2Secs of Mac OS 9 (disassembly; every impossible date checked in
        // SheepShaver): no validity check, so a month or day out of range rolls over (month 0 is the December before, day
        // 0 the day before the 1st, month 13 January of the next year), and a zero date is 1979-12-01. With
        // zeroIsNull (a creation date, where File Exchange shows "now" when the date word is 0), a zero date is null.
        public static MacDate? FromFields(ushort date, ushort time, bool zeroIsNull = false)
        {
            if (zeroIsNull && date == 0)
            {
                return null;
            }

            var year = (date >> 9) + 1980;
            if (year > 2031)
            {
                year = (date >> 9) + 1852;
            }

            return new MacDate(Date2Secs(year, (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2));
        }

        // Mac OS 9's native Date2Secs: 16-bit day arithmetic, divisions truncating toward zero, no clamping.
        private static uint Date2Secs(int year, int month, int day, int hour, int minute, int second)
        {
            var seconds = (uint)(second + minute * 60 + hour * 3600);
            var days = ((day - 1) & 0xFFFF) + ((((year - 1904) & 0xFFFF) * 1461 + 3) / 4 & 0xFFFF);
            days &= 0xFFFF;
            var m = (short)(month - 1);
            if (m > 1)
            {
                m = (short)(month - 3);
                days = (days + 59) & 0xFFFF;
                if (year % 4 == 0)
                {
                    days = (days + 1) & 0xFFFF;
                }
            }
            days = ((m * 3917 + 52) / 128 + days) & 0xFFFF;
            return unchecked((uint)days * 86400 + seconds);
        }

        // A host time for a file from a DOS disk, read the same way.
        public static MacDate? FromLocal(DateTime local)
        {
            local = local.AddTicks(-(local.Ticks % (2 * TimeSpan.TicksPerSecond)));
            if (local.Year >= 2032)
            {
                local = local.AddYears(-128);
            }

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
