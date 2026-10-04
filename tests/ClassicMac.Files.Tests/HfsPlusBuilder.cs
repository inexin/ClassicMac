using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// A sound HFS Plus volume for the First Aid tests, laid out by TN1150: 4 KiB blocks and nodes; block 0 (boot blocks and
// the volume header), the allocation file, the extents and catalog trees, then the files' forks (a free block between
// fragments, so more than eight make overflow records), free space, and the last block with the alternate header.
internal sealed class HfsPlusBuilder
{
    public const int Block = 4096;
    public const uint Root = 2;
    private const int ExtentsNodes = 8, CatalogNodes = 32;

    private readonly List<(uint Parent, string Name, uint Id)> folders = [];
    private readonly List<(uint Parent, string Name, uint Id, byte[] Data, byte[] Resource, int Fragments, FourCC Type, FourCC Creator)> files = [];
    private uint nextId = 16;

    /// <summary>Free blocks left after the files (before the last block).</summary>
    public int FreeBlocks { get; init; } = 64;

    public uint Folder(uint parent, string name)
    {
        folders.Add((parent, name, nextId));
        return nextId++;
    }

    public uint File(uint parent, string name, byte[] data, byte[] resource, int fragments = 1, string type = "TEXT", string creator = "ttxt")
    {
        files.Add((parent, name, nextId, data, resource, fragments, FourCC.FromString(type), FourCC.FromString(creator)));
        return nextId++;
    }

    internal static byte[] CatalogKey(uint parent, string name)
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(6 + 2 * name.Length);
        writer.WriteUInt32(parent);
        writer.WriteUInt16(name.Length);
        foreach (char c in name)
        {
            writer.WriteUInt16(c);
        }

