using System;
using System.Buffers.Binary;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers
{
    /// <summary>
    /// Writes AppleDouble version 2 header files (the <c>._</c> file beside a data file), after Apple's
    /// <i>AppleSingle/AppleDouble Formats for Foreign Files</i> Developer Note: Real Name, File Dates Info, Finder Info
    /// and Resource Fork entries, the resource fork last so it can be streamed.
    /// </summary>
    public static class AppleDoubleWriter
    {
        private const uint Magic = 0x00051607, Version2 = 0x00020000;
        private const uint ResourceForkEntry = 2, RealNameEntry = 3, FileDatesEntry = 8, FinderInfoEntry = 9;
        private const int HeaderLength = 26, EntryLength = 12, Entries = 4;
        private static readonly DateTime DateEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Writes <paramref name="file"/>'s name, dates, Finder info and resource fork. Mac dates are local, so they are
        /// converted to UTC with <paramref name="timeZone"/> (the Mac's zone; default local), as the reader converts back.
        /// </summary>
        public static void Write(MacFile file, Stream output, TimeZoneInfo? timeZone = null)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(output);
            var zone = timeZone ?? TimeZoneInfo.Local;
            var name = file.Name.Bytes;
            var first = HeaderLength + Entries * EntryLength;
            var header = new byte[first + name.Length + 16 + FinderInfo.Length];
            BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Version2);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), Entries);

            var offset = first;
            var entry = HeaderLength;
            void Entry(uint id, int length)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry), id);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 4), (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 8), (uint)length);
                entry += EntryLength;
                offset += length;
            }

            name.CopyTo(header.AsSpan(offset));
            Entry(RealNameEntry, name.Length);
            var dates = header.AsSpan(offset, 16);
            BinaryPrimitives.WriteInt32BigEndian(dates, Seconds(file.Created, zone));
            BinaryPrimitives.WriteInt32BigEndian(dates[4..], Seconds(file.Modified, zone));
            BinaryPrimitives.WriteInt32BigEndian(dates[8..], int.MinValue); // backup: unknown
            BinaryPrimitives.WriteInt32BigEndian(dates[12..], int.MinValue); // access: unknown
            Entry(FileDatesEntry, 16);
            file.FinderInfo.Write(header.AsSpan(offset));
            Entry(FinderInfoEntry, FinderInfo.Length);
            if (file.ResourceFork.Length > uint.MaxValue - (long)offset)
                throw new ArgumentException("The resource fork is too large for AppleDouble.", nameof(file));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry), ResourceForkEntry);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 8), (uint)file.ResourceFork.Length);

            output.Write(header);
            using var fork = file.ResourceFork.Open();
            fork.CopyTo(output);
        }

        /// <summary>The header file's bytes.</summary>
        public static byte[] ToArray(MacFile file, TimeZoneInfo? timeZone = null)
        {
            using var output = new MemoryStream();
            Write(file, output, timeZone);
            return output.ToArray();
        }

        // Signed seconds from 2000 UTC; int.MinValue when unknown.
        private static int Seconds(MacDate? date, TimeZoneInfo zone)
        {
            if (date is not { } d) return int.MinValue;
            var local = DateTime.SpecifyKind(d.ToDateTime(), DateTimeKind.Unspecified);
            var utc = zone.IsInvalidTime(local) ? local - zone.GetUtcOffset(local.AddHours(-1)) : TimeZoneInfo.ConvertTimeToUtc(local, zone);
            return (int)Math.Clamp((long)(utc - DateEpoch).TotalSeconds, int.MinValue + 1, int.MaxValue);
        }
    }
}
