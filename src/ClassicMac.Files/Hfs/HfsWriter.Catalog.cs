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
        ReadOnlyMemory<byte> resource, FinderInfo finderInfo, MacDate? created = null, MacDate? modified = null) =>
        CreateFile(new HfsVolume(image), macPath, data, resource, finderInfo, created, modified).ToArray();

    internal static HfsVolume CreateFile(HfsVolume image, string macPath, ReadOnlyMemory<byte> data,
        ReadOnlyMemory<byte> resource, FinderInfo finderInfo, MacDate? created = null, MacDate? modified = null)
    {
        ArgumentNullException.ThrowIfNull(finderInfo);
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        EnsureAbsent(state.Records, parent, name);
        uint id = U32(new BigEndianReader(state.Mdb), 0x1E);
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
        AddCount(state.Mdb, 0x1E, 1);
        AddCount(state.Mdb, 0x54, 1);
        if (parent == 2)
        {
            AddShortCount(state.Mdb, 0x0C, 1);
        }

        var result = CommitCatalog(state);
        if (!data.IsEmpty)
        {
            result = ReplaceFork(result, macPath, HfsFork.Data, data);
        }

        if (!resource.IsEmpty)
        {
            result = ReplaceFork(result, macPath, HfsFork.Resource, resource);
        }

        if (modified is not null && (!data.IsEmpty || !resource.IsEmpty))
        {
            // Fork replacement stamps its edit time. Restore the caller's file date after both forks are written.
            var dated = OpenCatalog(result);
            var entry = FindCatalogRecord(dated.Records, parent, name);
            new BigEndianWriter(entry.Data).WriteUInt32At(48, modified.Value.Seconds);
            result = CommitCatalog(dated);
        }
        return result;
    }

    /// <summary>Deletes an HFS file and releases the blocks in both forks, returning a new image.</summary>
    public static byte[] DeleteFile(ForkData image, string macPath) =>
        DeleteFile(new HfsVolume(image), macPath).ToArray();

    internal static HfsVolume DeleteFile(HfsVolume image, string macPath)
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
    public static byte[] CreateFolder(ForkData image, string macPath, MacDate? created = null, MacDate? modified = null) =>
        CreateFolder(new HfsVolume(image), macPath, created, modified).ToArray();

    internal static HfsVolume CreateFolder(HfsVolume image, string macPath, MacDate? created = null, MacDate? modified = null)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        EnsureAbsent(state.Records, parent, name);
        uint id = U32(new BigEndianReader(state.Mdb), 0x1E);
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
        AddCount(state.Mdb, 0x1E, 1);
        AddCount(state.Mdb, 0x58, 1);
        if (parent == 2)
        {
            AddShortCount(state.Mdb, 0x52, 1);
        }

        return CommitCatalog(state);
    }

    /// <summary>Deletes an empty HFS folder and its catalog thread, returning a new image.</summary>
    public static byte[] DeleteFolder(ForkData image, string macPath) =>
        DeleteFolder(new HfsVolume(image), macPath).ToArray();

    internal static HfsVolume DeleteFolder(HfsVolume image, string macPath)
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
        AddCount(state.Mdb, 0x58, -1);
        if (parent == 2)
        {
            AddShortCount(state.Mdb, 0x52, -1);
        }

        return CommitCatalog(state);
    }

    /// <summary>
    /// Renames a file or folder in its folder, returning a new image: the catalog record moves to its new key, and its
    /// thread record (a folder's always, a file's when it has one) takes the new name. Its ID, forks and Finder info stay.
    /// </summary>
    public static byte[] Rename(ForkData image, string macPath, string newName) =>
        Rename(new HfsVolume(image), macPath, newName).ToArray();

    internal static HfsVolume Rename(HfsVolume image, string macPath, string newName)
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

        if (thread.Data is not null)
        {
            // The thread's name, written at Mac OS's full length (46 bytes, the name padded to a Str31) whatever length it
            // had: a length byte and up to 31 bytes at +14 (hfs.md §1.9).
            var encoded = MacRoman.Encode(newName);
            var full = new byte[46];
            thread.Data.AsSpan(0, 14).CopyTo(full);
            full[14] = (byte)encoded.Length;
            encoded.CopyTo(full, 15);
            state.Records[state.Records.IndexOf(thread)] = (thread.Key, full);
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
    public static byte[] Move(ForkData image, string macPath, string folderPath) =>
        Move(new HfsVolume(image), macPath, folderPath).ToArray();

    internal static HfsVolume Move(HfsVolume image, string macPath, string folderPath)
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
        if (thread.Data is not null)
        {
            new BigEndianWriter(thread.Data).WriteUInt32At(10, destination);       // thdParID, in every thread's first 14 bytes
        }

        AdjustParentValence(state.Records, parent, -1);
        AdjustParentValence(state.Records, destination, 1);
        int rootCount = isFolder ? 0x52 : 0x0C;
        if (parent == 2)
        {
            AddShortCount(state.Mdb, rootCount, -1);
        }

        if (destination == 2)
        {
            AddShortCount(state.Mdb, rootCount, 1);
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
    public static byte[] SetLocked(ForkData image, string macPath, bool locked) =>
        SetLocked(new HfsVolume(image), macPath, locked).ToArray();

    internal static HfsVolume SetLocked(HfsVolume image, string macPath, bool locked)
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
    public static byte[] Bless(ForkData image, string folderPath) =>
        Bless(new HfsVolume(image), folderPath).ToArray();

    internal static HfsVolume Bless(HfsVolume image, string folderPath)
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

        new BigEndianWriter(state.Mdb).WriteUInt32At(0x5C, id);
        return CommitCatalog(state);
    }

    /// <summary>Sets a file's Finder info (its <c>FInfo</c> and <c>FXInfo</c>), returning a new image.</summary>
    public static byte[] SetFinderInfo(ForkData image, string macPath, FinderInfo finderInfo) =>
        SetFinderInfo(new HfsVolume(image), macPath, finderInfo).ToArray();

    internal static HfsVolume SetFinderInfo(HfsVolume image, string macPath, FinderInfo finderInfo)
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
    public static byte[] SetFolderFlags(ForkData image, string macPath, FinderFlags flags) =>
        SetFolderFlags(new HfsVolume(image), macPath, flags).ToArray();

    internal static HfsVolume SetFolderFlags(HfsVolume image, string macPath, FinderFlags flags)
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
    public static byte[] Delete(ForkData image, string macPath, bool recursive) =>
        Delete(new HfsVolume(image), macPath, recursive).ToArray();

    internal static HfsVolume Delete(HfsVolume image, string macPath, bool recursive)
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
    private static HfsVolume DeleteTree(CatalogEditState state, uint parent, string macPath, (byte[] Key, byte[] Data) item)
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
        AddCount(state.Mdb, 0x54, -files.Count);
        AddCount(state.Mdb, 0x58, -folders.Count);
        if (parent == 2)
        {
            AddShortCount(state.Mdb, item.Data[0] == 1 ? 0x52 : 0x0C, -1);
        }

        AddShortCount(state.Mdb, 0x22, checked((int)released));
        var result = CommitCatalog(state);
        VerifyKept(result, before, removedKeys, parent, overflow.Select(r => (r.Key, r.Data)).ToList(), fileIds, []);
        return result;
    }

    // After an edit: the result opens as the writer opens a volume (its trees, counts, bitmap and extents agree); every
    // catalog record kept is byte for byte the source's (except the records in removedKeys, and the parent folder's
    // valence); every extents overflow record is the source's except those of the files in overflowFiles and of the
    // B-tree files; and every sector the edit wrote lies in the MDB, the alternate MDB, the bitmap, the catalog and extents
    // files, or the blocks of skipBlocks. So no file kept has changed, without reading any fork.
    private static void VerifyKept(HfsVolume result, List<(byte[] Key, byte[] Data)> before, HashSet<string> removedKeys, uint parent,
        List<(byte[] Key, byte[] Data)> overflowBefore, HashSet<uint> overflowFiles, HashSet<uint> skipBlocks)
    {
        var after = OpenCatalog(result, writable: false);
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

        // The sectors an edit may write (the B-tree files only grow, so their extents now include those before).
        var allowed = new HashSet<long>();
        void Allow(long offset, long length)
        {
            for (long sector = offset / BlockSize; sector * BlockSize < offset + length; sector++)
            {
                allowed.Add(sector);
            }
        }

        Allow(MdbOffset, BlockSize);
        Allow(result.Length - 2 * BlockSize, BlockSize);
        Allow(after.BitmapOffset, after.Bitmap.Length);
        foreach (var (start, count) in after.ExtentsTreeExtents.Concat(after.CatalogExtents))
        {
            Allow(after.FirstBlock + (long)start * after.BlockSize, (long)count * after.BlockSize);
        }

        foreach (var block in skipBlocks)
        {
            Allow(after.FirstBlock + (long)block * after.BlockSize, after.BlockSize);
        }

        if (result.ChangedSectors.FirstOrDefault(sector => !allowed.Contains(sector), -1) is var stray and >= 0)
        {
            throw new InvalidDataException($"The edited HFS volume wrote sector {stray}, outside what the edit may change.");
        }
    }

    private sealed class CatalogEditState(HfsVolume source, byte[] mdb, byte[] catalog, byte[] extentsTree,
        List<(ushort Start, ushort Count)> extentsTreeExtents,
        List<(ushort Start, ushort Count)> catalogExtents, List<(byte[] Key, byte[] Data)> records,
        uint firstBlock, uint blockSize, uint blockCount, int bitmapOffset, byte[] bitmap)
    {
        private HfsVolume? result;

        public HfsVolume Source { get; } = source;

        // The volume being edited: a fork of the source, made when first written (a check never makes it).
        public HfsVolume Result => result ??= Source.Fork();

        // The MDB's sector as edited, written to the result when the catalog is committed.
        public byte[] Mdb { get; } = mdb;
        public byte[] Catalog { get; set; } = catalog;

        // The catalog as read, and its leaf records with the nodes they are in: an edit writes only the nodes it changes.
        public byte[] OriginalCatalog { get; } = catalog.ToArray();
        public TreeRecord[] OriginalRecords { get; init; } = [];
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

        // The bitmap as read: an edit writes only its sectors that change.
        public byte[] OriginalBitmap { get; } = bitmap.ToArray();
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
        var volume = new HfsVolume(image);
        PlainVolume(volume);
        try
        {
            OpenCatalog(volume, writable: false);
            return null;
        }
        catch (Exception fault) when (fault is InvalidDataException or EndOfStreamException or OverflowException or
                                          ArgumentException or IndexOutOfRangeException)
        {
            return fault.Message;
        }
    }

    // The MDB's sector of a plain HFS volume: refused when the image has no HFS signature or wraps HFS Plus.
    private static byte[] PlainVolume(HfsVolume source)
    {
        if (source.Length < MdbOffset + MdbSize)
        {
            throw new InvalidDataException("The input is not a plain HFS volume.");
        }

        var sector = new byte[BlockSize];
        source.Read(MdbOffset, sector.AsSpan(0, (int)Math.Min(BlockSize, source.Length - MdbOffset)));
        var mdb = new BigEndianReader(sector);
        if (U16(mdb, 0) != 0x4244)
        {
            throw new InvalidDataException("The input is not a plain HFS volume.");
        }

        if (U16(mdb, 0x7C) == 0x482B)
        {
            throw new InvalidDataException("The HFS volume wraps an HFS Plus volume.");
        }

        return sector;
    }

    private static CatalogEditState OpenCatalog(ForkData image, bool writable = true) => OpenCatalog(new HfsVolume(image), writable);

    private static CatalogEditState OpenCatalog(HfsVolume source, bool writable = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        var mdbSector = PlainVolume(source);
        var mdb = new BigEndianReader(mdbSector.AsMemory(0, MdbSize));
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

        byte[] workingBitmap = new byte[bitmapLength];
        source.Read(bitmapOffset, workingBitmap);
        ValidateBitmapFreeCount(blockCount, workingBitmap, U16(mdb, 0x22));
        ValidateExtentOwnership(blockCount, workingBitmap, extentsTreeExtents,
            WithOverflow(catalogExtents, overflow, 0, 4), catalogRecords, overflow,
            new TreeRecord([], [], 0, 0, 0), 0, -1, []);
        return new CatalogEditState(source, mdbSector, catalog, extentsTree, extentsTreeExtents,
            WithOverflow(catalogExtents, overflow, 0, 4),
            records, firstBlock, blockSize, blockCount, bitmapOffset,
            workingBitmap) { OriginalRecords = [.. catalogRecords.Select(record => record with { Key = record.Key.ToArray(), Data = record.Data.ToArray() })] };
    }

    private static HfsVolume CommitCatalog(CatalogEditState state)
    {
        state.Records.Sort((left, right) => CompareCatalogKeys(left.Key, right.Key));
        // The tree's file grown when an operation finds too few free nodes, as the BTree manager grows it; on a volume with no
        // block to grow into, the tree is built again instead, which a deletion needs no new node for.
        CatalogUpdate update;
        while ((update = TryUpdateLeaves(state)) == CatalogUpdate.NeedsNodes)
        {
            try
            {
                GrowCatalogTree(state);
            }
            catch (InvalidDataException)
            {
                update = CatalogUpdate.Rebuild;
                break;
            }
        }

        bool inPlace = update == CatalogUpdate.Done;
        while (true)
        {
            try
            {
                if (!inPlace)
                {
                    RebuildBTree(state.Catalog, state.Records, validateExtents: false);
                }

                ValidateCatalogTree(state.Catalog);
                ValidateCatalogAccounting(new BigEndianReader(state.Mdb.AsMemory(0, MdbSize)),
                    state.Records);
                break;
            }
            catch (BTreeNeedsNodesException)
            {
                GrowCatalogTree(state);
            }
        }
        WriteChangedNodes(state);
        if (state.ExtentsTreeChanged)
        {
            WriteFork(state.Result, state.FirstBlock, state.BlockSize,
                state.ExtentsTreeExtents, state.ExtentsTree);
        }

        uint allocatedSystemBlocks = checked(state.AllocatedCatalogBlocks + state.AllocatedExtentsTreeBlocks);
        var volume = new BigEndianWriter(state.Mdb);
        var volumeReader = new BigEndianReader(state.Mdb);
        if (allocatedSystemBlocks != 0)
        {
            ushort oldFree = U16(volumeReader, 0x22);
            if (oldFree < allocatedSystemBlocks)
            {
                throw new InvalidDataException("The HFS free-block count cannot cover catalog growth.");
            }

            volume.WriteUInt16At(0x22, oldFree - allocatedSystemBlocks);
            volume.WriteUInt32At(0x92, state.Catalog.Length);
            state.Mdb.AsSpan(0x96, 12).Clear();
            for (int index = 0; index < Math.Min(3, state.CatalogExtents.Count); index++)
            {
                WriteExtent(state.Mdb.AsSpan(0x96, 12), index,
                    state.CatalogExtents[index].Start, state.CatalogExtents[index].Count);
            }

            if (state.AllocatedExtentsTreeBlocks != 0)
            {
                volume.WriteUInt32At(0x82, state.ExtentsTree.Length);
                state.Mdb.AsSpan(0x86, 12).Clear();
                for (int index = 0; index < state.ExtentsTreeExtents.Count; index++)
                {
                    WriteExtent(state.Mdb.AsSpan(0x86, 12), index,
                        state.ExtentsTreeExtents[index].Start, state.ExtentsTreeExtents[index].Count);
                }
            }
        }
        uint now = MacDate.FromDateTime(DateTime.Now).Seconds;
        volume.WriteUInt32At(0x06, now);
        volume.WriteUInt32At(0x46, unchecked(U32(volumeReader, 0x46) + 1));
        for (int at = 0; at < state.Bitmap.Length; at += BlockSize)
        {
            int length = Math.Min(BlockSize, state.Bitmap.Length - at);
            if (!state.Bitmap.AsSpan(at, length).SequenceEqual(state.OriginalBitmap.AsSpan(at, length)))
            {
                state.Result.Write(state.BitmapOffset + at, state.Bitmap.AsSpan(at, length));
            }
        }
        state.Result.Write(MdbOffset, state.Mdb);
        long alternateMdbOffset = state.Result.Length - 2 * BlockSize;
        if (allocatedSystemBlocks != 0 && alternateMdbOffset >= 0 &&
            (ulong)alternateMdbOffset >= state.FirstBlock + (ulong)state.BlockCount * state.BlockSize &&
            ReadUInt16(state.Source, alternateMdbOffset) == 0x4244)
        {
            state.Result.Write(alternateMdbOffset, state.Mdb);
        }

        var diagnostics = new List<Diagnostic>();
        HfsReader.Instance.Read(state.Result.AsForkData(), new ContainerContext(diagnostics: diagnostics));

        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidDataException("The edited HFS catalog did not reopen cleanly.");
        }

        return state.Result;
    }

    // How an edit in place of the catalog ended.
    private enum CatalogUpdate
    {
        // Every change is written.
        Done,

        // The tree has fewer free nodes than an operation may need: grow its file and try again.
        NeedsNodes,

        // A change the editor does not make in place: build the tree again.
        Rebuild,
    }

    // Writes the records' changes into the catalog record by record, as Apple's BTree manager does (hfs.md §1.8), on a
    // copy of it: records removed, then changed, then added, each in key order [ClassicMac order; Doc: the manager's
    // rules for each]. NeedsNodes, with the catalog unchanged, when an operation finds fewer free nodes than the tree's
    // depth + 1, as the manager grows the file then.
    private static CatalogUpdate TryUpdateLeaves(CatalogEditState state)
    {
        var original = state.OriginalRecords;
        var records = state.Records;
        if (original.Length == 0)
        {
            return CatalogUpdate.Rebuild;
        }

        var removed = new List<byte[]>();
        var changed = new List<(byte[] Key, byte[] Data)>();
        var added = new List<(byte[] Key, byte[] Data)>();
        int i = 0, j = 0;
        while (i < original.Length || j < records.Count)
        {
            int order = i == original.Length ? 1 : j == records.Count ? -1 : CompareCatalogKeys(original[i].Key, records[j].Key);
            if (order == 0)
            {
                if (!original[i].Key.AsSpan().SequenceEqual(records[j].Key) || !original[i].Data.AsSpan().SequenceEqual(records[j].Data))
                {
                    changed.Add(records[j]);
                }

                i++;
                j++;
            }
            else if (order < 0)
            {
                removed.Add(original[i].Key);
                i++;
            }
            else
            {
                added.Add(records[j]);
                j++;
            }
        }

        var tree = new CatalogTreeEdit(state.Catalog.ToArray());
        try
        {
            foreach (var key in removed)
            {
                tree.Delete(key);
            }

            foreach (var record in changed)
            {
                tree.Replace(record);
            }

            foreach (var record in added)
            {
                tree.Insert(record);
            }
        }
        catch (CatalogTreeEdit.NeedsNodesException)
        {
            return CatalogUpdate.NeedsNodes;
        }
        catch (CatalogTreeEdit.RebuildException)
        {
            return CatalogUpdate.Rebuild;
        }

        state.Catalog = tree.Bytes;
        return CatalogUpdate.Done;
    }

    // A B-tree edited in place by the BTree manager's rules (hfs.md §1.8) [Doc: Apple's hfs sources, BTreeTreeOps.c,
    // BTreeAllocate.c, BTree.c]: inserts that fit go in; a full node rotates records into its left sibling, or splits to
    // the left into the first free node; a node's new first key is deleted from its parent and inserted again; an emptied
    // node is unlinked, zeroed and freed, its parent's record deleted; a root left with one record gives way to its child.
    private sealed class CatalogTreeEdit
    {
        public sealed class NeedsNodesException : Exception;

        public sealed class RebuildException : Exception;

        private readonly int maxKeyLength;
        private readonly List<(int Offset, int Length)> maps = [];
        private Dictionary<uint, uint>? parents;

        public CatalogTreeEdit(byte[] bytes)
        {
            Bytes = bytes;
            maxKeyLength = U16(Reader, 14 + 20);
            maps.Add((U16(Reader, NodeSize - 6), U16(Reader, NodeSize - 8) - U16(Reader, NodeSize - 6)));
            var seen = new HashSet<uint>();
            for (uint mapNode = U32(Reader, 0); mapNode != 0; mapNode = U32(Reader, checked((int)mapNode * NodeSize)))
            {
                if (mapNode >= NodeCount || !seen.Add(mapNode))
                {
                    throw new InvalidDataException("The HFS catalog B-tree map-node chain is invalid.");
                }

                int offset = checked((int)mapNode * NodeSize);
                int start = U16(Reader, offset + NodeSize - 2);
                maps.Add((offset + start, U16(Reader, offset + NodeSize - 4) - start));
            }
        }

        public byte[] Bytes { get; }

        private BigEndianReader Reader => new(Bytes);

        private BigEndianWriter Writer => new(Bytes);

        private uint NodeCount => (uint)(Bytes.Length / NodeSize);

        private int Depth
        {
            get => U16(Reader, 14);
            set => Writer.WriteUInt16At(14, value);
        }

        private uint Root
        {
            get => U32(Reader, 14 + 2);
            set => Writer.WriteUInt32At(14 + 2, value);
        }

        private uint FreeNodes
        {
            get => U32(Reader, 14 + 26);
            set => Writer.WriteUInt32At(14 + 26, value);
        }

        private void AddLeafRecords(int delta) => Writer.WriteUInt32At(14 + 6, checked((uint)(U32(Reader, 14 + 6) + delta)));

        private static int Offset(uint node) => checked((int)node * NodeSize);

        private uint FLink(uint node) => U32(Reader, Offset(node));

        private uint BLink(uint node) => U32(Reader, Offset(node) + 4);

        private void SetFLink(uint node, uint value) => Writer.WriteUInt32At(Offset(node), value);

        private void SetBLink(uint node, uint value) => Writer.WriteUInt32At(Offset(node) + 4, value);

        private bool IsLeaf(uint node) => Bytes[Offset(node) + 8] == 0xFF;

        private List<(byte[] Key, byte[] Data)> Records(uint node) => ReadNodeRecords(Bytes, node);

        private static uint Child(byte[] data) => U32(new BigEndianReader(data), 0);

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
                int at = records.FindLastIndex(record => CompareCatalogKeys(record.Key, key) <= 0);
                node = Child(records[Math.Max(0, at)].Data);
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
            int at = records.FindIndex(existing => CompareCatalogKeys(existing.Key, record.Key) >= 0);
            at = at < 0 ? records.Count : at;
            if (at < records.Count && CompareCatalogKeys(records[at].Key, record.Key) == 0)
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
            int at = records.FindIndex(existing => CompareCatalogKeys(existing.Key, key) == 0);
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
            int at = records.FindIndex(existing => CompareCatalogKeys(existing.Key, record.Key) == 0);
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
            int at = siblings.FindIndex(record => Child(record.Data) == node);
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
            int at = records.FindIndex(record => Child(record.Data) == node);
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
                if (U32(Reader, 14 + 10) == node)
                {
                    Writer.WriteUInt32At(14 + 10, right);
                }

                if (U32(Reader, 14 + 14) == node)
                {
                    Writer.WriteUInt32At(14 + 14, left);
                }
            }

            uint parent = ParentOf(node);
            Free(node);
            parents = null;
            var siblings = Records(parent);
            int at = siblings.FindIndex(record => Child(record.Data) == node);
            siblings.RemoveAt(at);
            DeleteFrom(parent, siblings, at);
        }

        // CollapseTree: while the root is an index node with one record, its child becomes the root.
        private void Collapse()
        {
            while (Depth > 1 && Records(Root) is [var only])
            {
                uint old = Root;
                Root = Child(only.Data);
                Depth--;
                Free(old);
                parents = null;
            }
        }

        // ClearNode and FreeNode: the node zeroed, its map bit cleared, the free count raised.
        private void Free(uint node)
        {
            Bytes.AsSpan(Offset(node), NodeSize).Clear();
            SetMapBit(node, false);
            FreeNodes++;
        }

        // AllocateNode: the first free node by the map, marked used.
        private uint Allocate()
        {
            for (uint node = 1; node < NodeCount; node++)
            {
                if (!MapBit(node))
                {
                    SetMapBit(node, true);
                    FreeNodes--;
                    return node;
                }
            }

            throw new NeedsNodesException();
        }

        private (int At, byte Bit) MapPlace(uint node)
        {
            int mapByte = checked((int)(node >> 3));
            foreach (var (offset, length) in maps)
            {
                if (mapByte < length)
                {
                    return (offset + mapByte, (byte)(0x80 >> (int)(node & 7)));
                }

                mapByte -= length;
            }

            throw new RebuildException();
        }

        private bool MapBit(uint node)
        {
            var (at, bit) = MapPlace(node);
            return (Bytes[at] & bit) != 0;
        }

        private void SetMapBit(uint node, bool used)
        {
            var (at, bit) = MapPlace(node);
            Bytes[at] = used ? (byte)(Bytes[at] | bit) : (byte)(Bytes[at] & ~bit);
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
                        uint child = Child(data);
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

    // The catalog's nodes that differ from the catalog as read, and every node it grew by, written through its extents.
    private static void WriteChangedNodes(CatalogEditState state)
    {
        for (int node = 0; node < state.Catalog.Length; node += NodeSize)
        {
            var bytes = state.Catalog.AsSpan(node, NodeSize);
            if (node + NodeSize <= state.OriginalCatalog.Length && bytes.SequenceEqual(state.OriginalCatalog.AsSpan(node, NodeSize)))
            {
                continue;
            }

            long at = node;
            foreach (var (start, count) in state.CatalogExtents)
            {
                long length = (long)count * state.BlockSize;
                if (at < length)
                {
                    state.Result.Write(state.FirstBlock + (long)start * state.BlockSize + at, bytes);
                    break;
                }

                at -= length;
            }
        }
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

        // Each folder's children (folder and file records) by their keys' parent ID, counted in one pass.
        var children = new Dictionary<uint, int>();
        foreach (var record in records)
        {
            if (record.Data.Length > 0 && record.Data[0] is 1 or 2)
            {
                uint parent = U32(new BigEndianReader(record.Key), 2);
                children[parent] = children.GetValueOrDefault(parent) + 1;
            }
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

            if (U16(data, 4) != children.GetValueOrDefault(id))
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
        // drNmFls is not checked: Disk First Aid passes a wrong one and the File Manager keeps it, changing it by each
        // file added or removed, as the writer does (hfs.md §2.7) [Verified: Disk First Aid 8.5, Mac OS 9.0].
        if (U32(mdb, 0x54) != files.Length || U32(mdb, 0x58) != folders.Length - 1 ||
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
        // The parent IDs, big-endian, compare as their bytes do (no reader per comparison: a sort makes thousands).
        int byParent = left.AsSpan(2, 4).SequenceCompareTo(right.AsSpan(2, 4));
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

    // The volume with what hfsutils writes and Disk First Aid rejects made as Mac OS writes it, and how many records of
    // each: thread records shorter than Mac OS's 46 bytes (hfsutils writes a thread only as long as its name) written at
    // full length, the name padded to a Str31; file records with nonzero filStBlk (+$18), filRStBlk (+$22) or filResrv
    // (+$62), which Disk First Aid reports as reserved fields with incorrect data and its Repair clears (hfs.md §1.9,
    // §1.7) [Verified: Disk First Aid 8.5]. The volume itself when there are none.
    internal static (HfsVolume Volume, int Threads, int FileRecords) RepairCatalog(HfsVolume image)
    {
        var state = OpenCatalog(image);
        int threads = 0, files = 0;
        for (var index = 0; index < state.Records.Count; index++)
        {
            var (key, data) = state.Records[index];
            if (data[0] is 3 or 4 && data.Length < 46)
            {
                var full = new byte[46];
                data.AsSpan(0, 15 + data[14]).CopyTo(full);
                state.Records[index] = (key, full);
                threads++;
            }
            else if (data[0] == 2 && data.Length >= 0x66)
            {
                var reader = new BigEndianReader(data);
                if (reader.ReadUInt16At(0x18) != 0 || reader.ReadUInt16At(0x22) != 0 || reader.ReadUInt32At(0x62) != 0)
                {
                    var cleared = data.ToArray();
                    var writer = new BigEndianWriter(cleared);
                    writer.WriteUInt16At(0x18, (ushort)0);
                    writer.WriteUInt16At(0x22, (ushort)0);
                    writer.WriteUInt32At(0x62, 0u);
                    state.Records[index] = (key, cleared);
                    files++;
                }
            }
        }

        return threads + files == 0 ? (image, 0, 0) : (CommitCatalog(state), threads, files);
    }

    // A count in the MDB's sector (offset from its start) changed by delta.
    private static void AddCount(byte[] mdb, int offset, int delta) =>
        new BigEndianWriter(mdb).WriteUInt32At(offset, checked((uint)((long)U32(new BigEndianReader(mdb), offset) + delta)));

    private static void AddShortCount(byte[] mdb, int offset, int delta) =>
        new BigEndianWriter(mdb).WriteUInt16At(offset, checked((ushort)(U16(new BigEndianReader(mdb), offset) + delta)));

    // A big-endian u16 read from a volume (a field outside the buffers an edit holds: the alternate MDB's signature).
    private static ushort ReadUInt16(HfsVolume volume, long offset)
    {
        var bytes = new byte[2];
        volume.Read(offset, bytes);
        return new BigEndianReader(bytes).ReadUInt16At(0);
    }
}
