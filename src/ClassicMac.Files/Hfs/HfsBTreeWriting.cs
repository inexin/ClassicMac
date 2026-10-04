using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsCatalogKeys;

namespace ClassicMac.Files.Hfs;

// HfsWriter's B-tree work: rebuilding a tree from its records, inserting and updating extents records, growing the
// node map, and the writer's checks of a tree (hfs.md §1.6–§1.8).
internal static class HfsBTreeWriting
{
    internal sealed record TreeRecord(byte[] Key, byte[] Data, int NodeOffset, int RecordStart, int RecordEnd);

    internal sealed class BTreeNeedsNodesException : Exception
    {
        public BTreeNeedsNodesException() : base("The extents-overflow B-tree needs more nodes.") { }
    }

    internal static IEnumerable<TreeRecord> LeafRecords(byte[] tree)
    {
        if (tree.Length < NodeSize)
        {
            throw new InvalidDataException("An HFS B-tree is shorter than one node.");
        }

        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        var seen = new HashSet<uint>();
        for (uint node = file.FirstLeaf; node != 0; node = file.Node(node).FLink)
        {
            if (node >= file.NodeCount || !seen.Add(node))
            {
                throw new InvalidDataException("An HFS B-tree leaf link is invalid or loops.");
            }

            var descriptor = file.Node(node);
            if (descriptor.Kind != BTreeNode.LeafKind)
            {
                throw new InvalidDataException("An HFS B-tree leaf chain links to a non-leaf node.");
            }

            if (descriptor.RecordCount > (NodeSize - 14) / 2 - 1)
            {
                throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
            }

            for (int i = 0; i < descriptor.RecordCount; i++)
            {
                if (!file.TryRecord(node, i, out var key, out var data) || !file.TryRecordBounds(node, i, out int start, out int end))
                {
                    throw new InvalidDataException("An HFS B-tree record has invalid offsets.");
                }

                yield return new TreeRecord(key.ToArray(), data.ToArray(), file.Offset(node), start, end);
            }
        }
    }

    internal static void UpdateCatalogRecord(byte[] tree, TreeRecord target)
    {
        // Locate the same leaf record from its key; its data starts at the even boundary after the key.
        int keyLength = target.Key.Length;
        int dataStart = (target.RecordStart + keyLength + 1) & ~1;
        target.Data.CopyTo(tree, target.NodeOffset + dataStart);
    }

    internal static void UpdateTreeRecord(byte[] tree, byte[] key, byte[] data)
    {
        var target = LeafRecords(tree).FirstOrDefault(record => CompareExtentsKeys(record.Key, key) == 0);
        if (target is null || target.Data.Length != data.Length)
        {
            throw new InvalidDataException("The extents-overflow record to update was not found at its current location.");
        }

        int dataStart = (target.RecordStart + target.Key.Length + 1) & ~1;
        data.CopyTo(tree, target.NodeOffset + dataStart);
    }

    internal static void DeleteExtentsRecord(byte[] tree, byte[] key)
    {
        var records = LeafRecords(tree).ToList();
        int recordIndex = records.FindIndex(record => CompareExtentsKeys(record.Key, key) == 0);
        if (recordIndex < 0)
        {
            throw new InvalidDataException("The extents-overflow record to remove was not found.");
        }

        if (TryEditExtentsTree(tree, edit => edit.Delete(key)))
        {
            return;
        }

        records.RemoveAt(recordIndex);
        RebuildBTree(tree, records.Select(record => (record.Key, record.Data)).ToList(), validateExtents: true);
    }

    /// <summary>
    /// Edits the extents tree in place by the BTree manager's rules, as the catalog is edited (hfs.md §1.8); false, with
    /// the tree unchanged, when the edit needs a rebuild instead (an empty tree, too few free nodes, a node that would not
    /// fit). The result is validated before it is kept.
    /// </summary>
    internal static bool TryEditExtentsTree(byte[] tree, Action<BTreeEdit> change)
    {
        try
        {
            var edit = new BTreeEdit(tree.ToArray(), CompareExtentsKeys);
            change(edit);
            ValidateExtentsTree(edit.Bytes);
            edit.Bytes.CopyTo(tree, 0);
            return true;
        }
        catch (Exception e) when (e is BTreeEdit.NeedsNodesException or BTreeEdit.RebuildException)
        {
            return false;
        }
    }

