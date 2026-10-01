using System;
using System.Buffers.Binary;
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
    public sealed class HfsReader : IContainerReader
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
            if (input.Length < MdbOffset + MdbLength) return false;
            var signature = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(MdbOffset, 2).ToArray());
            return signature is HfsSignature or HfsPlusSignature or HfsXSignature;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException("Not an HFS volume.");
            var mdb = input.Slice(MdbOffset, MdbLength).ToArray();
            if (BinaryPrimitives.ReadUInt16BigEndian(mdb) is HfsPlusSignature or HfsXSignature)
                return HfsPlusReader.Read(input, context);
            if (BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x7C)) == HfsPlusSignature)
                return ReadEmbeddedPlus(input, mdb, context);
            ReportAlternateMdbProblem(input, context);
            return new Volume(input, mdb, context).Files();
        }

        private static void ReportAlternateMdbProblem(ForkData input, ContainerContext context)
        {
            const int AlternateMdbOffsetFromEnd = 1024;
            if (input.Length < AlternateMdbOffsetFromEnd + sizeof(ushort) ||
                BinaryPrimitives.ReadUInt16BigEndian(
                    input.Slice(input.Length - AlternateMdbOffsetFromEnd, sizeof(ushort)).ToArray()) != HfsSignature)
                context.Report(DiagnosticSeverity.Warning, "hfs.alternate-mdb",
                    "The HFS volume's alternate master directory block is missing or has an invalid signature.",
                    Math.Max(0, input.Length - AlternateMdbOffsetFromEnd));
        }

        private static IReadOnlyList<MacFile> ReadEmbeddedPlus(ForkData input, byte[] mdb, ContainerContext context)
        {
            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(0x14));
            uint allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x12));
            uint allocationStart = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x1C));
            uint embeddedStart = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x7E));
            uint embeddedBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x80));
            if (blockSize == 0 || allocationBlocks == 0 || embeddedBlocks == 0 ||
                (ulong)embeddedStart + embeddedBlocks > allocationBlocks)
                throw new InvalidDataException("The HFS wrapper's embedded HFS Plus extent is invalid.");

            ulong allocationAreaStart = (ulong)allocationStart * 512;
            ulong offset = allocationAreaStart + (ulong)embeddedStart * blockSize;
            ulong length = (ulong)embeddedBlocks * blockSize;
            ulong allocationAreaEnd = allocationAreaStart + (ulong)allocationBlocks * blockSize;
            if (offset > (ulong)input.Length || length > (ulong)input.Length - offset ||
                offset + length > allocationAreaEnd || length < 1536)
                throw new InvalidDataException("The HFS wrapper's embedded HFS Plus volume lies outside the image.");
            ReportUnallocatedEmbeddedVolume(input, mdb, embeddedStart, embeddedBlocks, context);
            return HfsPlusReader.Read(input.Slice(checked((long)offset), checked((long)length)), context);
        }

        private static void ReportUnallocatedEmbeddedVolume(ForkData input, byte[] mdb, uint embeddedStart,
            uint embeddedBlocks, ContainerContext context)
        {
            int allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x12));
            int bitmapStartBlock = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(0x0E));
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
                if ((bitmap[checked((int)(block / 8))] & (0x80 >> (int)(block & 7))) == 0)
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.wrapper-extent-unallocated",
                        $"The HFS wrapper's embedded HFS Plus extent includes allocation block {block}, which the wrapper bitmap marks free.",
                        bitmapOffset + block / 8);
                    return;
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
                var m = mdb.AsSpan();
                blockSize = BinaryPrimitives.ReadUInt32BigEndian(m[0x14..]);
                firstBlock = BinaryPrimitives.ReadUInt16BigEndian(m[0x1C..]) * 512L;
                blockCount = BinaryPrimitives.ReadUInt16BigEndian(m[0x12..]);
                bitmapStartBlock = BinaryPrimitives.ReadUInt16BigEndian(m[0x0E..]);
                freeBlockCount = BinaryPrimitives.ReadUInt16BigEndian(m[0x22..]);
                Name = new MacString(m.Slice(0x25, Math.Min(m[0x24], (byte)27)));
                FileCount = BinaryPrimitives.ReadUInt32BigEndian(m[0x54..]);
                FolderCount = BinaryPrimitives.ReadUInt32BigEndian(m[0x58..]);
                ExtentsLength = BinaryPrimitives.ReadUInt32BigEndian(m[0x82..]);
                ExtentsRecord = m.Slice(0x86, 12).ToArray();
                CatalogLength = BinaryPrimitives.ReadUInt32BigEndian(m[0x92..]);
                CatalogRecord = m.Slice(0x96, 12).ToArray();
            }

            public MacString Name { get; }

            private uint FileCount { get; }

            private uint FolderCount { get; }

            private long ExtentsLength { get; }

            private byte[] ExtentsRecord { get; }

            private long CatalogLength { get; }

            private byte[] CatalogRecord { get; }

            public IReadOnlyList<MacFile> Files()
            {
                if (blockSize == 0 || blockSize % 512 != 0)
                    throw new InvalidDataException($"The allocation block size {blockSize} is not a multiple of 512.");
                CheckFreeBlockCount();
                // The extents overflow file never overflows itself; the catalog may.
                var extentsFile = Fork(ExtentsRecord, 0, 3, ExtentsLength, "extents overflow file");
                if (extentsFile is not null) ReadOverflow(extentsFile);
                var catalog = Fork(CatalogRecord, 0, CatalogFileId, CatalogLength, "catalog file")
                    ?? throw new InvalidDataException("The catalog file cannot be read.");

                var folders = new Dictionary<uint, (uint Parent, MacString Name)>();
                var files = new List<(uint Parent, MacString Name, byte[] Record)>();
                var catalogIds = new HashSet<uint>();
                var entries = 0;
                foreach (var (key, data) in LeafRecords(catalog, "catalog"))
                {
                    // Key: length, reserved byte, parent ID, name (Str31).
                    if (key.Length < 7 || data.Length < 2) continue;
                    var parent = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2));
                    var name = new MacString(key.AsSpan(7, Math.Min(key[6], key.Length - 7)));
                    switch (data[0])
                    {
                        case 1 when data.Length >= 70: // folder
                            uint folderId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6));
                            if (folderId < 16 && folderId != RootFolderId)
                                context.Report(DiagnosticSeverity.Warning, "hfs.reserved-id",
                                    $"Catalog folder ID {folderId} is reserved; only the root folder may use ID 2.");
                            if (!catalogIds.Add(folderId))
                                context.Report(DiagnosticSeverity.Warning, "hfs.duplicate-id",
                                    $"Catalog folder ID {folderId} appears more than once.");
                            else
                                folders[folderId] = (parent, name);
                            break;
                        case 2 when data.Length >= 102: // file
                            uint fileId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
                            if (fileId < 16)
                                context.Report(DiagnosticSeverity.Warning, "hfs.reserved-id",
                                    $"Catalog file ID {fileId} is reserved; file IDs must be at least 16.");
                            if (!catalogIds.Add(fileId))
                                context.Report(DiagnosticSeverity.Warning, "hfs.duplicate-id",
                                    $"Catalog file ID {fileId} appears more than once.");
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
                foreach (var (parent, name, r) in files) result.Add(File(parent, name, r, folders));

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
                    if ((bitmap[block / 8] & (0x80 >> (block & 7))) != 0)
                        usedBlocks++;

                int actualFreeBlocks = blockCount - usedBlocks;
                if (actualFreeBlocks != freeBlockCount)
                    context.Report(DiagnosticSeverity.Info, "hfs.free-blocks",
                        $"The HFS volume bitmap contains {actualFreeBlocks} free allocation blocks, but the MDB records {freeBlockCount}.");
            }

            private MacFile File(uint parent, MacString name, byte[] r, Dictionary<uint, (uint Parent, MacString Name)> folders)
            {
                var id = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(20));
                var info = FinderInfo.Read([.. r.AsSpan(4, 16), .. r.AsSpan(56, 16)]);
                var label = $"\"{name}\"";
                var data = Fork(r.AsSpan(74, 12).ToArray(), 0x00, id, BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(26)), $"{label}'s data fork");
                var resource = Fork(r.AsSpan(86, 12).ToArray(), 0xFF, id, BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(36)), $"{label}'s resource fork");
                return new MacFile
                {
                    Name = name,
                    FolderPath = FolderPath(parent, folders, label),
                    FinderInfo = info,
                    Created = Date(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(44))),
                    Modified = Date(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(48))),
                    DataFork = data ?? ForkData.Empty,
                    ResourceFork = resource ?? ForkData.Empty,
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
                void Add(ReadOnlySpan<byte> record)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        int start = BinaryPrimitives.ReadUInt16BigEndian(record[(i * 4)..]);
                        int count = BinaryPrimitives.ReadUInt16BigEndian(record[(i * 4 + 2)..]);
                        if (count == 0) continue;
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
                            context.Report(DiagnosticSeverity.Warning, "hfs.overflow-start",
                                $"The extents overflow key for {what} starts at file allocation block {startBlock}; " +
                                $"the preceding extents cover {expectedFileAllocationBlock} blocks.");
                        Add(record);
                    }
                }
                if (covered == long.MaxValue) return null;
                if (logicalLength == 0) return ForkData.Empty;
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
                    if (take > 0) inImage.Add((offset, take));
                    available += take;
                    if (take < count) break;
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
                if (volumeBitmap is null) return;
                for (int block = start; block < start + count; block++)
                {
                    if ((volumeBitmap[block / 8] & (0x80 >> (block & 7))) != 0) continue;
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
                        context.Report(DiagnosticSeverity.Warning, "hfs.overflow-record",
                            "An extents overflow record has an invalid key or extent record length.");
                    if (!validKey || data.Length < 12) continue;
                    var fork = key[1];
                    var file = BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(2));
                    var start = BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(6));
                    if (!overflow.TryGetValue((fork, file), out var list)) overflow[(fork, file)] = list = [];
                    list.Add((start, data[..12]));
                }
            }

            // The records of a B-tree's leaf nodes in order: from the header's first leaf along the forward links, every
            // node visited once and every offset checked. Each record is its key (length byte included) and its data.
            private IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(ForkData file, string name)
            {
                if (file.Length < NodeSize) yield break;
                // B-tree files are small; read once rather than node by node through the image.
                var tree = file.ToArray(context.Options.MaxExpandedBytesPerInput);
                if (tree.Length % NodeSize != 0)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree fork ends with {tree.Length % NodeSize} partial bytes after its complete nodes.");
                var nodes = tree.Length / NodeSize;
                var header = Node(tree, 0);
                var headerRecordCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10));
                var reserved = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(12));
                if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 0 ||
                    header[8] != 1 || header[9] != 0 || headerRecordCount != 3 || reserved != 0)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree header node has an invalid backward link, kind, height, record count, or reserved field.");
                ushort declaredNodeSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14 + 18));
                if (declaredNodeSize != NodeSize)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {declaredNodeSize}-byte nodes; classic HFS nodes are {NodeSize} bytes.");
                uint declaredNodeCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 22));
                if (declaredNodeCount != nodes)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {declaredNodeCount} nodes, but its fork contains {nodes} complete nodes.");
                ushort depth = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14));
                uint root = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 2));
                if (root >= nodes || (root == 0) != (depth == 0))
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree root node {root} and depth {depth} are inconsistent with its node count.");
                ushort expectedMaxKeyLength = name == "catalog" ? (ushort)37 : (ushort)7;
                ushort maxKeyLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(14 + 20));
                if (maxKeyLength != expectedMaxKeyLength)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares a maximum key length of {maxKeyLength}; expected {expectedMaxKeyLength}.");
                var mapNodes = ValidateNodeMap(tree, header, nodes, name);
                var node = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 10));
                var expectedLastLeaf = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 14));
                var expectedLeafRecords = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 6));
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
                        context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-map",
                            $"Node {node} of the {name} tree is marked free in its node map.");
                    var bytes = Node(tree, node);
                    if ((sbyte)bytes[8] != -1 || bytes[9] != 1)
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.not-leaf",
                            $"Node {node} of the {name} tree is linked as a leaf but has type {(sbyte)bytes[8]} and height {bytes[9]}; stopped.");
                        yield break;
                    }
                    uint backwardLink = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
                    if (backwardLink != previousLeaf)
                        context.Report(DiagnosticSeverity.Warning, "hfs.bad-link",
                            $"Leaf node {node} of the {name} tree links backward to {backwardLink}; expected {previousLeaf}.");
                    lastLeaf = node;
                    previousLeaf = node;
                    int records = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10));
                    leafRecordCount += checked((uint)records);
                    for (var i = 0; i < records; i++)
                    {
                        var at = NodeSize - 2 * (i + 1);
                        var next = NodeSize - 2 * (i + 2);
                        if (next < 14) break;
                        int start = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at));
                        int end = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(next));
                        if (start < 14 || end > next || end <= start)
                        {
                            context.Report(DiagnosticSeverity.Error, "hfs.bad-record-offset",
                                $"Record {i} of node {node} in the {name} tree has offsets {start}–{end}; skipped.");
                            continue;
                        }
                        int keyLength = bytes[start];
                        var dataStart = start + 1 + keyLength;
                        if ((dataStart & 1) != 0) dataStart++;
                        if (dataStart > end) continue;
                        var key = bytes[start..(start + 1 + keyLength)];
                        if (IsValidKey(name, key))
                        {
                            if (previousKey is not null && CompareKeys(name, previousKey, key) >= 0)
                                context.Report(DiagnosticSeverity.Warning, "hfs.key-order",
                                    $"A {name} B-tree key is duplicate or out of order at node {node}, record {i}.");
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
                    node = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                }

                if (lastLeaf != expectedLastLeaf)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares last leaf {expectedLastLeaf}, but its leaf chain ends at {lastLeaf}.");
                if (leafRecordCount != expectedLeafRecords)
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-header",
                        $"The {name} B-tree declares {expectedLeafRecords} leaf records, but its leaves contain {leafRecordCount} records.");
            }

            private List<uint>? ValidateNodeMap(byte[] tree, byte[] header, int nodes, string name)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10)) != 3) return null;

                int headerRecordStart = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 2));
                int userRecordStart = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 4));
                int mapStart = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 6));
                int mapEnd = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 8));
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
                uint nextMapNode = BinaryPrimitives.ReadUInt32BigEndian(header);
                while (nextMapNode != 0)
                {
                    if (nextMapNode >= (uint)nodes || !mapNodes.Add(nextMapNode))
                    {
                        ReportBadMap($"The {name} B-tree map-node chain is outside the tree or cyclic.");
                        return null;
                    }
                    mapNodeOrder.Add(nextMapNode);

                    int offset = checked((int)nextMapNode * NodeSize);
                    var mapNode = tree.AsSpan(offset, NodeSize);
                    if (mapNode[8] != 2 || mapNode[9] != 0 ||
                        BinaryPrimitives.ReadUInt16BigEndian(mapNode[10..]) != 1 ||
                        BinaryPrimitives.ReadUInt16BigEndian(mapNode[12..]) != 0 ||
                        BinaryPrimitives.ReadUInt32BigEndian(mapNode[4..]) != 0 ||
                        BinaryPrimitives.ReadUInt16BigEndian(mapNode[(NodeSize - 2)..]) != 14 ||
                        BinaryPrimitives.ReadUInt16BigEndian(mapNode[(NodeSize - 4)..]) != NodeSize - 6)
                    {
                        ReportBadMap($"Map node {nextMapNode} of the {name} B-tree has an invalid descriptor or record layout.");
                        return null;
                    }
                    nextMapNode = BinaryPrimitives.ReadUInt32BigEndian(mapNode);
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
                    if (mapIndex >= mapNodeOrder.Count) return false;
                    uint mapByte = continuationBit % MapNodeCapacity / 8;
                    uint mapNodeNumber = mapNodeOrder[(int)mapIndex];
                    int continuationByteOffset = checked((int)mapNodeNumber * NodeSize + 14 + (int)mapByte);
                    return (tree[continuationByteOffset] & (0x80 >> (int)(continuationBit & 7))) != 0;
                }

                if (!IsAllocated(0)) ReportBadMap($"The {name} B-tree header node is marked free in its node map.");
                foreach (uint mapNode in mapNodes)
                    if (!IsAllocated(mapNode)) ReportBadMap($"Map node {mapNode} of the {name} B-tree is marked free.");

                uint freeNodes = 0;
                for (uint nodeNumber = 0; nodeNumber < nodes; nodeNumber++)
                    if (!IsAllocated(nodeNumber)) freeNodes++;
                uint declaredFree = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 26));
                if (freeNodes != declaredFree)
                    ReportBadMap($"The {name} B-tree declares {declaredFree} free nodes, but its map contains {freeNodes}.");

                return mapNodeOrder;

                void ReportBadMap(string message) =>
                    context.Report(DiagnosticSeverity.Warning, "hfs.bad-btree-map", message);
            }

            private static bool IsNodeAllocated(byte[] tree, byte[] header, List<uint> mapNodes, uint nodeNumber)
            {
                int mapStart = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 6));
                int mapEnd = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(NodeSize - 8));
                uint headerCapacity = checked((uint)((mapEnd - mapStart) * 8));
                const uint MapNodeCapacity = (NodeSize - 20) * 8;
                if (nodeNumber < headerCapacity)
                    return (tree[mapStart + (int)(nodeNumber / 8)] & (0x80 >> (int)(nodeNumber & 7))) != 0;

                uint continuationBit = nodeNumber - headerCapacity;
                uint mapIndex = continuationBit / MapNodeCapacity;
                if (mapIndex >= mapNodes.Count) return false;
                int byteOffset = checked((int)mapNodes[(int)mapIndex] * NodeSize + 14 +
                    (int)(continuationBit % MapNodeCapacity / 8));
                return (tree[byteOffset] & (0x80 >> (int)(continuationBit & 7))) != 0;
            }

            private static bool IsValidKey(string name, byte[] key)
            {
                if (name == "catalog")
                {
                    if (key.Length < 7 || key[0] != key.Length - 1 || key[1] != 0 || key[6] > 31) return false;
                    int expectedLength = 7 + key[6] + (key[6] % 2 == 0 ? 1 : 0);
                    return key.Length == expectedLength;
                }

                return key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
            }

            private static int CompareKeys(string name, byte[] left, byte[] right)
            {
                if (name == "catalog") return HfsWriter.CompareCatalogKeys(left, right);

                int comparison = BinaryPrimitives.ReadUInt32BigEndian(left.AsSpan(2))
                    .CompareTo(BinaryPrimitives.ReadUInt32BigEndian(right.AsSpan(2)));
                if (comparison != 0) return comparison;
                comparison = left[1].CompareTo(right[1]);
                return comparison != 0
                    ? comparison
                    : BinaryPrimitives.ReadUInt16BigEndian(left.AsSpan(6))
                        .CompareTo(BinaryPrimitives.ReadUInt16BigEndian(right.AsSpan(6)));
            }

            private static byte[] Node(byte[] tree, long index) => tree.AsSpan((int)(index * NodeSize), NodeSize).ToArray();

            private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
        }
    }
}
