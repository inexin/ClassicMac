using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// HFS Plus structures and offsets follow Apple Technical Note TN1150.
internal static class HfsPlusReader
{
    private const int HeaderOffset = 1024;
    private const int HeaderLength = 512;
    private const uint RootFolderId = 2;

    public static IReadOnlyList<MacFile> Read(ForkData image, ContainerContext context)
    {
        byte[] header = image.Slice(HeaderOffset, HeaderLength).ToArray();
        ushort signature = U16(header, 0);
        ushort version = U16(header, 2);
        if (signature is not (0x482B or 0x4858))
            throw new InvalidDataException($"Unknown HFS Plus volume signature 0x{signature:X4}.");
        if ((signature == 0x482B && version != 4) || (signature == 0x4858 && version != 5))
            throw new InvalidDataException($"Unsupported HFS Plus version {version}.");
        uint blockSize = U32(header, 40);
        uint totalBlocks = U32(header, 44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 ||
            totalBlocks == 0 || (ulong)blockSize * totalBlocks > (ulong)image.Length)
            throw new InvalidDataException("The HFS Plus allocation area is invalid.");

        var overflow = new Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>();
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(192, 8)) != 0)
        {
            ForkData extentsFork = ReadFork(image, header.AsSpan(192, 80), blockSize, totalBlocks);
            foreach (var (key, data) in LeafRecords(
                extentsFork.ToArray(context.Options.MaxExpandedBytesPerInput), "extents-overflow"))
            {
                if (key.Length != 12 || U16(key, 0) != 10 || data.Length < 64 || key[2] is not (0 or 0xFF))
                    throw new InvalidDataException("An HFS Plus extents-overflow record is invalid.");
                var id = (key[2], U32(key, 4));
                uint start = U32(key, 8);
                if (!overflow.TryGetValue(id, out var entries)) overflow[id] = entries = [];
                entries.Add((start, data.AsSpan(0, 64).ToArray()));
            }
        }

        var catalogFork = ReadFork(image, header.AsSpan(272, 80), blockSize, totalBlocks, overflow, 0, 4);
        byte[] catalog = catalogFork.ToArray();
        var records = LeafRecords(catalog, "catalog", isHfsX: signature == 0x4858).ToArray();
        var folders = new Dictionary<uint, (uint Parent, string Name, uint Valence)>();
        var catalogIds = new HashSet<uint>();
        var catalogNodes = new Dictionary<uint, CatalogNode>();
        var catalogThreads = new Dictionary<uint, CatalogThread>();
        foreach (var (key, data) in records)
        {
            if (key.Length < 8 || data.Length < 2)
                throw new InvalidDataException("An HFS Plus catalog record is truncated.");
            switch (U16(data, 0))
            {
                case 1:
                    if (data.Length < 88) throw new InvalidDataException("An HFS Plus folder record is truncated.");
                    uint id = U32(data, 8);
                    if (!catalogIds.Add(id))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    uint parent = U32(key, 2);
                    string name = Name(key);
                    if (!folders.TryAdd(id, (parent, name, U32(data, 4))))
                        throw new InvalidDataException("Duplicate HFS Plus folder ID.");
                    catalogNodes.Add(id, new CatalogNode(parent, name, IsFolder: true));
                    break;
                case 2:
                    if (data.Length < 248) throw new InvalidDataException("An HFS Plus file record is truncated.");
                    uint fileId = U32(data, 8);
                    if (!catalogIds.Add(fileId))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    if ((U16(data, 2) & 0x0002) == 0)
                        throw new InvalidDataException("An HFS Plus file is missing its required thread flag.");
                    catalogNodes.Add(fileId, new CatalogNode(U32(key, 2), Name(key), IsFolder: false));
                    break;
                case 3 or 4:
                    if (key.Length != 8 || U16(key, 0) != 6 || U16(key, 6) != 0)
                        throw new InvalidDataException("An HFS Plus catalog thread key is invalid.");
                    if (data.Length < 10) throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    ushort threadNameLength = U16(data, 8);
                    if (threadNameLength > 255 || data.Length < 10 + 2 * threadNameLength)
                        throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    uint threadId = U32(key, 2);
                    var thread = new CatalogThread(U32(data, 4),
                        Encoding.BigEndianUnicode.GetString(data, 10, threadNameLength * 2)
                            .Normalize(NormalizationForm.FormC),
                        IsFolder: U16(data, 0) == 3);
                    if (!catalogThreads.TryAdd(threadId, thread))
                        throw new InvalidDataException("Duplicate HFS Plus catalog thread ID.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown HFS Plus catalog record type {U16(data, 0)}.");
            }
        }
        if (!folders.ContainsKey(RootFolderId))
            throw new InvalidDataException("The HFS Plus root folder is missing.");
        const uint catalogNodeIdsReused = 1u << 12;
        if ((U32(header, 4) & catalogNodeIdsReused) == 0 && U32(header, 64) <= catalogIds.Max())
            throw new InvalidDataException("The HFS Plus next catalog ID is not greater than all catalog IDs.");
        ValidateCatalogThreads(catalogNodes, catalogThreads);
        var childCounts = new Dictionary<uint, uint>(folders.Count);
        foreach (uint folderId in folders.Keys) childCounts.Add(folderId, 0);
        foreach (CatalogNode node in catalogNodes.Values)
            if (childCounts.TryGetValue(node.Parent, out uint childCount))
                childCounts[node.Parent] = checked(childCount + 1);
        foreach (var (folderId, folder) in folders)
        {
            uint childCount = childCounts[folderId];
            if (folder.Valence != childCount)
                throw new InvalidDataException(
                    $"HFS Plus folder {folderId} has valence {folder.Valence}, but {childCount} catalog children.");
            if (folderId != RootFolderId)
                _ = FolderPath(folder.Parent, folders);
        }

        var result = new List<MacFile>();
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            string name = Name(key);
            uint parent = U32(key, 2);
            byte[] info = [.. data.AsSpan(48, 16), .. data.AsSpan(64, 16)];
            var path = FolderPath(parent, folders);
            result.Add(new MacFile
            {
                Name = LegacyName(name),
                UnicodeName = name,
                FolderPath = path.Select(LegacyName).ToArray(),
                UnicodeFolderPath = path,
                FinderInfo = FinderInfo.Read(info),
                Created = Date(U32(data, 12)),
                Modified = Date(U32(data, 16)),
                DataFork = ReadFork(image, data.AsSpan(88, 80), blockSize, totalBlocks, overflow, 0, U32(data, 8)),
                ResourceFork = ReadFork(image, data.AsSpan(168, 80), blockSize, totalBlocks, overflow, 0xFF, U32(data, 8)),
            });
        }
        uint expectedFiles = U32(header, 32);
        uint expectedFolders = U32(header, 36);
        if (result.Count != expectedFiles || folders.Count - 1 != expectedFolders)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-counts",
                $"The HFS Plus catalog has {result.Count} files and {folders.Count - 1} folders; " +
                $"the volume header says {expectedFiles} and {expectedFolders}.");
        return result;
    }

    private static void ValidateCatalogThreads(Dictionary<uint, CatalogNode> nodes,
        Dictionary<uint, CatalogThread> threads)
    {
        foreach (var (id, node) in nodes)
        {
            if (!threads.TryGetValue(id, out var thread))
                throw new InvalidDataException($"The HFS Plus catalog node {id} has no thread record.");
            if (thread.IsFolder != node.IsFolder || thread.Parent != node.Parent ||
                !string.Equals(thread.Name, node.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"The HFS Plus catalog thread for node {id} is inconsistent.");
        }
        if (threads.Count != nodes.Count)
            throw new InvalidDataException("The HFS Plus catalog has a thread for a missing node.");
    }

    private static ForkData ReadFork(ForkData image, ReadOnlySpan<byte> fork, uint blockSize, uint totalBlocks,
        Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>? overflow = null,
        byte forkType = 0, uint fileId = 0)
    {
        ulong logical = BinaryPrimitives.ReadUInt64BigEndian(fork);
        if (logical == 0) return ForkData.Empty;
        if (logical > long.MaxValue) throw new InvalidDataException("An HFS Plus fork is too large.");
        uint allocatedBlocks = U32(fork, 12);
        if (allocatedBlocks == 0)
            throw new InvalidDataException("A nonempty HFS Plus fork has no allocated blocks.");
        var ranges = new List<(long Offset, long Length)>();
        uint coveredBlocks = 0;
        void AddExtents(ReadOnlySpan<byte> extents)
        {
            for (int index = 0; index < 8; index++)
            {
                uint start = U32(extents, index * 8);
                uint count = U32(extents, index * 8 + 4);
                if (count == 0) break;
                if (count > allocatedBlocks - coveredBlocks)
                    throw new InvalidDataException("An HFS Plus fork's extents exceed its allocated block count.");
                if ((ulong)start + count > totalBlocks)
                    throw new InvalidDataException("An HFS Plus extent lies outside the allocation area.");
                long offset = checked((long)start * blockSize);
                long length = checked((long)count * blockSize);
                if (offset > image.Length - length)
                    throw new InvalidDataException("An HFS Plus extent lies outside the image.");
                ranges.Add((offset, length));
                coveredBlocks = checked(coveredBlocks + count);
            }
        }
        AddExtents(fork.Slice(16, 64));
        if (coveredBlocks < allocatedBlocks && overflow is not null &&
            overflow.TryGetValue((forkType, fileId), out var entries))
            foreach (var entry in entries.OrderBy(e => e.Start))
            {
                if (coveredBlocks >= allocatedBlocks) break;
                if (entry.Start != coveredBlocks)
                    throw new InvalidDataException("An HFS Plus overflow extent is not contiguous with the fork.");
                AddExtents(entry.Extents);
            }
        if (coveredBlocks != allocatedBlocks)
            throw new InvalidDataException("An HFS Plus fork's extent count differs from its allocated block count.");
        if ((ulong)coveredBlocks * blockSize < logical)
            throw new InvalidDataException("An HFS Plus fork has insufficient extents for its logical length.");
        return new ExtentForkData(image, ranges, checked((long)logical));
    }

    private static IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(byte[] tree, string name,
        bool isHfsX = false)
    {
        if (tree.Length < 512 || tree[8] != 1)
            throw new InvalidDataException($"The HFS Plus {name} tree has no B-tree header.");
        if (U32(tree, 4) != 0 || U16(tree, 10) != 3)
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node is invalid.");
        if (tree[14 + 36] != 0)
            throw new InvalidDataException($"The HFS Plus {name} B-tree has an invalid tree type.");
        uint attributes = U32(tree, 14 + 38);
        bool variableIndexKeys = (attributes & 0x00000004) != 0;
        if ((attributes & 0x00000002) == 0 || variableIndexKeys != (name == "catalog"))
            throw new InvalidDataException($"The HFS Plus {name} B-tree key-layout attributes are invalid.");
        bool caseSensitiveCatalog = false;
        if (name == "catalog" && isHfsX)
        {
            byte keyCompareType = tree[14 + 37];
            if (keyCompareType is not (0xBC or 0xCF))
                throw new InvalidDataException("The HFSX catalog has an unsupported key comparison type.");
            caseSensitiveCatalog = keyCompareType == 0xBC;
        }
        int nodeSize = U16(tree, 32);
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0 || tree.Length % nodeSize != 0)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is invalid.");
        uint totalNodes = U32(tree, 36);
        if (totalNodes == 0 || totalNodes > tree.Length / nodeSize)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node count is invalid.");
        uint first = U32(tree, 24);
        uint last = U32(tree, 28);
        ushort depth = U16(tree, 14);
        uint root = U32(tree, 16);
        uint expectedRecords = U32(tree, 20);
        if (expectedRecords == 0)
        {
            if (depth != 0 || root != 0 || first != 0 || last != 0)
                throw new InvalidDataException($"The empty HFS Plus {name} B-tree has root or leaf nodes.");
            ValidateNodeMap(tree, nodeSize, []);
            yield break;
        }
        if (depth == 0 || root == 0 || root >= totalNodes || first == 0 || last == 0 ||
            first >= totalNodes || last >= totalNodes)
            throw new InvalidDataException($"The HFS Plus {name} B-tree root or leaf endpoints are invalid.");
        int rootOffset = checked((int)root * nodeSize);
        if (depth == 1)
        {
            if (root != first || first != last || tree[rootOffset + 8] != 0xFF || tree[rootOffset + 9] != 1)
                throw new InvalidDataException($"The single-leaf HFS Plus {name} B-tree has an invalid root.");
        }
        else if (tree[rootOffset + 8] != 0 || tree[rootOffset + 9] != depth)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree root kind or height is invalid.");
        }
        (HashSet<uint> indexedLeaves, HashSet<uint> indexedNodes) =
            ValidateIndexGraph(tree, name, nodeSize, totalNodes, root, depth);
        uint readRecords = 0;
        uint previous = 0;
        uint finalLeaf = 0;
        var seen = new HashSet<uint>();
        var keys = new HashSet<byte[]>(ByteArrayEqualityComparer.Instance);
        byte[]? previousExtentKey = null;
        byte[]? previousCatalogKey = null;
        for (uint node = first; node != 0; node = U32(tree, checked((int)node * nodeSize)))
        {
            if (!seen.Add(node) || node >= totalNodes)
                throw new InvalidDataException($"The HFS Plus {name} B-tree leaf chain is invalid.");
            int start = checked((int)node * nodeSize);
            if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                throw new InvalidDataException($"An HFS Plus {name} B-tree linked leaf has an invalid type.");
            if (U32(tree, start + 4) != previous)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has an invalid backward link.");
            int count = U16(tree, start + 10);
            if (count > (nodeSize - 14) / 2)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has too many records.");
            for (int index = 0; index < count; index++)
            {
                int begin = U16(tree, start + nodeSize - 2 * (index + 1));
                int end = U16(tree, start + nodeSize - 2 * (index + 2));
                if (begin < 14 || end <= begin || end > nodeSize - 2 * (count + 1))
                    throw new InvalidDataException($"An HFS Plus {name} B-tree record offset is invalid.");
                int offset = start + begin;
                int keyLength = U16(tree, offset);
                if (keyLength < 6 || 2 + keyLength > end - begin)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree key is invalid.");
                int dataOffset = offset + 2 + keyLength;
                byte[] key = tree.AsSpan(offset, 2 + keyLength).ToArray();
                if (name == "catalog" && caseSensitiveCatalog)
                {
                    ValidateCatalogKey(key);
                    if (previousCatalogKey is not null && CompareHfsXCatalogKeys(previousCatalogKey, key) >= 0)
                        throw new InvalidDataException("The HFSX catalog keys are not strictly ordered.");
                    previousCatalogKey = key;
                }
                else if (name == "extents-overflow")
                {
                    if (key.Length != 12 || keyLength != 10 || key[2] is not (0 or 0xFF))
                        throw new InvalidDataException("An HFS Plus extents-overflow key is invalid.");
                    if (previousExtentKey is not null && CompareExtentKeys(previousExtentKey, key) >= 0)
                        throw new InvalidDataException("The HFS Plus extents-overflow keys are not strictly ordered.");
                    previousExtentKey = key;
                }
                else if (!keys.Add(key))
                    throw new InvalidDataException($"The HFS Plus {name} B-tree has a duplicate leaf key.");
                yield return (key,
                    tree.AsSpan(dataOffset, start + end - dataOffset).ToArray());
                readRecords++;
            }
            finalLeaf = node;
            previous = node;
        }
        if (readRecords != expectedRecords)
            throw new InvalidDataException($"The HFS Plus {name} B-tree leaf-record count is inconsistent.");
        if (finalLeaf != last)
            throw new InvalidDataException($"The HFS Plus {name} B-tree ends at leaf {finalLeaf}, not {last}.");
        if (!seen.SetEquals(indexedLeaves))
            throw new InvalidDataException($"The HFS Plus {name} B-tree index and leaf chain disagree.");
        ValidateNodeMap(tree, nodeSize, indexedNodes);
    }

    private static (HashSet<uint> Leaves, HashSet<uint> Nodes) ValidateIndexGraph(byte[] tree, string name,
        int nodeSize, uint totalNodes, uint root, ushort depth)
    {
        var visitedNodes = new HashSet<uint>();
        var leafNodes = new HashSet<uint>();
        var nodesByHeight = new Dictionary<ushort, List<uint>>();
        int maxKeyLength = U16(tree, 34);

        void AddAtHeight(uint nodeNumber, ushort height)
        {
            if (!nodesByHeight.TryGetValue(height, out var nodes)) nodesByHeight.Add(height, nodes = []);
            nodes.Add(nodeNumber);
        }

        void Visit(uint nodeNumber, ushort expectedHeight)
        {
            if (nodeNumber >= totalNodes || !visitedNodes.Add(nodeNumber))
                throw new InvalidDataException($"The HFS Plus {name} B-tree index graph is cyclic or out of range.");
            int start = checked((int)nodeNumber * nodeSize);
            if (expectedHeight == 1)
            {
                if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index points to a non-leaf node.");
                AddAtHeight(nodeNumber, expectedHeight);
                leafNodes.Add(nodeNumber);
                return;
            }

            if (tree[start + 8] != 0 || tree[start + 9] != expectedHeight)
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid kind or height.");
            AddAtHeight(nodeNumber, expectedHeight);
            int count = U16(tree, start + 10);
            if (count < 2 || count > (nodeSize - 14) / 2)
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid record count.");
            int offsetTableStart = nodeSize - 2 * (count + 1);
            for (int index = 0; index < count; index++)
            {
                int begin = U16(tree, start + nodeSize - 2 * (index + 1));
                int end = U16(tree, start + nodeSize - 2 * (index + 2));
                if (begin < 14 || end <= begin || end > offsetTableStart)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record offset is invalid.");
                int keyLength = U16(tree, start + begin);
                if (keyLength < 6 || keyLength > maxKeyLength)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index key length is invalid.");
                int storedKeyLength = name == "catalog" ? keyLength : maxKeyLength;
                int childOffset = begin + 2 + storedKeyLength;
                if ((childOffset & 1) != 0) childOffset++;
                if (childOffset > end - 4)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record is truncated.");
                Visit(U32(tree, start + childOffset), checked((ushort)(expectedHeight - 1)));
            }
        }

        Visit(root, depth);
        foreach (var (height, nodes) in nodesByHeight)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                int start = checked((int)nodes[index] * nodeSize);
                uint expectedForward = index + 1 < nodes.Count ? nodes[index + 1] : 0;
                uint expectedBackward = index > 0 ? nodes[index - 1] : 0;
                if (U32(tree, start) != expectedForward || U32(tree, start + 4) != expectedBackward)
                    throw new InvalidDataException(
                        $"The HFS Plus {name} B-tree height-{height} sibling links are invalid.");
            }
        }
        return (leafNodes, visitedNodes);
    }

    private static void ValidateNodeMap(byte[] tree, int nodeSize, HashSet<uint> referencedNodes)
    {
        const int MapOffset = 14 + 106 + 128;
        int mapLength = nodeSize - 256;
        uint addressableNodes = checked((uint)(mapLength * 8));

        void RequireAllocated(uint nodeNumber)
        {
            if (nodeNumber >= addressableNodes) return; // Later nodes are represented by chained map nodes.
            int byteOffset = MapOffset + checked((int)(nodeNumber / 8));
            byte mask = (byte)(0x80 >> (int)(nodeNumber % 8));
            if ((tree[byteOffset] & mask) == 0)
                throw new InvalidDataException($"HFS Plus B-tree node {nodeNumber} is referenced but marked free.");
        }

        RequireAllocated(0);
        foreach (uint nodeNumber in referencedNodes) RequireAllocated(nodeNumber);
    }

    private static string Name(ReadOnlySpan<byte> key)
    {
        if (key.Length < 8) throw new InvalidDataException("An HFS Plus catalog name is truncated.");
        int length = U16(key, 6);
        if (length > 255 || key.Length < 8 + 2 * length)
            throw new InvalidDataException("An HFS Plus catalog name is invalid.");
        return Encoding.BigEndianUnicode.GetString(key.Slice(8, 2 * length)).Normalize(NormalizationForm.FormC);
    }

    private static int CompareExtentKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = U32(left, 4).CompareTo(U32(right, 4));
        if (comparison != 0) return comparison;
        comparison = left[2].CompareTo(right[2]);
        return comparison != 0 ? comparison : U32(left, 8).CompareTo(U32(right, 8));
    }

    private static void ValidateCatalogKey(ReadOnlySpan<byte> key)
    {
        int nameLength = U16(key, 6);
        if (nameLength > 255 || key.Length != 8 + nameLength * 2)
            throw new InvalidDataException("An HFSX catalog key has an invalid name length.");
    }

    private static int CompareHfsXCatalogKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = U32(left, 2).CompareTo(U32(right, 2));
        if (comparison != 0) return comparison;
        int leftLength = U16(left, 6);
        int rightLength = U16(right, 6);
        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            comparison = U16(left, 8 + index * 2).CompareTo(U16(right, 8 + index * 2));
            if (comparison != 0) return comparison;
        }
        return leftLength.CompareTo(rightLength);
    }

    private static IReadOnlyList<string> FolderPath(uint parent,
        Dictionary<uint, (uint Parent, string Name, uint Valence)> folders)
    {
        var path = new List<string>();
        var seen = new HashSet<uint>();
        while (parent != RootFolderId)
        {
            if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
                throw new InvalidDataException("An HFS Plus folder path is missing or cyclic.");
            path.Insert(0, folder.Name);
            parent = folder.Parent;
        }
        return path;
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
    private readonly record struct CatalogNode(uint Parent, string Name, bool IsFolder);
    private readonly record struct CatalogThread(uint Parent, string Name, bool IsFolder);
    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayEqualityComparer Instance { get; } = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] key)
        {
            var hash = new HashCode();
            foreach (byte value in key) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    private static MacString LegacyName(string name)
    {
        try { return MacString.FromMacRoman(name); }
        catch (ArgumentException) { return MacString.FromMacRoman("?"); }
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
