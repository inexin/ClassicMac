using System;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// Disk First Aid's B-tree check (BTCheck; hfs.md §5.6), for the extents tree and the catalog (and HFS Plus's attributes
// tree, with HFS Plus's key lengths and orders): the header node, a
// depth-first walk from the root checking each node's keys, links, kind and height, the node map, and the header record
// against what the walk computed [Code: Disk First Aid 8.5.5, CODE 1 $1FEB4 BTCheck, $2097C BTKeyChk, $205B4 BTMapChk,
// $20710 CmpBTH, $25B28 AllocBTN].
internal sealed class BTreeCheck
{
    private const int MaxDepth = 8;
    private readonly FirstAidRun run;
    private readonly FirstAidTree tree;
    private readonly BTreeFile file;
    private readonly (uint Node, uint FLink)?[] lastAtLevel = new (uint, uint)?[MaxDepth + 2];
    private int treeDepth;

    private BTreeCheck(FirstAidRun run, FirstAidTree tree)
    {
        this.run = run;
        this.tree = tree;
        file = tree.File;
    }

    public static bool Run(FirstAidRun run, FirstAidTree tree) => new BTreeCheck(run, tree).Check();

    private bool Check()
    {
        try
        {
            return Header() && Walk() && Map() && CompareHeader();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return run.Fatal(22);                                                // a node that cannot be parsed
        }
    }

    // The header node, node 0 (part A).
    private bool Header()
    {
        if (!Reach(0))
        {
            return false;
        }

        var header = file.Node(0);
        if (header.Kind != BTreeNode.HeaderKind || header.RecordCount != 3)
        {
            return run.Fatal(28);
        }

        if (header.Height != 0)
        {
            return run.Fatal(5);                                                 // ends this tree's check
        }

        if (!file.TryRecordBounds(0, 0, out int start, out int end) || end - start != 106)
        {
            return run.Fatal(13);
        }

        treeDepth = file.Depth;
        if (treeDepth > MaxDepth)
        {
            return run.Fatal(29);
        }

        if (file.Root >= tree.TotalNodes)
        {
            return run.Fatal(15);
        }

        if ((treeDepth == 0) != (file.Root == 0))
        {
            return run.Fatal(29);
        }

        return true;
    }

    private bool Walk() => treeDepth == 0 || Visit(file.Root, 1, null);

    // A node on its first visit (part B); level 1 is the root's.
    private bool Visit(uint node, int level, byte[]? parentKey)
    {
        if (node >= tree.TotalNodes)
        {
            return run.Fatal(22, 0, node);
        }

        if (!Reach(node))
        {
            return false;
        }

        var descriptor = file.Node(node);
        if (!Keys(node, descriptor, out var firstKey))
        {
            return false;
        }

        // Each level's nodes are linked in walk order.
        if (lastAtLevel[level] is { } last ? descriptor.BLink != last.Node || last.FLink != node : descriptor.BLink != 0)
        {
            return run.Fatal(21, 0, node);
        }

        lastAtLevel[level] = (node, descriptor.FLink);
        if (descriptor.Kind is not (BTreeNode.IndexKind or BTreeNode.LeafKind))
        {
            FlagTree(16, FirstAidRepairs.RebuildBTree, 0, node);
            return true;
        }

        if (descriptor.Height != treeDepth - level + 1)
        {
            FlagTree(5, FirstAidRepairs.RebuildBTree, 0, node);
        }

        if (parentKey is not null && firstKey is not null && Compare(firstKey, parentKey) != 0)
        {
            FlagTree(19, FirstAidRepairs.RebuildBTree, 0, node);
        }

        return descriptor.Kind == BTreeNode.LeafKind ? Leaf(node, descriptor) : Index(node, descriptor, level);
    }

    // BTKeyChk: an empty node with no links is malformed; each key is no longer than the tree allows and greater than the
    // one before it.
    private bool Keys(uint node, BTreeNode descriptor, out byte[]? firstKey)
    {
        firstKey = null;
        if (descriptor.RecordCount == 0 && descriptor.FLink == 0 && descriptor.BLink == 0)
        {
            return run.Fatal(22, 0, node);
        }

        byte[]? previous = null;
        for (var i = 0; i < descriptor.RecordCount; i++)
        {
            if (!file.TryRecord(node, i, out var key, out _))
            {
                return run.Fatal(22, 0, node);
            }

            if (tree.KeyLength(key.Span) > tree.MaxKeyLength || !tree.KeyFits(key.Span))
            {
                return run.Fatal(25, 0, node);
            }

            var bytes = key.ToArray();
            if (previous is not null && Compare(bytes, previous) <= 0)
            {
                return run.Fatal(26, 0, node);
            }

            firstKey ??= bytes;
            previous = bytes;
        }

        return true;
    }

