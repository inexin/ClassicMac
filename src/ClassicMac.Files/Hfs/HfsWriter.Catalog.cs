using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

public static partial class HfsWriter
{
    private static readonly ushort[] CatalogNameWeights = BuildCatalogNameWeights();

    /// <summary>Creates a file and both of its forks in a plain HFS volume, returning a new image.</summary>
    public static byte[] CreateFile(ForkData image, string macPath, ReadOnlyMemory<byte> data,
        ReadOnlyMemory<byte> resource, FinderInfo finderInfo, MacDate? created = null, MacDate? modified = null)
    {
        ArgumentNullException.ThrowIfNull(finderInfo);
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        EnsureAbsent(state.Records, parent, name);
        uint id = U32(new BigEndianReader(state.Result), MdbOffset + 0x1E);
        if (id < 16 || id == uint.MaxValue)
        {
            throw new InvalidDataException("The HFS volume has no available catalog ID.");
        }

        var record = new byte[102];
        record[0] = 2;
        byte[] finderBytes = finderInfo.ToArray();
        finderBytes.AsSpan(0, 16).CopyTo(record.AsSpan(4));
        finderBytes.AsSpan(16, 16).CopyTo(record.AsSpan(56));
        var writer = new BigEndianWriter(record);
        writer.WriteUInt32At(20, id);
        uint now = MacDate.FromDateTime(DateTime.Now).Seconds;
        writer.WriteUInt32At(44, created?.Seconds ?? now);
        writer.WriteUInt32At(48, modified?.Seconds ?? now);
        state.Records.Add((CatalogKey(parent, name), record));
        AdjustParentValence(state.Records, parent, 1);
        AddCount(state.Result, 0x1E, 1);
        AddCount(state.Result, 0x54, 1);
        if (parent == 2)
        {
            AddShortCount(state.Result, 0x0C, 1);
        }

        byte[] result = CommitCatalog(state);
        if (!data.IsEmpty)
        {
            result = ReplaceFork(ForkData.FromBytes(result), macPath, HfsFork.Data, data);
        }

        if (!resource.IsEmpty)
        {
            result = ReplaceFork(ForkData.FromBytes(result), macPath, HfsFork.Resource, resource);
        }

        if (modified is not null && (!data.IsEmpty || !resource.IsEmpty))
        {
            // Fork replacement stamps its edit time. Restore the caller's file date after both forks are written.
            var dated = OpenCatalog(ForkData.FromBytes(result));
            var entry = FindCatalogRecord(dated.Records, parent, name);
            new BigEndianWriter(entry.Data).WriteUInt32At(48, modified.Value.Seconds);
            result = CommitCatalog(dated);
        }
        return result;
    }

    /// <summary>Deletes an HFS file and releases the blocks in both forks, returning a new image.</summary>
    public static byte[] DeleteFile(ForkData image, string macPath)
    {
        var initial = OpenCatalog(image);
        var (parent, name) = ResolveParent(initial.Records, macPath);
        var existing = FindCatalogRecord(initial.Records, parent, name);
        if (existing.Data is null || existing.Data.Length < 102 || existing.Data[0] != 2)
        {
            throw new InvalidDataException("The HFS file to delete was not found.");
        }

        if ((existing.Data[2] & 1) != 0)
        {
            throw new InvalidDataException("The HFS file is locked.");
        }

        return DeleteTree(initial, parent, macPath, existing);
    }

    /// <summary>Creates an HFS folder and its catalog thread, returning a new image.</summary>
    public static byte[] CreateFolder(ForkData image, string macPath, MacDate? created = null, MacDate? modified = null)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        EnsureAbsent(state.Records, parent, name);
        uint id = U32(new BigEndianReader(state.Result), MdbOffset + 0x1E);
        if (id < 16 || id == uint.MaxValue)
        {
            throw new InvalidDataException("The HFS volume has no available catalog ID.");
        }

        uint now = MacDate.FromDateTime(DateTime.Now).Seconds;

        var folder = new byte[70];
        folder[0] = 1;
        var folderWriter = new BigEndianWriter(folder);
        folderWriter.WriteUInt32At(6, id);
        folderWriter.WriteUInt32At(10, created?.Seconds ?? now);
        folderWriter.WriteUInt32At(14, modified?.Seconds ?? now);
        var thread = new byte[46];
        thread[0] = 3;
        new BigEndianWriter(thread).WriteUInt32At(10, parent);
        byte[] nameBytes = MacRoman.Encode(name);
        thread[14] = checked((byte)nameBytes.Length);
        nameBytes.CopyTo(thread, 15);
        state.Records.Add((CatalogKey(parent, name), folder));
        state.Records.Add((CatalogKey(id, ""), thread));
        AdjustParentValence(state.Records, parent, 1);
        AddCount(state.Result, 0x1E, 1);
        AddCount(state.Result, 0x58, 1);
        if (parent == 2)
        {
            AddShortCount(state.Result, 0x52, 1);
        }

