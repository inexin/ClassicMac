using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// An HFS Plus B-tree file written whole from its leaf records in key order (hfs-plus.md §1.4, §3): the header node,
// map nodes after it when the header's map record cannot cover every node, then the leaves packed full and the index
// levels above them, each level linked both ways. Index keys are the child's first key, at their own length in the
// catalog and attributes trees and padded to the maximum in the extents tree [Doc: TN1150].
internal static class HfsPlusBTreeWriter
{
    private const int DescriptorLength = 14, HeaderRecordLength = 106, UserRecordLength = 128;
    private const uint BigKeys = 0x2, VariableIndexKeys = 0x4;

    /// <summary>A tree's key layout: its maximum key length and whether its index keys are variable-length.</summary>
    internal sealed record TreeKind(ushort MaxKeyLength, bool VariableIndexKeys);

    public static readonly TreeKind Extents = new(10, false);
    public static readonly TreeKind Catalog = new(516, true);
    public static readonly TreeKind Attributes = new(266, true);

    /// <summary>
    /// The tree file of <paramref name="totalNodes"/> nodes holding <paramref name="records"/> (keys with their 16-bit
    /// length, in key order); <see cref="InvalidDataException"/> when they do not fit.
    /// </summary>
    public static byte[] Build(TreeKind kind, IReadOnlyList<(byte[] Key, byte[] Data)> records, int nodeSize, uint totalNodes, uint clumpSize,
        byte keyCompareType = 0, byte btreeType = 0)
    {
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0 || totalNodes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeSize), "An HFS Plus B-tree needs a power-of-two node size from 512 to 32,768 and nodes.");
        }

        var tree = new byte[checked(nodeSize * (long)totalNodes)];
        long headerCapacity = (nodeSize - 256) * 8L, mapNodeCapacity = (nodeSize - 20) * 8L;
        int mapNodes = totalNodes <= headerCapacity ? 0 : (int)((totalNodes - headerCapacity + mapNodeCapacity - 1) / mapNodeCapacity);
        uint next = 1 + (uint)mapNodes;
        var used = new List<uint> { 0 };
        for (uint map = 1; map <= mapNodes; map++)
        {
            used.Add(map);
        }

        uint Allocate()
        {
            if (next >= totalNodes)
            {
                throw new InvalidDataException("The HFS Plus B-tree records need more nodes than the tree has.");
            }

            used.Add(next);
            return next++;
        }

        // The leaves, then each index level above them.
        var level = Level(tree, nodeSize, records, height: 1, Allocate);
        var leaves = level;
        ushort depth = (ushort)(records.Count == 0 ? 0 : 1);
        while (level.Count > 1)
        {
            depth++;
            var entries = new List<(byte[] Key, byte[] Data)>();
            foreach (var (number, firstKey) in level)
            {
                var child = new byte[4];
                new BigEndianWriter(child).WriteUInt32At(0, number);
                entries.Add((IndexKey(kind, firstKey), child));
            }

            level = Level(tree, nodeSize, entries, (byte)depth, Allocate);
        }

        WriteHeaderNode(tree, nodeSize, mapNodes);
        WriteMapNodes(tree, nodeSize, mapNodes);
        foreach (uint node in used)
        {
            SetMapBit(tree, nodeSize, node);
        }

        var header = new BigEndianWriter(tree);
        header.WriteUInt16At(DescriptorLength, depth);
        header.WriteUInt32At(DescriptorLength + 2, level.Count == 0 ? 0u : level[0].Number);
        header.WriteUInt32At(DescriptorLength + 6, records.Count);
        header.WriteUInt32At(DescriptorLength + 10, leaves.Count == 0 ? 0u : leaves[0].Number);
        header.WriteUInt32At(DescriptorLength + 14, leaves.Count == 0 ? 0u : leaves[^1].Number);
        header.WriteUInt16At(DescriptorLength + 18, nodeSize);
        header.WriteUInt16At(DescriptorLength + 20, kind.MaxKeyLength);
        header.WriteUInt32At(DescriptorLength + 22, totalNodes);
        header.WriteUInt32At(DescriptorLength + 26, totalNodes - used.Count);
        header.WriteUInt32At(DescriptorLength + 32, clumpSize);
        header.WriteByteAt(DescriptorLength + 36, btreeType);
        header.WriteByteAt(DescriptorLength + 37, keyCompareType);
        header.WriteUInt32At(DescriptorLength + 38, BigKeys | (kind.VariableIndexKeys ? VariableIndexKeys : 0));
        return tree;
    }

    // One level's nodes, each as full as it goes, linked to their neighbours: (node, first key) for the level above.
    private static List<(uint Number, byte[] FirstKey)> Level(byte[] tree, int nodeSize, IReadOnlyList<(byte[] Key, byte[] Data)> entries, byte height,
        Func<uint> allocate)
    {
        var level = new List<(uint, byte[])>();
        int at = 0;
        while (at < entries.Count)
        {
            uint number = allocate();
            int start = checked((int)(number * (long)nodeSize));
            var node = new BigEndianWriter(tree);
            int end = DescriptorLength, count = 0;
            while (at < entries.Count)
            {
                var (key, data) = entries[at];
                int length = key.Length + data.Length;
                if ((length & 1) != 0 || end + length > nodeSize - 2 * (count + 2))
                {
                    if (count == 0 || (length & 1) != 0)
                    {
                        throw new InvalidDataException("An HFS Plus B-tree record does not fit a node.");
                    }

                    break;
                }

                key.CopyTo(tree, start + end);
                data.CopyTo(tree, start + end + key.Length);
                node.WriteUInt16At(start + nodeSize - 2 * (count + 1), end);
                end += length;
                count++;
                at++;
            }

            node.WriteUInt16At(start + nodeSize - 2 * (count + 1), end);
            tree[start + 8] = height == 1 ? (byte)0xFF : (byte)0;
            tree[start + 9] = height;
            node.WriteUInt16At(start + 10, count);
            level.Add((number, entries[at - count].Key));
        }

        var links = new BigEndianWriter(tree);
        for (var i = 0; i < level.Count; i++)
        {
            int start = checked((int)(level[i].Item1 * (long)nodeSize));
            links.WriteUInt32At(start, i + 1 < level.Count ? level[i + 1].Item1 : 0u);
            links.WriteUInt32At(start + 4, i > 0 ? level[i - 1].Item1 : 0u);
        }

        return level;
    }

    // An index record's key: the child's first key, padded to the maximum length in a tree of fixed index keys.
    private static byte[] IndexKey(TreeKind kind, byte[] key)
    {
        if (kind.VariableIndexKeys)
        {
            return key;
        }

        var padded = new byte[2 + kind.MaxKeyLength];
        key.AsSpan(2, Math.Min(key.Length - 2, kind.MaxKeyLength)).CopyTo(padded.AsSpan(2));
        new BigEndianWriter(padded).WriteUInt16At(0, kind.MaxKeyLength);
        return padded;
    }

    // The header node: header, user and map records; its forward link to the first map node.
    private static void WriteHeaderNode(byte[] tree, int nodeSize, int mapNodes)
    {
        var node = new BigEndianWriter(tree);
        node.WriteUInt32At(0, mapNodes > 0 ? 1u : 0u);
        tree[8] = 1;
        node.WriteUInt16At(10, 3);
        node.WriteUInt16At(nodeSize - 2, DescriptorLength);
        node.WriteUInt16At(nodeSize - 4, DescriptorLength + HeaderRecordLength);
        node.WriteUInt16At(nodeSize - 6, DescriptorLength + HeaderRecordLength + UserRecordLength);
        node.WriteUInt16At(nodeSize - 8, nodeSize - 8);
    }

    // The map nodes, 1 to mapNodes, chained: one record each from offset 14.
    private static void WriteMapNodes(byte[] tree, int nodeSize, int mapNodes)
    {
        var node = new BigEndianWriter(tree);
        for (var map = 1; map <= mapNodes; map++)
        {
            int start = map * nodeSize;
            node.WriteUInt32At(start, map < mapNodes ? (uint)(map + 1) : 0u);
            node.WriteUInt32At(start + 4, (uint)(map - 1));
            tree[start + 8] = 2;
            node.WriteUInt16At(start + 10, 1);
            node.WriteUInt16At(start + nodeSize - 2, DescriptorLength);
            node.WriteUInt16At(start + nodeSize - 4, nodeSize - 6);
        }
    }

    // A node's bit in the map: the header's map record first, then each map node's.
    private static void SetMapBit(byte[] tree, int nodeSize, uint node)
    {
        long headerBits = (nodeSize - 256) * 8L, mapNodeBits = (nodeSize - 20) * 8L;
        long offset;
        if (node < headerBits)
        {
            offset = DescriptorLength + HeaderRecordLength + UserRecordLength + node / 8;
        }
        else
        {
            long index = node - headerBits;
            long map = 1 + index / mapNodeBits, within = index % mapNodeBits;
            offset = map * nodeSize + DescriptorLength + within / 8;
        }

        tree[offset] |= (byte)(0x80 >> (int)(node % 8));
    }
}
