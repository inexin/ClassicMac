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
    /// image in place. HFS Plus volumes are recognised but not read yet.
    /// </summary>
    public sealed class HfsReader : IContainerReader
    {
        private const int MdbOffset = 1024;
        private const int MdbLength = 162;
        private const ushort HfsSignature = 0x4244; // 'BD'
        private const ushort HfsPlusSignature = 0x482B; // 'H+'
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
            return signature is HfsSignature or HfsPlusSignature;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException("Not an HFS volume.");
            var mdb = input.Slice(MdbOffset, MdbLength).ToArray();
            if (BinaryPrimitives.ReadUInt16BigEndian(mdb) == HfsPlusSignature)
            {
                context.Report(DiagnosticSeverity.Info, "hfs.plus", "This is an HFS Plus volume, which is not read yet.");
                return [];
            }
            return new Volume(input, mdb, context).Files();
        }

        // One HFS volume being read.
        private sealed class Volume
        {
            private readonly ForkData image;
            private readonly ContainerContext context;
            private readonly long blockSize;
            private readonly long firstBlock;
            private readonly int blockCount;
            private readonly Dictionary<(byte Fork, uint File), List<(uint StartBlock, byte[] Record)>> overflow = [];

            public Volume(ForkData image, byte[] mdb, ContainerContext context)
            {
                this.image = image;
                this.context = context;
                var m = mdb.AsSpan();
                blockSize = BinaryPrimitives.ReadUInt32BigEndian(m[0x14..]);
                firstBlock = BinaryPrimitives.ReadUInt16BigEndian(m[0x1C..]) * 512L;
                blockCount = BinaryPrimitives.ReadUInt16BigEndian(m[0x12..]);
                Name = new MacString(m.Slice(0x25, Math.Min(m[0x24], (byte)27)));
                FileCount = BinaryPrimitives.ReadUInt32BigEndian(m[0x54..]);
                FolderCount = BinaryPrimitives.ReadUInt32BigEndian(m[0x58..]);
                EmbeddedSignature = BinaryPrimitives.ReadUInt16BigEndian(m[0x7C..]);
                ExtentsLength = BinaryPrimitives.ReadUInt32BigEndian(m[0x82..]);
                ExtentsRecord = m.Slice(0x86, 12).ToArray();
                CatalogLength = BinaryPrimitives.ReadUInt32BigEndian(m[0x92..]);
                CatalogRecord = m.Slice(0x96, 12).ToArray();
            }

            public MacString Name { get; }

            private uint FileCount { get; }

            private uint FolderCount { get; }

            private ushort EmbeddedSignature { get; }

            private long ExtentsLength { get; }

            private byte[] ExtentsRecord { get; }

            private long CatalogLength { get; }

            private byte[] CatalogRecord { get; }

            public IReadOnlyList<MacFile> Files()
            {
                if (blockSize == 0 || blockSize % 512 != 0)
                    throw new InvalidDataException($"The allocation block size {blockSize} is not a multiple of 512.");
                if (EmbeddedSignature == HfsPlusSignature)
                {
                    context.Report(DiagnosticSeverity.Info, "hfs.plus-wrapper",
                        "This HFS volume wraps an HFS Plus volume, which is not read yet; only the wrapper's files are listed.");
                }

                // The extents overflow file never overflows itself; the catalog may.
                var extentsFile = Fork(ExtentsRecord, 0, 3, ExtentsLength, "extents overflow file");
                if (extentsFile is not null) ReadOverflow(extentsFile);
                var catalog = Fork(CatalogRecord, 0, CatalogFileId, CatalogLength, "catalog file")
                    ?? throw new InvalidDataException("The catalog file cannot be read.");

                var folders = new Dictionary<uint, (uint Parent, MacString Name)>();
                var files = new List<(uint Parent, MacString Name, byte[] Record)>();
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
                            folders[BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6))] = (parent, name);
                            break;
                        case 2 when data.Length >= 102: // file
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
                if (logicalLength == 0) return ForkData.Empty;
                var ranges = new List<(long, long)>();
                long covered = 0;
                void Add(ReadOnlySpan<byte> record)
                {
                    for (var i = 0; i < 3 && covered < logicalLength; i++)
                    {
                        int start = BinaryPrimitives.ReadUInt16BigEndian(record[(i * 4)..]);
                        int count = BinaryPrimitives.ReadUInt16BigEndian(record[(i * 4 + 2)..]);
                        if (count == 0) continue;
                        if (start + count > blockCount)
                        {
                            context.Report(DiagnosticSeverity.Error, "hfs.extent-outside",
                                $"An extent of {what} ({count} blocks at {start}) lies outside the volume's {blockCount} blocks.");
                            covered = long.MaxValue;
                            return;
                        }
                        ranges.Add((firstBlock + start * blockSize, count * blockSize));
                        covered += count * blockSize;
                    }
                }

                Add(firstExtents);
                if (covered < logicalLength && overflow.TryGetValue((forkType, fileId), out var more))
                {
                    foreach (var (_, record) in more.OrderBy(m => m.StartBlock))
                    {
                        if (covered >= logicalLength) break;
                        Add(record);
                    }
                }
                if (covered == long.MaxValue) return null;
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

            // Extents overflow leaf records: key (length 7, fork type, file ID, first allocation block), 3 extents.
            private void ReadOverflow(ForkData extentsFile)
            {
                foreach (var (key, data) in LeafRecords(extentsFile, "extents overflow"))
                {
                    if (key.Length < 8 || data.Length < 12) continue;
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
                var nodes = tree.Length / NodeSize;
                var header = Node(tree, 0);
                var node = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14 + 10));
                var visited = new HashSet<uint>();
                while (node != 0)
                {
                    if (node >= nodes || !visited.Add(node))
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.bad-link",
                            $"The {name} tree links to node {node}, which is outside the tree or already read; stopped.");
                        yield break;
                    }
                    var bytes = Node(tree, node);
                    if ((sbyte)bytes[8] != -1)
                    {
                        context.Report(DiagnosticSeverity.Error, "hfs.not-leaf",
                            $"Node {node} of the {name} tree is linked as a leaf but has type {(sbyte)bytes[8]}; stopped.");
                        yield break;
                    }
                    int records = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10));
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
                        yield return (bytes[start..(start + 1 + keyLength)], bytes[dataStart..end]);
                    }
                    node = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                }
            }

            private static byte[] Node(byte[] tree, long index) => tree.AsSpan((int)(index * NodeSize), NodeSize).ToArray();

            private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
        }
    }
}
