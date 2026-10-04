using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsAllocation;
using static ClassicMac.Files.Hfs.HfsForkWriting;
using static ClassicMac.Files.Hfs.HfsFormatter;
using static ClassicMac.Files.Hfs.HfsResizer;

namespace ClassicMac.Files.Hfs;

/// <summary>Conservative copy-on-write edits to plain HFS volumes.</summary>
public static class HfsWriter
{
    internal const int MdbOffset = 1024, MdbSize = 162, BlockSize = 512, NodeSize = 512;

    // The clock the writer's dates come from (the local time of the call), which a test may set for its own flow.
    internal static readonly System.Threading.AsyncLocal<TimeProvider?> Clock = new();

    internal static DateTime Now => (Clock.Value ?? TimeProvider.System).GetLocalNow().DateTime;

    /// <summary>Takes the writer's dates from <paramref name="clock"/> on this logical flow until disposed (reproducible edits).</summary>
    internal static IDisposable UseClock(TimeProvider clock)
    {
        Clock.Value = clock;
        return new ClockScope();
    }

    internal sealed class ClockScope : IDisposable
    {
        public void Dispose() => Clock.Value = null;
    }

    /// <summary>
    /// Replaces one fork on a plain HFS volume and returns a new image. The source image is never modified.
    /// </summary>
    /// <exception cref="InvalidDataException">The image, target, or requested edit is not supported or valid.</exception>
    public static byte[] ReplaceFork(ForkData image, string macPath, HfsFork fork, ReadOnlyMemory<byte> data) =>
        ReplaceFork(new HfsVolume(image), macPath, fork, data).ToArray();