    internal static void RebuildBTree(byte[] tree, List<(byte[] Key, byte[] Data)> records, bool validateExtents)
    {
        var treeReader = new BigEndianReader(tree);
        uint nodeCount = U32(treeReader, 14 + 22);
        // Index keys are written at the tree's maximum key length, zero-padded, as Mac OS writes them: HFS B-trees do
        // not set kBTVariableIndexKeysMask [Code: Mac OS 9.0 ROM; Verified: Mac OS 9's catalog] (hfs.md §1.8).
        int maxKeyLength = U16(treeReader, 14 + 20);
        byte[] IndexKey(byte[] key)
        {
            if (key.Length == 0 || key[0] >= maxKeyLength)
            {
                return key;
            }

            var padded = new byte[maxKeyLength + 1];
            key.AsSpan(0, Math.Min(key.Length, key[0] + 1)).CopyTo(padded);
            padded[0] = (byte)maxKeyLength;
            return padded;
        }

        if (!new BTreeFile(tree, NodeSize, wordKeyLength: false).TryReadMap(out var map, out var mapProblem))
        {
            throw new InvalidDataException($"The B-tree's node map is invalid: {mapProblem}");
        }

        var reserved = new HashSet<uint>(map.MapNodes) { 0 };
        var available = Enumerable.Range(1, checked((int)nodeCount - 1))
            .Select(number => (uint)number).Where(number => !reserved.Contains(number)).ToArray();
        var built = new List<(uint Number, byte[] FirstKey, byte[] Bytes)>();
        int nextNode = 0;
        uint AllocateNode()
        {
            if (nextNode == available.Length)
            {
                throw new BTreeNeedsNodesException();
            }

            return available[nextNode++];
        }

        // The records in node-sized groups, each as full as it goes: a record joins the group while its key (padded to
        // even) and data end before the offset table, one entry longer (TryBuildLeafNode's layout).
        List<List<(byte[] Key, byte[] Data)>> Partition(List<(byte[] Key, byte[] Data)> entries)
        {
            static int After(int end, (byte[] Key, byte[] Data) entry) => checked(((end + entry.Key.Length + 1) & ~1) + entry.Data.Length);

            var groups = new List<List<(byte[] Key, byte[] Data)>>();
            var group = new List<(byte[] Key, byte[] Data)>();
            int end = 14;
            foreach (var entry in entries)
            {
                int next = After(end, entry);
                if (next <= NodeSize - 2 * (group.Count + 2))
                {
                    group.Add(entry);
                    end = next;
                    continue;
                }

                if (group.Count == 0)
                {
                    throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                }

                groups.Add(group);
                group = [entry];
                end = After(14, entry);
                if (end > NodeSize - 4)
                {
                    throw new InvalidDataException("An extents-overflow record cannot fit in a B-tree node.");
                }
            }
            if (group.Count > 0)
            {
                groups.Add(group);
            }

            return groups;
        }

        List<(uint Number, byte[] FirstKey, byte[] Bytes)> BuildLevel(
            List<(byte[] Key, byte[] Data)> entries, byte height)
        {
            var level = new List<(uint Number, byte[] FirstKey, byte[] Bytes)>();
            foreach (var group in Partition(entries))
            {
                var template = new byte[NodeSize];
                template[8] = height == 1 ? (byte)0xFF : (byte)0;
                template[9] = height;
                if (!TryBuildLeafNode(template, group, out var node))
                {
                    throw new InvalidDataException("An extents-overflow node could not be rebuilt.");
                }

                var item = (AllocateNode(), group[0].Key, node);
                level.Add(item);
                built.Add(item);
            }
            for (int index = 0; index < level.Count; index++)
            {
                var links = new BigEndianWriter(level[index].Bytes);
                links.WriteUInt32At(0, index + 1 < level.Count ? level[index + 1].Number : 0);
                links.WriteUInt32At(4, index > 0 ? level[index - 1].Number : 0);
            }
            return level;
        }

        byte[] rebuilt = tree.ToArray();
        var leaves = BuildLevel(records, 1);
        var levelNodes = leaves;
        byte depth = records.Count == 0 ? (byte)0 : (byte)1;
        while (levelNodes.Count > 1)
        {
            if (depth == 127)
            {
                throw new InvalidDataException("The extents-overflow B-tree is too deep.");
            }

            depth++;
            var entries = levelNodes.Select(node => (IndexKey(node.FirstKey), ChildNode(node.Number))).ToList();
            levelNodes = BuildLevel(entries, depth);
        }

        foreach (uint number in available)
        {
            Array.Clear(rebuilt, checked((int)number * NodeSize), NodeSize);
        }

        foreach (var node in built)
        {
            node.Bytes.CopyTo(rebuilt, checked((int)node.Number * NodeSize));
        }

        var allocated = new HashSet<uint>(reserved);
        allocated.UnionWith(built.Select(node => node.Number));
        if (!new BTreeFile(rebuilt, NodeSize, wordKeyLength: false).TryReadMap(out var rebuiltMap, out mapProblem))
        {
            throw new InvalidDataException($"The B-tree's node map is invalid: {mapProblem}");
        }

        for (uint number = 0; number < nodeCount; number++)
        {
            if (number < rebuiltMap.Capacity)
            {
                rebuiltMap.SetAllocated(number, allocated.Contains(number));
            }
        }

        var header = new BigEndianWriter(rebuilt);
        header.WriteUInt16At(14, depth);
        header.WriteUInt32At(14 + 2, levelNodes.Count == 0 ? 0 : levelNodes[0].Number);
        header.WriteUInt32At(14 + 6, records.Count);
        header.WriteUInt32At(14 + 10, leaves.Count == 0 ? 0 : leaves[0].Number);
        header.WriteUInt32At(14 + 14, leaves.Count == 0 ? 0 : leaves[^1].Number);
        header.WriteUInt32At(14 + 26, nodeCount - checked((uint)allocated.Count));
        if (validateExtents)
        {
            ValidateExtentsTree(rebuilt);
        }

        rebuilt.CopyTo(tree, 0);
    }