        return writer.ToArray();
    }

    internal static byte[] ExtentsKey(byte fork, uint fileId, uint startBlock)
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(10);
        writer.WriteByte(fork);
        writer.WriteByte(0);
        writer.WriteUInt32(fileId);
        writer.WriteUInt32(startBlock);
        return writer.ToArray();
    }

    public byte[] Build(string volumeName)
    {
        // Blocks: 0 the header, then the allocation file (one block holds 32,768 bits), the trees, then the forks.
        uint block = 2;                                                            // block 1: the allocation file
        var extentsStart = block;
        block += ExtentsNodes;
        var catalogStart = block;
        block += CatalogNodes;
        var forks = new Dictionary<(uint Id, byte Fork), List<(uint Start, uint Count)>>();
        foreach (var file in files)
        {
            forks[(file.Id, 0)] = Place(ref block, Blocks(file.Data), file.Fragments);
            forks[(file.Id, 0xFF)] = Place(ref block, Blocks(file.Resource), file.Fragments);
        }

        uint totalBlocks = block + (uint)FreeBlocks + 1;
        var image = new byte[totalBlocks * (long)Block];
        foreach (var file in files)
        {
            Copy(image, file.Data, forks[(file.Id, 0)]);
            Copy(image, file.Resource, forks[(file.Id, 0xFF)]);
        }

        // The extents tree: each fork's extents after its first eight, eight to a record.
        var overflow = new List<(byte[] Key, byte[] Data)>();
        foreach (var ((id, fork), extents) in forks.OrderBy(f => f.Key.Fork).ThenBy(f => f.Key.Id))
        {
            uint before = (uint)extents.Take(8).Sum(e => e.Count);
            for (var i = 8; i < extents.Count; i += 8)
            {
                overflow.Add((ExtentsKey(fork, id, before), ExtentRecord(extents.Skip(i).Take(8))));
                before += (uint)extents.Skip(i).Take(8).Sum(e => e.Count);
            }
        }

        var extentsTree = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Extents, overflow, Block, ExtentsNodes, ExtentsNodes * Block);
        extentsTree.CopyTo(image, extentsStart * Block);
        var catalog = HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Catalog, CatalogRecords(volumeName, forks), Block, CatalogNodes, CatalogNodes * Block);
        catalog.CopyTo(image, catalogStart * Block);

        // The allocation file: every block before the free space, and the last.
        var used = new HashSet<uint>(Enumerable.Range(0, (int)block).Select(b => (uint)b)) { totalBlocks - 1 };
        foreach (var gap in forks.Values.SelectMany(GapBlocks))
        {
            used.Remove(gap);
        }

        foreach (uint b in used)
        {
            image[Block + b / 8] |= (byte)(0x80 >> (int)(b % 8));
        }

        var header = Header(totalBlocks, totalBlocks - (uint)used.Count, extentsStart, catalogStart);
        header.CopyTo(image, 1024);
        header.CopyTo(image, image.Length - 1024);
        return image;
    }

    private byte[] Header(uint totalBlocks, uint freeBlocks, uint extentsStart, uint catalogStart)
    {
        var header = new byte[512];
        var w = new BigEndianWriter(header);
        w.WriteUInt16At(0, (ushort)0x482B);                                        // 'H+'
        w.WriteUInt16At(2, (ushort)4);
        w.WriteUInt32At(4, 0x100u);                                                // unmounted cleanly
        w.WriteUInt32At(8, 0x382E3130u);                                           // '8.10': Mac OS 8.1 to 9
        w.WriteUInt32At(32, files.Count);
        w.WriteUInt32At(36, folders.Count);
        w.WriteUInt32At(40, Block);
        w.WriteUInt32At(44, totalBlocks);
        w.WriteUInt32At(48, freeBlocks);
        w.WriteUInt32At(56, Block);
        w.WriteUInt32At(60, Block);
        w.WriteUInt32At(64, nextId);
        w.WriteUInt64At(72, 1ul);                                                  // Mac OS Roman
        ForkData(w, 112, (totalBlocks + 7) / 8, [(1, 1)]);
        ForkData(w, 192, ExtentsNodes * Block, [(extentsStart, ExtentsNodes)]);
        ForkData(w, 272, CatalogNodes * Block, [(catalogStart, CatalogNodes)]);
        return header;
    }

    private List<(byte[] Key, byte[] Data)> CatalogRecords(string volumeName, Dictionary<(uint Id, byte Fork), List<(uint Start, uint Count)>> forks)
    {
        var records = new List<(byte[] Key, byte[] Data)>
        {
            (CatalogKey(1, volumeName), FolderRecord(Root, folders.Count(f => f.Parent == Root) + files.Count(f => f.Parent == Root))),
            (CatalogKey(Root, ""), Thread(3, 1, volumeName)),
        };
        foreach (var (parent, name, id) in folders)
        {
            int valence = folders.Count(f => f.Parent == id) + files.Count(f => f.Parent == id);
            records.Add((CatalogKey(parent, name), FolderRecord(id, valence)));
            records.Add((CatalogKey(id, ""), Thread(3, parent, name)));
        }

        foreach (var file in files)
        {
            var data = new byte[248];
            var w = new BigEndianWriter(data);
            w.WriteUInt16At(0, (ushort)2);
            w.WriteUInt16At(2, (ushort)0x0002);                                    // its thread exists
            w.WriteUInt32At(8, file.Id);
            w.WriteFourCCAt(48, file.Type);
            w.WriteFourCCAt(52, file.Creator);
            ForkData(w, 88, file.Data.Length, forks[(file.Id, 0)]);
            ForkData(w, 168, file.Resource.Length, forks[(file.Id, 0xFF)]);
            records.Add((CatalogKey(file.Parent, file.Name), data));
            records.Add((CatalogKey(file.Id, ""), Thread(4, file.Parent, file.Name)));
        }

        records.Sort((a, b) => HfsPlusBTree.CompareCatalogKeys(a.Key, b.Key, caseFolding: true));
        return records;
    }

    private static byte[] FolderRecord(uint id, int valence)
    {
        var data = new byte[88];
        var w = new BigEndianWriter(data);
        w.WriteUInt16At(0, (ushort)1);
        w.WriteUInt32At(4, valence);
        w.WriteUInt32At(8, id);
        return data;
    }

    private static byte[] Thread(ushort type, uint parent, string name)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(type);
        w.WriteUInt16((ushort)0);
        w.WriteUInt32(parent);
        w.WriteUInt16(name.Length);
        foreach (char c in name)
        {
            w.WriteUInt16(c);
        }

        return w.ToArray();
    }

    // An HFSPlusForkData at offset: logical size, clump size, total blocks and the first eight extents.
    private static void ForkData(BigEndianWriter w, int offset, long logicalSize, List<(uint Start, uint Count)> extents)
    {
        w.WriteUInt64At(offset, (ulong)logicalSize);
        w.WriteUInt32At(offset + 12, extents.Sum(e => (long)e.Count));
        w.WriteBytesAt(offset + 16, ExtentRecord(extents.Take(8)));
    }

    private static byte[] ExtentRecord(IEnumerable<(uint Start, uint Count)> extents)
    {
        var record = new byte[64];
        var w = new BigEndianWriter(record);
        var at = 0;
        foreach (var (start, count) in extents)
        {
            w.WriteUInt32At(at, start);
            w.WriteUInt32At(at + 4, count);
            at += 8;
        }

        return record;
    }

    private static uint Blocks(byte[] fork) => (uint)((fork.Length + Block - 1) / Block);

    // A fork's blocks from block, in fragments with a free block between them.
    private static List<(uint Start, uint Count)> Place(ref uint block, uint blocks, int fragments)
    {
        var extents = new List<(uint, uint)>();
        uint per = Math.Max(1, (uint)Math.Ceiling(blocks / (double)Math.Max(1, fragments)));
        for (uint left = blocks; left > 0;)
        {
            uint count = Math.Min(per, left);
            extents.Add((block, count));
            left -= count;
            block += count + (fragments > 1 && left > 0 ? 1u : 0u);
        }

        return extents;
    }

    // The free block after each fragment but the last.
    private static IEnumerable<uint> GapBlocks(List<(uint Start, uint Count)> extents) =>
        extents.Count > 1 ? extents.SkipLast(1).Select(e => e.Start + e.Count) : [];

    private static void Copy(byte[] image, byte[] fork, List<(uint Start, uint Count)> extents)
    {
        long at = 0;
        foreach (var (start, count) in extents)
        {
            int length = (int)Math.Min(count * (long)Block, fork.Length - at);
            fork.AsSpan((int)at, length).CopyTo(image.AsSpan((int)(start * (long)Block)));
            at += length;
        }
    }
}
