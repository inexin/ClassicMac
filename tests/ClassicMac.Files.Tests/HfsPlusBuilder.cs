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
    private readonly Dictionary<uint, (ushort Flags, ushort FinderFlags, uint Special)> fileLinks = [];
    private readonly Dictionary<uint, (ushort Flags, uint Special)> folderLinks = [];
    private uint nextId = 16;
    private uint? privateFiles, privateFolders;

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

    /// <summary>
    /// A file hard link set (TN1150): the indirect file iNode&lt;n&gt; in the private folder, its link count the links, and
    /// each link an hlnk/hfs+ file whose special is n. Returns the indirect file's CNID, which is n.
    /// </summary>
    public uint HardLinks(byte[] data, params (uint Parent, string Name)[] links)
    {
        privateFiles ??= Folder(Root, "\0\0\0\0HFS+ Private Data");
        uint inode = File(privateFiles.Value, $"iNode{nextId}", data, []);
        fileLinks[inode] = (0, 0, (uint)links.Length);
        foreach (var (parent, name) in links)
        {
            fileLinks[File(parent, name, [], [], type: "hlnk", creator: "hfs+")] = (0, 0, inode);
        }

        return inode;
    }

    /// <summary>
    /// A directory hard link set: the folder dir_&lt;id&gt; in the private directory folder, its link count the aliases,
    /// and each alias an alis/MACS alias file with the link-chain flag whose special is the folder's CNID.
    /// </summary>
    public uint DirectoryHardLinks(params (uint Parent, string Name)[] links)
    {
        privateFolders ??= Folder(Root, ".HFS+ Private Directory Data\r");
        uint inode = Folder(privateFolders.Value, $"dir_{nextId}");
        folderLinks[inode] = (0x0020, (uint)links.Length);
        foreach (var (parent, name) in links)
        {
            fileLinks[File(parent, name, [], [], type: "alis", creator: "MACS")] = (0x0020, 0x8000, inode);
        }

        return inode;
    }

    /// <summary>
    /// A journal of this many blocks (0: none): the root's .journal_info_block and .journal files, the volume marked
    /// journaled, the journal empty (TN1150; 512-byte journal sectors, 4 KB block list headers).
    /// </summary>
    public int JournalBlocks { get; init; }

    public const int JournalSector = 512, BlockListHeader = 4096;

    private uint? journalInfoFile, journalFile;
    private const int AttributeNodes = 8;
    private readonly List<(uint FileId, string Name, byte[] Data, bool Fork)> attributes = [];

    /// <summary>An extended attribute of a file or folder: inline data, or (fork) data in its own blocks.</summary>
    public void Attribute(uint fileId, string name, byte[] data, bool fork = false) => attributes.Add((fileId, name, data, fork));

    internal static byte[] AttributeKey(uint fileId, string name, uint startBlock = 0)
    {
        var writer = new BigEndianWriter();
        writer.WriteUInt16(12 + 2 * name.Length);
        writer.WriteUInt16((ushort)0);
        writer.WriteUInt32(fileId);
        writer.WriteUInt32(startBlock);
        writer.WriteUInt16(name.Length);
        foreach (char c in name)
        {
            writer.WriteUInt16(c);
        }

        return writer.ToArray();
    }

    public byte[] Build(string volumeName)
    {
        if (JournalBlocks > 0 && journalFile is null)
        {
            // As Mac OS X makes them: type 'jrnl', creator 'hfs+', the info block's file a whole block.
            journalInfoFile = File(Root, ".journal_info_block", new byte[Block], [], type: "jrnl", creator: "hfs+");
            journalFile = File(Root, ".journal", new byte[JournalBlocks * Block], [], type: "jrnl", creator: "hfs+");
        }

        // Blocks: 0 the header, then the allocation file (one block holds 32,768 bits), the trees, then the forks.
        uint block = 2;                                                          // block 1: the allocation file
        var extentsStart = block;
        block += ExtentsNodes;
        var catalogStart = block;
        block += CatalogNodes;
        uint attributesStart = block;
        block += attributes.Count > 0 ? (uint)AttributeNodes : 0;
        var forks = new Dictionary<(uint Id, byte Fork), List<(uint Start, uint Count)>>();
        var attributeForks = attributes.Where(a => a.Fork).ToDictionary(a => (a.FileId, a.Name), a => Place(ref block, Blocks(a.Data), 1));
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

        foreach (var (fileId, name, data, _) in attributes.Where(a => a.Fork))
        {
            Copy(image, data, attributeForks[(fileId, name)]);
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
        if (attributes.Count > 0)
        {
            var records = attributes.Select(a => (AttributeKey(a.FileId, a.Name), a.Fork ? ForkAttribute(a.Data.Length, attributeForks[(a.FileId, a.Name)]) : InlineAttribute(a.Data)))
                .OrderBy(r => r.Item1, Comparer<byte[]>.Create((x, y) => HfsPlusAttributes.CompareAttributeKeys(x, y))).ToList();
            HfsPlusBTreeWriter.Build(HfsPlusBTreeWriter.Attributes, records, Block, AttributeNodes, AttributeNodes * Block).CopyTo(image, attributesStart * Block);
        }

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
        if (attributes.Count > 0)
        {
            ForkData(new BigEndianWriter(header), 352, AttributeNodes * Block, [(attributesStart, AttributeNodes)]);
        }
        if (journalFile is { } journal)
        {
            uint info = forks[(journalInfoFile!.Value, 0)][0].Start, start = forks[(journal, 0)][0].Start;
            var w = new BigEndianWriter(header);
            w.WriteUInt32At(4, 0x100u | 0x2000u);                                  // journaled
            w.WriteUInt32At(12, info);
            var jib = new BigEndianWriter(image);
            jib.WriteUInt32At((int)(info * Block), 1u);                            // in this file system
            jib.WriteUInt64At((int)(info * Block) + 36, start * (ulong)Block);
            jib.WriteUInt64At((int)(info * Block) + 44, (ulong)(JournalBlocks * Block));
            WriteJournalHeader(image, (int)(start * Block), JournalBlocks * (long)Block, JournalSector, JournalSector, LittleEndianJournal);
        }

        header.CopyTo(image, 1024);
        header.CopyTo(image, image.Length - 1024);
        return image;
    }

    /// <summary>The journal in the byte order of an Intel Mac (TN1150: the order of the Mac that wrote it).</summary>
    public bool LittleEndianJournal { get; init; }

    /// <summary>A journal header at <paramref name="at"/>, its checksum over the 44-byte header (TN1150).</summary>
    public static void WriteJournalHeader(byte[] image, int at, long size, long start, long end, bool little = false)
    {
        image.AsSpan(at, JournalSector).Clear();
        var h = image.AsSpan(at);
        Put32(h, 0, 0x4A4E4C78u, little);
        Put32(h, 4, 0x12345678u, little);
        Put64(h, 8, (ulong)start, little);
        Put64(h, 16, (ulong)end, little);
        Put64(h, 24, (ulong)size, little);
        Put32(h, 32, BlockListHeader, little);
        Put32(h, 40, JournalSector, little);
        Put32(h, 36, Checksum(image.AsSpan(at, 44), 36), little);
    }

    public static void Put32(Span<byte> bytes, int at, uint value, bool little)
    {
        if (little)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[at..], value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes[at..], value);
        }
    }

    public static void Put64(Span<byte> bytes, int at, ulong value, bool little)
    {
        if (little)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[at..], value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes[at..], value);
        }
    }

    /// <summary>TN1150's calc_checksum over bytes, the 4-byte checksum field at <paramref name="field"/> taken as zero.</summary>
    public static uint Checksum(ReadOnlySpan<byte> bytes, int field)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            byte value = i >= field && i < field + 4 ? (byte)0 : bytes[i];
            sum = unchecked((sum << 8) ^ (sum + value));
        }

        return ~sum;
    }

    /// <summary>
    /// The volume in an HFS wrapper (TN1150): the wrapper's MDB, with 512-byte blocks from sector 2 and its bitmap at
    /// sector 3, names the embedded volume as its blocks 4 onward; its alternate MDB follows the volume.
    /// </summary>
    public byte[] BuildWrapped(string volumeName)
    {
        var embedded = Build(volumeName);
        const int EmbeddedOffset = 6 * 512;
        var wrapper = new byte[EmbeddedOffset + embedded.Length + 1024];
        embedded.CopyTo(wrapper, EmbeddedOffset);
        var mdb = new BigEndianWriter(wrapper);
        mdb.WriteUInt16At(1024, (ushort)0x4244);
        mdb.WriteUInt16At(1024 + 0x0E, (ushort)3);
        mdb.WriteUInt16At(1024 + 0x12, wrapper.Length / 512 - 4);
        mdb.WriteUInt32At(1024 + 0x14, 512u);
        mdb.WriteUInt16At(1024 + 0x1C, (ushort)2);
        mdb.WriteUInt16At(1024 + 0x7C, (ushort)0x482B);
        mdb.WriteUInt16At(1024 + 0x7E, (ushort)4);
        mdb.WriteUInt16At(1024 + 0x80, embedded.Length / 512);
        for (int block = 4; block < 4 + embedded.Length / 512; block++)
        {
            wrapper[3 * 512 + block / 8] |= (byte)(0x80 >> (block % 8));
        }

        wrapper.AsSpan(1024, 512).CopyTo(wrapper.AsSpan(wrapper.Length - 1024));
        return wrapper;
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
            var folder = FolderRecord(id, valence);
            if (folderLinks.TryGetValue(id, out var link))
            {
                var fw = new BigEndianWriter(folder);
                fw.WriteUInt16At(2, link.Flags);
                fw.WriteUInt32At(44, link.Special);
            }

            if (HasAttributes(id))
            {
                folder[3] |= 0x04;
            }

            records.Add((CatalogKey(parent, name), folder));
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
            if (fileLinks.TryGetValue(file.Id, out var link))
            {
                w.WriteUInt16At(2, (ushort)(0x0002 | link.Flags));
                w.WriteUInt32At(44, link.Special);
                w.WriteUInt16At(56, link.FinderFlags);
            }

            if (HasAttributes(file.Id))
            {
                data[3] |= 0x04;
            }

            ForkData(w, 88, file.Data.Length, forks[(file.Id, 0)]);
            ForkData(w, 168, file.Resource.Length, forks[(file.Id, 0xFF)]);
            records.Add((CatalogKey(file.Parent, file.Name), data));
            records.Add((CatalogKey(file.Id, ""), Thread(4, file.Parent, file.Name)));
        }

        records.Sort((a, b) => HfsPlusBTree.CompareCatalogKeys(a.Key, b.Key, caseFolding: true));
        return records;
    }

    // An inline attribute record: type $10, 8 reserved bytes, the size, the data, padded to even.
    private static byte[] InlineAttribute(byte[] data)
    {
        var record = new byte[(16 + data.Length + 1) & ~1];
        var w = new BigEndianWriter(record);
        w.WriteUInt32At(0, 0x10u);
        w.WriteUInt32At(12, data.Length);
        data.CopyTo(record, 16);
        return record;
    }

    // A fork-data attribute record: type $20, a reserved word, the fork data.
    private static byte[] ForkAttribute(long length, List<(uint Start, uint Count)> extents)
    {
        var record = new byte[88];
        var w = new BigEndianWriter(record);
        w.WriteUInt32At(0, 0x20u);
        ForkData(w, 8, length, extents);
        return record;
    }

    private bool HasAttributes(uint id) => attributes.Exists(a => a.FileId == id);

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
