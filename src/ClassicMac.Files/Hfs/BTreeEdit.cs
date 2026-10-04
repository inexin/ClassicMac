using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// An HFS B-tree, the catalog or the extents tree, edited in place by the BTree manager's rules (hfs.md §1.8), in the
// tree's own key order [Doc: Apple's hfs sources, BTreeTreeOps.c, BTreeAllocate.c, BTree.c]: inserts that fit go in; a full node rotates records into its left sibling, or splits to
// the left into the first free node; a node's new first key is deleted from its parent and inserted again; an emptied
// node is unlinked, zeroed and freed, its parent's record deleted; a root left with one record gives way to its child.
internal sealed class BTreeEdit
{
    public sealed class NeedsNodesException : Exception;

    public sealed class RebuildException : Exception;

    private readonly int maxKeyLength;
    private readonly Comparison<byte[]> compare;
    private readonly BTreeMap map;
    private Dictionary<uint, uint>? parents;

    public BTreeEdit(byte[] bytes, Comparison<byte[]> compare)
    {
        Bytes = bytes;
        this.compare = compare;
        maxKeyLength = Reader.ReadUInt16At(14 + 20);
        if (!new BTreeFile(bytes, NodeSize, wordKeyLength: false).TryReadMap(out map, out var problem))
        {
            throw new InvalidDataException($"The HFS B-tree node map is invalid: {problem}");
        }
    }

    public byte[] Bytes { get; }

    private BigEndianReader Reader => new(Bytes);

    private BigEndianWriter Writer => new(Bytes);

    private uint NodeCount => (uint)(Bytes.Length / NodeSize);

    private int Depth
    {
        get => Reader.ReadUInt16At(14);
        set => Writer.WriteUInt16At(14, value);
    }

    private uint Root
    {
        get => Reader.ReadUInt32At(14 + 2);
        set => Writer.WriteUInt32At(14 + 2, value);
    }

    private uint FreeNodes
    {
        get => Reader.ReadUInt32At(14 + 26);
        set => Writer.WriteUInt32At(14 + 26, value);
    }

    private void AddLeafRecords(int delta) => Writer.WriteUInt32At(14 + 6, checked((uint)(Reader.ReadUInt32At(14 + 6) + delta)));

    private static int Offset(uint node) => checked((int)node * NodeSize);

    private uint FLink(uint node) => Reader.ReadUInt32At(Offset(node));

    private uint BLink(uint node) => Reader.ReadUInt32At(Offset(node) + 4);

    private void SetFLink(uint node, uint value) => Writer.WriteUInt32At(Offset(node), value);

    private void SetBLink(uint node, uint value) => Writer.WriteUInt32At(Offset(node) + 4, value);

    private bool IsLeaf(uint node) => Bytes[Offset(node) + 8] == 0xFF;

    private List<(byte[] Key, byte[] Data)> Records(uint node) => ReadNodeRecords(Bytes, node);


    // A record's bytes in a node, with its offset slot: key (padded to even) and data, + 2.
    private static int Size((byte[] Key, byte[] Data) record) => ((record.Key.Length + 1) & ~1) + record.Data.Length + 2;

    private static bool Fits(List<(byte[] Key, byte[] Data)> records) => 14 + 2 + records.Sum(Size) <= NodeSize;

    private void Write(uint node, List<(byte[] Key, byte[] Data)> records)
    {
        if (!TryBuildLeafNode(Bytes.AsSpan(Offset(node), NodeSize).ToArray(), records, out var rebuilt))
        {
            throw new RebuildException();
        }

        rebuilt.CopyTo(Bytes, Offset(node));
    }

    // The leaf for a key, by the index: in each index node the last record whose key is not above it (the first when
    // all are).
    private uint LeafFor(byte[] key)
    {
        if (Depth == 0)
        {
            throw new RebuildException();
        }

        uint node = Root;
        while (!IsLeaf(node))
        {
            var records = Records(node);
            int at = records.FindLastIndex(record => compare(record.Key, key) <= 0);
            node = IndexChild(records[Math.Max(0, at)].Data);
        }

        return node;
    }

    // The manager grows the tree's file before an operation when it has fewer free nodes than its depth + 1.
    private void RequireNodes()
    {
        if (FreeNodes < Depth + 1)
        {
            throw new NeedsNodesException();
        }
    }

