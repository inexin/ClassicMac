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
        private const uint DoubleMagic = 0x00051607, SingleMagic = 0x00051600, Version2 = 0x00020000;
        private const uint DataForkEntry = 1, ResourceForkEntry = 2, RealNameEntry = 3, FileDatesEntry = 8, FinderInfoEntry = 9;
        private const int HeaderLength = 26, EntryLength = 12;
        private static readonly DateTime DateEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Writes <paramref name="file"/>'s name, dates, Finder info and resource fork. Mac dates are local, so they are
        /// converted to UTC with <paramref name="timeZone"/> (the Mac's zone; default local), as the reader converts back.
        /// </summary>
        public static void Write(MacFile file, Stream output, TimeZoneInfo? timeZone = null) => Write(file, output, timeZone, single: false);

        /// <summary>
        /// Writes <paramref name="file"/> as an AppleSingle version 2 file: the entries of an AppleDouble header file
        /// (<see cref="Write(MacFile, Stream, TimeZoneInfo?)"/>) with the magic number $00051600, and a Data Fork entry
        /// before the Resource Fork entry; both forks follow the header, data fork first.
        /// </summary>
        public static void WriteAppleSingle(MacFile file, Stream output, TimeZoneInfo? timeZone = null) => Write(file, output, timeZone, single: true);

        private static void Write(MacFile file, Stream output, TimeZoneInfo? timeZone, bool single)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(output);
            var zone = timeZone ?? TimeZoneInfo.Local;
            var name = file.Name.Bytes;
            var entries = single ? 5 : 4;
            var first = HeaderLength + entries * EntryLength;
            var header = new byte[first + name.Length + 16 + FinderInfo.Length];
            BinaryPrimitives.WriteUInt32BigEndian(header, single ? SingleMagic : DoubleMagic);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Version2);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), (ushort)entries);

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
            var data = single ? file.DataFork.Length : 0;
            if (data + file.ResourceFork.Length > uint.MaxValue - (long)offset)
                throw new ArgumentException("The forks are too large for AppleSingle or AppleDouble.", nameof(file));
            if (single)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry), DataForkEntry);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 4), (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 8), (uint)data);
                entry += EntryLength;
                offset += (int)data;
            }
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry), ResourceForkEntry);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 8), (uint)file.ResourceFork.Length);

            output.Write(header);
            if (single)
            {
                using var dataFork = file.DataFork.Open();
                dataFork.CopyTo(output);
            }
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