    internal static HfsVolume ReplaceFork(HfsVolume image, string macPath, HfsFork fork, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(macPath);
        if (fork is not (HfsFork.Data or HfsFork.Resource))
        {
            throw new ArgumentOutOfRangeException(nameof(fork));
        }

        if (macPath.StartsWith(':') || macPath.EndsWith(':') || macPath.Contains("::", StringComparison.Ordinal))
        {
            throw new ArgumentException("Use a colon-separated HFS path without empty components.", nameof(macPath));
        }

        var source = image;
        if (source.Length < MdbOffset + MdbSize || ReadUInt16(source, MdbOffset) != 0x4244)
        {
            throw new InvalidDataException("The input is not a plain HFS volume.");
        }

        var mdbSector = new byte[BlockSize];
        source.Read(MdbOffset, mdbSector.AsSpan(0, (int)Math.Min(BlockSize, source.Length - MdbOffset)));
        var mdb = new BigEndianReader(mdbSector.AsMemory(0, MdbSize));
        if (U16(mdb, 0x7C) == 0x482B)
        {
            throw new InvalidDataException("Writing an HFS wrapper around HFS Plus is not supported.");
        }

        if ((U16(mdb, 0x0A) & 0x8000) != 0)
        {
            throw new InvalidDataException("The HFS volume is software-locked.");
        }

        uint blockSize = U32(mdb, 0x14);
        uint blockCount = U16(mdb, 0x12);
        uint firstBlock = (uint)U16(mdb, 0x1C) * 512;
        if (blockSize < 512 || blockSize % 512 != 0 || firstBlock + (ulong)blockCount * blockSize > (ulong)source.Length)
        {
            throw new InvalidDataException("The HFS allocation area is invalid or extends beyond the image.");
        }

        var overflow = new Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>>();
        var extFileExtents = ParseExtents(mdb, 0x86);
        var extFile = ReadFork(source, firstBlock, blockSize, blockCount, extFileExtents, U32(mdb, 0x82), overflow, 0xFF, 3, false);
        ValidateExtentsTree(extFile);
        var extFileRecords = LeafRecords(extFile).ToArray();
        foreach (var record in extFileRecords)
        {
            if (record.Key.Length < 8 || record.Data.Length < 12)
            {
                continue;
            }

            var key = new BigEndianReader(record.Key);
            var id = U32(key, 2);
            var start = U16(key, 6);
            var kind = record.Key[1];
            if (!overflow.TryGetValue((kind, id), out var list))
            {
                overflow[(kind, id)] = list = [];
            }

            list.Add((start, record.Data.AsSpan(0, 12).ToArray()));
        }

        var catalogExtents = ParseExtents(mdb, 0x96);
        var catalog = ReadFork(source, firstBlock, blockSize, blockCount, catalogExtents, U32(mdb, 0x92), overflow, 0, 4, true);
        ValidateCatalogTree(catalog);
        var catalogRecords = LeafRecords(catalog).ToArray();
        var catalogBefore = catalogRecords.Select(r => (r.Key, r.Data.ToArray())).ToList();
        var overflowBefore = extFileRecords.Select(r => (r.Key, r.Data.ToArray())).ToList();
        // Names as Mac OS Roman text to match the caller's path, and escaped as MacFile.MacPath shows them.
        var folders = new Dictionary<uint, (uint Parent, string Name)>();
        var shownFolders = new Dictionary<uint, (uint Parent, string Name)>();
        foreach (var entry in catalogRecords)
        {
            if (entry.Key.Length >= 7 && entry.Data.Length >= 70 && entry.Data[0] == 1)
            {
                uint folderId = U32(new BigEndianReader(entry.Data), 6), folderParent = U32(new BigEndianReader(entry.Key), 2);
                folders[folderId] = (folderParent, DecodeName(entry.Key));
                shownFolders[folderId] = (folderParent, ShownName(entry.Key));
            }
        }

        string wanted = macPath;
        var match = catalogRecords.FirstOrDefault(entry => entry.Key.Length >= 7 && entry.Data.Length >= 102 && entry.Data[0] == 2 &&
            HfsPathEquals(PathOf(U32(new BigEndianReader(entry.Key), 2), DecodeName(entry.Key), folders), wanted));
        if (match is null)
        {
            throw new InvalidDataException($"The HFS file '{macPath}' was not found.");
        }

        if ((match.Data[2] & 1) != 0)
        {
            throw new InvalidDataException($"The HFS file '{macPath}' is locked.");
        }

        string canonicalPath = PathOf(U32(new BigEndianReader(match.Key), 2), ShownName(match.Key), shownFolders);

        int forkLengthOffset = fork == HfsFork.Data ? 26 : 36;
        int forkPhysicalLengthOffset = fork == HfsFork.Data ? 30 : 40;
        int forkExtentOffset = fork == HfsFork.Data ? 74 : 86;
        var matchData = new BigEndianReader(match.Data);
        uint fileId = U32(matchData, 20);
        uint logicalLength = U32(matchData, forkLengthOffset);
        var primaryExtentRecord = new BigEndianReader(match.Data.AsSpan(forkExtentOffset, 12).ToArray());
        LastExtentSlot(primaryExtentRecord);
        var extents = ParseExtents(primaryExtentRecord);
        var overflowKey = (fork == HfsFork.Data ? (byte)0 : (byte)0xFF, fileId);
        var targetOverflow = extFileRecords.Where(r => r.Key.Length >= 8 && r.Key[1] == overflowKey.Item1 &&
                U32(new BigEndianReader(r.Key), 2) == fileId)
            .OrderBy(r => U16(new BigEndianReader(r.Key), 6)).ToArray();
        uint forkBlockNumber = (uint)extents.Aggregate(0, (count, extent) => count + extent.Count);
        foreach (var overflowRecord in targetOverflow)
        {
            if (U16(new BigEndianReader(overflowRecord.Key), 6) != forkBlockNumber ||
                overflowRecord.Data.Length < 12)
            {
                throw new InvalidDataException("The file's extents-overflow keys are not contiguous with its fork extents.");
            }

            var overflowData = new BigEndianReader(overflowRecord.Data);
            LastExtentSlot(overflowData);
            var next = ParseExtents(overflowData);
            extents.AddRange(next);
            forkBlockNumber += (uint)next.Aggregate(0, (count, extent) => count + extent.Count);
        }

        ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
        if (logicalLength > capacity)
        {
            throw new InvalidDataException("The existing fork length exceeds its allocated extents.");
        }

        foreach (var (start, count) in extents)
        {
            if ((uint)start + count > blockCount || (ulong)firstBlock + (ulong)start * blockSize + (ulong)count * blockSize > (ulong)source.Length)
            {
                throw new InvalidDataException("A target fork extent lies outside the HFS allocation area or image.");
            }
        }

        var result = source.Fork();
        var mdbOut = mdbSector.ToArray();
        var volume = new BigEndianWriter(mdbOut);
        var bitmapOffset = checked((int)U16(mdb, 0x0E) * 512);
        int bitmapLength = checked(((int)blockCount + 7) / 8);
        if (bitmapOffset < 0 || bitmapOffset + bitmapLength > source.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap lies outside the image.");
        }

        var workingBitmap = new byte[bitmapLength];
        source.Read(bitmapOffset, workingBitmap);
        ValidateBitmapFreeCount(blockCount, workingBitmap, U16(mdb, 0x22));
        ValidateExtentOwnership(blockCount, workingBitmap, extFileExtents,
            WithOverflow(catalogExtents, overflow, 0, 4), catalogRecords, overflow, match, fileId, forkExtentOffset, extents);
        var resizeContext = new ForkResizeContext(extFile, match, forkExtentOffset, targetOverflow,
            extents, workingBitmap, blockCount, overflowKey.Item1, fileId, extFileExtents, blockSize);
        var resize = ResizeFork(resizeContext,
            ((ulong)data.Length + blockSize - 1) / blockSize);
        extFile = resizeContext.ExtentsTree;
        ushort freeBlocks = U16(mdb, 0x22);
        long remainingFreeBlocks = (long)freeBlocks - resize.AllocatedBlocks + resize.ReleasedBlocks;
        if (remainingFreeBlocks < 0 || remainingFreeBlocks > blockCount)
        {
            throw new InvalidDataException("The HFS volume free-block count disagrees with the requested allocation change.");
        }

        if (resize.AllocatedBlocks != 0 || resize.ReleasedBlocks != 0)
        {
            volume.WriteUInt16At(0x22, remainingFreeBlocks);
            result.Write(bitmapOffset, workingBitmap);
        }
        bool changedExtentsTree = resize.TreeChanged;
        uint newlyAllocatedBlocks = resize.AllocatedBlocks;
        uint releasedBlocks = resize.ReleasedBlocks;
        if (resizeContext.AllocatedTreeBlocks != 0)
        {
            volume.WriteUInt32At(0x82, extFile.Length);
            mdbOut.AsSpan(0x86, 12).Clear();
            for (int index = 0; index < extFileExtents.Count; index++)
            {
                WriteExtent(mdbOut.AsSpan(0x86, 12), index,
                    extFileExtents[index].Start, extFileExtents[index].Count);
            }
        }

        ulong physicalBytes = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (total, e) => total + (ulong)e.Count * blockSize);
        if (physicalBytes > uint.MaxValue)
        {
            throw new InvalidDataException("The allocated fork exceeds HFS's 32-bit physical length field.");
        }