    // The node map of a tree file grown from oldNodeCount to newNodeCount nodes: map nodes added where the map cannot
    // cover them, the new nodes marked free and the new map nodes used; returns how many map nodes it added.
    internal static uint ExtendBTreeNodeMap(byte[] tree, uint oldNodeCount, uint newNodeCount)
    {
        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        if (!file.TryReadMap(out var map, out var problem) || map.MapNodes.Any(node => node >= oldNodeCount))
        {
            throw new InvalidDataException($"The HFS B-tree node map is invalid: {problem ?? "a map node lies past the tree"}");
        }

        if (map.Capacity < oldNodeCount)
        {
            throw new InvalidDataException("The HFS B-tree node map does not cover its existing nodes.");
        }

        var newMapNodes = new List<uint>();
        var writer = new BigEndianWriter(tree);
        uint lastMapNode = map.MapNodes.Count == 0 ? 0 : map.MapNodes[^1];
        for (long capacity = map.Capacity; capacity < newNodeCount; capacity += (NodeSize - 20) * 8)
        {
            // At the old end of the tree, one after another, as Mac OS places them [Verified: Mac OS 9.0]; a map node of one
            // record (offsets 14 and nodeSize - 6) whose backward link stays 0, as Apple's ExtendBTree leaves it [Doc:
            // Apple's hfs sources, BTreeAllocate.c].
            uint number = oldNodeCount + (uint)newMapNodes.Count;
            if (number >= newNodeCount)
            {
                throw new InvalidDataException("The HFS B-tree has no node available for another map record.");
            }

            int at = file.Offset(number);
            writer.WriteUInt32At(file.Offset(lastMapNode), number);
            tree[at + 8] = 2;
            writer.WriteUInt16At(at + 10, 1);
            writer.WriteUInt16At(at + NodeSize - 2, 14);
            writer.WriteUInt16At(at + NodeSize - 4, NodeSize - 6);
            newMapNodes.Add(number);
            lastMapNode = number;
        }

        if (!file.TryReadMap(out map, out problem))
        {
            throw new InvalidDataException($"The HFS B-tree node map is invalid: {problem}");
        }

        for (uint number = oldNodeCount; number < newNodeCount; number++)
        {
            map.SetAllocated(number, newMapNodes.Contains(number));
        }

        return checked((uint)newMapNodes.Count);
    }