        return CommitCatalog(state);
    }

    /// <summary>Deletes an empty HFS folder and its catalog thread, returning a new image.</summary>
    public static byte[] DeleteFolder(ForkData image, string macPath)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var folder = FindCatalogRecord(state.Records, parent, name);
        if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
        {
            throw new InvalidDataException("The HFS folder to delete was not found.");
        }

        uint id = U32(new BigEndianReader(folder.Data), 6);
        if (state.Records.Any(record =>
                U32(new BigEndianReader(record.Key), 2) == id && DecodeName(record.Key).Length != 0))
        {
            throw new InvalidDataException("A nonempty HFS folder cannot be deleted.");
        }

        var thread = FindCatalogRecord(state.Records, id, "");
        if (thread.Data is null || thread.Data[0] != 3)
        {
            throw new InvalidDataException("The HFS folder thread is missing.");
        }

        state.Records.Remove(folder);
        state.Records.Remove(thread);
        AdjustParentValence(state.Records, parent, -1);
        AddCount(state.Result, 0x58, -1);
        if (parent == 2)
        {
            AddShortCount(state.Result, 0x52, -1);
        }

        return CommitCatalog(state);
    }

    /// <summary>
    /// Renames a file or folder in its folder, returning a new image: the catalog record moves to its new key, and its
    /// thread record (a folder's always, a file's when it has one) takes the new name. Its ID, forks and Finder info stay.
    /// </summary>
    public static byte[] Rename(ForkData image, string macPath, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        ValidateCatalogName(newName);
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var item = FindCatalogRecord(state.Records, parent, name);
        if (item.Data is null || item.Data[0] is not (1 or 2))
        {
            throw new InvalidDataException($"The HFS item '{name}' was not found.");
        }

        var newKey = CatalogKey(parent, newName);
        var taken = FindCatalogRecord(state.Records, parent, newName);
        if (taken.Data is not null && !ReferenceEquals(taken.Data, item.Data))
        {
            throw new InvalidDataException("An HFS catalog item with that name already exists.");
        }

        state.Records.Remove(item);
        state.Records.Add((newKey, item.Data));
        var isFolder = item.Data[0] == 1;
        uint id = U32(new BigEndianReader(item.Data), isFolder ? 6 : 20);
        var thread = FindCatalogRecord(state.Records, id, "");
        if (isFolder && (thread.Data is null || thread.Data[0] != 3))
        {
            throw new InvalidDataException("The HFS folder thread is missing.");
        }

        if (thread.Data is not null && thread.Data.Length >= 46)
        {
            // The thread's name: a length byte and up to 31 bytes at +14.
            var encoded = MacRoman.Encode(newName);
            thread.Data.AsSpan(14, 32).Clear();
            thread.Data[14] = (byte)encoded.Length;
            encoded.CopyTo(thread.Data, 15);
        }

        return CommitCatalog(state);
    }

    /// <summary>
    /// Moves a file or folder into another folder of its volume (<paramref name="folderPath"/>, empty for the root),
    /// returning a new image, as PBCatMove moves it (hfs.md §3): the record's key takes the folder's ID as its parent,
    /// its thread record (a folder's always, a file's when it has one) records the new parent, and both folders'
    /// valences change, with the MDB's root counts. Its name, ID, forks, Finder info and dates stay. A move into the
    /// folder it is in, onto a name the folder holds, or of a folder into itself or a folder inside it is refused.
    /// </summary>
    public static byte[] Move(ForkData image, string macPath, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var item = FindCatalogRecord(state.Records, parent, name);
        if (item.Data is null || item.Data[0] is not (1 or 2))
        {
            throw new InvalidDataException($"The HFS item '{name}' was not found.");
        }

        uint destination = 2;
        if (folderPath.Length != 0)
        {
            var (folderParent, folderName) = ResolveParent(state.Records, folderPath);
            var folder = FindCatalogRecord(state.Records, folderParent, folderName);
            if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
            {
                throw new InvalidDataException($"The HFS folder '{folderPath}' was not found.");
            }

            destination = U32(new BigEndianReader(folder.Data), 6);
        }

        if (destination == parent)
        {
            throw new InvalidDataException($"The HFS item '{name}' is in that folder already.");
        }

        var isFolder = item.Data[0] == 1;
        uint id = U32(new BigEndianReader(item.Data), isFolder ? 6 : 20);
        if (isFolder)
        {
            // Up from the destination through the folder threads to the root's parent (1): the folder moved must not
            // be on the way (PBCatMove's badMovErr).
            for (uint at = destination; at > 1; at = ThreadParent(state.Records, at))
            {
                if (at == id)
                {
                    throw new InvalidDataException("An HFS folder cannot move into itself or a folder inside it.");
                }
            }
        }

        EnsureAbsent(state.Records, destination, name);
        var thread = FindCatalogRecord(state.Records, id, "");
        if (isFolder && (thread.Data is null || thread.Data[0] != 3))
        {
            throw new InvalidDataException("The HFS folder thread is missing.");
        }

        state.Records.Remove(item);
        state.Records.Add((CatalogKey(destination, name), item.Data));
        if (thread.Data is not null && thread.Data.Length >= 46)
        {
            new BigEndianWriter(thread.Data).WriteUInt32At(10, destination);
        }

        AdjustParentValence(state.Records, parent, -1);
        AdjustParentValence(state.Records, destination, 1);
        int rootCount = isFolder ? 0x52 : 0x0C;
        if (parent == 2)
        {
            AddShortCount(state.Result, rootCount, -1);
        }

        if (destination == 2)
        {
            AddShortCount(state.Result, rootCount, 1);
        }

        return CommitCatalog(state);
    }

    // A folder's parent ID, from its thread record (+10).
    private static uint ThreadParent(List<(byte[] Key, byte[] Data)> records, uint folder)
    {
        var thread = FindCatalogRecord(records, folder, "");
        if (thread.Data is null || thread.Data.Length < 14 || thread.Data[0] != 3)
        {
            throw new InvalidDataException("The HFS folder thread is missing.");
        }

        return U32(new BigEndianReader(thread.Data), 10);
    }

    /// <summary>
    /// Locks or unlocks a file (<c>filFlags</c> bit 0, as PBHSetFLock and PBHRstFLock set it), returning a new image. An
    /// HFS folder has no lock.
    /// </summary>
    public static byte[] SetLocked(ForkData image, string macPath, bool locked)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var file = FindCatalogRecord(state.Records, parent, name);
        if (file.Data is null || file.Data.Length < 102 || file.Data[0] != 2)
        {
            throw new InvalidDataException(file.Data is { } data && data[0] == 1
                ? "An HFS folder cannot be locked; only files have a lock."
                : $"The HFS file '{name}' was not found.");
        }

        file.Data[2] = (byte)(locked ? file.Data[2] | 0x01 : file.Data[2] & ~0x01);
        return CommitCatalog(state);
    }

    /// <summary>
    /// Blesses a folder as the volume's System Folder (hfs.md §3): its ID goes in the MDB's <c>drFndrInfo[0]</c>, where
    /// the boot code looks for the System file. Only a folder holding a System file (type <c>zsys</c>) is blessed.
    /// </summary>
    public static byte[] Bless(ForkData image, string folderPath)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, folderPath);
        var folder = FindCatalogRecord(state.Records, parent, name);
        if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
        {
            throw new InvalidDataException($"The HFS folder '{folderPath}' was not found.");
        }

        uint id = U32(new BigEndianReader(folder.Data), 6);
        var system = FourCC.FromString("zsys");
        if (!state.Records.Any(record => record.Data.Length >= 102 && record.Data[0] == 2 &&
                U32(new BigEndianReader(record.Key), 2) == id && new BigEndianReader(record.Data).ReadFourCCAt(4) == system))
        {
            throw new InvalidDataException($"The HFS folder '{folderPath}' holds no System file (type 'zsys'), so it cannot be blessed.");
        }

        new BigEndianWriter(state.Result).WriteUInt32At(MdbOffset + 0x5C, id);
        return CommitCatalog(state);
    }

    /// <summary>Sets a file's Finder info (its <c>FInfo</c> and <c>FXInfo</c>), returning a new image.</summary>
    public static byte[] SetFinderInfo(ForkData image, string macPath, FinderInfo finderInfo)
    {
        ArgumentNullException.ThrowIfNull(finderInfo);
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var file = FindCatalogRecord(state.Records, parent, name);
        if (file.Data is null || file.Data.Length < 102 || file.Data[0] != 2)
        {
            throw new InvalidDataException($"The HFS file '{name}' was not found.");
        }

        var bytes = finderInfo.ToArray();
        bytes.AsSpan(0, 16).CopyTo(file.Data.AsSpan(4));
        bytes.AsSpan(16, 16).CopyTo(file.Data.AsSpan(56));
        return CommitCatalog(state);
    }

    /// <summary>Sets a folder's Finder flags (<c>DInfo.frFlags</c>), returning a new image.</summary>
    public static byte[] SetFolderFlags(ForkData image, string macPath, FinderFlags flags)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var folder = FindCatalogRecord(state.Records, parent, name);
        if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
        {
            throw new InvalidDataException($"The HFS folder '{name}' was not found.");
        }

        new BigEndianWriter(folder.Data).WriteUInt16At(30, (ushort)flags);
        return CommitCatalog(state);
    }

    /// <summary>
    /// Deletes a file, or a folder: an empty one, or with <paramref name="recursive"/> one and everything in it (files
    /// and folders deepest first). Returns a new image; a failure on the way leaves the given image as it was.
    /// </summary>
    public static byte[] Delete(ForkData image, string macPath, bool recursive)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var item = FindCatalogRecord(state.Records, parent, name);
        if (item.Data is null || item.Data[0] is not (1 or 2))
        {
            throw new InvalidDataException($"The HFS item '{name}' was not found.");
        }

        if (item.Data[0] == 1 && !recursive)
        {
            uint id = U32(new BigEndianReader(item.Data), 6);
            if (state.Records.Any(r => U32(new BigEndianReader(r.Key), 2) == id && r.Key[6] != 0))
            {
                throw new InvalidDataException("A nonempty HFS folder cannot be deleted.");
            }
        }

        return DeleteTree(state, parent, macPath, item);
    }

    // Deletes an item and, for a folder, everything below it, in one pass over the catalog (hfs.md §3): every fork's
    // blocks freed in the bitmap (overflow extents too, and their records removed), the file, folder and thread records
    // removed, the parent's valence, the MDB's counts and its free-block count changed. The result is checked as the
    // writer checks a volume, and every record and allocated block it keeps is compared with the source.
    private static byte[] DeleteTree(CatalogEditState state, uint parent, string macPath, (byte[] Key, byte[] Data) item)
    {
        var before = state.Records.Select(r => (r.Key, Data: r.Data.ToArray())).ToList();
        var children = state.Records.Where(r => r.Data[0] is 1 or 2).ToLookup(r => U32(new BigEndianReader(r.Key), 2));
        var files = new List<(byte[] Key, byte[] Data)>();
        var folders = new List<(byte[] Key, byte[] Data)>();
        var pending = new Queue<((byte[] Key, byte[] Data) Record, string Path)>([(item, macPath)]);
        while (pending.TryDequeue(out var next))
        {
            var (record, path) = next;
            if (record.Data[0] == 2)
            {
                if ((record.Data[2] & 1) != 0)
                {
                    throw new InvalidDataException($"The HFS file '{path}' is locked.");
                }

                files.Add(record);
                continue;
            }

            folders.Add(record);
            foreach (var child in children[U32(new BigEndianReader(record.Data), 6)])
            {
                pending.Enqueue((child, path + ":" + DecodeName(child.Key)));
            }
        }

        var removed = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        var fileIds = new HashSet<uint>();
        var overflow = LeafRecords(state.ExtentsTree).ToList();
        long released = 0;
        foreach (var file in files)
        {
            var data = new BigEndianReader(file.Data);
            uint id = U32(data, 20);
            fileIds.Add(id);
            foreach (var (offset, fork) in new[] { (74, (byte)0x00), (86, (byte)0xFF) })
            {
                var extents = ParseExtents(data, offset);
                extents.AddRange(overflow.Where(r => r.Key[1] == fork && U32(new BigEndianReader(r.Key), 2) == id)
                    .OrderBy(r => U16(new BigEndianReader(r.Key), 6)).SelectMany(r => ParseExtents(new BigEndianReader(r.Data))));
                foreach (var (start, count) in extents)
                {
                    for (var block = start; block < start + count; block++)
                    {
                        if (block >= state.BlockCount || !IsAllocated(state.Bitmap, (ushort)block))
                        {
                            throw new InvalidDataException($"The HFS file '{DecodeName(file.Key)}' has a block that is free or outside the volume.");
                        }

                        SetBitmap(state.Bitmap, (ushort)block, false);
                        released++;
                    }
                }
            }

            removed.Add(file.Data);
            var thread = FindCatalogRecord(state.Records, id, "");
            if ((file.Data[2] & 2) != 0 && (thread.Data is null || thread.Data[0] != 4))
            {
                throw new InvalidDataException("The HFS file thread is missing.");
            }

            if (thread.Data is { } fileThread)
            {
                if (fileThread[0] != 4)
                {
                    throw new InvalidDataException("The HFS file has an invalid thread record.");
                }

                removed.Add(fileThread);
            }
        }

        foreach (var folder in folders)
        {
            removed.Add(folder.Data);
            var thread = FindCatalogRecord(state.Records, U32(new BigEndianReader(folder.Data), 6), "");
            if (thread.Data is null || thread.Data[0] != 3)
            {
                throw new InvalidDataException("The HFS folder thread is missing.");
            }

            removed.Add(thread.Data);
        }

        var removedKeys = state.Records.Where(r => removed.Contains(r.Data)).Select(r => Convert.ToHexString(r.Key)).ToHashSet();
        state.Records.RemoveAll(r => removed.Contains(r.Data));
        var kept = overflow.Where(r => !(r.Key[1] is 0x00 or 0xFF && fileIds.Contains(U32(new BigEndianReader(r.Key), 2)))).ToList();
        if (kept.Count != overflow.Count)
        {
            RebuildBTree(state.ExtentsTree, kept.Select(r => (r.Key, r.Data)).ToList(), validateExtents: true);
            state.ExtentsTreeChanged = true;
        }

        AdjustParentValence(state.Records, parent, -1);
        AddCount(state.Result, 0x54, -files.Count);
        AddCount(state.Result, 0x58, -folders.Count);
        if (parent == 2)
        {
            AddShortCount(state.Result, item.Data[0] == 1 ? 0x52 : 0x0C, -1);
        }

        AddShortCount(state.Result, 0x22, checked((int)released));
        state.Bitmap.CopyTo(state.Result, state.BitmapOffset);
        var result = CommitCatalog(state);
        VerifyKept(state.Source, result, before, removedKeys, parent, overflow.Select(r => (r.Key, r.Data)).ToList(), fileIds, []);
        return result;
    }

    // After an edit: the result opens as the writer opens a volume (its trees, counts, bitmap and extents agree); every
    // catalog record kept is byte for byte the source's (except the records in removedKeys, and the parent folder's
    // valence); every extents overflow record is the source's except those of the files in overflowFiles and of the
    // B-tree files; and every allocated block outside the catalog and extents files and skipBlocks is the source's. So no
    // file kept has changed, without reading any fork.
    private static void VerifyKept(byte[] source, byte[] result, List<(byte[] Key, byte[] Data)> before, HashSet<string> removedKeys, uint parent,
        List<(byte[] Key, byte[] Data)> overflowBefore, HashSet<uint> overflowFiles, HashSet<uint> skipBlocks)
    {
        var after = OpenCatalog(ForkData.FromBytes(result), writable: false);
        bool Kept(byte[] key) => !overflowFiles.Contains(U32(new BigEndianReader(key), 2)) && U32(new BigEndianReader(key), 2) is not (3 or 4);
        var overflowAfter = LeafRecords(after.ExtentsTree).Where(r => Kept(r.Key)).ToDictionary(r => Convert.ToHexString(r.Key), r => r.Data);
        var overflowKept = overflowBefore.Where(r => Kept(r.Key)).ToList();
        if (overflowKept.Count != overflowAfter.Count ||
            overflowKept.Any(r => !overflowAfter.TryGetValue(Convert.ToHexString(r.Key), out var now) || !now.AsSpan().SequenceEqual(r.Data)))
        {
            throw new InvalidDataException("The edited HFS extents tree changed a record of a file it kept.");
        }

        var records = after.Records.ToDictionary(r => Convert.ToHexString(r.Key), r => r.Data);
        foreach (var (key, data) in before)
        {
            if (removedKeys.Contains(Convert.ToHexString(key)))
            {
                continue;
            }

            bool isParent = data.Length >= 70 && data[0] == 1 && U32(new BigEndianReader(data), 6) == parent;
            if (!records.TryGetValue(Convert.ToHexString(key), out var now) ||
                !(isParent ? now.AsSpan(0, 4).SequenceEqual(data.AsSpan(0, 4)) && now.AsSpan(6).SequenceEqual(data.AsSpan(6)) : now.AsSpan().SequenceEqual(data)))
            {
                throw new InvalidDataException("The edited HFS catalog changed a record it should have kept.");
            }
        }

        var system = new HashSet<uint>();
        foreach (var (start, count) in after.ExtentsTreeExtents.Concat(after.CatalogExtents))
        {
            for (uint block = start; block < (uint)start + count; block++)
            {
                system.Add(block);
            }
        }

        for (uint block = 0; block < after.BlockCount; block++)
        {
            if (system.Contains(block) || skipBlocks.Contains(block) || !IsAllocated(after.Bitmap, (ushort)block))
            {
                continue;
            }

            long offset = after.FirstBlock + (long)block * after.BlockSize;
            if (!source.AsSpan((int)offset, (int)after.BlockSize).SequenceEqual(result.AsSpan((int)offset, (int)after.BlockSize)))
            {
                throw new InvalidDataException("The edited HFS volume changed a block of a file it kept.");
            }
        }
    }

    private sealed class CatalogEditState(byte[] source, byte[] catalog, byte[] extentsTree,
        List<(ushort Start, ushort Count)> extentsTreeExtents,
        List<(ushort Start, ushort Count)> catalogExtents, List<(byte[] Key, byte[] Data)> records,
        uint firstBlock, uint blockSize, uint blockCount, int bitmapOffset, byte[] bitmap)
    {
        private byte[]? result;

        public byte[] Source { get; } = source;

        // The image being edited: a copy of the source, made when first written (a check never makes it).
        public byte[] Result => result ??= Source.ToArray();
        public byte[] Catalog { get; set; } = catalog;
        public byte[] ExtentsTree { get; set; } = extentsTree;
        public List<(ushort Start, ushort Count)> ExtentsTreeExtents { get; } = extentsTreeExtents;
        public bool ExtentsTreeChanged { get; set; }
        public uint AllocatedExtentsTreeBlocks { get; set; }
        public List<(ushort Start, ushort Count)> CatalogExtents { get; } = catalogExtents;
        public List<(byte[] Key, byte[] Data)> Records { get; } = records;
        public uint FirstBlock { get; } = firstBlock;
        public uint BlockSize { get; } = blockSize;
        public uint BlockCount { get; } = blockCount;
        public int BitmapOffset { get; } = bitmapOffset;
        public byte[] Bitmap { get; } = bitmap;
        public uint AllocatedCatalogBlocks { get; set; }
    }

    /// <summary>
    /// Checks a plain HFS volume as the writer does before every edit (hfs.md §5.5): the MDB's allocation area, both
    /// B-trees, the catalog's counts and valences, the bitmap's free count and every extent's ownership. A software lock
    /// does not stop the check.
    /// </summary>
    /// <returns>The first fault found, or null when the writer would edit the volume (were it not locked).</returns>
    /// <exception cref="InvalidDataException">The image is not a plain HFS volume (no HFS signature, or an HFS Plus wrapper).</exception>
    public static string? Check(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        PlainVolume(Bytes(image));
        try
        {
            OpenCatalog(image, writable: false);
            return null;
        }
        catch (Exception fault) when (fault is InvalidDataException or EndOfStreamException or OverflowException or
                                          ArgumentException or IndexOutOfRangeException)
        {
            return fault.Message;
        }
    }

    // The MDB of a plain HFS volume: refused when the image has no HFS signature or wraps HFS Plus.
    private static BigEndianReader PlainVolume(byte[] source)
    {
        if (source.Length < MdbOffset + MdbSize || U16(new BigEndianReader(source), MdbOffset) != 0x4244)
        {
            throw new InvalidDataException("The input is not a plain HFS volume.");
        }

        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        if (U16(mdb, 0x7C) == 0x482B)
        {
            throw new InvalidDataException("The HFS volume wraps an HFS Plus volume.");
        }

        return mdb;
    }

    private static CatalogEditState OpenCatalog(ForkData image, bool writable = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] source = Bytes(image);
        var mdb = PlainVolume(source);
        if (writable && (U16(mdb, 0x0A) & 0x8000) != 0)
        {
            throw new InvalidDataException("The HFS volume is software-locked.");
        }

        uint blockSize = U32(mdb, 0x14);
        uint blockCount = U16(mdb, 0x12);
        uint firstBlock = (uint)U16(mdb, 0x1C) * BlockSize;
        if (blockSize < BlockSize || blockSize % BlockSize != 0 ||
            firstBlock + (ulong)blockCount * blockSize > (ulong)source.Length)
        {
            throw new InvalidDataException("The HFS allocation area is invalid.");
        }

        var overflow = new Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>>();
        var extentsTreeExtents = ParseExtents(mdb, 0x86);
        byte[] extentsTree = ReadFork(source, firstBlock, blockSize, blockCount, extentsTreeExtents,
            U32(mdb, 0x82), overflow, 0xFF, 3, false);
        ValidateExtentsTree(extentsTree);
        foreach (var record in LeafRecords(extentsTree))
        {
            byte kind = record.Key[1];
            var key = new BigEndianReader(record.Key);
            uint id = U32(key, 2);
            ushort start = U16(key, 6);
            if (!overflow.TryGetValue((kind, id), out var list))
            {
                overflow[(kind, id)] = list = [];
            }

            list.Add((start, record.Data));
        }
        var catalogExtents = ParseExtents(mdb, 0x96);
        byte[] catalog = ReadFork(source, firstBlock, blockSize, blockCount, catalogExtents,
            U32(mdb, 0x92), overflow, 0, 4, true);
        ValidateCatalogTree(catalog);
        TreeRecord[] catalogRecords = LeafRecords(catalog).ToArray();
        var records = catalogRecords.Select(record => (record.Key, record.Data)).ToList();
        ValidateCatalogAccounting(mdb, records);
        int bitmapOffset = checked(U16(mdb, 0x0E) * BlockSize);
        int bitmapLength = checked(((int)blockCount + 7) / 8);
        if (bitmapOffset + bitmapLength > source.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap lies outside the image.");
        }

        byte[] workingBitmap = source.AsSpan(bitmapOffset, bitmapLength).ToArray();
        ValidateBitmapFreeCount(blockCount, workingBitmap, U16(mdb, 0x22));
        ValidateExtentOwnership(blockCount, workingBitmap, extentsTreeExtents,
            WithOverflow(catalogExtents, overflow, 0, 4), catalogRecords, overflow,
            new TreeRecord([], [], 0, 0, 0), 0, -1, []);
        return new CatalogEditState(source, catalog, extentsTree, extentsTreeExtents,
            WithOverflow(catalogExtents, overflow, 0, 4),
            records, firstBlock, blockSize, blockCount, bitmapOffset,
            workingBitmap);
    }

    private static byte[] CommitCatalog(CatalogEditState state)
    {
        state.Records.Sort((left, right) => CompareCatalogKeys(left.Key, right.Key));
        while (true)
        {
            try
            {
                RebuildBTree(state.Catalog, state.Records, validateExtents: false);
                ValidateCatalogTree(state.Catalog);
                ValidateCatalogAccounting(new BigEndianReader(state.Result.AsMemory(MdbOffset, MdbSize)),
                    state.Records);
                break;
            }
            catch (BTreeNeedsNodesException)
            {
                GrowCatalogTree(state);
            }
        }
        WriteFork(state.Result, state.FirstBlock, state.BlockSize, state.CatalogExtents, state.Catalog);
        if (state.ExtentsTreeChanged)
        {
            WriteFork(state.Result, state.FirstBlock, state.BlockSize,
                state.ExtentsTreeExtents, state.ExtentsTree);
        }

        uint allocatedSystemBlocks = checked(state.AllocatedCatalogBlocks + state.AllocatedExtentsTreeBlocks);
        var volume = new BigEndianWriter(state.Result);
        var volumeReader = new BigEndianReader(state.Result);
        if (allocatedSystemBlocks != 0)
        {
            ushort oldFree = U16(volumeReader, MdbOffset + 0x22);
            if (oldFree < allocatedSystemBlocks)
            {
                throw new InvalidDataException("The HFS free-block count cannot cover catalog growth.");
            }

            volume.WriteUInt16At(MdbOffset + 0x22, oldFree - allocatedSystemBlocks);
            state.Bitmap.CopyTo(state.Result, state.BitmapOffset);
            volume.WriteUInt32At(MdbOffset + 0x92, state.Catalog.Length);
            state.Result.AsSpan(MdbOffset + 0x96, 12).Clear();
            for (int index = 0; index < Math.Min(3, state.CatalogExtents.Count); index++)
            {
                WriteExtent(state.Result.AsSpan(MdbOffset + 0x96, 12), index,
                    state.CatalogExtents[index].Start, state.CatalogExtents[index].Count);
            }

            if (state.AllocatedExtentsTreeBlocks != 0)
            {
                volume.WriteUInt32At(MdbOffset + 0x82, state.ExtentsTree.Length);
                state.Result.AsSpan(MdbOffset + 0x86, 12).Clear();
                for (int index = 0; index < state.ExtentsTreeExtents.Count; index++)
                {
                    WriteExtent(state.Result.AsSpan(MdbOffset + 0x86, 12), index,
                        state.ExtentsTreeExtents[index].Start, state.ExtentsTreeExtents[index].Count);
                }
            }
        }
        uint now = MacDate.FromDateTime(DateTime.Now).Seconds;
        volume.WriteUInt32At(MdbOffset + 0x06, now);
        volume.WriteUInt32At(MdbOffset + 0x46, unchecked(U32(volumeReader, MdbOffset + 0x46) + 1));
        int alternateMdbOffset = state.Result.Length - 2 * BlockSize;
        if (allocatedSystemBlocks != 0 && alternateMdbOffset >= 0 &&
            (ulong)alternateMdbOffset >= state.FirstBlock + (ulong)state.BlockCount * state.BlockSize &&
            U16(new BigEndianReader(state.Source), alternateMdbOffset) == 0x4244)
        {
            state.Result.AsSpan(MdbOffset, BlockSize).CopyTo(state.Result.AsSpan(alternateMdbOffset, BlockSize));
        }

        var diagnostics = new List<Diagnostic>();
        HfsReader.Instance.Read(ForkData.FromBytes(state.Result), new ContainerContext(diagnostics: diagnostics));
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidDataException("The edited HFS catalog did not reopen cleanly.");
        }

        return state.Result;
    }

    private static void GrowCatalogTree(CatalogEditState state)
    {
        if (state.Catalog.Length > int.MaxValue - state.BlockSize)
        {
            throw new InvalidDataException("The HFS catalog B-tree cannot grow further.");
        }

        uint oldNodeCount = (uint)(state.Catalog.Length / NodeSize);
        uint nodesPerBlock = state.BlockSize / NodeSize;
        uint maxBlocks = 8;
        var extents = state.CatalogExtents;
        var last = extents[^1];
        uint adjacent = (uint)last.Start + last.Count;
        ushort newBlock = adjacent < state.BlockCount && !IsAllocated(state.Bitmap, checked((ushort)adjacent))
            ? checked((ushort)adjacent)
            : AllocateRuns(state.Bitmap, checked((ushort)state.BlockCount), 1, 1)[0].Start;
        uint chosenBlocks = 0;
        while (chosenBlocks < maxBlocks && (uint)newBlock + chosenBlocks < state.BlockCount &&
            !IsAllocated(state.Bitmap, checked((ushort)(newBlock + chosenBlocks))))
        {
            chosenBlocks++;
        }

        if (chosenBlocks == 0)
        {
            throw new InvalidDataException("No free HFS blocks are available for catalog growth.");
        }

        bool extendLast = adjacent == newBlock && last.Count <= ushort.MaxValue - chosenBlocks;
        if (extendLast)
        {
            extents[^1] = (last.Start, checked((ushort)(last.Count + chosenBlocks)));
        }
        else
        {
            extents.Add((newBlock, checked((ushort)chosenBlocks)));
        }
        for (uint block = newBlock; block < (uint)newBlock + chosenBlocks; block++)
        {
            SetBitmap(state.Bitmap, checked((ushort)block), allocated: true);
        }

        uint addedNodes = checked(chosenBlocks * nodesPerBlock);
        uint newNodeCount = checked(oldNodeCount + addedNodes);
        byte[] grown = new byte[checked(state.Catalog.Length + (int)(chosenBlocks * state.BlockSize))];
        state.Catalog.CopyTo(grown, 0);
        uint newMapNodes = ExtendBTreeNodeMap(grown, oldNodeCount, newNodeCount);
        var header = new BigEndianWriter(grown);
        header.WriteUInt32At(14 + 22, newNodeCount);
        header.WriteUInt32At(14 + 26, checked(U32(new BigEndianReader(grown), 14 + 26) + addedNodes - newMapNodes));
        state.Catalog = grown;
        state.AllocatedCatalogBlocks += chosenBlocks;
        if (extents.Count > 3)
        {
            UpdateCatalogOverflowExtents(state, newBlock, checked((ushort)chosenBlocks), extendLast,
                checked((ushort)(extents.Take(extents.Count - (extendLast ? 0 : 1)).Sum(extent => extent.Count))));
        }
    }

    private static void UpdateCatalogOverflowExtents(CatalogEditState state, ushort start,
        ushort count, bool extendLast, ushort forkBlock)
    {
        var lastRecord = LeafRecords(state.ExtentsTree)
            .Where(record => record.Key.Length == 8 && record.Key[1] == 0 &&
                U32(new BigEndianReader(record.Key), 2) == 4)
            .OrderBy(record => U16(new BigEndianReader(record.Key), 6)).LastOrDefault();
        var lastData = lastRecord is null ? null : new BigEndianReader(lastRecord.Data);
        if (extendLast || (lastData is not null && LastExtentSlot(lastData) < 2))
        {
            if (lastRecord is null || lastData is null)
            {
                throw new InvalidDataException("The catalog overflow extent record is missing.");
            }

            int slot = LastExtentSlot(lastData);
            if (extendLast)
            {
                if (slot < 0 || U16(lastData, slot * 4) != state.CatalogExtents[^1].Start)
                {
                    throw new InvalidDataException("The catalog's terminal overflow extent is inconsistent.");
                }

                WriteExtent(lastRecord.Data, slot, start: state.CatalogExtents[^1].Start,
                    count: state.CatalogExtents[^1].Count);
            }
            else
            {
                WriteExtent(lastRecord.Data, slot + 1, start, count);
            }

            UpdateTreeRecord(state.ExtentsTree, lastRecord.Key, lastRecord.Data);
        }
        else
        {
            var keyWriter = new BigEndianWriter(8);
            keyWriter.WriteByte(7);
            keyWriter.WriteByte(0);
            keyWriter.WriteUInt32(4);
            keyWriter.WriteUInt16(forkBlock);
            var key = keyWriter.ToArray();
            var record = new byte[12];
            WriteExtent(record, 0, start, count);
            while (true)
            {
                try
                {
                    InsertExtentsRecord(state.ExtentsTree, key, record);
                    break;
                }
                catch (BTreeNeedsNodesException)
                {
                    var context = new ForkResizeContext(state.ExtentsTree,
                        new TreeRecord([], [], 0, 0, 0), 0, [], [], state.Bitmap,
                        state.BlockCount, 0, 4, state.ExtentsTreeExtents, state.BlockSize);
                    GrowExtentsTree(context);
                    state.ExtentsTree = context.ExtentsTree;
                    state.AllocatedExtentsTreeBlocks += context.AllocatedTreeBlocks;
                }
            }
        }
        state.ExtentsTreeChanged = true;
    }

    private static (uint Parent, string Name) ResolveParent(List<(byte[] Key, byte[] Data)> records, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.StartsWith(':') || path.EndsWith(':') || path.Contains("::", StringComparison.Ordinal))
        {
            throw new ArgumentException("Use a colon-separated HFS path without empty components.", nameof(path));
        }

        string[] parts = path.Split(':');
        uint parent = 2;
        foreach (string component in parts[..^1])
        {
            ValidateCatalogName(component);
            var folder = FindCatalogRecord(records, parent, component);
            if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
            {
                throw new InvalidDataException($"The HFS parent folder '{component}' was not found.");
            }

            parent = U32(new BigEndianReader(folder.Data), 6);
        }
        ValidateCatalogName(parts[^1]);
        return (parent, parts[^1]);
    }

    private static void ValidateCatalogAccounting(BigEndianReader mdb,
        List<(byte[] Key, byte[] Data)> records)
    {
        var folders = records.Where(record => record.Data.Length >= 70 && record.Data[0] == 1).ToArray();
        var files = records.Where(record => record.Data.Length >= 102 && record.Data[0] == 2).ToArray();
        var folderIds = new HashSet<uint>();
        foreach (var folder in folders)
        {
            uint id = U32(new BigEndianReader(folder.Data), 6);
            if (!folderIds.Add(id))
            {
                throw new InvalidDataException("The HFS catalog has duplicate folder IDs.");
            }
        }
        if (!folderIds.Contains(2) || folders.Count(folder =>
                U32(new BigEndianReader(folder.Data), 6) == 2 && U32(new BigEndianReader(folder.Key), 2) == 1) != 1)
        {
            throw new InvalidDataException("The HFS root folder is missing or duplicated.");
        }

        foreach (var folder in folders)
        {
            var data = new BigEndianReader(folder.Data);
            uint id = U32(data, 6);
            uint parent = U32(new BigEndianReader(folder.Key), 2);
            if (id != 2 && !folderIds.Contains(parent))
            {
                throw new InvalidDataException("An HFS folder has no parent folder.");
            }

            int children = records.Count(record => record.Data.Length > 0 && record.Data[0] is 1 or 2 &&
                U32(new BigEndianReader(record.Key), 2) == id);
            if (U16(data, 4) != children)
            {
                throw new InvalidDataException("An HFS folder valence disagrees with its catalog children.");
            }
        }
        var catalogIds = new HashSet<uint>(folderIds);
        foreach (var file in files)
        {
            if (!catalogIds.Add(U32(new BigEndianReader(file.Data), 20)))
            {
                throw new InvalidDataException("The HFS catalog has duplicate file or folder IDs.");
            }

            if (!folderIds.Contains(U32(new BigEndianReader(file.Key), 2)))
            {
                throw new InvalidDataException("An HFS file has no parent folder.");
            }
        }
        if (U32(mdb, 0x54) != files.Length || U32(mdb, 0x58) != folders.Length - 1 ||
            U16(mdb, 0x0C) != files.Count(file => U32(new BigEndianReader(file.Key), 2) == 2) ||
            U16(mdb, 0x52) != folders.Count(folder => U32(new BigEndianReader(folder.Key), 2) == 2))
        {
            throw new InvalidDataException("The HFS volume counts disagree with its catalog records.");
        }
    }

    private static void ValidateCatalogName(string name)
    {
        byte[] encoded = MacRoman.Encode(name);
        if (encoded.Length is < 1 or > 31 || name.Contains(':') || name.Contains('\0'))
        {
            throw new ArgumentException("An HFS name must contain 1 to 31 Mac Roman bytes and no colon or null.", nameof(name));
        }
    }

    private static (byte[] Key, byte[] Data) FindCatalogRecord(List<(byte[] Key, byte[] Data)> records,
        uint parent, string name)
    {
        byte[] wanted = CatalogKey(parent, name);
        return records.FirstOrDefault(record => CompareCatalogKeys(record.Key, wanted) == 0);
    }

    private static void EnsureAbsent(List<(byte[] Key, byte[] Data)> records, uint parent, string name)
    {
        if (FindCatalogRecord(records, parent, name).Data is not null)
        {
            throw new InvalidDataException("An HFS catalog item with that name already exists.");
        }
    }

    private static byte[] CatalogKey(uint parent, string name)
    {
        byte[] encoded = MacRoman.Encode(name);
        int length = (7 + encoded.Length + 1) & ~1;
        var key = new BigEndianWriter(length);
        key.WriteByte(length - 1);
        key.WriteByte(0);
        key.WriteUInt32(parent);
        key.WriteByte(encoded.Length);
        key.WriteBytes(encoded);
        key.WriteZeros(length - key.Length);
        return key.ToArray();
    }

    internal static int CompareCatalogKeys(byte[] left, byte[] right)
    {
        int byParent = U32(new BigEndianReader(left), 2).CompareTo(U32(new BigEndianReader(right), 2));
        if (byParent != 0)
        {
            return byParent;
        }

        int leftLength = left[6], rightLength = right[6];
        if (left.Length < 7 + leftLength || right.Length < 7 + rightLength)
        {
            throw new InvalidDataException("An HFS catalog key has an invalid name length.");
        }

        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            int comparison = CatalogNameWeights[left[7 + index]].CompareTo(CatalogNameWeights[right[7 + index]]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return leftLength.CompareTo(rightLength);
    }

    /// <summary>Whether two Mac OS Roman names are the same name to an HFS catalog: equal by its ordering (case-insensitive, diacritics kept).</summary>
    internal static bool CatalogNamesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int index = 0; index < left.Length; index++)
        {
            if (CatalogNameWeights[left[index]] != CatalogNameWeights[right[index]])
            {
                return false;
            }
        }

        return true;
    }

    private static ushort[] BuildCatalogNameWeights()
    {
        // Inside Macintosh: Text, RelString; the exceptions here follow the Mac OS 9 ROM rules
        // recorded in docs/formats/file-systems/hfs.md §1.11. Unlisted Mac Roman bytes keep their code order.
        var weights = new ushort[256];
        for (int value = 0; value < weights.Length; value++)
        {
            weights[value] = (ushort)(value << 8);
        }

        for (char value = 'a'; value <= 'z'; value++)
        {
            Set(value.ToString(), (ushort)((value - 32) << 8));
        }

        void Set(string chars, ushort weight)
        {
            foreach (char value in chars)
            {
                if (!MacRoman.TryGetByte(value, out byte encoded))
                {
                    throw new InvalidOperationException($"The HFS comparison table contains an unencodable character: {value}.");
                }

                weights[encoded] = weight;
            }
        }

        Set("`", 0x4180);
        Set("\u00A0", 0x2000);
        Set("äÄ", 0x4108);
        Set("åÅ", 0x410C);
        Set("àÀ", 0x4104);
        Set("ãÃ", 0x410A);
        Set("æÆ", 0x4114);
        Set("çÇ", 0x4310);
        Set("éÉ", 0x4502);
        Set("ñÑ", 0x4E0A);
        Set("öÖ", 0x4F08);
        Set("õÕ", 0x4F0A);
        Set("øØ", 0x4F0E);
        Set("œŒ", 0x4F14);
        Set("üÜ", 0x5508);
        Set("á", 0x4182);
        Set("â", 0x4186);
        Set("è", 0x4584);
        Set("ê", 0x4586);
        Set("ë", 0x4588);
        Set("í", 0x4982);
        Set("ì", 0x4984);
        Set("î", 0x4986);
        Set("ï", 0x4988);
        Set("ó", 0x4F82);
        Set("ò", 0x4F84);
        Set("ô", 0x4F86);
        Set("ú", 0x5582);
        Set("ù", 0x5584);
        Set("û", 0x5586);
        Set("ß", 0x5382);
        Set("ÿ", 0x5988);
        Set("ª", 0x4192);
        Set("º", 0x4F92);
        Set("“", 0x2202);
        Set("”", 0x2204);
        Set("«", 0x2206);
        Set("»", 0x2208);
        Set("‘", 0x2702);
        Set("’", 0x2704);
        return weights;
    }

    private static void AdjustParentValence(List<(byte[] Key, byte[] Data)> records, uint parent, int adjustment)
    {
        var folder = records.FirstOrDefault(record => record.Data.Length >= 70 && record.Data[0] == 1 &&
            U32(new BigEndianReader(record.Data), 6) == parent);
        if (folder.Data is null)
        {
            throw new InvalidDataException("The HFS parent folder record is missing.");
        }

        ushort value = U16(new BigEndianReader(folder.Data), 4);
        int updated = value + adjustment;
        if (updated is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The HFS parent folder valence cannot represent this change.");
        }

        new BigEndianWriter(folder.Data).WriteUInt16At(4, updated);
    }

    private static void AddCount(byte[] image, int offset, int delta) =>
        new BigEndianWriter(image).WriteUInt32At(MdbOffset + offset,
            checked((uint)((long)U32(new BigEndianReader(image), MdbOffset + offset) + delta)));

    private static void AddShortCount(byte[] image, int offset, int delta) =>
        new BigEndianWriter(image).WriteUInt16At(MdbOffset + offset,
            checked((ushort)(U16(new BigEndianReader(image), MdbOffset + offset) + delta)));
}