    public void Insert((byte[] Key, byte[] Data) record)
    {
        RequireNodes();
        uint leaf = LeafFor(record.Key);
        var records = Records(leaf);
        int at = records.FindIndex(existing => compare(existing.Key, record.Key) >= 0);
        at = at < 0 ? records.Count : at;
        if (at < records.Count && compare(records[at].Key, record.Key) == 0)
        {
            throw new RebuildException();
        }

        records.Insert(at, record);
        InsertInto(leaf, records, at, skipRotate: false);
        AddLeafRecords(1);
    }

    public void Delete(byte[] key)
    {
        uint leaf = LeafFor(key);
        var records = Records(leaf);
        int at = records.FindIndex(existing => compare(existing.Key, key) == 0);
        if (at < 0)
        {
            throw new RebuildException();
        }

        if (at == 0)
        {
            RequireNodes();
        }

        records.RemoveAt(at);
        DeleteFrom(leaf, records, at);
        AddLeafRecords(-1);
        Collapse();
    }

    // A record whose key compares the same: in place when it fits, otherwise deleted and inserted.
    public void Replace((byte[] Key, byte[] Data) record)
    {
        uint leaf = LeafFor(record.Key);
        var records = Records(leaf);
        int at = records.FindIndex(existing => compare(existing.Key, record.Key) == 0);
        if (at < 0)
        {
            throw new RebuildException();
        }

        records[at] = record;
        if (Fits(records))
        {
            Write(leaf, records);
            return;
        }

        Delete(record.Key);
        Insert(record);
    }

    // The node's records, the new one at index, written: in place when they fit, else rotated left or split left.
    private void InsertInto(uint node, List<(byte[] Key, byte[] Data)> records, int index, bool skipRotate)
    {
        if (Fits(records))
        {
            Write(node, records);
            if (index == 0 && node != Root)
            {
                UpdateParentKey(node, records[0].Key);
            }

            return;
        }

        uint left = BLink(node);
        if (left != 0 && !skipRotate && TryRotateLeft(left, node, records))
        {
            UpdateParentKey(node, Records(node)[0].Key);
            return;
        }

        // Split left: a free node becomes the node's left sibling and takes about half its bytes.
        uint added = Allocate();
        int offset = Offset(node), addedOffset = Offset(added);
        Bytes.AsSpan(addedOffset, NodeSize).Clear();
        Bytes[addedOffset + 8] = Bytes[offset + 8];
        Bytes[addedOffset + 9] = Bytes[offset + 9];
        SetFLink(added, node);
        SetBLink(added, left);
        SetBLink(node, added);
        if (left != 0)
        {
            SetFLink(left, added);
        }
        else if (IsLeaf(node))
        {
            Writer.WriteUInt32At(14 + 10, added);
        }

        Write(added, []);
        if (!TryRotateLeft(added, node, records))
        {
            throw new RebuildException();
        }

        parents = null;
        if (node == Root)
        {
            AddRoot(added, node);
            return;
        }

        UpdateParentKey(node, Records(node)[0].Key);
        uint parent = ParentOf(node);
        var siblings = Records(parent);
        int at = siblings.FindIndex(record => IndexChild(record.Data) == node);
        siblings.Insert(at, (IndexKey(Records(added)[0].Key, siblings[0].Key.Length), ChildNode(added)));
        InsertInto(parent, siblings, at, skipRotate: true);
        parents = null;
    }

    // RotateLeft: records move from the front of the right node (its new record counted) into the left while the
    // left holds fewer bytes, the last move undone if it overfills the left; false, with nothing written, when the
    // right still overflows.
    private bool TryRotateLeft(uint left, uint right, List<(byte[] Key, byte[] Data)> rightRecords)
    {
        var leftRecords = Records(left);
        var moving = rightRecords.ToList();
        int leftBytes = leftRecords.Sum(Size), rightBytes = moving.Sum(Size), moved = 0;
        while (leftBytes < rightBytes && moved < moving.Count - 1)
        {
            leftBytes += Size(moving[moved]);
            rightBytes -= Size(moving[moved]);
            moved++;
        }

        var newLeft = leftRecords.Concat(moving.Take(moved)).ToList();
        if (moved > 0 && !Fits(newLeft))
        {
            moved--;
            newLeft = [.. leftRecords, .. moving.Take(moved)];
        }

        var newRight = moving.Skip(moved).ToList();
        if (moved == 0 || !Fits(newRight) || !Fits(newLeft))
        {
            return false;
        }

        Write(left, newLeft);
        Write(right, newRight);
        if (!IsLeaf(left))
        {
            parents = null;
        }

        return true;
    }

