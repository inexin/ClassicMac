using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Files.Tests;

// Builds a small HFS volume byte by byte from Inside Macintosh: Files: boot blocks, MDB at 1024, 512-byte allocation
// blocks from block 4; an extents overflow file (header and leaves) and a catalog (header, two leaves and index) linked in
// order); forks split into the requested number of extents, one free block apart, with extents past the third in
// overflow records.
internal sealed class HfsBuilder
{
    public const int Block = 512;
    public const int FirstAllocationBlock = 4;
    public const uint Root = 2;

    public int ExtentsTreeNodes { get; init; } = 2;

    private readonly List<(uint Id, uint Parent, string Name)> folders = [];
    private readonly List<(uint Parent, string Name, string Type, string Creator, byte[] Data, byte[] Resource, int Fragments, uint Id, bool Thread)> files = [];
    private uint nextId = 16;

    public uint Folder(uint parent, string name)
    {
        var id = nextId++;
        folders.Add((id, parent, name));
        return id;
    }

    public void File(uint parent, string name, byte[] data, byte[] resource, string type = "TEXT", string creator = "ttxt",
        int fragments = 1, bool thread = false) =>
        files.Add((parent, name, type, creator, data, resource, fragments, nextId++, thread));

    // Where the first file's first data extent starts, for tests that patch it.
    public int FirstFileRecordOffset { get; private set; }

