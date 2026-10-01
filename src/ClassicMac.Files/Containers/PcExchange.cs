using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers
{
    /// <summary>One record of a PC Exchange / File Exchange <c>FINDER.DAT</c> file.</summary>
    /// <param name="MacName">The Mac name (at most 31 bytes).</param>
    /// <param name="FinderInfo">Finder info (FInfo and FXInfo; DInfo and DXInfo for folders).</param>
    /// <param name="Created">Creation date, Mac local time, or null when zero.</param>
    /// <param name="Modified">Modification date, Mac local time, or null when zero.</param>
    /// <param name="FileNumber">The file number the volume assigned (counting down from $7FFFFFFF).</param>
    /// <param name="DosName">The item's DOS 8.3 name, 11 characters space-padded without the dot: the lookup key.</param>
    public sealed record PcExchangeRecord(
        MacString MacName, FinderInfo FinderInfo, MacDate? Created, MacDate? Modified, uint FileNumber, string DosName);

    /// <summary>
    /// The private format PC Exchange (Mac OS 7.1–8) and File Exchange (Mac OS 9) keep on FAT volumes, from the
    /// disassembly of PC Exchange 1.0.4 and File Exchange 3.0.2 (identical in both): in every directory a hidden
    /// <c>RESOURCE.FRK</c> folder with each file's raw resource fork under the file's 8.3 name, and a hidden
    /// <c>FINDER.DAT</c> of 92-byte records (Mac name, Finder info, dates, file number, 8.3 name). No Apple code uses
    /// AppleSingle or AppleDouble; this is what a Mac wrote to DOS disks.
    /// </summary>
    public static class PcExchange
    {
        /// <summary>The size of a <c>FINDER.DAT</c> record.</summary>
        public const int RecordLength = 92;

        /// <summary>The name of the folder holding resource forks.</summary>
        public const string ResourceFolder = "RESOURCE.FRK";

        /// <summary>The name of the file holding Finder info.</summary>
        public const string FinderData = "FINDER.DAT";

        // Records never straddle a cluster (floor(cluster / 92) per cluster), and the file does not record the cluster
        // size, so the reader tries the FAT sizes and keeps the one whose records all read plausibly. Fitted, not from
        // Apple code: the Mac knew its volume's cluster size.
        private static readonly int[] ClusterSizes = [0, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

        /// <summary>
        /// The used records of a <c>FINDER.DAT</c> (free records, with a zero name length, are skipped). Nothing in the
        /// file is validated by the Mac, so corrupt records come back as they are.
        /// </summary>
        public static IReadOnlyList<PcExchangeRecord> ReadFinderData(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return ReadRecords(data);
        }

        /// <summary>
        /// The used records of a <c>FINDER.DAT</c> read from its volume, whose cluster size is known: every record not
        /// marked free, as the Mac uses them, corrupt or not.
        /// </summary>
        public static IReadOnlyList<PcExchangeRecord> ReadFinderData(byte[] data, int clusterSize)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (clusterSize < RecordLength) throw new ArgumentOutOfRangeException(nameof(clusterSize));
            var records = new List<PcExchangeRecord>();
            foreach (var offset in Offsets(data.Length, clusterSize))
            {
                if (Used(data, offset)) records.Add(Parse(data.AsMemory(offset, RecordLength)));
            }
            return records;
        }

        /// <summary>
        /// The Finder info File Exchange gives a file with no FINDER.DAT record: <c>TEXT</c>/<c>dosa</c>, no flags, and the
        /// default put-away folder, 2 (the root directory's ID), which File Exchange never reads.
        /// </summary>
        public static FinderInfo Placeholder { get; } = new()
        {
            Type = FourCC.FromString("TEXT"),
            Creator = FourCC.FromString("dosa"),
            Extended = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 },
        };

        // A file from a DOS disk as File Exchange presents it (confirmed in SheepShaver): with a record, its Mac name and
        // Finder info; creation from the DOS entry (the record's only when there is none: PC Exchange 1.0.4 read it,
        // File Exchange writes but never reads it); modification the later of the DOS entry's and the record's. Without
        // a record, the placeholder type and the DOS dates.
        internal static MacFile Apply(MacFile file, PcExchangeRecord? record, MacDate? dosCreated, MacDate? dosModified)
        {
            if (record is null)
                return file with { FinderInfo = Placeholder, Created = dosCreated, Modified = dosModified };
            var modified = record.Modified is { } m && (dosModified is null || m.Seconds >= dosModified.Value.Seconds)
                ? record.Modified
                : dosModified;
            return file with
            {
                Name = record.MacName,
                FinderInfo = record.FinderInfo,
                Created = dosCreated ?? record.Created,
                Modified = modified,
            };
        }

        private static List<PcExchangeRecord> ReadRecords(byte[] data)
        {
            var best = new List<int>();
            var bestPlausible = -1;
            foreach (var cluster in ClusterSizes)
            {
                var offsets = Offsets(data.Length, cluster).ToList();
                var used = offsets.Where(o => Used(data, o)).ToList();
                var plausible = used.Count(o => Plausible(data.AsSpan(o, RecordLength)));
                if (plausible == used.Count && plausible > bestPlausible)
                {
                    best = used;
                    bestPlausible = plausible;
                }
            }
            var records = new List<PcExchangeRecord>(best.Count);
            foreach (var offset in best) records.Add(Parse(data.AsMemory(offset, RecordLength)));
            return records;
        }

        /// <summary>
        /// The 11-character lookup key for a host name that is a valid 8.3 name (<c>FANTAS~1.EML</c> →
        /// <c>"FANTAS~1EML"</c>), or null.
        /// </summary>
        public static string? DosKey(string hostName)
        {
            var dot = hostName.LastIndexOf('.');
            var stem = dot < 0 ? hostName : hostName[..dot];
            var extension = dot < 0 ? "" : hostName[(dot + 1)..];
            if (stem.Length is < 1 or > 8 || extension.Length > 3 || stem.Contains('.')) return null;
            if (!(stem + extension).All(c => c is > ' ' and < (char)0x7F)) return null;
            return (stem.PadRight(8) + extension.PadRight(3)).ToUpperInvariant();
        }

        private static IEnumerable<int> Offsets(int length, int cluster)
        {
            if (cluster == 0)
            {
                for (var o = 0; o + RecordLength <= length; o += RecordLength) yield return o;
                yield break;
            }
            for (var c = 0; c < length; c += cluster)
            {
                for (var i = 0; i < cluster / RecordLength; i++)
                {
                    var o = c + i * RecordLength;
                    if (o + RecordLength <= length) yield return o;
                }
            }
        }

        // A free record has a zero name length or, as File Exchange leaves some stray ones, a zero first 8.3 byte.
        private static bool Used(byte[] data, int offset) => data[offset] != 0 && data[offset + 0x50] != 0;

        // A used record has a name of 1–31 bytes and a printable, space-padded 8.3 name.
        private static bool Plausible(ReadOnlySpan<byte> record) =>
            record[0] is >= 1 and <= 31 && record.Slice(0x50, 11).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0;

        private static PcExchangeRecord Parse(ReadOnlyMemory<byte> record)
        {
            var nameLength = Math.Min(record.Span[0], (byte)31);
            var reader = new BigEndianReader(record);
            return new PcExchangeRecord(
                new MacString(record.Span.Slice(1, nameLength)),
                FinderInfo.Read(record.Span.Slice(0x20, FinderInfo.Length)),
                Date(reader.ReadUInt32At(0x40)),
                Date(reader.ReadUInt32At(0x44)),
                reader.ReadUInt32At(0x4C),
                Encoding.Latin1.GetString(record.Span.Slice(0x50, 11)));
        }

        private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
    }
}
