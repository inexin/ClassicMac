using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs
{
    /// <summary>
    /// HFS volumes, from <i>Inside Macintosh: Files</i>, "Data Organization on Volumes": the master directory block at
    /// byte 1024, the catalog B-tree (folders, files, threads) and the extents overflow B-tree, 512-byte nodes. Every
    /// file on the volume comes out with its folder path, Finder info, dates and both forks, which are read from the
    /// image in place. HFS Plus volumes use the corresponding HFS Plus reader.
    /// </summary>
    public sealed class HfsReader : IContainerReader, IVolumeReader
    {
        private const int MdbOffset = 1024;
        private const int MdbLength = 162;
        private const ushort HfsSignature = 0x4244; // 'BD'
        private const ushort HfsPlusSignature = 0x482B; // 'H+'
        private const ushort HfsXSignature = 0x4858; // 'HX'
        private const int NodeSize = 512;
        private const uint RootParentId = 1, RootFolderId = 2, CatalogFileId = 4;

        /// <summary>The reader.</summary>
        public static HfsReader Instance { get; } = new();

        private HfsReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "HFS volume";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            if (input.Length < MdbOffset + MdbLength)
            {
                return false;
            }

            var signature = new BigEndianReader(input.Slice(MdbOffset, 2).ToArray()).ReadUInt16At(0);
            return signature is HfsSignature or HfsPlusSignature or HfsXSignature;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) => Read(input, context, null);

        /// <summary>
        /// The volume's dates (docs/formats/file-systems/hfs.md §1.3, hfs-plus.md §1.1): an HFS volume's from its MDB, an
        /// HFS Plus volume's from its header, and a wrapped one's from the embedded volume's header; null when the input
        /// is not a volume or the wrapper's embedded extent is unusable.
        /// </summary>
        public VolumeInfo? ReadVolumeInfo(ForkData input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!CanRead(input))
            {
                return null;
            }

            var mdb = new BigEndianReader(input.Slice(MdbOffset, MdbLength).ToArray());
            var signature = mdb.ReadUInt16At(0);
            if (signature is HfsPlusSignature or HfsXSignature)
            {
                return PlusInfo(mdb);
            }

            if (mdb.ReadUInt16At(0x7C) == HfsPlusSignature)
            {
                return EmbeddedOffset(input, mdb) is { } offset && offset <= input.Length - (MdbOffset + MdbLength)
                    && new BigEndianReader(input.Slice(offset + MdbOffset, MdbLength).ToArray()) is var header
                    && header.ReadUInt16At(0) is HfsPlusSignature or HfsXSignature
                    ? PlusInfo(header)
                    : null;
            }

            // The MDB (hfs.md §1.3): drAtrb +$0A, drNmAlBlks +$12, drAlBlkSiz +$14, drFreeBks +$22, drVN +$24 (a Str27),
            // drFilCnt +$54, drDirCnt +$58.
            var attributes = mdb.ReadUInt16At(0x0A);
            return new VolumeInfo("HFS", Date(mdb.ReadUInt32At(0x02)), Date(mdb.ReadUInt32At(0x06)), Date(mdb.ReadUInt32At(0x40)))
            {
                Name = VolumeName(mdb, 0x24),
                BlockSize = mdb.ReadUInt32At(0x14),
                TotalBlocks = mdb.ReadUInt16At(0x12),
                FreeBlocks = mdb.ReadUInt16At(0x22),
                Files = mdb.ReadUInt32At(0x54),
                Folders = mdb.ReadUInt32At(0x58),
                SoftwareLocked = (attributes & 0x8000) != 0,
                HardwareLocked = (attributes & 0x0080) != 0,
                BlessedFolderId = Blessed(mdb.ReadUInt32At(0x5C)),                                  // drFndrInfo[0]
            };
        }

        private static uint? Blessed(uint id) => id == 0 ? null : id;

        // A drVN: a length byte and up to 27 Mac OS Roman characters.
        internal static string VolumeName(BigEndianReader mdb, int offset) =>
            MacRoman.Decode(mdb.Source.Span.Slice(offset + 1, Math.Min(mdb.ReadByteAt(offset), (byte)27)));

        // The volume header (hfs-plus.md §1.1): attributes +$04, createDate (local time), modifyDate and backupDate (UTC)
        // at +$10, +$14 and +$18, fileCount +$20, folderCount +$24, blockSize +$28, totalBlocks +$2C, freeBlocks +$30.
        private static VolumeInfo PlusInfo(BigEndianReader header)
        {
            var attributes = header.ReadUInt32At(0x04);
            return new("HFS Plus", Date(header.ReadUInt32At(0x10)), Date(header.ReadUInt32At(0x14)), Date(header.ReadUInt32At(0x18)))
            {
                BlockSize = header.ReadUInt32At(0x28),
                TotalBlocks = header.ReadUInt32At(0x2C),
                FreeBlocks = header.ReadUInt32At(0x30),
                Files = header.ReadUInt32At(0x20),
                Folders = header.ReadUInt32At(0x24),
                SoftwareLocked = (attributes & 0x8000) != 0,
                HardwareLocked = (attributes & 0x0080) != 0,
                BlessedFolderId = Blessed(header.ReadUInt32At(0x50)),                               // finderInfo[0]
            };
        }

        private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

        // Where a wrapper's embedded HFS Plus volume starts, as ReadEmbeddedPlus finds it; null when the extent is invalid.
        private static long? EmbeddedOffset(ForkData input, BigEndianReader mdb)
        {
            uint blockSize = mdb.ReadUInt32At(0x14);
            uint allocationBlocks = mdb.ReadUInt16At(0x12);
            uint embeddedStart = mdb.ReadUInt16At(0x7E);
            uint embeddedBlocks = mdb.ReadUInt16At(0x80);
            if (blockSize == 0 || allocationBlocks == 0 || embeddedBlocks == 0 || (ulong)embeddedStart + embeddedBlocks > allocationBlocks)
            {
                return null;
            }

            ulong offset = (ulong)mdb.ReadUInt16At(0x1C) * 512 + (ulong)embeddedStart * blockSize;
            return offset > (ulong)input.Length ? null : (long)offset;
        }

        /// <summary>
        /// The volume's folders with their Finder information (window, icon place, flags) and dates, the root folder
        /// included (HFS and HFS Plus, plain or wrapped). The volume is read as <see cref="Read(ForkData, ContainerContext)"/>
        /// reads it, with the same checks and diagnostics; HFS Plus's private hard-link folders are left out.
        /// </summary>
        public IReadOnlyList<MacFolder> ReadFolders(ForkData input, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(context);
            var folders = new List<MacFolder>();
            Read(input, context, folders);
            return folders;
        }

        private IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context, List<MacFolder>? folders)
        {
            if (!CanRead(input))
            {
                throw new InvalidDataException("Not an HFS volume.");
            }

            var mdb = input.Slice(MdbOffset, MdbLength).ToArray();
            var reader = new BigEndianReader(mdb);
            if (reader.ReadUInt16At(0) is HfsPlusSignature or HfsXSignature)
            {
                return HfsPlusReader.Read(input, context, folders);
            }

            if (reader.ReadUInt16At(0x7C) == HfsPlusSignature)
            {
                return ReadEmbeddedPlus(input, mdb, context, folders);
            }

            ReportAlternateMdbProblem(input, context);
            return new Volume(input, mdb, context).Files(folders);
        }

        private static void ReportAlternateMdbProblem(ForkData input, ContainerContext context)
        {
            const int AlternateMdbOffsetFromEnd = 1024;
            if (input.Length < AlternateMdbOffsetFromEnd + sizeof(ushort) ||
                new BigEndianReader(input.Slice(input.Length - AlternateMdbOffsetFromEnd, sizeof(ushort)).ToArray())
                    .ReadUInt16At(0) != HfsSignature)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.alternate-mdb",
                    "The HFS volume's alternate master directory block is missing or has an invalid signature.",
                    Math.Max(0, input.Length - AlternateMdbOffsetFromEnd));
            }
        }

        private static IReadOnlyList<MacFile> ReadEmbeddedPlus(ForkData input, byte[] mdb, ContainerContext context, List<MacFolder>? folders)
        {
            var reader = new BigEndianReader(mdb);
            uint blockSize = reader.ReadUInt32At(0x14);
            uint allocationBlocks = reader.ReadUInt16At(0x12);
            uint allocationStart = reader.ReadUInt16At(0x1C);
            uint embeddedStart = reader.ReadUInt16At(0x7E);
            uint embeddedBlocks = reader.ReadUInt16At(0x80);
            if (blockSize == 0 || allocationBlocks == 0 || embeddedBlocks == 0 ||
                (ulong)embeddedStart + embeddedBlocks > allocationBlocks)
            {
                throw new InvalidDataException("The HFS wrapper's embedded HFS Plus extent is invalid.");
            }

            ulong allocationAreaStart = (ulong)allocationStart * 512;
            ulong offset = allocationAreaStart + (ulong)embeddedStart * blockSize;
            ulong length = (ulong)embeddedBlocks * blockSize;
            ulong allocationAreaEnd = allocationAreaStart + (ulong)allocationBlocks * blockSize;
            if (offset > (ulong)input.Length || length > (ulong)input.Length - offset ||
                offset + length > allocationAreaEnd || length < 1536)
            {
                throw new InvalidDataException("The HFS wrapper's embedded HFS Plus volume lies outside the image.");
            }

            ReportUnallocatedEmbeddedVolume(input, mdb, embeddedStart, embeddedBlocks, context);
            return HfsPlusReader.Read(input.Slice(checked((long)offset), checked((long)length)), context, folders);
        }

        private static void ReportUnallocatedEmbeddedVolume(ForkData input, byte[] mdb, uint embeddedStart,
            uint embeddedBlocks, ContainerContext context)
        {
            var reader = new BigEndianReader(mdb);
            int allocationBlocks = reader.ReadUInt16At(0x12);
            int bitmapStartBlock = reader.ReadUInt16At(0x0E);
            int bitmapLength = (allocationBlocks + 7) / 8;
            long bitmapOffset = bitmapStartBlock * 512L;
            if (bitmapOffset > input.Length || bitmapLength > input.Length - bitmapOffset)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.bitmap-truncated",
                    "The HFS wrapper's volume bitmap does not fit in the image.", bitmapOffset);
                return;
            }

            byte[] bitmap = input.Slice(bitmapOffset, bitmapLength).ToArray(bitmapLength);
            uint end = embeddedStart + embeddedBlocks;
            for (uint block = embeddedStart; block < end; block++)
            {
                if ((bitmap[checked((int)(block / 8))] & (0x80 >> (int)(block & 7))) == 0)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.wrapper-extent-unallocated",
                        $"The HFS wrapper's embedded HFS Plus extent includes allocation block {block}, which the wrapper bitmap marks free.",
                        bitmapOffset + block / 8);
                    return;
                }
            }
        }

        // One HFS volume being read.
        private sealed class Volume
        {
            private readonly ForkData image;
            private readonly ContainerContext context;
            private readonly long blockSize;
            private readonly long firstBlock;
            private readonly int blockCount;
            private readonly int bitmapStartBlock;
            private readonly int freeBlockCount;
            private byte[]? volumeBitmap;
            private readonly Dictionary<(byte Fork, uint File), List<(uint StartBlock, byte[] Record)>> overflow = [];

            public Volume(ForkData image, byte[] mdb, ContainerContext context)
            {
                this.image = image;
                this.context = context;
                var m = new BigEndianReader(mdb);
                blockSize = m.ReadUInt32At(0x14);
                firstBlock = m.ReadUInt16At(0x1C) * 512L;
                blockCount = m.ReadUInt16At(0x12);
                bitmapStartBlock = m.ReadUInt16At(0x0E);
                freeBlockCount = m.ReadUInt16At(0x22);
                Name = new MacString(m.ReadBytesAt(0x25, Math.Min(mdb[0x24], (byte)27)));
                FileCount = m.ReadUInt32At(0x54);
                FolderCount = m.ReadUInt32At(0x58);
                ExtentsLength = m.ReadUInt32At(0x82);
                ExtentsRecord = m.ReadBytesAt(0x86, 12).ToArray();
                CatalogLength = m.ReadUInt32At(0x92);
                CatalogRecord = m.ReadBytesAt(0x96, 12).ToArray();
            }

            public MacString Name { get; }

            private uint FileCount { get; }

            private uint FolderCount { get; }

            private long ExtentsLength { get; }

            private byte[] ExtentsRecord { get; }

            private long CatalogLength { get; }

            private byte[] CatalogRecord { get; }

            // The volume's files; its folders go to folderList when one is given.
            public IReadOnlyList<MacFile> Files(List<MacFolder>? folderList)
            {
                if (blockSize == 0 || blockSize % 512 != 0)
                {
                    throw new InvalidDataException($"The allocation block size {blockSize} is not a multiple of 512.");
                }

                CheckFreeBlockCount();
                // The extents overflow file never overflows itself; the catalog may.
                var extentsFile = Fork(ExtentsRecord, 0, 3, ExtentsLength, "extents overflow file");
                if (extentsFile is not null)
                {
                    ReadOverflow(extentsFile);
                }

                var catalog = Fork(CatalogRecord, 0, CatalogFileId, CatalogLength, "catalog file")
                    ?? throw new InvalidDataException("The catalog file cannot be read.");

                var folders = new Dictionary<uint, (uint Parent, MacString Name)>();
                var folderRecords = new List<(uint Id, byte[] Record)>();
                var files = new List<(uint Parent, MacString Name, byte[] Record)>();
                var catalogIds = new HashSet<uint>();
                var entries = 0;
                foreach (var (key, data) in LeafRecords(catalog, "catalog"))
                {
                    // Key: length, reserved byte, parent ID, name (Str31).
                    if (key.Length < 7 || data.Length < 2)
                    {
                        continue;
                    }

                    var parent = new BigEndianReader(key).ReadUInt32At(2);
                    var name = new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7)));
                    switch (data[0])
                    {
                        case 1 when data.Length >= 70: // folder
                            uint folderId = new BigEndianReader(data).ReadUInt32At(6);
                            if (folderId < 16 && folderId != RootFolderId)
                            {
                                context.Report(DiagnosticSeverity.Warning, "hfs.reserved-id",
                                    $"Catalog folder ID {folderId} is reserved; only the root folder may use ID 2.");
                            }

                            if (!catalogIds.Add(folderId))
                            {
                                context.Report(DiagnosticSeverity.Warning, "hfs.duplicate-id",
                                    $"Catalog folder ID {folderId} appears more than once.");
                            }
                            else
                            {
                                folders[folderId] = (parent, name);
                                folderRecords.Add((folderId, data));
                            }
                            break;
                        case 2 when data.Length >= 102: // file
                            uint fileId = new BigEndianReader(data).ReadUInt32At(20);
                            if (fileId < 16)
                            {
                                context.Report(DiagnosticSeverity.Warning, "hfs.reserved-id",
                                    $"Catalog file ID {fileId} is reserved; file IDs must be at least 16.");
                            }

                            if (!catalogIds.Add(fileId))
                            {
                                context.Report(DiagnosticSeverity.Warning, "hfs.duplicate-id",
                                    $"Catalog file ID {fileId} appears more than once.");
                            }

                            files.Add((parent, name, data));
                            break;
                        case 3 or 4: // threads: the same information, keyed by CNID
                            continue;
                        default:
                            context.Report(DiagnosticSeverity.Warning, "hfs.bad-record",
                                $"A catalog record of type {data[0]} ({data.Length} bytes) is not understood; skipped.");
                            continue;
                    }
                    if (++entries > context.Options.MaxVolumeEntries)
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.too-many-entries",
                            $"The catalog holds more than {context.Options.MaxVolumeEntries} entries; reading stopped.");
                        break;
                    }
                }

                var result = new List<MacFile>(files.Count);
                foreach (var (parent, name, r) in files)
                {
                    result.Add(File(parent, name, r, folders));
                }

                if (folderList is not null)
                {
                    foreach (var (id, r) in folderRecords)
                    {
                        folderList.Add(Folder(id, r, folders));
                    }
                }

                var foldersBelowRoot = folders.Count(f => f.Key != RootFolderId);
                if (files.Count != FileCount || foldersBelowRoot != FolderCount)
                {
                    context.Report(DiagnosticSeverity.Info, "hfs.counts",
                        $"The catalog holds {files.Count} files and {foldersBelowRoot} folders; the volume header says " +
                        $"{FileCount} and {FolderCount}.");
                }
                return result;
            }

            private void CheckFreeBlockCount()
            {
                int bitmapLength = (blockCount + 7) / 8;
                long bitmapOffset = bitmapStartBlock * 512L;
                if (bitmapOffset > image.Length || bitmapLength > image.Length - bitmapOffset)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bitmap-truncated",
                        "The HFS volume bitmap does not fit in the image.", bitmapOffset);
                    return;
                }

                byte[] bitmap = image.Slice(bitmapOffset, bitmapLength).ToArray(bitmapLength);
                volumeBitmap = bitmap;
                int usedBlocks = 0;
                for (int block = 0; block < blockCount; block++)
                {
                    if ((bitmap[block / 8] & (0x80 >> (block & 7))) != 0)
                    {
                        usedBlocks++;
                    }
                }

                int actualFreeBlocks = blockCount - usedBlocks;
                if (actualFreeBlocks != freeBlockCount)
                {
                    context.Report(DiagnosticSeverity.Info, "hfs.free-blocks",
                        $"The HFS volume bitmap contains {actualFreeBlocks} free allocation blocks, but the MDB records {freeBlockCount}.");
                }
            }

            private MacFile File(uint parent, MacString name, byte[] r, Dictionary<uint, (uint Parent, MacString Name)> folders)
            {
                var reader = new BigEndianReader(r);
                var id = reader.ReadUInt32At(20);
                var info = FinderInfo.Read([.. r.AsSpan(4, 16), .. r.AsSpan(56, 16)]);
                var label = $"\"{name}\"";
                var data = Fork(r.AsSpan(74, 12).ToArray(), 0x00, id, reader.ReadUInt32At(26), $"{label}'s data fork");
                var resource = Fork(r.AsSpan(86, 12).ToArray(), 0xFF, id, reader.ReadUInt32At(36), $"{label}'s resource fork");
                return new MacFile
                {
                    Name = name,
                    FolderPath = FolderPath(parent, folders, label),
                    FinderInfo = info,
                    IsLocked = (r[2] & 0x01) != 0, // filFlags bit 0 (Inside Macintosh: Files, cdrFilRec)
                    Created = Date(reader.ReadUInt32At(44)),
                    Modified = Date(reader.ReadUInt32At(48)),
                    DataFork = data ?? ForkData.Empty,
                    ResourceFork = resource ?? ForkData.Empty,
                    CatalogId = id,
                    ParentId = parent,
                };
            }

            // A folder from its cdrDirRec (Inside Macintosh: Files): dates at +10 and +14, DInfo at +22, DXInfo at +38.
            private MacFolder Folder(uint id, byte[] r, Dictionary<uint, (uint Parent, MacString Name)> folders)
            {
                var reader = new BigEndianReader(r);
                var (parent, name) = folders[id];
                return new MacFolder
                {
                    Name = name,
                    IsRoot = id == RootFolderId,
                    CatalogId = id,
                    FreeBytes = id == RootFolderId ? (long)freeBlockCount * blockSize : null,
                    FolderPath = id == RootFolderId ? [] : FolderPath(parent, folders, $"Folder \"{name}\""),
                    FinderInfo = FolderFinderInfo.Read(r.AsSpan(22, FolderFinderInfo.Length)),
                    Created = Date(reader.ReadUInt32At(10)),
                    Modified = Date(reader.ReadUInt32At(14)),
                };
            }

            // Folder names from the root down, following parent IDs; a missing or looping parent is reported.
            private List<MacString> FolderPath(uint parent, Dictionary<uint, (uint Parent, MacString Name)> folders, string label)
            {
                var path = new List<MacString>();
                var seen = new HashSet<uint>();
                while (parent != RootFolderId && parent != RootParentId)
                {
                    if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
                    {
                        context.Report(DiagnosticSeverity.Warning, "hfs.orphan",
                            $"{label}'s folder {parent} is missing or loops; its path starts there.");
                        break;
                    }
                    path.Insert(0, folder.Name);
                    parent = folder.Parent;
                }
                return path;
            }

            // A fork from its first extent record and any overflow records, cut to its logical length; null if unusable.
            private ForkData? Fork(byte[] firstExtents, byte forkType, uint fileId, long logicalLength, string what)
            {
                var ranges = new List<(long, long)>();
                long covered = 0;
                int expectedFileAllocationBlock = 0;
                void Add(ReadOnlyMemory<byte> record)
                {
                    var reader = new BigEndianReader(record);
                    for (var i = 0; i < 3; i++)
                    {
                        int start = reader.ReadUInt16();
                        int count = reader.ReadUInt16();
                        if (count == 0)
                        {
                            continue;
                        }

                        expectedFileAllocationBlock += count;
                        if (start + count > blockCount)
                        {
                            context.Report(DiagnosticSeverity.Error, "hfs.extent-outside",
                                $"An extent of {what} ({count} blocks at {start}) lies outside the volume's {blockCount} blocks.");
                            covered = long.MaxValue;
                            return;
                        }
                        ReportUnallocatedExtent(start, count, what);
                        if (covered < logicalLength)
                        {
                            ranges.Add((firstBlock + start * blockSize, count * blockSize));
                            covered += count * blockSize;
                        }
                    }
                }

                Add(firstExtents);
                if (overflow.TryGetValue((forkType, fileId), out var more))
                {
                    foreach (var (startBlock, record) in more.OrderBy(m => m.StartBlock))
                    {
                        if (startBlock != expectedFileAllocationBlock)
                        {
                            context.Report(DiagnosticSeverity.Warning, "hfs.overflow-start",
                                $"The extents overflow key for {what} starts at file allocation block {startBlock}; " +
                                $"the preceding extents cover {expectedFileAllocationBlock} blocks.");
                        }

                        Add(record);
                    }
                }
                if (covered == long.MaxValue)
                {
                    return null;
                }

                if (logicalLength == 0)
                {
                    return ForkData.Empty;
                }

                var length = logicalLength;
                if (covered < logicalLength)
                {
                    context.Report(DiagnosticSeverity.Error, "hfs.fork-short",
                        $"The extents of {what} hold {covered} of its {logicalLength} bytes; the rest is missing.");
                    length = covered;
                }

                // Extents that run past a truncated image are cut, and reported.
                var inImage = new List<(long, long)>();
                long available = 0;
                foreach (var (offset, count) in ranges)
                {
                    var take = Math.Clamp(image.Length - offset, 0, count);
                    if (take > 0)
                    {
                        inImage.Add((offset, take));
                    }

                    available += take;
                    if (take < count)
                    {
                        break;
                    }
                }
                if (available < length)
                {
                    context.Report(DiagnosticSeverity.Error, "hfs.image-truncated",
                        $"The image ends inside {what}; {available} of its {length} bytes are there.");
                    length = available;
                }
                return new ExtentForkData(image, inImage, length);
            }

            private void ReportUnallocatedExtent(int start, int count, string what)
            {
                if (volumeBitmap is null)
                {
                    return;
                }

                for (int block = start; block < start + count; block++)
                {
                    if ((volumeBitmap[block / 8] & (0x80 >> (block & 7))) != 0)
                    {
                        continue;
                    }

                    context.Report(DiagnosticSeverity.Warning, "hfs.extent-unallocated",
                        $"An extent of {what} includes allocation block {block}, which the volume bitmap marks free.",
                        bitmapStartBlock * 512L + block / 8);
                    return;
                }
            }

            // Extents overflow leaf records: key (length 7, fork type, file ID, first allocation block), 3 extents.
            private void ReadOverflow(ForkData extentsFile)
            {
                foreach (var (key, data) in LeafRecords(extentsFile, "extents overflow"))
                {
                    bool validKey = key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
                    if (!validKey || data.Length != 12)
                    {
                        context.Report(DiagnosticSeverity.Warning, "hfs.overflow-record",
                            "An extents overflow record has an invalid key or extent record length.");
                    }

                    if (!validKey || data.Length < 12)
                    {
                        continue;
                    }

                    var fork = key[1];
                    var reader = new BigEndianReader(key);
                    var file = reader.ReadUInt32At(2);
                    var start = reader.ReadUInt16At(6);
                    if (!overflow.TryGetValue((fork, file), out var list))
                    {
                        overflow[(fork, file)] = list = [];
                    }

                    list.Add((start, data[..12]));
                }
            }

            // The records of a B-tree's leaf nodes in order: from the header's first leaf along the forward links, every
            // node visited once and every offset checked. Each record is its key (length byte included) and its data.
            private IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(ForkData file, string name)
            {
                if (file.Length < NodeSize)
                {
                    yield break;
                }
                // B-tree files are small; read once rather than node by node through the image.
                var tree = file.ToArray(context.Options.MaxExpandedBytesPerInput);
                if (tree.Length % NodeSize != 0)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree fork ends with {tree.Length % NodeSize} partial bytes after its complete nodes.");
                }

                var nodes = tree.Length / NodeSize;
                var header = Node(tree, 0);
                var headerReader = new BigEndianReader(header);
                var headerRecordCount = headerReader.ReadUInt16At(10);
                var reserved = headerReader.ReadUInt16At(12);
                if (headerReader.ReadUInt32At(4) != 0 ||
                    header[8] != 1 || header[9] != 0 || headerRecordCount != 3 || reserved != 0)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree header node has an invalid backward link, kind, height, record count, or reserved field.");
                }

                ushort declaredNodeSize = headerReader.ReadUInt16At(14 + 18);
                if (declaredNodeSize != NodeSize)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {declaredNodeSize}-byte nodes; classic HFS nodes are {NodeSize} bytes.");
                }

                uint declaredNodeCount = headerReader.ReadUInt32At(14 + 22);
                if (declaredNodeCount != nodes)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {declaredNodeCount} nodes, but its fork contains {nodes} complete nodes.");
                }

                ushort depth = headerReader.ReadUInt16At(14);
                uint root = headerReader.ReadUInt32At(14 + 2);
                if (root >= nodes || (root == 0) != (depth == 0))
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree root node {root} and depth {depth} are inconsistent with its node count.");
                }

                ushort expectedMaxKeyLength = name == "catalog" ? (ushort)37 : (ushort)7;
                ushort maxKeyLength = headerReader.ReadUInt16At(14 + 20);
                if (maxKeyLength != expectedMaxKeyLength)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares a maximum key length of {maxKeyLength}; expected {expectedMaxKeyLength}.");
                }

                var mapNodes = ValidateNodeMap(tree, header, nodes, name);
                var node = headerReader.ReadUInt32At(14 + 10);
                var expectedLastLeaf = headerReader.ReadUInt32At(14 + 14);
                var expectedLeafRecords = headerReader.ReadUInt32At(14 + 6);
                uint lastLeaf = 0;
                uint previousLeaf = 0;
                ulong leafRecordCount = 0;
                byte[]? previousKey = null;
                var visited = new HashSet<uint>();
                while (node != 0)
                {
                    if (node >= nodes || !visited.Add(node))
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.bad-link",
                            $"The {name} tree links to node {node}, which is outside the tree or already read; stopped.");
                        yield break;
                    }
                    if (mapNodes?.Contains(node) == true)
                    {
                        context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-map",
                            $"Node {node} of the {name} tree is also linked as a map node.");
                        yield break;
                    }
                    if (mapNodes is not null && !IsNodeAllocated(tree, header, mapNodes, node))
                    {
                        context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-map",
                            $"Node {node} of the {name} tree is marked free in its node map.");
                    }

                    var bytes = Node(tree, node);
                    var nodeReader = new BigEndianReader(bytes);
                    if ((sbyte)bytes[8] != -1 || bytes[9] != 1)
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.not-leaf",
                            $"Node {node} of the {name} tree is linked as a leaf but has type {(sbyte)bytes[8]} and height {bytes[9]}; stopped.");
                        yield break;
                    }
                    uint backwardLink = nodeReader.ReadUInt32At(4);
                    if (backwardLink != previousLeaf)
                    {
                        context.Report(DiagnosticSeverity.Warning, "hfs.bad-link",
                            $"Leaf node {node} of the {name} tree links backward to {backwardLink}; expected {previousLeaf}.");
                    }

                    lastLeaf = node;
                    previousLeaf = node;
                    int records = nodeReader.ReadUInt16At(10);
                    leafRecordCount += checked((uint)records);
                    for (var i = 0; i < records; i++)
                    {
                        var at = NodeSize - 2 * (i + 1);
                        var next = NodeSize - 2 * (i + 2);
                        if (next < 14)
                        {
                            break;
                        }

                        int start = nodeReader.ReadUInt16At(at);
                        int end = nodeReader.ReadUInt16At(next);
                        if (start < 14 || end > next || end <= start)
                        {
                            context.Report(DiagnosticSeverity.Error, "hfs.bad-record-offset",
                                $"Record {i} of node {node} in the {name} tree has offsets {start}–{end}; skipped.");
                            continue;
                        }
                        int keyLength = bytes[start];
                        var dataStart = start + 1 + keyLength;
                        if ((dataStart & 1) != 0)
                        {
                            dataStart++;
                        }

                        if (dataStart > end)
                        {
                            continue;
                        }

                        var key = bytes[start..(start + 1 + keyLength)];
                        if (IsValidKey(name, key))
                        {
                            if (previousKey is not null && CompareKeys(name, previousKey, key) >= 0)
                            {
                                context.Report(DiagnosticSeverity.Warning, "hfs.key-order",
                                    $"A {name} B-tree key is duplicate or out of order at node {node}, record {i}.");
                            }

                            previousKey = key;
                        }
                        else if (name == "catalog")
                        {
                            context.Report(DiagnosticSeverity.Warning, "hfs.bad-record",
                                $"Catalog record {i} of node {node} has a malformed key; skipped.");
                            continue;
                        }
                        yield return (key, bytes[dataStart..end]);
                    }
                    node = nodeReader.ReadUInt32At(0);
                }

                if (lastLeaf != expectedLastLeaf)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares last leaf {expectedLastLeaf}, but its leaf chain ends at {lastLeaf}.");
                }

                if (leafRecordCount != expectedLeafRecords)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {expectedLeafRecords} leaf records, but its leaves contain {leafRecordCount} records.");
                }
            }

            private List<uint>? ValidateNodeMap(byte[] tree, byte[] header, int nodes, string name)
            {
                var headerReader = new BigEndianReader(header);
                if (headerReader.ReadUInt16At(10) != 3)
                {
                    return null;
                }

                int headerRecordStart = headerReader.ReadUInt16At(NodeSize - 2);
                int userRecordStart = headerReader.ReadUInt16At(NodeSize - 4);
                int mapStart = headerReader.ReadUInt16At(NodeSize - 6);
                int mapEnd = headerReader.ReadUInt16At(NodeSize - 8);
                if (headerRecordStart != 14 || userRecordStart != 14 + 106 || mapStart != 14 + 106 + 128 ||
                    mapEnd < mapStart || mapEnd > NodeSize - 8)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree header, user, or map record has invalid boundaries.");
                    return null;
                }

                byte[] headerMap = tree.AsSpan(mapStart, mapEnd - mapStart).ToArray();
                uint headerCapacity = checked((uint)(headerMap.Length * 8));
                const int MapNodeDataLength = NodeSize - 20;
                const uint MapNodeCapacity = MapNodeDataLength * 8;
                var mapNodes = new HashSet<uint>();
                var mapNodeOrder = new List<uint>();
                uint nextMapNode = headerReader.ReadUInt32At(0);
                while (nextMapNode != 0)
                {
                    if (nextMapNode >= (uint)nodes || !mapNodes.Add(nextMapNode))
                    {
                        ReportBadMap($"The {name} B-tree map-node chain is outside the tree or cyclic.");
                        return null;
                    }
                    mapNodeOrder.Add(nextMapNode);

                    int offset = checked((int)nextMapNode * NodeSize);
                    var mapNode = new BigEndianReader(tree.AsMemory(offset, NodeSize));
                    if (mapNode.ReadByteAt(8) != 2 || mapNode.ReadByteAt(9) != 0 ||
                        mapNode.ReadUInt16At(10) != 1 ||
                        mapNode.ReadUInt16At(12) != 0 ||
                        mapNode.ReadUInt32At(4) != 0 ||
                        mapNode.ReadUInt16At(NodeSize - 2) != 14 ||
                        mapNode.ReadUInt16At(NodeSize - 4) != NodeSize - 6)
                    {
                        ReportBadMap($"Map node {nextMapNode} of the {name} B-tree has an invalid descriptor or record layout.");
                        return null;
                    }
                    nextMapNode = mapNode.ReadUInt32At(0);
                }

                uint requiredMapNodes = (uint)nodes <= headerCapacity
                    ? 0
                    : checked((uint)(((ulong)(uint)nodes - headerCapacity + MapNodeCapacity - 1) / MapNodeCapacity));
                if ((uint)mapNodes.Count != requiredMapNodes)
                {
                    ReportBadMap($"The {name} B-tree map nodes do not provide the required bitmap coverage.");
                    return null;
                }

                bool IsAllocated(uint nodeNumber)
                {
                    if (nodeNumber < headerCapacity)
                    {
                        uint headerByteOffset = nodeNumber / 8;
                        return (headerMap[(int)headerByteOffset] & (0x80 >> (int)(nodeNumber & 7))) != 0;
                    }
                    uint continuationBit = nodeNumber - headerCapacity;
                    uint mapIndex = continuationBit / MapNodeCapacity;
                    if (mapIndex >= mapNodeOrder.Count)
                    {
                        return false;
                    }

                    uint mapByte = continuationBit % MapNodeCapacity / 8;
                    uint mapNodeNumber = mapNodeOrder[(int)mapIndex];
                    int continuationByteOffset = checked((int)mapNodeNumber * NodeSize + 14 + (int)mapByte);
                    return (tree[continuationByteOffset] & (0x80 >> (int)(continuationBit & 7))) != 0;
                }

                if (!IsAllocated(0))
                {
                    ReportBadMap($"The {name} B-tree header node is marked free in its node map.");
                }

                foreach (uint mapNode in mapNodes)
                {
                    if (!IsAllocated(mapNode))
                    {
                        ReportBadMap($"Map node {mapNode} of the {name} B-tree is marked free.");
                    }
                }

                uint freeNodes = 0;
                for (uint nodeNumber = 0; nodeNumber < nodes; nodeNumber++)
                {
                    if (!IsAllocated(nodeNumber))
                    {
                        freeNodes++;
                    }
                }

                uint declaredFree = headerReader.ReadUInt32At(14 + 26);
                if (freeNodes != declaredFree)
                {
                    ReportBadMap($"The {name} B-tree declares {declaredFree} free nodes, but its map contains {freeNodes}.");
                }

                return mapNodeOrder;

                void ReportBadMap(string message) =>
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-map", message);
            }

            private static bool IsNodeAllocated(byte[] tree, byte[] header, List<uint> mapNodes, uint nodeNumber)
            {
                var headerReader = new BigEndianReader(header);
                int mapStart = headerReader.ReadUInt16At(NodeSize - 6);
                int mapEnd = headerReader.ReadUInt16At(NodeSize - 8);
                uint headerCapacity = checked((uint)((mapEnd - mapStart) * 8));
                const uint MapNodeCapacity = (NodeSize - 20) * 8;
                if (nodeNumber < headerCapacity)
                {
                    return (tree[mapStart + (int)(nodeNumber / 8)] & (0x80 >> (int)(nodeNumber & 7))) != 0;
                }

                uint continuationBit = nodeNumber - headerCapacity;
                uint mapIndex = continuationBit / MapNodeCapacity;
                if (mapIndex >= mapNodes.Count)
                {
                    return false;
                }

                int byteOffset = checked((int)mapNodes[(int)mapIndex] * NodeSize + 14 +
                    (int)(continuationBit % MapNodeCapacity / 8));
                return (tree[byteOffset] & (0x80 >> (int)(continuationBit & 7))) != 0;
            }

            private static bool IsValidKey(string name, byte[] key)
            {
                if (name == "catalog")
                {
                    if (key.Length < 7 || key[0] != key.Length - 1 || key[1] != 0 || key[6] > 31)
                    {
                        return false;
                    }
                    // ckrKeyLen is 6 + n as the Mac OS File Manager writes it (Finder-made Desktop DB, threads with
                    // key length 6), or 7 + n for an even-length name when the alignment byte is counted (hfsutils,
                    // HfsWriter). Both lay the data out at the same even offset. [Verified: Mac OS-written volumes]
                    int nameLength = key[6];
                    return key.Length == 7 + nameLength || (nameLength % 2 == 0 && key.Length == 8 + nameLength);
                }

                return key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
            }

            private static int CompareKeys(string name, byte[] left, byte[] right)
            {
                if (name == "catalog")
                {
                    return HfsWriter.CompareCatalogKeys(left, right);
                }

                var leftReader = new BigEndianReader(left);
                var rightReader = new BigEndianReader(right);
                int comparison = leftReader.ReadUInt32At(2).CompareTo(rightReader.ReadUInt32At(2));
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = left[1].CompareTo(right[1]);
                return comparison != 0
                    ? comparison
                    : leftReader.ReadUInt16At(6).CompareTo(rightReader.ReadUInt16At(6));
            }

            private static byte[] Node(byte[] tree, long index) => tree.AsSpan((int)(index * NodeSize), NodeSize).ToArray();

            private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
        }
    }
}
