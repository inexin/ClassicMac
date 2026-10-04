using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusReader;
using static ClassicMac.Files.Hfs.HfsPlusJournal;
using static ClassicMac.Files.Hfs.HfsPlusAllocation;
using static ClassicMac.Files.Hfs.HfsPlusAttributes;
using static ClassicMac.Files.Hfs.HfsPlusLinks;

namespace ClassicMac.Files.Hfs;

// HFS Plus reading, for HfsPlusReader: the B-trees: walking a tree's leaves, checking its header, index graph and node map, and the key orders.
internal static class HfsPlusBTree
{
    internal static IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(byte[] tree, string name,
        bool isHfsX = false, ContainerContext? context = null)
    {
        if (tree.Length < 512 || tree[8] != 1)
        {
            throw new InvalidDataException($"The HFS Plus {name} tree has no B-tree header.");
        }

        var treeReader = new BigEndianReader(tree);
        if (U32(treeReader, 4) != 0 || tree[9] != 0 || U16(treeReader, 10) != 3)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node is invalid.");
        }

        byte treeType = tree[14 + 36];
        if (treeType != 0)
        {
            if (name == "attributes" && treeType == 0xFF)
            {
                context?.Report(DiagnosticSeverity.Warning, "hfs.plus-btree-type",
                    "The HFS Plus attributes B-tree uses a reserved tree type written by Mac OS X; it was read for compatibility.");
            }
            else
            {
                throw new InvalidDataException($"The HFS Plus {name} B-tree has an invalid tree type.");
            }
        }
        uint attributes = U32(treeReader, 14 + 38);
        bool variableIndexKeys = (attributes & 0x00000004) != 0;
        bool hasVariableIndexKeys = name is "catalog" or "attributes";
        if ((attributes & 0x00000002) == 0 || variableIndexKeys != hasVariableIndexKeys)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree key-layout attributes are invalid.");
        }

        bool caseSensitiveCatalog = false;
        bool caseFoldingCatalog = name == "catalog" && !isHfsX;
        if (name == "catalog" && isHfsX)
        {
            byte keyCompareType = tree[14 + 37];
            if (keyCompareType is not (0xBC or 0xCF))
            {
                throw new InvalidDataException("The HFSX catalog has an unsupported key comparison type.");
            }

            caseSensitiveCatalog = keyCompareType == 0xBC;
            caseFoldingCatalog = keyCompareType == 0xCF;
        }
        int nodeSize = U16(treeReader, 32);
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0 || tree.Length % nodeSize != 0)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is invalid.");
        }

        int maxKeyLength = U16(treeReader, 34);
        int definedMaxKeyLength = name switch
        {
            "catalog" => 516,
            "extents-overflow" => 10,
            // HFSPlusAttrKey is 268 bytes including its 2-byte keyLength field (Apple hfs_format.h).
            "attributes" => 266,
            _ => maxKeyLength
        };
        if (maxKeyLength != definedMaxKeyLength)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree maximum key length is invalid.");
        }

        ValidateHeaderNodeRecordLayout(treeReader, nodeSize, name);
        if ((name is "catalog" or "attributes") && nodeSize < 4096)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is below the 4 KiB minimum.");
        }

        uint totalNodes = U32(treeReader, 36);
        if (totalNodes == 0 || totalNodes != tree.Length / nodeSize)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree node count is invalid.");
        }

        uint first = U32(treeReader, 24);
        uint last = U32(treeReader, 28);
        ushort depth = U16(treeReader, 14);
        uint root = U32(treeReader, 16);
        uint expectedRecords = U32(treeReader, 20);
        if (expectedRecords == 0)
        {
            if (depth == 0 && root == 0 && first == 0 && last == 0)
            {
                ValidateNodeMap(tree, nodeSize, totalNodes, [], name);
                yield break;
            }

            if (depth != 1 || root == 0 || root >= totalNodes || root != first || root != last ||
                U16(treeReader, checked((int)root * nodeSize + 10)) != 0)
            {
                throw new InvalidDataException($"The empty HFS Plus {name} B-tree has invalid root or leaf fields.");
            }

            var (_, emptyRootNodes) = ValidateIndexGraph(tree, name, nodeSize, maxKeyLength, totalNodes,
                root, depth, caseSensitiveCatalog, caseFoldingCatalog, isHfsX);
            ValidateNodeMap(tree, nodeSize, totalNodes, emptyRootNodes, name);
            yield break;
        }
        if (depth == 0 || root == 0 || root >= totalNodes || first == 0 || last == 0 ||
            first >= totalNodes || last >= totalNodes)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree root or leaf endpoints are invalid.");
        }

        int rootOffset = checked((int)root * nodeSize);
        if (depth == 1)
        {
            if (root != first || first != last || tree[rootOffset + 8] != 0xFF || tree[rootOffset + 9] != 1)
            {
                throw new InvalidDataException($"The single-leaf HFS Plus {name} B-tree has an invalid root.");
            }
        }
        else if (tree[rootOffset + 8] != 0 || tree[rootOffset + 9] != depth)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree root kind or height is invalid.");
        }
        (HashSet<uint> indexedLeaves, HashSet<uint> indexedNodes) =
            ValidateIndexGraph(tree, name, nodeSize, maxKeyLength, totalNodes, root, depth, caseSensitiveCatalog,
                caseFoldingCatalog, isHfsX);
        uint readRecords = 0;
        uint previous = 0;
        uint finalLeaf = 0;
        var seen = new HashSet<uint>();
        var keys = new HashSet<byte[]>(ByteArrayEqualityComparer.Instance);
        byte[]? previousExtentKey = null;
        byte[]? previousCatalogKey = null;
        byte[]? previousAttributeKey = null;
        for (uint node = first; node != 0; node = U32(treeReader, checked((int)node * nodeSize)))
        {
            if (!seen.Add(node) || node >= totalNodes)
            {
                throw new InvalidDataException($"The HFS Plus {name} B-tree leaf chain is invalid.");
            }

            int start = checked((int)node * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(treeReader, start, nodeSize, name);
            if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree linked leaf has an invalid type.");
            }

            if (U32(treeReader, start + 4) != previous)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has an invalid backward link.");
            }

            int count = U16(treeReader, start + 10);
            if (count > (nodeSize - 14) / 2)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has too many records.");
            }

            for (int index = 0; index < count; index++)
            {
                int begin = U16(treeReader, start + nodeSize - 2 * (index + 1));
                int end = U16(treeReader, start + nodeSize - 2 * (index + 2));
                if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin ||
                    end > nodeSize - 2 * (count + 1))
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree record offset is invalid.");
                }

                int offset = start + begin;
                int keyLength = U16(treeReader, offset);
                if (keyLength < 6 || 2 + keyLength > end - begin)
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree key is invalid.");
                }

                int dataOffset = offset + 2 + keyLength;
                byte[] key = tree.AsSpan(offset, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    var keyReader = new BigEndianReader(key);
                    ValidateCatalogKey(keyReader, isHfsX);
                    ReadOnlyMemory<byte> recordData = tree.AsMemory(dataOffset, start + end - dataOffset);
                    if (U16(keyReader, 6) == 0 && recordData.Length >= 2 &&
                        U16(new BigEndianReader(recordData), 0) is 1 or 2)
                    {
                        throw new InvalidDataException("An HFS Plus file or folder catalog key has an empty name.");
                    }

                    if (caseSensitiveCatalog || caseFoldingCatalog)
                    {
                        if (previousCatalogKey is not null &&
                            CompareCatalogKeys(previousCatalogKey, key, caseFoldingCatalog) >= 0)
                        {
                            throw new InvalidDataException("The HFS Plus catalog keys are not strictly ordered.");
                        }

                        previousCatalogKey = key;
                    }
                    else if (!keys.Add(key))
                    {
                        throw new InvalidDataException("The HFS Plus catalog B-tree has a duplicate leaf key.");
                    }
                }
                else if (name == "extents-overflow")
                {
                    if (key.Length != 12 || keyLength != 10 || key[2] is not (0 or 0xFF))
                    {
                        throw new InvalidDataException("An HFS Plus extents-overflow key is invalid.");
                    }

                    if (previousExtentKey is not null && CompareExtentKeys(previousExtentKey, key) >= 0)
                    {
                        throw new InvalidDataException("The HFS Plus extents-overflow keys are not strictly ordered.");
                    }

                    previousExtentKey = key;
                }
                else if (name == "attributes")
                {
                    ValidateAttributeKey(key);
                    if (previousAttributeKey is not null && CompareAttributeKeys(previousAttributeKey, key) >= 0)
                    {
                        throw new InvalidDataException("The HFS Plus attributes B-tree keys are not strictly ordered.");
                    }

                    previousAttributeKey = key;
                }
                else if (!keys.Add(key))
                {
                    throw new InvalidDataException($"The HFS Plus {name} B-tree has a duplicate leaf key.");
                }

                yield return (key,
                    tree.AsSpan(dataOffset, start + end - dataOffset).ToArray());
                readRecords++;
            }
            finalLeaf = node;
            previous = node;
        }
        if (readRecords != expectedRecords)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree leaf-record count is inconsistent.");
        }

        if (finalLeaf != last)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree ends at leaf {finalLeaf}, not {last}.");
        }

        if (!seen.SetEquals(indexedLeaves))
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree index and leaf chain disagree.");
        }

        ValidateNodeMap(tree, nodeSize, totalNodes, indexedNodes, name);
    }

    internal static void ValidateHeaderNodeRecordLayout(BigEndianReader headerNode, int nodeSize, string name)
    {
        int freeSpaceOffset = nodeSize - 8;
        if (U16(headerNode, nodeSize - 2) != 14 ||
            U16(headerNode, nodeSize - 4) != 14 + 106 ||
            U16(headerNode, nodeSize - 6) != 14 + 106 + 128 ||
            U16(headerNode, freeSpaceOffset) != freeSpaceOffset)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node has an invalid record layout.");
        }
    }

    internal static void RequireFirstRecordStartsAtNodeDescriptorEnd(BigEndianReader tree, int nodeStart,
        int nodeSize, string name)
    {
        if (U16(tree, nodeStart + 10) != 0 && U16(tree, nodeStart + nodeSize - 2) != 14)
        {
            throw new InvalidDataException($"An HFS Plus {name} B-tree record does not start after its node descriptor.");
        }
    }

    internal static (HashSet<uint> Leaves, HashSet<uint> Nodes) ValidateIndexGraph(byte[] tree, string name,
        int nodeSize, int maxKeyLength, uint totalNodes, uint root, ushort depth, bool caseSensitiveCatalog,
        bool caseFoldingCatalog, bool isHfsX)
    {
        var visitedNodes = new HashSet<uint>();
        var leafNodes = new HashSet<uint>();
        var nodesByHeight = new Dictionary<ushort, List<uint>>();
        var indexKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)>();
        var subtreeKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)?>();
        bool validateChildKeyRanges = name is "catalog" or "extents-overflow" or "attributes";
        var treeReader = new BigEndianReader(tree);

        void AddAtHeight(uint nodeNumber, ushort height)
        {
            if (!nodesByHeight.TryGetValue(height, out var nodes))
            {
                nodesByHeight.Add(height, nodes = []);
            }

            nodes.Add(nodeNumber);
        }

        (byte[] First, byte[] Last)? Visit(uint nodeNumber, ushort expectedHeight)
        {
            if (nodeNumber >= totalNodes || !visitedNodes.Add(nodeNumber))
            {
                throw new InvalidDataException($"The HFS Plus {name} B-tree index graph is cyclic or out of range.");
            }

            int start = checked((int)nodeNumber * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(treeReader, start, nodeSize, name);
            if (expectedHeight == 1)
            {
                if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index points to a non-leaf node.");
                }

                AddAtHeight(nodeNumber, expectedHeight);
                leafNodes.Add(nodeNumber);
                var leafRange = LeafKeyRange(treeReader, start, nodeSize, maxKeyLength, name);
                subtreeKeyRanges.Add(nodeNumber, leafRange);
                return leafRange;
            }

            if (tree[start + 8] != 0 || tree[start + 9] != expectedHeight)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid kind or height.");
            }

            AddAtHeight(nodeNumber, expectedHeight);
            int count = U16(treeReader, start + 10);
            if (count < 2 || count > (nodeSize - 14) / 2)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid record count.");
            }

            int offsetTableStart = nodeSize - 2 * (count + 1);
            byte[]? previousIndexKey = null;
            byte[]? firstIndexKey = null;
            var childRanges = new List<(byte[] Key, (byte[] First, byte[] Last)? Range)>(count);
            for (int index = 0; index < count; index++)
            {
                int begin = U16(treeReader, start + nodeSize - 2 * (index + 1));
                int end = U16(treeReader, start + nodeSize - 2 * (index + 2));
                if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin || end > offsetTableStart)
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record offset is invalid.");
                }

                int keyLength = U16(treeReader, start + begin);
                if (keyLength < 6 || keyLength > maxKeyLength)
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index key length is invalid.");
                }

                byte[] indexKey = tree.AsSpan(start + begin, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    ValidateCatalogKey(new BigEndianReader(indexKey), isHfsX);
                    if ((caseSensitiveCatalog || caseFoldingCatalog) && previousIndexKey is not null &&
                        CompareCatalogKeys(previousIndexKey, indexKey, caseFoldingCatalog) >= 0)
                    {
                        throw new InvalidDataException("The HFS Plus catalog index keys are not strictly ordered.");
                    }
                }
                else if (name == "extents-overflow")
                {
                    if (keyLength != 10 || indexKey[2] is not (0 or 0xFF))
                    {
                        throw new InvalidDataException("An HFS Plus extents-overflow index key is invalid.");
                    }

                    if (previousIndexKey is not null && CompareExtentKeys(previousIndexKey, indexKey) >= 0)
                    {
                        throw new InvalidDataException("The HFS Plus extents-overflow index keys are not strictly ordered.");
                    }
                }
                else if (name == "attributes")
                {
                    ValidateAttributeKey(indexKey);
                    if (previousIndexKey is not null && CompareAttributeKeys(previousIndexKey, indexKey) >= 0)
                    {
                        throw new InvalidDataException("The HFS Plus attributes index keys are not strictly ordered.");
                    }
                }
                firstIndexKey ??= indexKey;
                previousIndexKey = indexKey;
                int storedKeyLength = (name is "catalog" or "attributes") ? keyLength : maxKeyLength;
                int childOffset = begin + 2 + storedKeyLength;
                if ((childOffset & 1) != 0)
                {
                    childOffset++;
                }

                if (childOffset + 4 != end)
                {
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record has an invalid length.");
                }

                var childRange = Visit(U32(treeReader, start + childOffset), checked((ushort)(expectedHeight - 1)));
                childRanges.Add((indexKey, childRange));
            }
            indexKeyRanges.Add(nodeNumber, (firstIndexKey!, previousIndexKey!));
            if (validateChildKeyRanges)
            {
                for (int index = 0; index < childRanges.Count; index++)
                {
                    var (separator, childRange) = childRanges[index];
                    if (childRange is not { } child)
                    {
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree index points to an empty child subtree.");
                    }

                    if (CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog, separator, child.First) != 0)
                    {
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree index key does not match the first key in its child subtree.");
                    }

                    if (index > 0 && childRanges[index - 1].Range is { } previousChild &&
                        CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog,
                            previousChild.Last, separator) >= 0)
                    {
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree child contains a key beyond its index range.");
                    }
                }
            }
            (byte[] First, byte[] Last)? subtreeRange = null;
            foreach (var child in childRanges)
            {
                if (child.Range is not { } range)
                {
                    continue;
                }

                subtreeRange = subtreeRange is { } existing
                    ? (existing.First, range.Last)
                    : range;
            }
            subtreeKeyRanges.Add(nodeNumber, subtreeRange);
            return subtreeRange;
        }

        _ = Visit(root, depth);
        foreach (var (height, nodes) in nodesByHeight)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                int start = checked((int)nodes[index] * nodeSize);
                uint expectedForward = index + 1 < nodes.Count ? nodes[index + 1] : 0;
                uint expectedBackward = index > 0 ? nodes[index - 1] : 0;
                if (U32(treeReader, start) != expectedForward || U32(treeReader, start + 4) != expectedBackward)
                {
                    throw new InvalidDataException(
                        $"The HFS Plus {name} B-tree height-{height} sibling links are invalid.");
                }

                if (index > 0 && height > 1 && indexKeyRanges.TryGetValue(nodes[index - 1], out var previousRange))
                {
                    var currentRange = indexKeyRanges[nodes[index]];
                    int comparison = name switch
                    {
                        "catalog" when caseSensitiveCatalog || caseFoldingCatalog =>
                            CompareCatalogKeys(previousRange.Last, currentRange.First, caseFoldingCatalog),
                        "extents-overflow" => CompareExtentKeys(previousRange.Last, currentRange.First),
                        "attributes" => CompareAttributeKeys(previousRange.Last, currentRange.First),
                        _ => -1
                    };
                    if (comparison >= 0)
                    {
                        throw new InvalidDataException(
                            $"The HFS Plus {name} B-tree index keys are not strictly ordered across sibling nodes.");
                    }
                }
                if (index > 0 && height > 1 && validateChildKeyRanges &&
                    subtreeKeyRanges[nodes[index - 1]] is { } previousSubtree &&
                    subtreeKeyRanges[nodes[index]] is { } currentSubtree &&
                    CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog,
                        previousSubtree.Last, currentSubtree.First) >= 0)
                {
                    throw new InvalidDataException(
                        $"The HFS Plus {name} B-tree child key ranges overlap across sibling nodes.");
                }
            }
        }
        return (leafNodes, visitedNodes);
    }

    internal static (byte[] First, byte[] Last)? LeafKeyRange(BigEndianReader tree, int nodeStart, int nodeSize,
        int maxKeyLength, string name)
    {
        int count = U16(tree, nodeStart + 10);
        if (count == 0)
        {
            return null;
        }

        int offsetTableStart = nodeSize - 2 * (count + 1);

        byte[] ReadKey(int index)
        {
            int begin = U16(tree, nodeStart + nodeSize - 2 * (index + 1));
            int end = U16(tree, nodeStart + nodeSize - 2 * (index + 2));
            if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin || end > offsetTableStart)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf record offset is invalid.");
            }

            int keyLength = U16(tree, nodeStart + begin);
            if (keyLength < 6 || keyLength > maxKeyLength || begin + 2 + keyLength > end)
            {
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf key length is invalid.");
            }

            byte[] key = tree.Source.Slice(nodeStart + begin, 2 + keyLength).ToArray();
            if (name == "attributes")
            {
                ValidateAttributeKey(key);
            }

            return key;
        }

        return (ReadKey(0), ReadKey(count - 1));
    }

    internal static int CompareTreeKeys(string name, bool caseSensitiveCatalog, bool caseFoldingCatalog,
        byte[] left, byte[] right) => name switch
        {
            "catalog" when caseSensitiveCatalog => CompareHfsXCatalogKeys(left, right),
            "catalog" when caseFoldingCatalog => CompareCatalogKeys(left, right, caseFolding: true),
            "extents-overflow" => CompareExtentKeys(left, right),
            "attributes" => CompareAttributeKeys(left, right),
            _ => throw new InvalidOperationException($"No key comparator is defined for the {name} B-tree.")
        };

    internal static void ValidateNodeMap(byte[] tree, int nodeSize, uint totalNodes,
        HashSet<uint> referencedNodes, string name)
    {
        const int MapOffset = 14 + 106 + 128;
        int mapLength = nodeSize - 256;
        uint headerMapCapacity = checked((uint)(mapLength * 8));
        int mapNodeLength = nodeSize - 20;
        uint mapNodeCapacity = checked((uint)(mapNodeLength * 8));
        var mapNodes = new List<uint>();
        var seenMapNodes = new HashSet<uint>();
        var treeReader = new BigEndianReader(tree);
        uint nextMapNode = U32(treeReader, 0);
        ulong capacity = headerMapCapacity;
        while (nextMapNode != 0)
        {
            if (nextMapNode >= totalNodes || !seenMapNodes.Add(nextMapNode) ||
                referencedNodes.Contains(nextMapNode))
            {
                throw new InvalidDataException("The HFS Plus B-tree map-node chain is cyclic or invalid.");
            }

            int offset = checked((int)nextMapNode * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(treeReader, offset, nodeSize, "node map");
            if (tree[offset + 8] != 2 || tree[offset + 9] != 0 || U16(treeReader, offset + 10) != 1 ||
                U32(treeReader, offset + 4) != 0 || U16(treeReader, offset + nodeSize - 4) != nodeSize - 6)
            {
                throw new InvalidDataException("An HFS Plus B-tree map node has an invalid descriptor or record layout.");
            }

            mapNodes.Add(nextMapNode);
            capacity += mapNodeCapacity;
            nextMapNode = U32(treeReader, offset);
        }
        uint requiredMapNodes = totalNodes <= headerMapCapacity
            ? 0
            : checked((totalNodes - headerMapCapacity + mapNodeCapacity - 1) / mapNodeCapacity);
        if (mapNodes.Count != requiredMapNodes)
        {
            throw new InvalidDataException("The HFS Plus B-tree map-node chain has an invalid length.");
        }

        if (capacity < totalNodes)
        {
            throw new InvalidDataException("The HFS Plus B-tree map nodes do not cover every node.");
        }

        // [Code] Apple's HFS verifier's CmpBTM checks that unused bytes at the end of the final map record are zero.
        if (totalNodes <= headerMapCapacity)
        {
            int usedMapBytes = checked((int)((totalNodes + 7UL) / 8));
            RequireZeroMapPadding(tree.AsSpan(MapOffset, mapLength), usedMapBytes);
        }
        else
        {
            ulong precedingMapBits = (ulong)headerMapCapacity + (ulong)mapNodeCapacity * (uint)(mapNodes.Count - 1);
            int usedMapBytes = checked((int)((totalNodes - precedingMapBits + 7) / 8));
            int lastMapOffset = checked((int)mapNodes[^1] * nodeSize + 14);
            RequireZeroMapPadding(tree.AsSpan(lastMapOffset, mapNodeLength), usedMapBytes);
        }

        void RequireAllocated(uint nodeNumber)
        {
            if (!IsAllocated(nodeNumber))
            {
                throw new InvalidDataException($"HFS Plus B-tree node {nodeNumber} is referenced but marked free.");
            }
        }

        bool IsAllocated(uint nodeNumber)
        {
            int byteOffset;
            uint bitInByte;
            if (nodeNumber < headerMapCapacity)
            {
                byteOffset = MapOffset + checked((int)(nodeNumber / 8));
                bitInByte = nodeNumber % 8;
            }
            else
            {
                uint continuationBit = nodeNumber - headerMapCapacity;
                uint mapIndex = continuationBit / mapNodeCapacity;
                if (mapIndex >= mapNodes.Count)
                {
                    throw new InvalidDataException("The HFS Plus B-tree node map is truncated.");
                }

                byteOffset = checked((int)mapNodes[(int)mapIndex] * nodeSize + 14 +
                    (int)((continuationBit % mapNodeCapacity) / 8));
                bitInByte = continuationBit % 8;
            }
            byte mask = (byte)(0x80 >> (int)bitInByte);
            return (tree[byteOffset] & mask) != 0;
        }

        RequireAllocated(0);
        foreach (uint nodeNumber in referencedNodes)
        {
            RequireAllocated(nodeNumber);
        }

        foreach (uint nodeNumber in mapNodes)
        {
            RequireAllocated(nodeNumber);
        }

        var knownNodes = new HashSet<uint>(referencedNodes) { 0 };
        knownNodes.UnionWith(mapNodes);
        uint freeNodes = 0;
        for (uint nodeNumber = 0; nodeNumber < totalNodes; nodeNumber++)
        {
            if (IsAllocated(nodeNumber))
            {
                if (!knownNodes.Contains(nodeNumber))
                {
                    throw new InvalidDataException(
                        $"HFS Plus B-tree node {nodeNumber} is allocated but not referenced by the tree.");
                }

                continue;
            }
            freeNodes++;

            // [Code] Apple's verifier calls BTCheckUnusedNodes for the catalog tree; it does not impose this
            // check on the extents-overflow or attributes trees.
            if (name == "catalog")
            {
                int offset = checked((int)nodeNumber * nodeSize);
                if (tree.AsSpan(offset, nodeSize).IndexOfAnyExcept((byte)0) >= 0)
                {
                    throw new InvalidDataException($"HFS Plus catalog B-tree free node {nodeNumber} is not zero-filled.");
                }
            }
        }
        if (freeNodes != U32(treeReader, 14 + 26))
        {
            throw new InvalidDataException("The HFS Plus B-tree free-node count differs from its node map.");
        }
    }

    internal static void RequireZeroMapPadding(ReadOnlySpan<byte> mapRecord, int usedBytes)
    {
        for (int index = usedBytes; index < mapRecord.Length; index++)
        {
            if (mapRecord[index] != 0)
            {
                throw new InvalidDataException("The HFS Plus B-tree node map has nonzero unused bytes.");
            }
        }
    }

    internal static int CompareExtentKeys(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int comparison = U32(leftReader, 4).CompareTo(U32(rightReader, 4));
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Span[2].CompareTo(right.Span[2]);
        return comparison != 0 ? comparison : U32(leftReader, 8).CompareTo(U32(rightReader, 8));
    }

    internal static int CompareHfsXCatalogKeys(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int comparison = U32(leftReader, 2).CompareTo(U32(rightReader, 2));
        if (comparison != 0)
        {
            return comparison;
        }

        int leftLength = U16(leftReader, 6);
        int rightLength = U16(rightReader, 6);
        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            comparison = U16(leftReader, 8 + index * 2).CompareTo(U16(rightReader, 8 + index * 2));
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return leftLength.CompareTo(rightLength);
    }

    internal static int CompareCatalogKeys(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right, bool caseFolding)
    {
        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int comparison = U32(leftReader, 2).CompareTo(U32(rightReader, 2));
        if (comparison != 0)
        {
            return comparison;
        }

        int leftLength = U16(leftReader, 6);
        int rightLength = U16(rightReader, 6);
        if (!caseFolding)
        {
            for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
            {
                comparison = U16(leftReader, 8 + index * 2).CompareTo(U16(rightReader, 8 + index * 2));
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return leftLength.CompareTo(rightLength);
        }

        return HfsPlusUnicodeComparison.CompareBigEndian(left.Slice(8, leftLength * 2),
            right.Slice(8, rightLength * 2));
    }

    internal sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayEqualityComparer Instance { get; } = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] key)
        {
            var hash = new HashCode();
            foreach (byte value in key)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
