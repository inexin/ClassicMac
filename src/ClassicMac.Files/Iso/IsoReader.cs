using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Iso
{
    /// <summary>
    /// ISO 9660 and High Sierra CD-ROM volumes, read as Mac OS 9's ISO 9660 File Access and High Sierra File Access
    /// (Foreign File Access plug-ins, version 5.3) present them. The on-disc layout is ECMA-119 (and its High Sierra
    /// predecessor); everything the Mac adds or decides — only the primary descriptor (no Joliet, no Rock Ridge), type
    /// and creator from Apple's 'AA'/'BA' system-use fields or <c>TEXT</c>/<c>hscd</c>, associated files as resource
    /// forks, 31-byte names, dates, icon positions, and where a directory listing stops — follows the disassembly of
    /// those plug-ins, confirmed in SheepShaver. Hybrid discs (with HFS) are read as HFS by the readers before this one,
    /// as the Mac mounts them.
    /// </summary>
    public sealed class IsoReader : IContainerReader
    {
        private const int Sector = 0x800;
        private const int FirstDescriptor = 16, DescriptorsTried = 64;
        private const byte FlagHidden = 0x01, FlagDirectory = 0x02, FlagAssociated = 0x04;

        /// <summary>The reader.</summary>
        public static IsoReader Instance { get; } = new();

        private IsoReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "ISO 9660 volume";

        /// <inheritdoc/>
        public bool CanRead(ForkData input) => Descriptor.Find(input) is not null;

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var descriptor = Descriptor.Find(input) ?? throw new InvalidDataException("Not an ISO 9660 or High Sierra volume.");
            return new Volume(input, descriptor, context).Files();
        }

        // The primary volume descriptor, as the plug-ins look for it: sectors 16 onwards until a terminator, "CD001" or
        // "CD-I" version 1 (ISO), or "CDROM" (High Sierra). Rejected, as the Mac rejects them: file structure version
        // other than 0 or 1, and (ISO but not CD-i) little- and big-endian path table sizes that differ.
        private sealed record Descriptor(bool HighSierra, bool CdI, bool Xa, int BlockSize, long RootOffset)
        {
            public static Descriptor? Find(ForkData input)
            {
                if (input.Length < (FirstDescriptor + 1) * (long)Sector) return null;
                for (var sector = FirstDescriptor; sector < FirstDescriptor + DescriptorsTried; sector++)
                {
                    if ((sector + 1) * (long)Sector > input.Length) return null;
                    var v = input.Slice(sector * (long)Sector, Sector).ToArray();
                    var id = System.Text.Encoding.ASCII.GetString(v, 1, 5).ToUpperInvariant();
                    var highSierra = System.Text.Encoding.ASCII.GetString(v, 9, 5).ToUpperInvariant() == "CDROM";
                    // A terminator ends the search: ISO's at byte 0, High Sierra's at byte 8.
                    if ((v[0] == 0xFF && id == "CD001") || (v[8] == 0xFF && highSierra)) return null;
                    if (v[0] == 1 && v[6] == 1 && id is "CD001" or "CD-I ")
                        return Iso(input, v, cdI: id == "CD-I ");
                    if (v[8] == 1 && v[14] == 1 && highSierra)
                        return ReadHighSierra(input, v);
                }
                return null;
            }

            private static Descriptor? Iso(ForkData input, byte[] v, bool cdI)
            {
                if (v[0x371] is not (0 or 1)) return null;
                var reader = new BigEndianReader(v);
                if (!cdI && BinaryPrimitives.ReadUInt32LittleEndian(v.AsSpan(0x84)) != reader.ReadUInt32At(0x88))
                    return null;
                var xa = v.AsSpan(0x400, 8).SequenceEqual("CD-XA001"u8);
                int blockSize = reader.ReadUInt16At(130);
                // The root comes from the first entry of the big-endian path table.
                var root = PathTableRoot(input, reader.ReadUInt32At(148), blockSize, 2);
                return root is { } r ? new Descriptor(false, cdI, xa, blockSize, r) : null;
            }

            private static Descriptor? ReadHighSierra(ForkData input, byte[] v)
            {
                var reader = new BigEndianReader(v);
                int blockSize = reader.ReadUInt16At(138);
                var root = PathTableRoot(input, reader.ReadUInt32At(164), blockSize, 0);
                return root is { } r ? new Descriptor(true, false, false, blockSize, r) : null;
            }

            private static long? PathTableRoot(ForkData input, uint table, int blockSize, int at)
            {
                if (blockSize is < 512 or > Sector || (blockSize & (blockSize - 1)) != 0) return null;
                var offset = (long)table * blockSize;
                if (offset + 8 > input.Length) return null;
                var extent = new BigEndianReader(input.Slice(offset, 8).ToArray()).ReadUInt32At(at);
                var root = (long)extent * blockSize;
                return root > 0 && root < input.Length ? root : null;
            }
        }

        // A directory record, as the plug-ins read it (the fields High Sierra moves are noted).
        private sealed class Record
        {
            private static readonly MacDate Latest = new(uint.MaxValue);

            private readonly byte[] b;

            public Record(byte[] bytes, bool highSierra)
            {
                b = bytes;
                HighSierra = highSierra;
                var reader = new BigEndianReader(bytes);
                Extent = reader.ReadInt32At(6);
                Size = reader.ReadUInt32At(14);
            }

            public bool HighSierra { get; }
            public int Length => b[0];
            public int AttributeBlocks => b[1];
            public int Extent { get; }
            public uint Size { get; }
            public byte Flags => HighSierra ? b[24] : b[25];
            public int UnitBlocks => b[26];
            public int GapBlocks => b[27];
            public int NameLength => b[32];
            public ReadOnlySpan<byte> RawName => b.AsSpan(33, NameLength);
            public ReadOnlyMemory<byte> Bytes => b;
            public bool IsAssociated => (Flags & FlagAssociated) != 0;
            public bool IsHidden => (Flags & FlagHidden) != 0;

            // A usable record: nonzero length and extent, a name of 1–37 bytes (High Sierra: year up to 2040).
            public bool IsValid => Length != 0 && Extent > 0 && NameLength is >= 1 and <= 0x25 && !(HighSierra && b[18] > 140);

            // The date, as local time: the GMT offset is ignored; ISO years from 2040 read as 2040-02-06 00:00; years
            // before 1904 read as 1904; an invalid month or day reads as 1.
            public MacDate Date
            {
                get
                {
                    int year = b[18] + 1900, month = b[19], day = b[20], hour = b[21], minute = b[22], second = b[23];
                    if (!HighSierra && year >= 2040) (year, month, day, hour, minute, second) = (2040, 2, 6, 0, 0, 0);
                    year = Math.Max(year, 1904);
                    if (month is < 1 or > 12) month = 1;
                    if (day is < 1 or > 31) day = 1;
                    var date = new DateTime(year, month, 1).AddDays(day - 1).AddHours(hour).AddMinutes(minute).AddSeconds(second);
                    return date > Latest.ToDateTime() ? Latest : MacDate.FromDateTime(date);
                }
            }
        }

        private sealed class Volume(ForkData image, Descriptor descriptor, ContainerContext context)
        {
            // The plug-ins read 0x10C bytes per ISO record, 0x52 per High Sierra one; the rest reads as zeros.
            private readonly int recordBuffer = descriptor.HighSierra ? 0x52 : 0x10C;
            private readonly List<MacFile> files = [];
            private readonly HashSet<long> directoriesRead = [];
            private int entries;

            public List<MacFile> Files()
            {
                ReadDirectory(descriptor.RootOffset, []);
                return files;
            }

            private Record RecordAt(long position)
            {
                var bytes = new byte[0x10C];
                if (position >= 0 && position < image.Length)
                    image.Slice(position, Math.Min(recordBuffer, image.Length - position)).ToArray().CopyTo(bytes, 0);
                return new Record(bytes, descriptor.HighSierra);
            }

            // A directory's listing runs from after its "." and ".." records to the end given by "."'s size.
            private void ReadDirectory(long directoryId, List<MacString> path)
            {
                var self = RecordAt(directoryId);
                var start = ((long)self.Extent + self.AttributeBlocks) * descriptor.BlockSize;
                var dot = RecordAt(start);
                var end = start + dot.Size;
                var dotDot = RecordAt(start + dot.Length);
                var position = start + dot.Length + dotDot.Length;

                var index = 0;
                while (NextEntry(ref position, end) is { } entry)
                {
                    var (record, id) = entry;
                    index++;
                    if (++entries > context.Options.MaxVolumeEntries)
                    {
                        context.Report(DiagnosticSeverity.Error, "iso.too-many-entries",
                            $"The volume holds more than {context.Options.MaxVolumeEntries} entries; reading stopped.");
                        return;
                    }
                    var name = Name(record);
                    if (IsDirectory(record))
                    {
                        if (!directoriesRead.Add(((long)record.Extent + record.AttributeBlocks) * descriptor.BlockSize))
                        {
                            context.Report(DiagnosticSeverity.Error, "iso.folder-loop", $"Folder \"{name}\" was already read; skipped.");
                            continue;
                        }
                        ReadDirectory(id, [.. path, name]);
                        continue;
                    }
                    files.Add(File(id, record, name, path, index));
                }
            }

            // The next entry and its ID (the byte offset of its first record). ISO puts the associated record (the
            // resource fork) first and pairs it with an immediately following record of the same name; High Sierra
            // puts the data record first and an associated one after it.
            private (Record Record, long Id)? NextEntry(ref long position, long end)
            {
                if (ReadRecord(ref position, end) is not { } first) return null;
                var id = position - first.Length;
                if (descriptor.HighSierra || first.IsAssociated)
                {
                    var after = position;
                    if (ReadRecord(ref after, end) is { } second && (!descriptor.HighSierra || second.IsAssociated)
                        && SameName(first, second))
                        position = after;
                }
                return (first, id);
            }

            // One record. An invalid one sends the reader to the next sector; an invalid record there, or one at a
            // sector start, ends the directory.
            private Record? ReadRecord(ref long position, long end)
            {
                if (position >= end) return null; // a record starts inside its directory's extent
                var record = RecordAt(position);
                if (!record.IsValid)
                {
                    var next = (position + Sector - 1) & ~(long)(Sector - 1);
                    if (next >= end) return null;
                    position = next;
                    record = RecordAt(position);
                    if (!record.IsValid) return null;
                }
                position += record.Length;
                return record;
            }

            private bool IsDirectory(Record record)
            {
                if (!descriptor.CdI) return (record.Flags & FlagDirectory) != 0;
                // CD-i keeps the directory bit in its system-use field.
                var su = 33 + record.NameLength + ((record.NameLength & 1) != 0 ? 0 : 1);
                return (record.Bytes.Span[su + 4] & 0x80) != 0;
            }

            // A name of at most 31 bytes, cut before the version is stripped, trailing spaces trimmed; then ";digits"
            // stripped and a final '.' dropped from names of up to 9 bytes. Case and ':' are kept.
            private static MacString Name(Record record)
            {
                var raw = record.RawName[..Math.Min(record.NameLength, 31)];
                var n = raw.Length;
                while (n > 0 && raw[n - 1] == 0x20) n--;
                var j = n;
                while (j > 0 && raw[j - 1] != (byte)';' && raw[j - 1] is >= (byte)'0' and <= (byte)'9') j--;
                if (j > 0 && raw[j - 1] == (byte)';') n = j - 1;
                if (n is > 0 and <= 9 && raw[n - 1] == (byte)'.') n--;
                return new MacString(raw[..n]);
            }

            private static bool SameName(Record a, Record b) =>
                string.Equals(Name(a).ToMacRoman(), Name(b).ToMacRoman(), StringComparison.OrdinalIgnoreCase);

            private MacFile File(long id, Record record, MacString name, List<MacString> path, int index)
            {
                // The data and resource records: ISO lists the associated (resource) record first, High Sierra second.
                Record? data, resource;
                var next = RecordAt(id + record.Length);
                var pairs = next.IsValid && SameName(record, next);
                if (!descriptor.HighSierra)
                {
                    resource = record.IsAssociated ? record : null;
                    data = !record.IsAssociated ? record : pairs && !next.IsAssociated ? next : null;
                }
                else
                {
                    data = !record.IsAssociated ? record : null;
                    resource = record.IsAssociated ? record : pairs && next.IsAssociated ? next : null;
                }

                // Finder info and date from the data record, then the resource record, which wins.
                var info = new FinderInfo
                {
                    Type = FourCC.FromString("TEXT"),
                    Creator = FourCC.FromString("hscd"),
                    // Listed by index: a six-column grid of 64-pixel cells.
                    Location = new MacPoint((short)((index - 1) / 6 * 64), (short)((index - 1) % 6 * 64)),
                };
                ushort flags = 0;
                MacDate? date = null;
                foreach (var fork in new[] { data, resource })
                {
                    if (fork is null) continue;
                    date = fork.Date;
                    var (type, creator, apply) = FinderInfoOf(fork);
                    info = info with { Type = type, Creator = creator };
                    flags = apply(flags);
                    if (fork.IsHidden) flags |= (ushort)FinderFlags.IsInvisible;
                }
                return new MacFile
                {
                    Name = name,
                    FolderPath = path,
                    FinderInfo = info with { Flags = (FinderFlags)flags },
                    Created = date,
                    Modified = date,
                    DataFork = data is null ? ForkData.Empty : Fork(data, name),
                    ResourceFork = resource is null ? ForkData.Empty : Fork(resource, name),
                };
            }

            // Type, creator and a Finder-flags rule from the system-use field. 'BA' at its start (the old form): kinds
            // 2 and 4 set hasBeenInited ($0100), 3 and 5 also hasBundle ($2100), 6 takes the stored flags masked
            // with $B020. Otherwise the first 'AA' entry of version 2 sets the flags to its own masked with $B020, plus
            // $0100 — so invisible, custom icon and stationery never come from 'AA'. Default TEXT/hscd.
            private (FourCC Type, FourCC Creator, Func<ushort, ushort> Flags) FinderInfoOf(Record record)
            {
                var b = record.Bytes.Span;
                var reader = new BigEndianReader(record.Bytes);
                var p = 0x21 + record.NameLength + ((record.NameLength & 1) != 0 ? 0 : 1);
                if (record.NameLength + 0x21 < record.Length)
                {
                    if (b[p] == (byte)'B' && b[p + 1] == (byte)'A')
                    {
                        var type = reader.ReadFourCCAt(p + 3);
                        var creator = reader.ReadFourCCAt(p + 7);
                        switch (b[p + 2])
                        {
                            case 2 or 4: return (type, creator, f => (ushort)(f | 0x0100));
                            case 3 or 5: return (type, creator, f => (ushort)(f | 0x2100));
                            case 6:
                                var stored = reader.ReadUInt16At(p + 11);
                                return (type, creator, f => (ushort)(f | (stored & 0xB020) | 0x0100));
                        }
                    }
                    else
                    {
                        if (descriptor.Xa) p += 14;
                        while (p + 14 <= b.Length && p < record.Length)
                        {
                            if (b[p] == (byte)'A' && b[p + 1] == (byte)'A' && b[p + 3] == 2)
                            {
                                var stored = reader.ReadUInt16At(p + 12);
                                return (reader.ReadFourCCAt(p + 4),
                                    reader.ReadFourCCAt(p + 8),
                                    _ => (ushort)((stored & 0xB020) | 0x0100));
                            }
                            if (b[p + 2] < 4) break;
                            p += b[p + 2];
                        }
                    }
                }
                return (FourCC.FromString("TEXT"), FourCC.FromString("hscd"), f => (ushort)(f | 0x0100));
            }

            // A fork's bytes: from (extent + extended attribute blocks), in interleaved units with gaps when the record
            // says so (multi-extent files are not joined: each extent is its own entry).
            private ForkData Fork(Record record, MacString name)
            {
                var blockSize = descriptor.BlockSize;
                var offset = ((long)record.Extent + record.AttributeBlocks) * blockSize;
                long wanted = record.Size;
                var ranges = new List<(long Offset, long Length)>();
                if (record.UnitBlocks == 0)
                    ranges.Add((offset, wanted));
                else
                {
                    var unit = (long)record.UnitBlocks * blockSize;
                    var step = unit + (long)record.GapBlocks * blockSize;
                    for (long covered = 0; covered < wanted; covered += unit, offset += step)
                        ranges.Add((offset, Math.Min(unit, wanted - covered)));
                }

                var inImage = new List<(long, long)>();
                long available = 0;
                foreach (var (at, count) in ranges)
                {
                    var take = Math.Clamp(image.Length - at, 0, count);
                    if (take > 0) inImage.Add((at, take));
                    available += take;
                    if (take < count) break;
                }
                if (available < wanted)
                {
                    context.Report(DiagnosticSeverity.Error, "iso.short",
                        $"\"{name}\" has {available} of its {wanted} bytes on the disc image; the rest is missing.");
                    wanted = available;
                }
                return new ExtentForkData(image, inImage, wanted);
            }
        }
    }
}
