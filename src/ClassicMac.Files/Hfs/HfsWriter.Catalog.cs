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
        if (id < 16 || id == uint.MaxValue) throw new InvalidDataException("The HFS volume has no available catalog ID.");

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
        if (parent == 2) AddShortCount(state.Result, 0x0C, 1);
        byte[] result = CommitCatalog(state);
        if (!data.IsEmpty) result = ReplaceFork(ForkData.FromBytes(result), macPath, HfsFork.Data, data);
        if (!resource.IsEmpty) result = ReplaceFork(ForkData.FromBytes(result), macPath, HfsFork.Resource, resource);
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
            throw new InvalidDataException("The HFS file to delete was not found.");
        if ((existing.Data[2] & 1) != 0) throw new InvalidDataException("The HFS file is locked.");

        byte[] cleared = ReplaceFork(ForkData.FromBytes(initial.Source), macPath, HfsFork.Data, Array.Empty<byte>());
        cleared = ReplaceFork(ForkData.FromBytes(cleared), macPath, HfsFork.Resource, Array.Empty<byte>());
        var state = OpenCatalog(ForkData.FromBytes(cleared));
        var target = FindCatalogRecord(state.Records, parent, name);
        if (!state.Records.Remove(target)) throw new InvalidDataException("The HFS file record disappeared during deletion.");
        uint fileId = U32(new BigEndianReader(target.Data), 20);
        var thread = FindCatalogRecord(state.Records, fileId, "");
        if ((target.Data[2] & 2) != 0 && (thread.Data is null || thread.Data[0] != 4))
            throw new InvalidDataException("The HFS file thread is missing.");
        if (thread.Data is not null)
        {
            if (thread.Data[0] != 4) throw new InvalidDataException("The HFS file has an invalid thread record.");
            state.Records.Remove(thread);
        }
        AdjustParentValence(state.Records, parent, -1);
        AddCount(state.Result, 0x54, -1);
        if (parent == 2) AddShortCount(state.Result, 0x0C, -1);
        return CommitCatalog(state);
    }

    /// <summary>Creates an HFS folder and its catalog thread, returning a new image.</summary>
    public static byte[] CreateFolder(ForkData image, string macPath, MacDate? created = null, MacDate? modified = null)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        EnsureAbsent(state.Records, parent, name);
        uint id = U32(new BigEndianReader(state.Result), MdbOffset + 0x1E);
        if (id < 16 || id == uint.MaxValue) throw new InvalidDataException("The HFS volume has no available catalog ID.");
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
        if (parent == 2) AddShortCount(state.Result, 0x52, 1);
        return CommitCatalog(state);
    }

    /// <summary>Deletes an empty HFS folder and its catalog thread, returning a new image.</summary>
    public static byte[] DeleteFolder(ForkData image, string macPath)
    {
        var state = OpenCatalog(image);
        var (parent, name) = ResolveParent(state.Records, macPath);
        var folder = FindCatalogRecord(state.Records, parent, name);
        if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
            throw new InvalidDataException("The HFS folder to delete was not found.");
        uint id = U32(new BigEndianReader(folder.Data), 6);
        if (state.Records.Any(record =>
                U32(new BigEndianReader(record.Key), 2) == id && DecodeName(record.Key).Length != 0))
            throw new InvalidDataException("A nonempty HFS folder cannot be deleted.");
        var thread = FindCatalogRecord(state.Records, id, "");
        if (thread.Data is null || thread.Data[0] != 3)
            throw new InvalidDataException("The HFS folder thread is missing.");
        state.Records.Remove(folder);
        state.Records.Remove(thread);
        AdjustParentValence(state.Records, parent, -1);
        AddCount(state.Result, 0x58, -1);
        if (parent == 2) AddShortCount(state.Result, 0x52, -1);
        return CommitCatalog(state);
    }

    private sealed class CatalogEditState(byte[] source, byte[] catalog, byte[] extentsTree,
        List<(ushort Start, ushort Count)> extentsTreeExtents,
        List<(ushort Start, ushort Count)> catalogExtents, List<(byte[] Key, byte[] Data)> records,
        uint firstBlock, uint blockSize, uint blockCount, int bitmapOffset, byte[] bitmap)
    {
        public byte[] Source { get; } = source;
        public byte[] Result { get; } = source.ToArray();
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

    private static CatalogEditState OpenCatalog(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] source = image.ToArray();
        if (source.Length < MdbOffset + MdbSize || U16(new BigEndianReader(source), MdbOffset) != 0x4244)
            throw new InvalidDataException("The input is not a plain HFS volume.");
        var mdb = new BigEndianReader(source.AsMemory(MdbOffset, MdbSize));
        if (U16(mdb, 0x7C) == 0x482B || (U16(mdb, 0x0A) & 0x8000) != 0)
            throw new InvalidDataException("The HFS volume is wrapped or software-locked.");
        uint blockSize = U32(mdb, 0x14);
        uint blockCount = U16(mdb, 0x12);
        uint firstBlock = (uint)U16(mdb, 0x1C) * BlockSize;
        if (blockSize < BlockSize || blockSize % BlockSize != 0 ||
            firstBlock + (ulong)blockCount * blockSize > (ulong)source.Length)
            throw new InvalidDataException("The HFS allocation area is invalid.");

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
            if (!overflow.TryGetValue((kind, id), out var list)) overflow[(kind, id)] = list = [];
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
            throw new InvalidDataException("The HFS volume bitmap lies outside the image.");
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
            WriteFork(state.Result, state.FirstBlock, state.BlockSize,
                state.ExtentsTreeExtents, state.ExtentsTree);
        uint allocatedSystemBlocks = checked(state.AllocatedCatalogBlocks + state.AllocatedExtentsTreeBlocks);
        var volume = new BigEndianWriter(state.Result);
        var volumeReader = new BigEndianReader(state.Result);
        if (allocatedSystemBlocks != 0)
        {
            ushort oldFree = U16(volumeReader, MdbOffset + 0x22);
            if (oldFree < allocatedSystemBlocks)
                throw new InvalidDataException("The HFS free-block count cannot cover catalog growth.");
            volume.WriteUInt16At(MdbOffset + 0x22, checked((ushort)(oldFree - allocatedSystemBlocks)));
            state.Bitmap.CopyTo(state.Result, state.BitmapOffset);
            volume.WriteUInt32At(MdbOffset + 0x92, checked((uint)state.Catalog.Length));
            state.Result.AsSpan(MdbOffset + 0x96, 12).Clear();
            for (int index = 0; index < Math.Min(3, state.CatalogExtents.Count); index++)
                WriteExtent(state.Result.AsSpan(MdbOffset + 0x96, 12), index,
                    state.CatalogExtents[index].Start, state.CatalogExtents[index].Count);
            if (state.AllocatedExtentsTreeBlocks != 0)
            {
                volume.WriteUInt32At(MdbOffset + 0x82, checked((uint)state.ExtentsTree.Length));
                state.Result.AsSpan(MdbOffset + 0x86, 12).Clear();
                for (int index = 0; index < state.ExtentsTreeExtents.Count; index++)
                    WriteExtent(state.Result.AsSpan(MdbOffset + 0x86, 12), index,
                        state.ExtentsTreeExtents[index].Start, state.ExtentsTreeExtents[index].Count);
            }
        }
        uint now = MacDate.FromDateTime(DateTime.Now).Seconds;
        volume.WriteUInt32At(MdbOffset + 0x06, now);
        volume.WriteUInt32At(MdbOffset + 0x46, unchecked(U32(volumeReader, MdbOffset + 0x46) + 1));
        int alternateMdbOffset = state.Result.Length - 2 * BlockSize;
        if (allocatedSystemBlocks != 0 && alternateMdbOffset >= 0 &&
            (ulong)alternateMdbOffset >= state.FirstBlock + (ulong)state.BlockCount * state.BlockSize &&
            U16(new BigEndianReader(state.Source), alternateMdbOffset) == 0x4244)
            state.Result.AsSpan(MdbOffset, BlockSize).CopyTo(state.Result.AsSpan(alternateMdbOffset, BlockSize));
        var diagnostics = new List<Diagnostic>();
        HfsReader.Instance.Read(ForkData.FromBytes(state.Result), new ContainerContext(diagnostics: diagnostics));
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException("The edited HFS catalog did not reopen cleanly.");
        return state.Result;
    }

    private static void GrowCatalogTree(CatalogEditState state)
    {
        if (state.Catalog.Length > int.MaxValue - state.BlockSize)
            throw new InvalidDataException("The HFS catalog B-tree cannot grow further.");
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
            !IsAllocated(state.Bitmap, checked((ushort)(newBlock + chosenBlocks)))) chosenBlocks++;
        if (chosenBlocks == 0) throw new InvalidDataException("No free HFS blocks are available for catalog growth.");
        bool extendLast = adjacent == newBlock && last.Count <= ushort.MaxValue - chosenBlocks;
        if (extendLast)
            extents[^1] = (last.Start, checked((ushort)(last.Count + chosenBlocks)));
        else
        {
            extents.Add((newBlock, checked((ushort)chosenBlocks)));
        }
        for (uint block = newBlock; block < (uint)newBlock + chosenBlocks; block++)
            SetBitmap(state.Bitmap, checked((ushort)block), allocated: true);
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
            UpdateCatalogOverflowExtents(state, newBlock, checked((ushort)chosenBlocks), extendLast,
                checked((ushort)(extents.Take(extents.Count - (extendLast ? 0 : 1)).Sum(extent => extent.Count))));
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
                throw new InvalidDataException("The catalog overflow extent record is missing.");
            int slot = LastExtentSlot(lastData);
            if (extendLast)
            {
                if (slot < 0 || U16(lastData, slot * 4) != state.CatalogExtents[^1].Start)
                    throw new InvalidDataException("The catalog's terminal overflow extent is inconsistent.");
                WriteExtent(lastRecord.Data, slot, start: state.CatalogExtents[^1].Start,
                    count: state.CatalogExtents[^1].Count);
            }
            else WriteExtent(lastRecord.Data, slot + 1, start, count);
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
            throw new ArgumentException("Use a colon-separated HFS path without empty components.", nameof(path));
        string[] parts = path.Split(':');
        uint parent = 2;
        foreach (string component in parts[..^1])
        {
            ValidateCatalogName(component);
            var folder = FindCatalogRecord(records, parent, component);
            if (folder.Data is null || folder.Data.Length < 70 || folder.Data[0] != 1)
                throw new InvalidDataException($"The HFS parent folder '{component}' was not found.");
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
            if (!folderIds.Add(id)) throw new InvalidDataException("The HFS catalog has duplicate folder IDs.");
        }
        if (!folderIds.Contains(2) || folders.Count(folder =>
                U32(new BigEndianReader(folder.Data), 6) == 2 && U32(new BigEndianReader(folder.Key), 2) == 1) != 1)
            throw new InvalidDataException("The HFS root folder is missing or duplicated.");
        foreach (var folder in folders)
        {
            var data = new BigEndianReader(folder.Data);
            uint id = U32(data, 6);
            uint parent = U32(new BigEndianReader(folder.Key), 2);
            if (id != 2 && !folderIds.Contains(parent))
                throw new InvalidDataException("An HFS folder has no parent folder.");
            int children = records.Count(record => record.Data.Length > 0 && record.Data[0] is 1 or 2 &&
                U32(new BigEndianReader(record.Key), 2) == id);
            if (U16(data, 4) != children)
                throw new InvalidDataException("An HFS folder valence disagrees with its catalog children.");
        }
        var catalogIds = new HashSet<uint>(folderIds);
        foreach (var file in files)
        {
            if (!catalogIds.Add(U32(new BigEndianReader(file.Data), 20)))
                throw new InvalidDataException("The HFS catalog has duplicate file or folder IDs.");
            if (!folderIds.Contains(U32(new BigEndianReader(file.Key), 2)))
                throw new InvalidDataException("An HFS file has no parent folder.");
        }
        if (U32(mdb, 0x54) != files.Length || U32(mdb, 0x58) != folders.Length - 1 ||
            U16(mdb, 0x0C) != files.Count(file => U32(new BigEndianReader(file.Key), 2) == 2) ||
            U16(mdb, 0x52) != folders.Count(folder => U32(new BigEndianReader(folder.Key), 2) == 2))
            throw new InvalidDataException("The HFS volume counts disagree with its catalog records.");
    }

    private static void ValidateCatalogName(string name)
    {
        byte[] encoded = MacRoman.Encode(name);
        if (encoded.Length is < 1 or > 31 || name.Contains(':') || name.Contains('\0'))
            throw new ArgumentException("An HFS name must contain 1 to 31 Mac Roman bytes and no colon or null.", nameof(name));
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
            throw new InvalidDataException("An HFS catalog item with that name already exists.");
    }

    private static byte[] CatalogKey(uint parent, string name)
    {
        byte[] encoded = MacRoman.Encode(name);
        int length = (7 + encoded.Length + 1) & ~1;
        var key = new BigEndianWriter(length);
        key.WriteByte(checked((byte)(length - 1)));
        key.WriteByte(0);
        key.WriteUInt32(parent);
        key.WriteByte(checked((byte)encoded.Length));
        key.WriteBytes(encoded);
        key.WriteZeros(length - key.Length);
        return key.ToArray();
    }

    internal static int CompareCatalogKeys(byte[] left, byte[] right)
    {
        int byParent = U32(new BigEndianReader(left), 2).CompareTo(U32(new BigEndianReader(right), 2));
        if (byParent != 0) return byParent;
        int leftLength = left[6], rightLength = right[6];
        if (left.Length < 7 + leftLength || right.Length < 7 + rightLength)
            throw new InvalidDataException("An HFS catalog key has an invalid name length.");
        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            int comparison = CatalogNameWeights[left[7 + index]].CompareTo(CatalogNameWeights[right[7 + index]]);
            if (comparison != 0) return comparison;
        }
        return leftLength.CompareTo(rightLength);
    }

    private static ushort[] BuildCatalogNameWeights()
    {
        // Inside Macintosh: Text, RelString; the exceptions here follow the Mac OS 9 ROM rules
        // recorded in docs/formats/HFS-MFS.md section 6.6. Unlisted Mac Roman bytes keep their code order.
        var weights = new ushort[256];
        for (int value = 0; value < weights.Length; value++) weights[value] = (ushort)(value << 8);
        for (char value = 'a'; value <= 'z'; value++) Set(value.ToString(), (ushort)((value - 32) << 8));
        void Set(string chars, ushort weight)
        {
            foreach (char value in chars)
            {
                if (!MacRoman.TryGetByte(value, out byte encoded))
                    throw new InvalidOperationException($"The HFS comparison table contains an unencodable character: {value}.");
                weights[encoded] = weight;
            }
        }

        Set("`", 0x4180);
        Set("\u00A0", 0x2000);
        Set("äÄ", 0x4108); Set("åÅ", 0x410C); Set("àÀ", 0x4104); Set("ãÃ", 0x410A);
        Set("æÆ", 0x4114); Set("çÇ", 0x4310); Set("éÉ", 0x4502); Set("ñÑ", 0x4E0A);
        Set("öÖ", 0x4F08); Set("õÕ", 0x4F0A); Set("øØ", 0x4F0E); Set("œŒ", 0x4F14);
        Set("üÜ", 0x5508);
        Set("á", 0x4182); Set("â", 0x4186); Set("è", 0x4584); Set("ê", 0x4586);
        Set("ë", 0x4588); Set("í", 0x4982); Set("ì", 0x4984); Set("î", 0x4986);
        Set("ï", 0x4988); Set("ó", 0x4F82); Set("ò", 0x4F84); Set("ô", 0x4F86);
        Set("ú", 0x5582); Set("ù", 0x5584); Set("û", 0x5586);
        Set("ß", 0x5382); Set("ÿ", 0x5988);
        Set("ª", 0x4192); Set("º", 0x4F92);
        Set("“", 0x2202); Set("”", 0x2204); Set("«", 0x2206); Set("»", 0x2208);
        Set("‘", 0x2702); Set("’", 0x2704);
        return weights;
    }

    private static void AdjustParentValence(List<(byte[] Key, byte[] Data)> records, uint parent, int adjustment)
    {
        var folder = records.FirstOrDefault(record => record.Data.Length >= 70 && record.Data[0] == 1 &&
            U32(new BigEndianReader(record.Data), 6) == parent);
        if (folder.Data is null) throw new InvalidDataException("The HFS parent folder record is missing.");
        ushort value = U16(new BigEndianReader(folder.Data), 4);
        int updated = value + adjustment;
        if (updated is < 0 or > ushort.MaxValue)
            throw new InvalidDataException("The HFS parent folder valence cannot represent this change.");
        new BigEndianWriter(folder.Data).WriteUInt16At(4, (ushort)updated);
    }

    private static void AddCount(byte[] image, int offset, int delta) =>
        new BigEndianWriter(image).WriteUInt32At(MdbOffset + offset,
            checked((uint)((long)U32(new BigEndianReader(image), MdbOffset + offset) + delta)));

    private static void AddShortCount(byte[] image, int offset, int delta) =>
        new BigEndianWriter(image).WriteUInt16At(MdbOffset + offset,
            checked((ushort)(U16(new BigEndianReader(image), MdbOffset + offset) + delta)));
}
