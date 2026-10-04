using System.Buffers.Binary;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The B-tree file layout HFS and HFS Plus share (Inside Macintosh: Files, "B*-Trees"; TN1150 "B-Trees"): node
// descriptors, records by the offset table (keys with a byte or a word length), the header record and the node map.
public sealed class BTreeFileTests
{
    // A tree of nodeSize-byte nodes: a header node whose map record covers headerMapBytes, and map nodes after it.
    private static byte[] Tree(int nodeSize, int nodes, int headerMapBytes, params uint[] mapNodes)
    {
        var tree = new byte[nodeSize * nodes];
        tree[8] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(10), 3);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(14 + 18), (ushort)nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(14 + 22), (uint)nodes);
        int mapStart = 14 + 106 + 128;
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(nodeSize - 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(nodeSize - 4), 14 + 106);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(nodeSize - 6), (ushort)mapStart);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(nodeSize - 8), (ushort)(mapStart + headerMapBytes));
        uint previous = 0;
        foreach (var node in mapNodes)
        {
            BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan((int)previous * nodeSize), node);
            int at = (int)node * nodeSize;
            tree[at + 8] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + 10), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + nodeSize - 2), 14);
            BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + nodeSize - 4), (ushort)(nodeSize - 6));
            previous = node;
        }

        return tree;
    }

    [Fact]
    public void The_header_record_is_read_and_written()
    {
        var file = new BTreeFile(Tree(512, 4, 8), 512, wordKeyLength: false);

        file.Depth = 2;
        file.Root = 3;
        file.LeafRecords = 70;
        file.FirstLeaf = 1;
        file.LastLeaf = 2;
        file.FreeNodes = 1;

        Assert.Equal((2, 3u, 70u, 1u, 2u, 1u, 4u, 4u), (file.Depth, file.Root, file.LeafRecords, file.FirstLeaf, file.LastLeaf, file.FreeNodes, file.TotalNodes, file.NodeCount));
        Assert.Equal(512, file.DeclaredNodeSize);
    }

    // Records by the offset table: a byte key length (HFS) or a word (HFS Plus), the data at the next even offset.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Records_split_into_key_and_data(bool wordKeyLength)
    {
        var tree = new byte[512 * 2];
        int at = 512, end = 512 + 512;
        tree[at + 8] = 0xFF;
        tree[at + 9] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(at), 7);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(at + 4), 5);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(at + 10), 1);
        byte[] key = wordKeyLength ? [0, 3, 1, 2, 3] : [3, 1, 2, 3];
        key.CopyTo(tree, at + 14);
        int dataStart = (14 + key.Length + 1) & ~1;
        tree[at + dataStart] = 0xAA;
        tree[at + dataStart + 1] = 0xBB;
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(end - 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(end - 4), (ushort)(dataStart + 2));
        var file = new BTreeFile(tree, 512, wordKeyLength);

        var node = file.Node(1);
        Assert.Equal((7u, 5u, (sbyte)-1, (byte)1, 1), (node.FLink, node.BLink, node.Kind, node.Height, node.RecordCount));
        Assert.True(file.TryRecord(1, 0, out var readKey, out var data));
        Assert.Equal(key, readKey.ToArray());
        Assert.Equal(new byte[] { 0xAA, 0xBB }, data.ToArray());
        Assert.False(file.TryRecord(1, 1, out _, out _));                          // past the record count
    }

    // A record whose offsets fall outside the node's records, or whose key runs past it, is not read.
    [Fact]
    public void Records_with_bad_offsets_are_refused()
    {
        var tree = new byte[512 * 2];
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(512 + 10), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(1024 - 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(1024 - 4), 600);
        var file = new BTreeFile(tree, 512, wordKeyLength: false);
        Assert.False(file.TryRecord(1, 0, out _, out _));

        BinaryPrimitives.WriteUInt16BigEndian(tree.AsSpan(1024 - 4), 20);
        tree[512 + 14] = 40;                                                          // a key past the record's end
        Assert.False(file.TryRecord(1, 0, out _, out _));
    }

    // The node map: the header node's map record, then each map node's, in chain order.
    [Fact]
    public void The_node_map_spans_the_header_and_its_map_nodes()
    {
        // 8 header map bytes (64 nodes), then a map node of 512 - 20 bytes.
        var file = new BTreeFile(Tree(512, 100, 8, 70), 512, wordKeyLength: false);

        Assert.True(file.TryReadMap(out var map, out var problem), problem);
        Assert.Equal([70u], map.MapNodes);
        Assert.Equal(64 + (512 - 20) * 8L, map.Capacity);
        map.SetAllocated(0, true);
        map.SetAllocated(63, true);
        map.SetAllocated(64, true);
        map.SetAllocated(70, true);

        Assert.True(map.IsAllocated(63));
        Assert.True(map.IsAllocated(64));
        Assert.Equal(0x82, file.Bytes[70 * 512 + 14]);                              // nodes 64 and 70: the map node's first byte
        Assert.Equal(1u, map.FirstFree(100));
        Assert.Equal(96u, map.CountFree(100));
        map.SetAllocated(63, false);
        Assert.False(map.IsAllocated(63));
    }

    [Theory]
    [InlineData(150u)]   // past the tree
    [InlineData(0u)]     // a cycle back to the header
    public void A_bad_map_node_chain_is_refused(uint link)
    {
        var tree = Tree(512, 100, 8, 70);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(70 * 512), link == 0 ? 70 : link);

        Assert.False(new BTreeFile(tree, 512, wordKeyLength: false).TryReadMap(out _, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void A_map_node_that_is_not_one_is_refused()
    {
        var tree = Tree(512, 100, 8, 70);
        tree[70 * 512 + 8] = 0xFF;

        Assert.False(new BTreeFile(tree, 512, wordKeyLength: false).TryReadMap(out _, out _));
    }

    // HFS Plus nodes are larger; the header's map record grows with them.
    [Fact]
    public void Larger_nodes_have_larger_maps()
    {
        var file = new BTreeFile(Tree(4096, 3, 4096 - 256), 4096, wordKeyLength: true);

        Assert.True(file.TryReadMap(out var map, out _));
        Assert.Equal((4096 - 256) * 8L, map.Capacity);
    }
}
