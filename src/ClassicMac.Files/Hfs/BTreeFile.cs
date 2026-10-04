using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// A B-tree file as HFS and HFS Plus lay it out (Inside Macintosh: Files, "B*-Trees"; TN1150 "B-Trees"): nodes of one
/// size, each a 14-byte descriptor (forward and backward links, kind, height, record count), records, and an offset
/// table at its end; node 0 the header node, whose records are the header record, the user record and the node map,
/// continued in map nodes chained from it. Only the layout: what a reader or the writer makes of a fault is theirs.
/// </summary>
internal sealed class BTreeFile
{
    private const int DescriptorLength = 14;
    private const int HeaderRecord = DescriptorLength;

    /// <param name="bytes">The tree file, whole nodes (a partial node at the end is not a node).</param>
    /// <param name="nodeSize">Its node size: 512 in HFS, 512 to 32,768 in HFS Plus.</param>
    /// <param name="wordKeyLength">Whether a key's length is a word (HFS Plus) rather than a byte (HFS).</param>
    public BTreeFile(byte[] bytes, int nodeSize, bool wordKeyLength)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeSize, 512);
        Bytes = bytes;
        NodeSize = nodeSize;
        WordKeyLength = wordKeyLength;
    }

    public byte[] Bytes { get; }

    public int NodeSize { get; }

    public bool WordKeyLength { get; }

    /// <summary>The whole nodes the file holds.</summary>
    public uint NodeCount => (uint)(Bytes.Length / NodeSize);

    private BigEndianReader Reader => new(Bytes);

    private BigEndianWriter Writer => new(Bytes);

    // The header record (node 0, record 0).
    public int Depth
    {
        get => Reader.ReadUInt16At(HeaderRecord);
        set => Writer.WriteUInt16At(HeaderRecord, value);
    }

    public uint Root
    {
        get => Reader.ReadUInt32At(HeaderRecord + 2);
        set => Writer.WriteUInt32At(HeaderRecord + 2, value);
    }

    public uint LeafRecords
    {
        get => Reader.ReadUInt32At(HeaderRecord + 6);
        set => Writer.WriteUInt32At(HeaderRecord + 6, value);
    }

    public uint FirstLeaf
    {
        get => Reader.ReadUInt32At(HeaderRecord + 10);
        set => Writer.WriteUInt32At(HeaderRecord + 10, value);
    }

    public uint LastLeaf
    {
        get => Reader.ReadUInt32At(HeaderRecord + 14);
        set => Writer.WriteUInt32At(HeaderRecord + 14, value);
    }

    public int DeclaredNodeSize => Reader.ReadUInt16At(HeaderRecord + 18);

    public int MaxKeyLength => Reader.ReadUInt16At(HeaderRecord + 20);

    public uint TotalNodes
    {
        get => Reader.ReadUInt32At(HeaderRecord + 22);
        set => Writer.WriteUInt32At(HeaderRecord + 22, value);
    }

    public uint FreeNodes
    {
        get => Reader.ReadUInt32At(HeaderRecord + 26);
        set => Writer.WriteUInt32At(HeaderRecord + 26, value);
    }

    /// <summary>The byte offset of a node in the file.</summary>
    public int Offset(uint node) => checked((int)node * NodeSize);

    /// <summary>A node's descriptor (the node must lie in the file).</summary>
    public BTreeNode Node(uint node)
    {
        int at = Offset(node);
        var reader = Reader;
        return new BTreeNode(node, reader.ReadUInt32At(at), reader.ReadUInt32At(at + 4), (sbyte)Bytes[at + 8], Bytes[at + 9],
            reader.ReadUInt16At(at + 10));
    }

    /// <summary>
    /// Where a record lies in its node, by the offset table: false when the index is past the record count or the
    /// offsets fall outside the records' space (after the descriptor, before the table).
    /// </summary>
    public bool TryRecordBounds(uint node, int index, out int start, out int end)
    {
        start = end = 0;
        int at = Offset(node);
        int count = Reader.ReadUInt16At(at + 10);
        int tableStart = NodeSize - 2 * (count + 1);
        if (index < 0 || index >= count || tableStart < DescriptorLength)
        {
            return false;
        }

        start = Reader.ReadUInt16At(at + NodeSize - 2 * (index + 1));
        end = Reader.ReadUInt16At(at + NodeSize - 2 * (index + 2));
        return start >= DescriptorLength && end > start && end <= tableStart;
    }

    /// <summary>
    /// A record's key (with its length field) and data (from the next even offset after the key), over the file's bytes:
    /// false when its bounds are bad or its key runs past it.
    /// </summary>
    public bool TryRecord(uint node, int index, out ReadOnlyMemory<byte> key, out ReadOnlyMemory<byte> data)
    {
        key = data = ReadOnlyMemory<byte>.Empty;
        if (!TryRecordBounds(node, index, out int start, out int end))
        {
            return false;
        }

        int at = Offset(node);
        int lengthField = WordKeyLength ? 2 : 1;
        if (start + lengthField > end)
        {
            return false;
        }

        int keyLength = WordKeyLength ? Reader.ReadUInt16At(at + start) : Bytes[at + start];
        int keyEnd = start + lengthField + keyLength;
        int dataStart = (keyEnd + 1) & ~1;
        if (keyEnd > end || dataStart > end)
        {
            return false;
        }

        key = Bytes.AsMemory(at + start, keyEnd - start);
        data = Bytes.AsMemory(at + dataStart, end - dataStart);
        return true;
    }

    /// <summary>
    /// The node map: the header node's map record (its third), then each map node's record in chain order. False, with
    /// the problem, when the header's map record has bad bounds, or a map node lies outside the file, repeats, is not a
    /// map node (kind 2) of one record, or has bad record bounds.
    /// </summary>
    public bool TryReadMap(out BTreeMap map, out string? problem)
    {
        map = null!;
        var records = new List<(int Offset, int Length)>();
        int headerStart = Reader.ReadUInt16At(NodeSize - 6), headerEnd = Reader.ReadUInt16At(NodeSize - 8);
        if (headerStart < DescriptorLength || headerEnd <= headerStart || headerEnd > NodeSize - 8)
        {
            problem = "The B-tree header node's map record has invalid bounds.";
            return false;
        }

        records.Add((headerStart, headerEnd - headerStart));
        var mapNodes = new List<uint>();
        var seen = new HashSet<uint> { 0 };
        for (uint node = Reader.ReadUInt32At(0); node != 0; node = Reader.ReadUInt32At(Offset(node)))
        {
            if (node >= NodeCount || !seen.Add(node))
            {
                problem = $"The B-tree map-node chain reaches node {node}, outside the tree or already in the chain.";
                return false;
            }

            var descriptor = Node(node);
            if (descriptor.Kind != BTreeNode.MapKind || descriptor.RecordCount != 1 || !TryRecordBounds(node, 0, out int start, out int end))
            {
                problem = $"B-tree node {node} is in the map-node chain but is not a map node of one record.";
                return false;
            }

            records.Add((Offset(node) + start, end - start));
            mapNodes.Add(node);
        }

        map = new BTreeMap(Bytes, records, mapNodes);
        problem = null;
        return true;
    }
}