    internal static void InsertExtentsRecord(byte[] tree, byte[] key, byte[] data)
    {
        if (key.Length != 8 || key[0] != 7 || data.Length != 12)
        {
            throw new ArgumentException("An HFS extents-overflow record must have an 8-byte key and a 12-byte extent record.");
        }

        var records = LeafRecords(tree).Select(record => (record.Key, record.Data)).ToList();
        int insertion = records.FindIndex(record => CompareExtentsKeys(record.Key, key) >= 0);
        if (insertion >= 0 && CompareExtentsKeys(records[insertion].Key, key) == 0)
        {
            throw new InvalidDataException("The extents-overflow B-tree already contains this file extent key.");
        }

        if (insertion < 0)
        {
            insertion = records.Count;
        }

        if (TryEditExtentsTree(tree, edit => edit.Insert((key, data))))
        {
            return;
        }

        records.Insert(insertion, (key, data));
        RebuildBTree(tree, records, validateExtents: true);
    }

    internal static List<(byte[] Key, byte[] Data)> ReadNodeRecords(byte[] tree, uint nodeNumber)
    {
        if (nodeNumber >= tree.Length / NodeSize)
        {
            throw new InvalidDataException("An HFS B-tree node lies outside the tree file.");
        }

        var file = new BTreeFile(tree, NodeSize, wordKeyLength: false);
        int count = file.Node(nodeNumber).RecordCount;
        if (count > (NodeSize - 14) / 2 - 1)
        {
            throw new InvalidDataException("An HFS B-tree node has too many records for its offset table.");
        }

        var records = new List<(byte[] Key, byte[] Data)>(count);
        for (int i = 0; i < count; i++)
        {
            if (!file.TryRecord(nodeNumber, i, out var key, out var data))
            {
                throw new InvalidDataException("An HFS B-tree node has invalid record offsets.");
            }

            records.Add((key.ToArray(), data.ToArray()));
        }
        return records;
    }

    internal static bool TryBuildLeafNode(byte[] original, List<(byte[] Key, byte[] Data)> records, out byte[] rebuilt)
    {
        int requiredEnd = 14;
        foreach (var record in records)
        {
            requiredEnd = checked((requiredEnd + record.Key.Length + 1) & ~1);
            requiredEnd = checked(requiredEnd + record.Data.Length);
        }
        int tableStart = NodeSize - 2 * (records.Count + 1);
        if (requiredEnd > tableStart)
        {
            rebuilt = [];
            return false;
        }

        rebuilt = original.ToArray();
        Array.Clear(rebuilt, 14, NodeSize - 14);
        int at = 14;
        var offsets = new List<ushort>(records.Count + 1);
        foreach (var record in records)
        {
            offsets.Add(checked((ushort)at));
            record.Key.CopyTo(rebuilt, at);
            at += record.Key.Length;
            at = (at + 1) & ~1;
            record.Data.CopyTo(rebuilt, at);
            at += record.Data.Length;
        }
        var writer = new BigEndianWriter(rebuilt);
        for (int i = 0; i < offsets.Count; i++)
        {
            writer.WriteUInt16At(NodeSize - 2 * (i + 1), offsets[i]);
        }

        writer.WriteUInt16At(NodeSize - 2 * (offsets.Count + 1), at);
        writer.WriteUInt16At(10, offsets.Count);
        return true;
    }

