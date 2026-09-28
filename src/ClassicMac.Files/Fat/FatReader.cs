using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Fat
{
    /// <summary>
    /// FAT12, FAT16 and FAT32 volumes, with the PC Exchange / File Exchange data Mac OS 7.1–9 kept on them. The disk
    /// layout follows Microsoft's FAT specification (boot sector, allocation tables, 32-byte directory entries, VFAT
    /// long names); the Mac view of each file — name, Finder info, dates and resource fork from <c>FINDER.DAT</c> and
    /// <c>RESOURCE.FRK</c> — follows File Exchange as disassembled and confirmed in SheepShaver (<see cref="PcExchange"/>).
    /// </summary>
    public sealed class FatReader : IContainerReader
    {
        private const int EntryLength = 32;
        private const byte AttrReadOnly = 0x01, AttrHidden = 0x02, AttrSystem = 0x04, AttrVolume = 0x08, AttrDirectory = 0x10;
        private const byte AttrLongName = 0x0F;

        /// <summary>The reader.</summary>
        public static FatReader Instance { get; } = new();

        private FatReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "FAT volume";

        /// <inheritdoc/>
        public bool CanRead(ForkData input) => Geometry.TryRead(input.ReadPrefix(512), input.Length, out _);

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!Geometry.TryRead(input.ReadPrefix(512), input.Length, out var geometry))
                throw new InvalidDataException("Not a FAT volume.");
            return new Volume(input, geometry, context).Files();
        }

        // The boot sector's parameters and what follows from them (Microsoft FAT specification, "BPB" and "FAT type
        // determination": the type is decided by the cluster count alone).
        internal sealed record Geometry(
            int BytesPerSector, int SectorsPerCluster, long FatOffset, long FatLength, long RootOffset, int RootEntries,
            long DataOffset, long Clusters, int Bits, uint RootCluster)
        {
            public int ClusterSize => BytesPerSector * SectorsPerCluster;

            public static bool TryRead(ReadOnlySpan<byte> boot, long length, out Geometry geometry)
            {
                geometry = null!;
                if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA) return false;
                if (boot[0] is not (0xEB or 0xE9)) return false;
                int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
                int sectorsPerCluster = boot[13];
                int reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
                int fats = boot[16];
                int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
                long totalSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
                if (totalSectors == 0) totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[32..]);
                long fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
                if (fatSectors == 0) fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
                if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return false;
                if (sectorsPerCluster == 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0) return false;
                if (reserved == 0 || fats is < 1 or > 2 || fatSectors == 0 || totalSectors == 0) return false;

                var rootSectors = (rootEntries * EntryLength + bytesPerSector - 1) / bytesPerSector;
                var firstData = reserved + fats * fatSectors + rootSectors;
                if (firstData >= totalSectors) return false;
                var clusters = (totalSectors - firstData) / sectorsPerCluster;
                var bits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
                var rootCluster = bits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]) & 0x0FFFFFFF : 0;
                if (bits == 32 && rootEntries != 0) return false;
                geometry = new Geometry(
                    bytesPerSector, sectorsPerCluster, (long)reserved * bytesPerSector, fatSectors * bytesPerSector,
                    (reserved + fats * fatSectors) * bytesPerSector, rootEntries, firstData * bytesPerSector, clusters,
                    bits, rootCluster);
                return (long)reserved * bytesPerSector + fatSectors * bytesPerSector <= length;
            }
        }

        // One directory entry, with its long name if it had one.
        private sealed record Entry(byte[] ShortName, string? LongName, byte Attributes, uint FirstCluster, long Size,
            ushort CreatedDate, ushort CreatedTime, ushort ModifiedDate, ushort ModifiedTime)
        {
            public bool IsDirectory => (Attributes & AttrDirectory) != 0;

            // The 11-byte 8.3 name as FINDER.DAT keys it.
            public string Key => Encoding.Latin1.GetString(ShortName);

            // The 8.3 name as a file name: "NOTE.TXT".
            public string DisplayShortName => MacShortName.ToMacRoman();

            // The 8.3 name as File Exchange shows it: the stored bytes as they are, each read as the Mac Roman byte of the
            // same value (no code page conversion, no case change); a lead $05 stands for $E5; the base cut at its first
            // byte of $20 or less, the extension at its first below $20 with trailing spaces trimmed ("A B" stays).
            public MacString MacShortName
            {
                get
                {
                    var stem = ShortName.AsSpan(0, 8).ToArray();
                    if (stem[0] == 0x05) stem[0] = 0xE5;
                    if (stem.AsSpan().IndexOfAnyInRange((byte)0, (byte)0x20) is var stemEnd and >= 0) stem = stem[..stemEnd];
                    ReadOnlySpan<byte> extension = ShortName.AsSpan(8, 3);
                    if (extension.IndexOfAnyInRange((byte)0, (byte)0x1F) is var cut and >= 0) extension = extension[..cut];
                    extension = extension.TrimEnd((byte)' ');
                    return new MacString(extension.Length > 0 ? [.. stem, (byte)'.', .. extension] : stem);
                }
            }
        }

        private sealed class Volume(ForkData image, Geometry geometry, ContainerContext context)
        {
            private readonly byte[] fat = image.Slice(geometry.FatOffset, Math.Min(geometry.FatLength, image.Length - geometry.FatOffset))
                .ToArray(context.Options.MaxExpandedBytesPerInput);
            private readonly List<MacFile> files = [];
            private readonly HashSet<uint> directoriesRead = [];
            private int entries;

            public List<MacFile> Files()
            {
                var root = geometry.Bits == 32
                    ? ReadChain(geometry.RootCluster, "the root folder") is { } chain ? chain.ToArray(context.Options.MaxExpandedBytesPerInput) : []
                    : image.Slice(geometry.RootOffset, Math.Min((long)geometry.RootEntries * EntryLength, image.Length - geometry.RootOffset)).ToArray();
                ReadDirectory(root, []);
                return files;
            }

            private void ReadDirectory(byte[] directory, List<MacString> path)
            {
                var list = Entries(directory);
                var finderData = list.FirstOrDefault(e => !e.IsDirectory && e.Key == "FINDER  DAT");
                var records = finderData is not null && ReadChain(finderData.FirstCluster, "FINDER.DAT", finderData.Size) is { } data
                    ? PcExchange.ReadFinderData(data.ToArray(context.Options.MaxExpandedBytesPerInput), geometry.ClusterSize)
                    : [];
                var resourceFolder = list.FirstOrDefault(e => e.IsDirectory && e.Key == "RESOURCEFRK");
                var forks = resourceFolder is not null && ReadChain(resourceFolder.FirstCluster, "RESOURCE.FRK") is { } rsrc
                    ? Entries(rsrc.ToArray(context.Options.MaxExpandedBytesPerInput)).Where(e => !e.IsDirectory).ToDictionary(e => e.Key, e => e)
                    : new Dictionary<string, Entry>();

                foreach (var entry in list)
                {
                    // File Exchange hides its own files by name.
                    if (entry.Key is "FINDER  DAT" or "FILEID  DAT" or "RESOURCEFRK") continue;
                    if (++entries > context.Options.MaxVolumeEntries)
                    {
                        context.Report(DiagnosticSeverity.Error, "fat.too-many-entries",
                            $"The volume holds more than {context.Options.MaxVolumeEntries} entries; reading stopped.");
                        return;
                    }
                    var record = records.FirstOrDefault(r => r.DosName == entry.Key);
                    // The record's name, else the long name, else the 8.3 name (File Exchange's lookup order).
                    var name = record?.MacName
                        ?? (entry.LongName is { } longName ? FatNames.FromLongName(longName) : entry.MacShortName);
                    if (record is not null && name.Bytes.IndexOfAnyInRange((byte)0, (byte)0x1F) >= 0)
                    {
                        // File Exchange can create a record with a garbage name (for a file with a resource fork and no
                        // record) and shows it as it is; so does this reader.
                        context.Report(DiagnosticSeverity.Warning, "fat.suspect-name",
                            $"The FINDER.DAT record for {entry.DisplayShortName} has control characters in its Mac name; shown as File Exchange shows it.");
                    }
                    if (entry.IsDirectory)
                    {
                        if (entry.FirstCluster == 0 || !directoriesRead.Add(entry.FirstCluster))
                        {
                            context.Report(DiagnosticSeverity.Error, "fat.folder-loop",
                                $"Folder \"{name}\" starts at cluster {entry.FirstCluster}, which is empty or already read; skipped.");
                            continue;
                        }
                        if (ReadChain(entry.FirstCluster, $"folder \"{name}\"") is { } folder)
                            ReadDirectory(folder.ToArray(context.Options.MaxExpandedBytesPerInput), [.. path, name]);
                        continue;
                    }

                    var file = new MacFile
                    {
                        Name = name,
                        FolderPath = path,
                        DataFork = entry.Size == 0 ? ForkData.Empty : ReadChain(entry.FirstCluster, $"\"{name}\"", entry.Size) ?? ForkData.Empty,
                        ResourceFork = forks.TryGetValue(entry.Key, out var fork) && fork.Size > 0
                            ? ReadChain(fork.FirstCluster, $"\"{name}\"'s resource fork", fork.Size) ?? ForkData.Empty
                            : ForkData.Empty,
                    };
                    file = PcExchange.Apply(file, record,
                        DosTime.FromFields(entry.CreatedDate, entry.CreatedTime), DosTime.FromFields(entry.ModifiedDate, entry.ModifiedTime));
                    if (context.Options.ExtensionMap is { } map)
                        file = file with { FinderInfo = map.Apply(file.FinderInfo, file.Name.ToMacRoman()) };
                    // DOS hidden or system makes the file invisible.
                    if ((entry.Attributes & (AttrHidden | AttrSystem)) != 0)
                        file = file with { FinderInfo = file.FinderInfo with { Flags = file.FinderInfo.Flags | FinderFlags.IsInvisible } };
                    files.Add(file);
                }
            }

            // The entries of a directory: to the first zero entry, deleted ones skipped, long names gathered and checked.
            private List<Entry> Entries(byte[] directory)
            {
                var result = new List<Entry>();
                var longParts = new List<(int Order, string Part)>();
                byte longChecksum = 0;
                for (var at = 0; at + EntryLength <= directory.Length; at += EntryLength)
                {
                    var e = directory.AsSpan(at, EntryLength);
                    if (e[0] == 0x00) break;
                    if (e[0] == 0xE5)
                    {
                        longParts.Clear();
                        continue;
                    }
                    if (e[11] == AttrLongName)
                    {
                        if ((e[0] & 0x40) != 0) longParts.Clear();
                        longChecksum = e[13];
                        longParts.Add((e[0] & 0x1F, LongPart(e)));
                        continue;
                    }
                    var shortName = e[..11].ToArray(); // as stored: FINDER.DAT keys compare these bytes
                    string? longName = null;
                    if (longParts.Count > 0 && Checksum(e[..11]) == longChecksum)
                        longName = string.Concat(longParts.OrderBy(p => p.Order).Select(p => p.Part));
                    longParts.Clear();
                    if ((e[11] & AttrVolume) != 0 && (e[11] & AttrDirectory) == 0) continue; // volume label
                    if (shortName[0] == (byte)'.') continue; // "." and ".."
                    uint cluster = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
                    if (geometry.Bits == 32) cluster |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16;
                    result.Add(new Entry(shortName, longName, e[11], cluster, BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(e[16..]), BinaryPrimitives.ReadUInt16LittleEndian(e[14..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(e[24..]), BinaryPrimitives.ReadUInt16LittleEndian(e[22..])));
                }
                return result;
            }

            private static string LongPart(ReadOnlySpan<byte> e)
            {
                var chars = new List<char>(13);
                foreach (var (from, count) in new[] { (1, 5), (14, 6), (28, 2) })
                {
                    for (var i = 0; i < count; i++)
                    {
                        var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[(from + 2 * i)..]);
                        if (c is '\0' or '￿') return new string([.. chars]);
                        chars.Add(c);
                    }
                }
                return new string([.. chars]);
            }

            private static byte Checksum(ReadOnlySpan<byte> shortName)
            {
                byte sum = 0;
                foreach (var b in shortName) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
                return sum;
            }

            // A cluster chain as a fork, cut to length (or whole clusters when no length is given); loops, free or bad
            // clusters and references outside the volume end it, reported.
            private ForkData? ReadChain(uint first, string what, long? length = null)
            {
                var ranges = new List<(long Offset, long Length)>();
                var seen = new HashSet<uint>();
                long covered = 0;
                var end = geometry.Bits switch { 12 => 0xFF8u, 16 => 0xFFF8u, _ => 0x0FFFFFF8u };
                for (var cluster = first; cluster < end;)
                {
                    if (cluster < 2 || cluster >= geometry.Clusters + 2 || !seen.Add(cluster))
                    {
                        context.Report(DiagnosticSeverity.Error, "fat.bad-chain",
                            $"The cluster chain of {what} reaches cluster {cluster}, outside the volume, free or already used.");
                        break;
                    }
                    var offset = geometry.DataOffset + (long)(cluster - 2) * geometry.ClusterSize;
                    if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == offset)
                        ranges[^1] = (ranges[^1].Offset, ranges[^1].Length + geometry.ClusterSize);
                    else
                        ranges.Add((offset, geometry.ClusterSize));
                    covered += geometry.ClusterSize;
                    if (length is { } l && covered >= l) break;
                    cluster = Next(cluster);
                }

                var inImage = new List<(long, long)>();
                long available = 0;
                foreach (var (offset, count) in ranges)
                {
                    var take = Math.Clamp(image.Length - offset, 0, count);
                    if (take > 0) inImage.Add((offset, take));
                    available += take;
                    if (take < count) break;
                }
                var wanted = length ?? available;
                if (available < wanted)
                {
                    context.Report(DiagnosticSeverity.Error, "fat.short",
                        $"{what} has {available} of its {wanted} bytes on the volume; the rest is missing.");
                    wanted = available;
                }
                return new ExtentForkData(image, inImage, wanted);
            }

            // The allocation table entry for a cluster: 12 bits packed two in three bytes, 16 bits, or 28 of 32 bits.
            private uint Next(uint cluster)
            {
                switch (geometry.Bits)
                {
                    case 12:
                    {
                        var at = (int)(cluster * 3 / 2);
                        if (at + 1 >= fat.Length) return 0;
                        var pair = (uint)(fat[at] | fat[at + 1] << 8);
                        return (cluster & 1) == 0 ? pair & 0xFFF : pair >> 4;
                    }
                    case 16:
                    {
                        var at = (int)(cluster * 2);
                        return at + 1 >= fat.Length ? 0u : BinaryPrimitives.ReadUInt16LittleEndian(fat.AsSpan(at));
                    }
                    default:
                    {
                        var at = (long)cluster * 4;
                        return at + 3 >= fat.Length ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan((int)at)) & 0x0FFFFFFF;
                    }
                }
            }
        }
    }
}