        var matchRecord = new BigEndianWriter(match.Data);
        matchRecord.WriteUInt32At(forkPhysicalLengthOffset, physicalBytes);
        matchRecord.WriteUInt32At(forkLengthOffset, data.Length);
        DateTime writeTime = Now;
        uint macWriteTime = MacDate.FromDateTime(writeTime).Seconds;
        matchRecord.WriteUInt32At(48, macWriteTime);
        volume.WriteUInt32At(0x06, macWriteTime);
        volume.WriteUInt32At(0x46, unchecked(U32(mdb, 0x46) + 1));

        WriteFork(result, firstBlock, blockSize, extents, data.Span);
        // Clear the unused tail within the existing allocation, so shortening a fork does not leave stale bytes.
        ClearForkTail(result, firstBlock, blockSize, extents, data.Length);
        UpdateCatalogRecord(catalog, match);
        ValidateCatalogTree(catalog);
        WriteFork(result, firstBlock, blockSize, WithOverflow(catalogExtents, overflow, 0, 4), catalog);
        if (changedExtentsTree)
        {
            WriteFork(result, firstBlock, blockSize, extFileExtents, extFile);
        }

        result.Write(MdbOffset, mdbOut);
        long alternateMdbOffset = result.Length - 2 * BlockSize;
        if (resizeContext.AllocatedTreeBlocks != 0 && alternateMdbOffset >= 0 &&
            (ulong)alternateMdbOffset >= firstBlock + (ulong)blockCount * blockSize &&
            ReadUInt16(source, alternateMdbOffset) == 0x4244)
        {
            result.Write(alternateMdbOffset, mdbOut);
        }