    private bool Leaf(uint node, BTreeNode descriptor)
    {
        if (tree.FirstLeaf == 0)
        {
            tree.FirstLeaf = node;
        }

        tree.LastLeaf = node;
        for (var i = 0; i < descriptor.RecordCount; i++)
        {
            file.TryRecord(node, i, out var key, out var data);
            tree.LeafRecords++;
            tree.Records.Add((key.ToArray(), data.ToArray(), node));
            if (tree.Plus ? tree.FileId == 3 && !PlusExtentRecords.Check(run, data, 0, node) : !tree.IsCatalog && !ExtentRecords.Check(run, data.Span, 0, node))
            {
                return false;
            }
        }

        return true;
    }

    private bool Index(uint node, BTreeNode descriptor, int level)
    {
        for (var i = 0; i < descriptor.RecordCount; i++)
        {
            file.TryRecord(node, i, out var key, out var data);
            if (data.Length < 4)
            {
                return run.Fatal(22, 0, node);
            }

            uint child = new BigEndianReader(data).ReadUInt32At(0);
            if (child == 0 || child >= tree.TotalNodes)
            {
                if (i == 0)
                {
                    return run.Fatal(20, 0, node);
                }

                FlagTree(20, FirstAidRepairs.RebuildBTree, 0, node);
                continue;
            }

            if (level + 1 > MaxDepth)
            {
                FlagTree(29, FirstAidRepairs.RebuildBTree, 0, node);
                continue;
            }

            if (!Visit(child, level + 1, key.ToArray()))
            {
                return false;
            }
        }

        return true;
    }

    // BTMapChk (part C): the map records, the header's and then each map node's, cover the tree exactly; then the map
    // must mark the nodes the walk reached, else repair writes it again.
    private bool Map()
    {
        long needed = (tree.TotalNodes + 7) / 8;
        if (!file.TryRecordBounds(0, 2, out int start, out int end))
        {
            return run.Fatal(22);
        }

        needed -= end - start;
        var map = new System.Collections.Generic.List<(uint Node, int Start, int End)> { (0, start, end) };
        for (uint next = file.Node(0).FLink; next != 0; next = file.Node(next).FLink)
        {
            if (needed <= 0)
            {
                return run.Fatal(24, 0, next);
            }

            if (next >= tree.TotalNodes)
            {
                return run.Fatal(22, 0, next);
            }

            if (!Reach(next))
            {
                return false;
            }

            var descriptor = file.Node(next);
            if (descriptor.Kind != BTreeNode.MapKind || descriptor.RecordCount != 1 || !file.TryRecordBounds(next, 0, out start, out end))
            {
                return run.Fatal(27, 0, next);
            }

            if (descriptor.Height != 0)
            {
                FlagTree(5, FirstAidRepairs.BTreeMap, 0, next);
            }

            needed -= end - start;
            map.Add((next, start, end));
        }

        if (needed > 0)
        {
            return run.Fatal(24);
        }

        uint bit = 0;
        foreach (var (mapNode, from, to) in map)
        {
            int at = file.Offset(mapNode);
            for (int b = from; b < to && bit < tree.TotalNodes; b++)
            {
                for (int mask = 0x80; mask != 0 && bit < tree.TotalNodes; mask >>= 1, bit++)
                {
                    if (((file.Bytes[at + b] & mask) != 0) != tree.Reached.Contains(bit))
                    {
                        run.Silent(FirstAidRepairs.BTreeMap);
                        run.TreesToRebuild.Add(tree.FileId);
                        return true;
                    }
                }
            }
        }

        return true;
    }

    // CmpBTH (part D): the header record's first 30 bytes against the walk's values.
    private bool CompareHeader()
    {
        tree.Depth = lastAtLevel.Length - 1;
        while (tree.Depth > 0 && lastAtLevel[tree.Depth] is null)
        {
            tree.Depth--;
        }

        uint free = tree.TotalNodes - (uint)tree.Reached.Count;
        if (file.Depth != tree.Depth || file.LeafRecords != tree.LeafRecords || file.FirstLeaf != tree.FirstLeaf
            || file.LastLeaf != tree.LastLeaf || file.DeclaredNodeSize != tree.NodeSize || file.MaxKeyLength != tree.MaxKeyLength
            || file.TotalNodes != tree.TotalNodes || file.FreeNodes != free)
        {
            FlagTree(54, FirstAidRepairs.BTreeHeader);
        }

        return true;
    }

    // A problem repair fixes by writing this tree again.
    private void FlagTree(int number, FirstAidRepairs repairs, long arg2 = 0, long arg3 = 0)
    {
        run.Flag(number, repairs, arg2, arg3);
        run.TreesToRebuild.Add(tree.FileId);
    }

    // AllocBTN: a node reached twice is an overlap.
    private bool Reach(uint node) => tree.Reached.Add(node) || run.Fatal(23, 0, node);

    private int Compare(byte[] left, byte[] right) => tree.Compare(left, right);
}