/// <summary>A B-tree node's descriptor.</summary>
internal readonly record struct BTreeNode(uint Number, uint FLink, uint BLink, sbyte Kind, byte Height, int RecordCount)
{
    public const sbyte LeafKind = -1, IndexKind = 0, HeaderKind = 1, MapKind = 2;
}

/// <summary>
/// A B-tree's node map over the file's bytes: one bit per node, most significant first, in the header node's map record
/// then the map nodes' in turn; a set bit is a node in use.
/// </summary>
internal sealed class BTreeMap(byte[] bytes, List<(int Offset, int Length)> records, List<uint> mapNodes)
{
    /// <summary>The map nodes, in chain order.</summary>
    public IReadOnlyList<uint> MapNodes => mapNodes;

    /// <summary>The nodes the map records can describe.</summary>
    public long Capacity
    {
        get
        {
            long bits = 0;
            foreach (var (_, length) in records)
            {
                bits += length * 8L;
            }

            return bits;
        }
    }

    /// <summary>Whether the map marks the node in use; a node past the map's capacity is not.</summary>
    public bool IsAllocated(uint node) => Place(node) is { } place && (bytes[place.At] & place.Bit) != 0;

    public void SetAllocated(uint node, bool used)
    {
        var place = Place(node) ?? throw new ArgumentOutOfRangeException(nameof(node), $"Node {node} is past the B-tree's node map.");
        bytes[place.At] = used ? (byte)(bytes[place.At] | place.Bit) : (byte)(bytes[place.At] & ~place.Bit);
    }

    /// <summary>The lowest-numbered free node among the first <paramref name="nodes"/>, as AllocateNode takes it; null when none is.</summary>
    public uint? FirstFree(uint nodes)
    {
        for (uint node = 0; node < nodes; node++)
        {
            if (!IsAllocated(node))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>The free nodes among the first <paramref name="nodes"/>.</summary>
    public uint CountFree(uint nodes)
    {
        uint free = 0;
        for (uint node = 0; node < nodes; node++)
        {
            if (!IsAllocated(node))
            {
                free++;
            }
        }

        return free;
    }

    private (int At, byte Bit)? Place(uint node)
    {
        long index = node >> 3;
        foreach (var (offset, length) in records)
        {
            if (index < length)
            {
                return (offset + (int)index, (byte)(0x80 >> (int)(node & 7)));
            }

            index -= length;
        }

        return null;
    }
}