        ValidateExtentsTree(ReadFork(result, firstBlock, blockSize, blockCount, extFileExtents,
            U32(new BigEndianReader(mdbOut), 0x82), overflow, 0xFF, 3, false));
        Verify(source, result, canonicalPath, fork, data.Span, writeTime, newlyAllocatedBlocks, releasedBlocks);
        var targetBlocks = new HashSet<uint>(extents.SelectMany(e => Enumerable.Range(e.Start, e.Count).Select(b => (uint)b)));
        VerifyKept(result, catalogBefore, [Convert.ToHexString(match.Key)], 0, overflowBefore, [fileId], targetBlocks);
        return result;
    }

    // An offset outside the data throws ArgumentOutOfRangeException.
    internal static ushort U16(BigEndianReader b, int o) =>
        b.TryReadUInt16At(o, out ushort value) ? value : throw new ArgumentOutOfRangeException(nameof(o));

    internal static uint U32(BigEndianReader b, int o) =>
        b.TryReadUInt32At(o, out uint value) ? value : throw new ArgumentOutOfRangeException(nameof(o));

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
        uint now = MacDate.FromDateTime(Now).Seconds;
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

        uint now = MacDate.FromDateTime(Now).Seconds;

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

    /// <summary>The smallest volume <see cref="Format"/> makes: 400 KB, a single-sided floppy.</summary>
    public const long MinimumFormatSize = 400 * 1024;

    /// <summary>The largest volume <see cref="Format"/> makes, as one in-memory image: just under 2 GB.</summary>
    public const long MaximumFormatSize = int.MaxValue / BlockSize * BlockSize;

    /// <summary>The largest volume <see cref="FormatTo"/> makes: 2 TB, HFS's limit (65,535 allocation blocks).</summary>
    public const long MaximumFormatToSize = 2L << 40;

    /// <summary>
    /// A new, empty HFS volume of <paramref name="size"/> bytes named <paramref name="volumeName"/>, laid out as Mac OS 9.0's
    /// initializer lays it out (hfs.md §3.1): boot blocks zero, the MDB, the bitmap, the extents overflow file and the
    /// catalog (with the root folder and its thread) at the start of the allocation area, and the alternate MDB.
    /// <paramref name="created"/> is the creation and modification date (the local time now when omitted). The result
    /// passes <see cref="Check"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not whole 512-byte blocks, or outside 400 KB to 2 GB.</exception>
    /// <exception cref="ArgumentException">The name is empty, over 27 bytes, has a colon, or a character Mac OS Roman has not.</exception>
    public static byte[] Format(long size, string volumeName, MacDate? created = null) => HfsFormatter.Format(size, volumeName, created);

    /// <summary>
    /// Writes a new, empty HFS volume to the file at <paramref name="path"/> (created, or replaced), laid out as
    /// <see cref="Format"/> lays it out, up to 2 TB: the file is made <paramref name="size"/> bytes long and only the MDB,
    /// the bitmap, the B-tree files and the alternate MDB are written (a few MB).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not whole 512-byte blocks, or outside 400 KB to 2 TB.</exception>
    /// <exception cref="ArgumentException">The name is empty, over 27 bytes, has a colon, or a character Mac OS Roman has not.</exception>
    public static void FormatTo(string path, long size, string volumeName, MacDate? created = null) => HfsFormatter.FormatTo(path, size, volumeName, created);

    internal static HfsVolume FormatVolume(long size, string volumeName, MacDate? created = null) => HfsFormatter.FormatVolume(size, volumeName, created);

    /// <summary>
    /// A plain HFS volume grown to <paramref name="size"/> bytes within its allocation block size (hfs.md §3.2): the new
    /// allocation blocks are free, the bitmap covers them (the allocation area moved up whole sectors when the bitmap's
    /// sectors are full), and the alternate MDB is at the new end. Every file, folder and CNID stays. Returns a new image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not whole 512-byte blocks, or is past what an image in memory can hold.</exception>
    /// <exception cref="InvalidDataException">
    /// The volume is not one the writer edits, or the size is not larger, or the volume would need more than 65,535
    /// allocation blocks of its size.
    /// </exception>
    public static byte[] Resize(ForkData image, long size) => HfsResizer.Resize(image, size);
}

/// <summary>The fork selected for an HFS file edit.</summary>
public enum HfsFork
{
    /// <summary>The data fork.</summary>
    Data,
    /// <summary>The resource fork.</summary>
    Resource,
}