    internal static byte[] ChildNode(uint node)
    {
        var child = new BigEndianWriter(4);
        child.WriteUInt32(node);
        return child.ToArray();
    }

    internal static void ValidateExtentsTree(byte[] tree) => ValidateBTree(tree, catalog: false);

    internal static void ValidateCatalogTree(byte[] tree) => ValidateBTree(tree, catalog: true);

    internal static void ValidateBTree(byte[] tree, bool catalog)
    {
        string treeKind = catalog ? "catalog" : "extents-overflow";
        int CompareKeys(byte[] left, byte[] right) => catalog
            ? CompareCatalogKeys(left, right) : CompareExtentsKeys(left, right);
        var treeReader = new BigEndianReader(tree);
        if (tree.Length < NodeSize || tree.Length % NodeSize != 0 || tree[8] != 1 || U16(treeReader, 10) != 3)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid header node or length.");
        }

        uint nodeCount = U32(treeReader, 14 + 22);
        if (nodeCount != (uint)(tree.Length / NodeSize) || U16(treeReader, 14 + 18) != NodeSize)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree header disagrees with its node file.");
        }

        int mapStart = U16(treeReader, NodeSize - 6);
        int mapEnd = U16(treeReader, NodeSize - 8);
        if (U16(treeReader, NodeSize - 2) != 14 || U16(treeReader, NodeSize - 4) != 14 + 106 ||
            mapStart != 14 + 106 + 128 || mapEnd < mapStart || mapEnd > NodeSize - 8)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree header records have invalid offsets.");
        }

        // The node map; a map node's backward link is not read (Apple's ExtendBTree leaves it 0; DFA's BTMapChk ignores
        // it) (hfs.md §1.8).
        if (!new BTreeFile(tree, NodeSize, wordKeyLength: false).TryReadMap(out var map, out var mapProblem))
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree node map is invalid: {mapProblem}");
        }

        var usedNodes = new HashSet<uint>(map.MapNodes) { 0 };
        if (map.Capacity < nodeCount)
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree node map is truncated.");
        }

        var nodesByHeight = new Dictionary<int, List<uint>>();
        uint leafRecordCount = 0;
        ushort depth = U16(treeReader, 14);
        uint root = U32(treeReader, 14 + 2);
        if (depth == 0)
        {
            if (root != 0 || U32(treeReader, 14 + 6) != 0 || U32(treeReader, 14 + 10) != 0 ||
                U32(treeReader, 14 + 14) != 0)
            {
                throw new InvalidDataException($"The empty HFS {treeKind} B-tree has inconsistent header fields.");
            }
        }
        else
        {
            if (depth > 127 || root == 0)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree has an invalid root or depth.");
            }

            Visit(root, depth);
            var leaves = nodesByHeight[1];
            if (U32(treeReader, 14 + 10) != leaves[0] || U32(treeReader, 14 + 14) != leaves[^1] ||
                U32(treeReader, 14 + 6) != leafRecordCount)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree leaf header disagrees with its nodes.");
            }
        }

        foreach (var (height, nodes) in nodesByHeight)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                int offset = checked((int)nodes[i] * NodeSize);
                uint previous = i == 0 ? 0 : nodes[i - 1];
                uint next = i + 1 == nodes.Count ? 0 : nodes[i + 1];
                if (U32(treeReader, offset) != next || U32(treeReader, offset + 4) != previous)
                {
                    throw new InvalidDataException($"The HFS {treeKind} B-tree level {height} has inconsistent sibling links.");
                }
            }
        }

        uint actualFreeNodes = 0;
        for (uint node = 0; node < nodeCount; node++)
        {
            bool allocated = map.IsAllocated(node);
            if (allocated != usedNodes.Contains(node))
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree node map disagrees with its node graph.");
            }

            if (!allocated)
            {
                actualFreeNodes++;
            }
        }
        if (actualFreeNodes != U32(treeReader, 14 + 26))
        {
            throw new InvalidDataException($"The HFS {treeKind} B-tree free-node count disagrees with its map.");
        }

        (byte[] First, byte[] Last) Visit(uint number, int height)
        {
            if (number == 0 || number >= nodeCount || !usedNodes.Add(number))
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree index graph is invalid or cyclic.");
            }

            int offset = checked((int)number * NodeSize);
            if (tree[offset + 8] != (height == 1 ? (byte)0xFF : (byte)0) || tree[offset + 9] != height)
            {
                throw new InvalidDataException($"The HFS {treeKind} B-tree index has an invalid child type or height.");
            }

            var records = ReadNodeRecords(tree, number);
            if (records.Count == 0)
            {
                throw new InvalidDataException($"An active HFS {treeKind} B-tree node is empty.");
            }

            if (!nodesByHeight.TryGetValue(height, out var peers))
            {
                nodesByHeight[height] = peers = [];
            }

            peers.Add(number);
            byte[]? first = null, last = null;
            foreach (var (key, data) in records)
            {
                // A catalog key is as long as its name (padded to even), or in an index node the tree's maximum key
                // length, zero-padded after the name, as Mac OS writes index keys (hfs.md §1.8).
                bool validKey = catalog
                    ? key.Length >= 7 && key[0] == key.Length - 1 &&
                      (key.Length == 7 + key[6] ||
                       (key.Length == ((8 + key[6]) & ~1) && key[^1] == 0) ||
                       (height > 1 && key.Length == 38 && 7 + key[6] <= 38 && key.AsSpan(7 + key[6]).IndexOfAnyExcept((byte)0) < 0))
                    : key.Length == 8 && key[0] == 7 && key[1] is 0 or 0xFF;
                if (!validKey)
                {
                    throw new InvalidDataException($"An HFS {treeKind} B-tree record has an invalid key.");
                }

                if (height == 1)
                {
                    bool validData = catalog
                        ? data.Length >= 2 && data[0] switch
                        {
                            1 => data.Length >= 70,
                            2 => data.Length >= 102,
                            // A thread as long as its name, as hfsutils writes it and Mac OS keeps it, or Mac OS's 46
                            // bytes (hfs.md §1.9) [Verified: Mac OS 9 used such a volume].
                            3 or 4 => data.Length >= 15 && data[14] <= 31 && data.Length >= 15 + data[14],
                            _ => false,
                        }
                        : data.Length == 12;
                    if (!validData || (last is not null && CompareKeys(last, key) >= 0))
                    {
                        throw new InvalidDataException($"The HFS {treeKind} leaf records are invalid or out of order.");
                    }

                    first ??= key;
                    last = key;
                    leafRecordCount = checked(leafRecordCount + 1);
                }
                else
                {
                    if (data.Length != 4)
                    {
                        throw new InvalidDataException($"An HFS {treeKind} index record has an invalid child pointer.");
                    }

                    var child = Visit(U32(new BigEndianReader(data), 0), height - 1);
                    if (CompareKeys(key, child.First) != 0 ||
                        (last is not null && CompareKeys(last, child.First) >= 0))
                    {
                        throw new InvalidDataException($"An HFS {treeKind} index key disagrees with its child.");
                    }

                    first ??= child.First;
                    last = child.Last;
                }
            }
            return (first!, last!);
        }
    }

    internal static int CompareExtentsKeys(byte[] left, byte[] right)
    {
        if (left.Length < 8 || right.Length < 8)
        {
            throw new InvalidDataException("An extents-overflow key is shorter than eight bytes.");
        }

        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int order = U32(leftReader, 2).CompareTo(U32(rightReader, 2));
        if (order != 0)
        {
            return order;
        }

        order = left[1].CompareTo(right[1]);
        return order != 0 ? order : U16(leftReader, 6).CompareTo(U16(rightReader, 6));
    }
}