    // A new root over the two nodes, a level up.
    private void AddRoot(uint left, uint right)
    {
        uint root = Allocate();
        int offset = Offset(root);
        Bytes.AsSpan(offset, NodeSize).Clear();
        int height = Bytes[Offset(left) + 9] + 1;
        Bytes[offset + 9] = (byte)height;
        Write(root, [(IndexKey(Records(left)[0].Key), ChildNode(left)), (IndexKey(Records(right)[0].Key), ChildNode(right))]);
        Root = root;
        Depth = height;
        parents = null;
    }

    // The parent's record for the node deleted and inserted again with the node's new first key.
    private void UpdateParentKey(uint node, byte[] first)
    {
        uint parent = ParentOf(node);
        var records = Records(parent);
        int at = records.FindIndex(record => IndexChild(record.Data) == node);
        if (at < 0)
        {
            throw new RebuildException();
        }

        var key = IndexKey(first, records[at].Key.Length);
        if (key.AsSpan().SequenceEqual(records[at].Key))
        {
            return;
        }

        var data = records[at].Data;
        records.RemoveAt(at);
        records.Insert(at, (key, data));
        InsertInto(parent, records, at, skipRotate: false);
    }

    // The node's records after a deletion at index: an emptied node unlinked, zeroed and freed, and its parent's
    // record deleted in turn; a new first key carried up.
    private void DeleteFrom(uint node, List<(byte[] Key, byte[] Data)> records, int index)
    {
        if (records.Count > 0)
        {
            Write(node, records);
            if (index == 0 && node != Root)
            {
                UpdateParentKey(node, records[0].Key);
            }

            return;
        }

        if (node == Root)
        {
            Free(node);
            Root = 0;
            Depth = 0;
            Writer.WriteUInt32At(14 + 10, 0u);
            Writer.WriteUInt32At(14 + 14, 0u);
            return;
        }

        uint left = BLink(node), right = FLink(node);
        if (left != 0)
        {
            SetFLink(left, right);
        }

        if (right != 0)
        {
            SetBLink(right, left);
        }

        if (IsLeaf(node))
        {
            if (Reader.ReadUInt32At(14 + 10) == node)
            {
                Writer.WriteUInt32At(14 + 10, right);
            }

            if (Reader.ReadUInt32At(14 + 14) == node)
            {
                Writer.WriteUInt32At(14 + 14, left);
            }
        }

        uint parent = ParentOf(node);
        Free(node);
        parents = null;
        var siblings = Records(parent);
        int at = siblings.FindIndex(record => IndexChild(record.Data) == node);
        siblings.RemoveAt(at);
        DeleteFrom(parent, siblings, at);
    }

    // CollapseTree: while the root is an index node with one record, its child becomes the root.
    private void Collapse()
    {
        while (Depth > 1 && Records(Root) is [var only])
        {
            uint old = Root;
            Root = IndexChild(only.Data);
            Depth--;
            Free(old);
            parents = null;
        }
    }

    // ClearNode and FreeNode: the node zeroed, its map bit cleared, the free count raised.
    private void Free(uint node)
    {
        Bytes.AsSpan(Offset(node), NodeSize).Clear();
        map.SetAllocated(node, false);
        FreeNodes++;
    }

    // AllocateNode: the first free node by the map, marked used.
    private uint Allocate()
    {
        uint node = map.FirstFree(NodeCount) ?? throw new NeedsNodesException();
        map.SetAllocated(node, true);
        FreeNodes--;
        return node;
    }

    // A key as the index stores it: at the tree's maximum key length, zero-padded, as Mac OS writes index keys
    // (hfs.md §1.8), or as long as the stored ones are where the index keeps keys so.
    private byte[] IndexKey(byte[] key, int length) => length == maxKeyLength + 1 ? IndexKey(key) : key;

    private byte[] IndexKey(byte[] key)
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

    private uint ParentOf(uint node) => Parents.TryGetValue(node, out uint parent) ? parent : throw new RebuildException();

    // Each node's parent, by a walk from the root.
    private Dictionary<uint, uint> Parents
    {
        get
        {
            if (parents is not null)
            {
                return parents;
            }

            parents = [];
            var pending = new Stack<uint>();
            if (Depth > 1)
            {
                pending.Push(Root);
            }

            while (pending.Count > 0)
            {
                uint node = pending.Pop();
                bool aboveIndex = Bytes[Offset(node) + 9] > 2;
                foreach (var (_, data) in Records(node))
                {
                    uint child = IndexChild(data);
                    parents[child] = node;
                    if (aboveIndex)
                    {
                        pending.Push(child);
                    }
                }
            }

            return parents;
        }
    }
}
