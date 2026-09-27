using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Files.Tests;

// Builds a small HFS volume byte by byte from Inside Macintosh: Files: boot blocks, MDB at 1024, 512-byte allocation
// blocks from block 4; an extents overflow file (header + one leaf) and a catalog (header + two leaves linked in
// order); forks split into the requested number of extents, one free block apart, with extents past the third in
// overflow records.
internal sealed class HfsBuilder
{
    public const int Block = 512;
    public const int FirstAllocationBlock = 4;
    public const uint Root = 2;

    private readonly List<(uint Id, uint Parent, string Name)> folders = [];
    private readonly List<(uint Parent, string Name, string Type, string Creator, byte[] Data, byte[] Resource, int Fragments, uint Id)> files = [];
    private uint nextId = 16;

    public uint Folder(uint parent, string name)
    {
        var id = nextId++;
        folders.Add((id, parent, name));
        return id;
    }

    public void File(uint parent, string name, byte[] data, byte[] resource, string type = "TEXT", string creator = "ttxt", int fragments = 1) =>
        files.Add((parent, name, type, creator, data, resource, fragments, nextId++));

    // Where the first file's first data extent starts, for tests that patch it.
    public int FirstFileRecordOffset { get; private set; }

    public byte[] Build(string volumeName)
    {
        var allocation = new List<byte[]>(); // allocation blocks from 0
        var overflow = new List<byte[]>(); // extents overflow leaf records
        allocation.Add(new byte[Block]); // extents file: header node
        allocation.Add(new byte[Block]); // extents file: leaf node
        var catalogStart = allocation.Count;
        allocation.AddRange([new byte[Block], new byte[Block], new byte[Block]]); // catalog: header + 2 leaves

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
                }
                allocation.Add(new byte[Block]); // a gap, so the extents are not contiguous
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
        records.Add((1, volumeName, FolderRecord(Root)));
        records.Add((Root, "", ThreadRecord(1, volumeName)));
        foreach (var (id, parent, name) in folders) records.Add((parent, name, FolderRecord(id)));
        foreach (var f in files)
        {
            var r = new byte[102];
            r[0] = 2;
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
        }

        // Catalog: records in two leaves, linked in order.
        var keyed = records.Select(r => Keyed(r.Parent, r.Name, r.Record)).ToList();
        var half = (keyed.Count + 1) / 2;
        allocation[catalogStart + 1] = Leaf(keyed.Take(half).ToList(), forward: 2, backward: 0);
        allocation[catalogStart + 2] = Leaf(keyed.Skip(half).ToList(), forward: 0, backward: 1);
        allocation[catalogStart] = Header(firstLeaf: 1, lastLeaf: 2, nodes: 3, records: keyed.Count);
        var leafWithFile = FindFirstFileRecord(allocation[catalogStart + 1]) >= 0 ? 1 : 2;
        var fileKeyOffset = FindFirstFileRecord(allocation[catalogStart + leafWithFile]);
        FirstFileRecordOffset = fileKeyOffset < 0 ? -1 : (FirstAllocationBlock + catalogStart + leafWithFile) * Block + fileKeyOffset;

        // Extents file: overflow records in one leaf.
        allocation[0] = Header(firstLeaf: overflow.Count > 0 ? 1u : 0, lastLeaf: overflow.Count > 0 ? 1u : 0, nodes: 2, records: overflow.Count);
        allocation[1] = overflow.Count > 0 ? Leaf(overflow, 0, 0) : new byte[Block];

        var image = new byte[(FirstAllocationBlock + allocation.Count) * Block];
        for (var i = 0; i < allocation.Count; i++) allocation[i].CopyTo(image, (FirstAllocationBlock + i) * Block);

        var mdb = image.AsSpan(1024);
        BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x02..], 2_526_595_200);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x06..], 2_526_595_200);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x0C..], (ushort)files.Count(f => f.Parent == Root));
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x0E..], 3);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x12..], (ushort)allocation.Count);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x14..], Block);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[0x1C..], FirstAllocationBlock);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x1E..], nextId);
        mdb[0x24] = (byte)volumeName.Length;
        Encoding.ASCII.GetBytes(volumeName).CopyTo(mdb[0x25..]);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x54..], (uint)files.Count);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x58..], (uint)folders.Count);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x82..], 2 * Block);
        ExtentRecord([(0, 2)]).CopyTo(mdb[0x86..]);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[0x92..], 3 * Block);
        ExtentRecord([(catalogStart, 3)]).CopyTo(mdb[0x96..]);
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

    private static byte[] FolderRecord(uint id)
    {
        var r = new byte[70];
        r[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(6), id);
        return r;
    }

    private static byte[] ThreadRecord(uint parent, string name)
    {
        var r = new byte[46];
        r[0] = 3;
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
        return [.. key, .. record];
    }

    private static byte[] Leaf(List<byte[]> records, uint forward, uint backward)
    {
        var node = new byte[Block];
        BinaryPrimitives.WriteUInt32BigEndian(node, forward);
        BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4), backward);
        node[8] = 0xFF;
        node[9] = 1;
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

    private static byte[] Header(uint firstLeaf, uint lastLeaf, uint nodes, int records)
    {
        var node = new byte[Block];
        node[8] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), 3);
        var h = node.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(h, (ushort)(firstLeaf == 0 ? 0 : 1));
        BinaryPrimitives.WriteUInt32BigEndian(h[2..], firstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(h[6..], (uint)records);
        BinaryPrimitives.WriteUInt32BigEndian(h[10..], firstLeaf);
        BinaryPrimitives.WriteUInt32BigEndian(h[14..], lastLeaf);
        BinaryPrimitives.WriteUInt16BigEndian(h[18..], Block);
        BinaryPrimitives.WriteUInt32BigEndian(h[22..], nodes);
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