    public byte[] Build(string volumeName)
    {
        var allocation = new List<byte[]>(); // allocation blocks from 0
        var allocated = new List<bool>();
        var overflow = new List<byte[]>(); // extents overflow leaf records
        if (ExtentsTreeNodes < 2) throw new InvalidOperationException("The extents tree needs room for a header and leaf.");
        for (var i = 0; i < ExtentsTreeNodes; i++)
        {
            allocation.Add(new byte[Block]);
            allocated.Add(true);
        }
        var catalogStart = allocation.Count;
        allocation.AddRange([new byte[Block], new byte[Block], new byte[Block], new byte[Block]]);
        allocated.AddRange([true, true, true, true]);

        // Place each fork, fragmented, and remember its extents.
        byte[] ForkExtents(byte[] fork, int fragments, byte forkType, uint id)
        {
            var blocks = (fork.Length + Block - 1) / Block;
            var extents = new List<(int Start, int Count)>();
            var per = Math.Max(1, (int)Math.Ceiling(blocks / (double)Math.Max(1, fragments)));
            for (var done = 0; done < blocks; done += per)
            {
                var count = Math.Min(per, blocks - done);
                extents.Add((allocation.Count, count));
                for (var b = 0; b < count; b++)
                {
                    var chunk = new byte[Block];
                    var from = (done + b) * Block;
                    fork.AsSpan(from, Math.Min(Block, fork.Length - from)).CopyTo(chunk);
                    allocation.Add(chunk);
                    allocated.Add(true);
                }
                allocation.Add(new byte[Block]); // a gap, so the extents are not contiguous
                allocated.Add(false);
            }
            for (var i = 3; i < extents.Count; i += 3)
            {
                var key = new byte[8];
                key[0] = 7;
                key[1] = forkType;
                BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), id);
                BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), (ushort)extents.Take(i).Sum(e => e.Count));
                overflow.Add([.. key, .. ExtentRecord(extents.Skip(i).Take(3).ToList())]);
            }
            return ExtentRecord(extents.Take(3).ToList());
        }

        var records = new List<(uint Parent, string Name, byte[] Record)>();
        records.Add((1, volumeName, FolderRecord(Root, folders.Count(f => f.Parent == Root) + files.Count(f => f.Parent == Root))));
        records.Add((Root, "", ThreadRecord(1, volumeName)));
        foreach (var (id, parent, name) in folders)
        {
            records.Add((parent, name, FolderRecord(id, folders.Count(f => f.Parent == id) + files.Count(f => f.Parent == id))));
            records.Add((id, "", ThreadRecord(parent, name)));
        }
        foreach (var f in files)
        {
            var r = new byte[102];
            r[0] = 2;
            if (f.Thread) r[2] = 2;
            Encoding.ASCII.GetBytes(f.Type).CopyTo(r, 4);
            Encoding.ASCII.GetBytes(f.Creator).CopyTo(r, 8);
            BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(12), 0x0100);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(20), f.Id);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(26), (uint)f.Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(36), (uint)f.Resource.Length);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(44), 2_526_595_200); // 1984-01-24
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(48), 2_526_595_260);
            r[56] = 0xFE; // a recognisable FXInfo byte
            ForkExtents(f.Data, f.Fragments, 0x00, f.Id).CopyTo(r, 74);
            ForkExtents(f.Resource, 1, 0xFF, f.Id).CopyTo(r, 86);
            records.Add((f.Parent, f.Name, r));
            if (f.Thread) records.Add((f.Id, "", ThreadRecord(f.Parent, f.Name, kind: 4)));
        }

        // Catalog: records in two leaves, linked in order.
        var keyed = records.OrderBy(r => r.Parent).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => Keyed(r.Parent, r.Name, r.Record)).ToList();
        var half = (keyed.Count + 1) / 2;
        allocation[catalogStart + 1] = Leaf(keyed.Take(half).ToList(), forward: 2, backward: 0);
        allocation[catalogStart + 2] = Leaf(keyed.Skip(half).ToList(), forward: 0, backward: 1);
        allocation[catalogStart + 3] = Leaf([IndexRecord(keyed[0], 1), IndexRecord(keyed[half], 2)],
            forward: 0, backward: 0, kind: 0, height: 2);
        allocation[catalogStart] = Header(firstLeaf: 1, lastLeaf: 2, nodes: 4, records: keyed.Count,
            usedNodes: 4, maxKeyLength: 37, root: 3, depth: 2);
        var leafWithFile = FindFirstFileRecord(allocation[catalogStart + 1]) >= 0 ? 1 : 2;
        var fileKeyOffset = FindFirstFileRecord(allocation[catalogStart + leafWithFile]);
        FirstFileRecordOffset = fileKeyOffset < 0 ? -1 : (FirstAllocationBlock + catalogStart + leafWithFile) * Block + fileKeyOffset;

        // Build a one- or two-leaf extents tree, leaving any unused nodes mapped free.
        if (overflow.Count > 22)
        {
            if (ExtentsTreeNodes < 4 || overflow.Count > 44)
                throw new InvalidOperationException("This fixture needs a larger extents tree.");
            var split = overflow.Count / 2;
            allocation[1] = Leaf(overflow.Take(split).ToList(), 2, 0);
            allocation[2] = Leaf(overflow.Skip(split).ToList(), 0, 1);
            allocation[3] = Leaf([IndexRecord(overflow[0], 1), IndexRecord(overflow[split], 2)], 0, 0, kind: 0, height: 2);
            allocation[0] = Header(1, 2, (uint)ExtentsTreeNodes, overflow.Count, usedNodes: 4, root: 3, depth: 2, maxKeyLength: 7);
        }
        else
        {
            allocation[1] = overflow.Count > 0 ? Leaf(overflow, 0, 0) : new byte[Block];
            allocation[0] = Header(overflow.Count > 0 ? 1u : 0, overflow.Count > 0 ? 1u : 0,
                (uint)ExtentsTreeNodes, overflow.Count, usedNodes: overflow.Count > 0 ? 2 : 1, maxKeyLength: 7);
        }

        var image = new byte[(FirstAllocationBlock + allocation.Count) * Block];
        for (var i = 0; i < allocation.Count; i++) allocation[i].CopyTo(image, (FirstAllocationBlock + i) * Block);

        var mdb = image.AsSpan(1024);
        BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x02..], 2_526_595_200);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x06..], 2_526_595_200);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x0C..], (ushort)files.Count(f => f.Parent == Root));
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x12..], (ushort)allocation.Count);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x0E..], 3); // volume bitmap starts in allocation block 3
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x22..], checked((ushort)allocated.Count(value => !value)));
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x14..], Block);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x1C..], FirstAllocationBlock);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x1E..], nextId);
        mdb[0x24] = (byte)volumeName.Length;
        Encoding.ASCII.GetBytes(volumeName).CopyTo(mdb[0x25..]);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x54..], (uint)files.Count);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x58..], (uint)folders.Count);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x52..], checked((ushort)folders.Count(f => f.Parent == Root)));
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x82..], (uint)(ExtentsTreeNodes * Block));
        ExtentRecord([(0, ExtentsTreeNodes)]).CopyTo(mdb[0x86..]);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x92..], 4 * Block);
        ExtentRecord([(catalogStart, 4)]).CopyTo(mdb[0x96..]);
        for (var i = 0; i < allocated.Count; i++)
            if (allocated[i]) image[3 * Block + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        return image;
    }

    private static byte[] ExtentRecord(List<(int Start, int Count)> extents)
    {
        var record = new byte[12];
        for (var i = 0; i < extents.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(i * 4), (ushort)extents[i].Start);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(i * 4 + 2), (ushort)extents[i].Count);
        }
        return record;
    }

    private static byte[] FolderRecord(uint id, int valence)
    {
        var r = new byte[70];
        r[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), checked((ushort)valence));
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(6), id);
        return r;
    }

    private static byte[] ThreadRecord(uint parent, string name, byte kind = 3)
    {
        var r = new byte[46];
        r[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(10), parent);
        r[14] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(r, 15);
        return r;
    }

    // Key (length, reserved, parent, Str31 name), padded to an even length, then the record.
    private static byte[] Keyed(uint parent, string name, byte[] record)
    {
        var key = new List<byte> { (byte)(6 + name.Length), 0 };
        var id = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(id, parent);
        key.AddRange(id);
        key.Add((byte)name.Length);
        key.AddRange(Encoding.ASCII.GetBytes(name));
        if (key.Count % 2 != 0) key.Add(0);
        key[0] = checked((byte)(key.Count - 1));
        return [.. key, .. record];
    }

    private static byte[] IndexRecord(byte[] leafRecord, uint child)
    {
        int keyBytes = (leafRecord[0] + 2) & ~1;
        var record = new byte[keyBytes + 4];
        leafRecord.AsSpan(0, keyBytes).CopyTo(record);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(keyBytes), child);
        return record;
    }

    private static byte[] Leaf(List<byte[]> records, uint forward, uint backward, byte kind = 0xFF, byte height = 1)
    {
        var node = new byte[Block];
        BinaryPrimitives.WriteUInt32BigEndian(node, forward);
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4), backward);
        node[8] = kind;
        node[9] = height;
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), (ushort)records.Count);
        var at = 14;
        for (var i = 0; i < records.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 2 * (i + 1)), (ushort)at);
            records[i].CopyTo(node, at);
            at += records[i].Length;
        }
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 2 * (records.Count + 1)), (ushort)at);
        if (at > Block - 2 * (records.Count + 1)) throw new InvalidOperationException("Too many records for one leaf.");
        return node;
    }

    private static byte[] Header(uint firstLeaf, uint lastLeaf, uint nodes, int records,
        int usedNodes, ushort maxKeyLength, uint root = 0, ushort depth = 0)
    {
        var node = new byte[Block];
        node[8] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), 3);
        var h = node.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(h, depth != 0 ? depth : (ushort)(firstLeaf == 0 ? 0 : 1));
        BinaryPrimitives.WriteUInt32BigEndian(h[2..], root != 0 ? root : firstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(h[6..], (uint)records);
        BinaryPrimitives.WriteUInt32BigEndian(h[10..], firstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(h[14..], lastLeaf);
        BinaryPrimitives.WriteUInt16BigEndian(h[18..], Block);
        BinaryPrimitives.WriteUInt16BigEndian(h[20..], maxKeyLength);
        BinaryPrimitives.WriteUInt32BigEndian(h[22..], nodes);
        BinaryPrimitives.WriteUInt32BigEndian(h[26..], nodes - (uint)usedNodes);
        const int mapStart = 14 + 106 + 128;
        const int mapLength = Block - mapStart - 8;
        for (var i = 0; i < usedNodes; i++)
            node[mapStart + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 4), 14 + 106);
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 6), mapStart);
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(Block - 8), checked((ushort)(mapStart + mapLength)));
        return node;
    }

    private static int FindFirstFileRecord(byte[] leaf)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(leaf.AsSpan(10));
        for (var i = 0; i < count; i++)
        {
            int start = BinaryPrimitives.ReadUInt16BigEndian(leaf.AsSpan(Block - 2 * (i + 1)));
            var dataStart = start + 1 + leaf[start];
            if ((dataStart & 1) != 0) dataStart++;
            if (leaf[dataStart] == 2) return dataStart;
        }
        return -1;
    }
}
