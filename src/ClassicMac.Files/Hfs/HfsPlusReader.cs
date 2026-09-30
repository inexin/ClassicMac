using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// HFS Plus structures and offsets follow Apple Technical Note TN1150.
internal static class HfsPlusReader
{
    private const int HeaderOffset = 1024;
    private const int HeaderLength = 512;
    private const uint RootParentId = 1;
    private const uint RootFolderId = 2;
    private const uint BadBlockFileId = 5;

    public static IReadOnlyList<MacFile> Read(ForkData image, ContainerContext context)
    {
        byte[] header = image.Slice(HeaderOffset, HeaderLength).ToArray();
        ushort signature = U16(header, 0);
        ushort version = U16(header, 2);
        if (signature is not (0x482B or 0x4858))
            throw new InvalidDataException($"Unknown HFS Plus volume signature 0x{signature:X4}.");
        if ((signature == 0x482B && version != 4) || (signature == 0x4858 && version != 5))
            throw new InvalidDataException($"Unsupported HFS Plus version {version}.");
        ReportAlternateHeaderProblem(image, signature, version, context);
        uint blockSize = U32(header, 40);
        uint totalBlocks = U32(header, 44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 ||
            totalBlocks == 0 || (ulong)blockSize * totalBlocks > (ulong)image.Length)
            throw new InvalidDataException("The HFS Plus allocation area is invalid.");
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(112, 8)) == 0)
            throw new InvalidDataException("The HFS Plus volume has no allocation file.");

        var overflow = new Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>();
        var allocationExtents = new List<(uint Start, uint End)>();
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(192, 8)) != 0)
        {
            ForkData extentsFork = ReadFork(image, header.AsSpan(192, 80), blockSize, totalBlocks,
                allocationExtents: allocationExtents);
            foreach (var (key, data) in LeafRecords(
                extentsFork.ToArray(context.Options.MaxExpandedBytesPerInput), "extents-overflow"))
            {
                if (key.Length != 12 || U16(key, 0) != 10 || data.Length < 64 || key[2] is not (0 or 0xFF))
                    throw new InvalidDataException("An HFS Plus extents-overflow record is invalid.");
                var id = (Fork: key[2], File: U32(key, 4));
                if (id.File == BadBlockFileId && id.Fork != 0)
                    throw new InvalidDataException("An HFS Plus bad-block extent must use the data fork.");
                uint start = U32(key, 8);
                AddExtentRecord(data.AsSpan(0, 64), totalBlocks, allocationExtents,
                    "An HFS Plus overflow extent lies outside the allocation area.");
                if (!overflow.TryGetValue(id, out var entries)) overflow[id] = entries = [];
                entries.Add((start, data.AsSpan(0, 64).ToArray()));
            }
        }

        ForkData allocationFork = ReadFork(image, header.AsSpan(112, 80), blockSize, totalBlocks,
            overflow, 0, 6, allocationExtents);
        byte[] allocationBitmap = allocationFork.ToArray(context.Options.MaxExpandedBytesPerInput);
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(432, 8)) != 0)
            _ = ReadFork(image, header.AsSpan(432, 80), blockSize, totalBlocks, overflow, 0, 7,
                allocationExtents);
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(352, 8)) != 0)
        {
            ForkData attributesFork = ReadFork(image, header.AsSpan(352, 80), blockSize, totalBlocks,
                overflow, 0, 8, allocationExtents);
            byte[] attributes = attributesFork.ToArray(context.Options.MaxExpandedBytesPerInput);
            foreach (var (_, data) in LeafRecords(attributes, "attributes"))
                AddAttributeRecordExtents(data, blockSize, totalBlocks, allocationExtents);
        }

        var catalogFork = ReadFork(image, header.AsSpan(272, 80), blockSize, totalBlocks, overflow, 0, 4,
            allocationExtents);
        byte[] catalog = catalogFork.ToArray();
        var records = LeafRecords(catalog, "catalog", isHfsX: signature == 0x4858).ToArray();
        var folders = new Dictionary<uint, (uint Parent, string Name, uint Valence)>();
        var catalogIds = new HashSet<uint>();
        var catalogNodes = new Dictionary<uint, CatalogNode>();
        var catalogThreads = new Dictionary<uint, CatalogThread>();
        foreach (var (key, data) in records)
        {
            if (key.Length < 8 || data.Length < 2)
                throw new InvalidDataException("An HFS Plus catalog record is truncated.");
            switch (U16(data, 0))
            {
                case 1:
                    if (data.Length != 88)
                        throw new InvalidDataException("An HFS Plus folder record must be exactly 88 bytes.");
                    if ((U16(data, 2) & 0x0003) != 0)
                        throw new InvalidDataException("An HFS Plus folder record sets file-only flags.");
                    uint id = U32(data, 8);
                    if (id < 16 && id != RootFolderId)
                        throw new InvalidDataException($"HFS Plus folder catalog ID {id} is reserved.");
                    if (!catalogIds.Add(id))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    uint parent = U32(key, 2);
                    string name = Name(key);
                    if (!folders.TryAdd(id, (parent, name, U32(data, 4))))
                        throw new InvalidDataException("Duplicate HFS Plus folder ID.");
                    catalogNodes.Add(id, new CatalogNode(parent, name, IsFolder: true));
                    break;
                case 2:
                    if (data.Length != 248)
                        throw new InvalidDataException("An HFS Plus file record must be exactly 248 bytes.");
                    uint fileId = U32(data, 8);
                    if (fileId < 16)
                        throw new InvalidDataException($"HFS Plus file catalog ID {fileId} is reserved.");
                    if (!catalogIds.Add(fileId))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    if ((U16(data, 2) & 0x0002) == 0)
                        throw new InvalidDataException("An HFS Plus file is missing its required thread flag.");
                    catalogNodes.Add(fileId, new CatalogNode(U32(key, 2), Name(key), IsFolder: false));
                    break;
                case 3 or 4:
                    if (key.Length != 8 || U16(key, 0) != 6 || U16(key, 6) != 0)
                        throw new InvalidDataException("An HFS Plus catalog thread key is invalid.");
                    if (data.Length < 10) throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    if (data.Length > 520)
                        throw new InvalidDataException("An HFS Plus catalog thread record exceeds 520 bytes.");
                    ushort threadNameLength = U16(data, 8);
                    if (threadNameLength > 255 || data.Length < 10 + 2 * threadNameLength)
                        throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    if (!HfsPlusUnicodeNormalization.IsCanonical(data.AsSpan(10, threadNameLength * 2)))
                        throw new InvalidDataException(
                            "An HFS Plus catalog thread name is not canonically decomposed.");
                    uint threadId = U32(key, 2);
                    var thread = new CatalogThread(U32(data, 4),
                        Encoding.BigEndianUnicode.GetString(data, 10, threadNameLength * 2)
                            .Normalize(NormalizationForm.FormC),
                        IsFolder: U16(data, 0) == 3);
                    if (!catalogThreads.TryAdd(threadId, thread))
                        throw new InvalidDataException("Duplicate HFS Plus catalog thread ID.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown HFS Plus catalog record type {U16(data, 0)}.");
            }
        }
        if (!folders.ContainsKey(RootFolderId))
            throw new InvalidDataException("The HFS Plus root folder is missing.");
        if (folders[RootFolderId].Parent != RootParentId)
            throw new InvalidDataException("The HFS Plus root folder does not use the reserved root parent ID.");
        const uint catalogNodeIdsReused = 1u << 12;
        uint nextCatalogId = U32(header, 64);
        if (nextCatalogId < 16)
            throw new InvalidDataException("The HFS Plus next catalog ID is reserved.");
        if ((U32(header, 4) & catalogNodeIdsReused) == 0 && nextCatalogId <= catalogIds.Max())
            throw new InvalidDataException("The HFS Plus next catalog ID is not greater than all catalog IDs.");
        ValidateCatalogThreads(catalogNodes, catalogThreads);
        var childCounts = new Dictionary<uint, uint>(folders.Count);
        foreach (uint folderId in folders.Keys) childCounts.Add(folderId, 0);
        foreach (CatalogNode node in catalogNodes.Values)
            if (childCounts.TryGetValue(node.Parent, out uint childCount))
                childCounts[node.Parent] = checked(childCount + 1);
        foreach (var (folderId, folder) in folders)
        {
            uint childCount = childCounts[folderId];
            if (folder.Valence != childCount)
                throw new InvalidDataException(
                    $"HFS Plus folder {folderId} has valence {folder.Valence}, but {childCount} catalog children.");
            if (folderId != RootFolderId)
                _ = FolderPath(folder.Parent, folders);
        }

        uint? privateDataFolderId = folders
            .Where(folder => folder.Value.Parent == RootFolderId && folder.Value.Name == "\0\0\0\0HFS+ Private Data")
            .Select(folder => (uint?)folder.Key)
            .SingleOrDefault();
        var privateDataFolderIds = new HashSet<uint>();
        if (privateDataFolderId is { } privateId)
        {
            privateDataFolderIds.Add(privateId);
            bool added;
            do
            {
                added = false;
                foreach (var (id, folder) in folders)
                    if (privateDataFolderIds.Contains(folder.Parent) && privateDataFolderIds.Add(id))
                        added = true;
            }
            while (added);
        }

        var catalogFiles = new Dictionary<uint, CatalogFileData>();
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            byte[] info = [.. data.AsSpan(48, 16), .. data.AsSpan(64, 16)];
            uint fileId = U32(data, 8);
            uint linkReference = U32(data, 44);
            FinderInfo finderInfo = FinderInfo.Read(info);
            if (IsHardLinkFile(finderInfo) && linkReference == 0)
                throw new InvalidDataException("An HFS Plus hard link has the reserved zero link reference.");
            catalogFiles.Add(fileId, new CatalogFileData(
                Name(key), U32(key, 2), linkReference, U16(data, 42), finderInfo,
                Date(U32(data, 12)), Date(U32(data, 16)),
                ReadFork(image, data.AsSpan(88, 80), blockSize, totalBlocks, overflow, 0, fileId,
                    allocationExtents),
                ReadFork(image, data.AsSpan(168, 80), blockSize, totalBlocks, overflow, 0xFF, fileId,
                    allocationExtents)));
        }

        var hardLinkTargets = new Dictionary<uint, CatalogFileData>();
        if (privateDataFolderId is { } dataFolderId)
            foreach (CatalogFileData file in catalogFiles.Values)
            {
                if (file.Parent != dataFolderId || !file.Name.StartsWith("iNode", StringComparison.Ordinal) ||
                    !uint.TryParse(file.Name.AsSpan(5), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out uint reference))
                    continue;
                if (!hardLinkTargets.TryAdd(reference, file))
                    throw new InvalidDataException("Duplicate HFS Plus hard-link indirect node reference.");
            }

        var result = new List<MacFile>();
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            string name = Name(key);
            uint parent = U32(key, 2);
            uint fileId = U32(data, 8);
            if (privateDataFolderIds.Contains(parent)) continue;
            CatalogFileData file = catalogFiles[fileId];
            bool isHardLink = IsHardLinkFile(file.FinderInfo);
            CatalogFileData target = default;
            bool hasHardLinkTarget = isHardLink && hardLinkTargets.TryGetValue(file.LinkReference, out target);
            if (isHardLink && !hasHardLinkTarget)
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-target-missing",
                    $"The HFS Plus hard link '{name}' has no matching indirect node.");
            CatalogFileData content = hasHardLinkTarget ? target : file;
            var path = FolderPath(parent, folders);
            result.Add(new MacFile
            {
                Name = LegacyName(name),
                UnicodeName = name,
                FolderPath = path.Select(LegacyName).ToArray(),
                UnicodeFolderPath = path,
                FinderInfo = content.FinderInfo,
                Created = content.Created,
                Modified = content.Modified,
                DataFork = content.DataFork,
                ResourceFork = content.ResourceFork,
                SymbolicLinkTarget = ReadSymbolicLinkTarget(content.Mode, content.FinderInfo,
                    content.DataFork, content.ResourceFork,
                    context.Options.MaxExpandedBytesPerInput),
                HardLinkReference = isHardLink ? file.LinkReference : null,
            });
        }
        ValidateAllocationExtents(allocationExtents);
        ValidateAllocationBitmap(allocationBitmap, totalBlocks, blockSize, allocationExtents);
        uint freeBlocks = CountFreeAllocationBlocks(allocationBitmap, totalBlocks);
        uint declaredFreeBlocks = U32(header, 48);
        if (freeBlocks != declaredFreeBlocks)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-free-blocks",
                $"The allocation bitmap has {freeBlocks} free blocks; the volume header says {declaredFreeBlocks}.");
        uint expectedFiles = U32(header, 32);
        uint expectedFolders = U32(header, 36);
        if (catalogFiles.Count != expectedFiles || folders.Count - 1 != expectedFolders)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-counts",
                $"The HFS Plus catalog has {catalogFiles.Count} files and {folders.Count - 1} folders; " +
                $"the volume header says {expectedFiles} and {expectedFolders}.");
        return result;
    }

    private static void ReportAlternateHeaderProblem(ForkData image, ushort signature, ushort version,
        ContainerContext context)
    {
        if (image.Length < HeaderOffset + HeaderLength + 1024)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-alternate-header",
                "The HFS Plus volume is too small to contain an alternate volume header.");
            return;
        }

        long offset = image.Length - 1024;
        byte[] alternate = image.Slice(offset, HeaderLength).ToArray(HeaderLength);
        if (U16(alternate, 0) != signature || U16(alternate, 2) != version)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-alternate-header",
                "The HFS Plus alternate volume header is missing or has an invalid signature or version.", offset);
    }

    private static void ValidateAllocationExtents(List<(uint Start, uint End)> extents)
    {
        // TN1150's allocation-file consistency check assigns allocation blocks to fork extents. A block cannot
        // belong to two extents in a valid volume; this follows from that ownership model.
        extents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int index = 1; index < extents.Count; index++)
            if (extents[index].Start < extents[index - 1].End)
                throw new InvalidDataException("HFS Plus forks claim overlapping allocation blocks.");
    }

    private static void AddAttributeRecordExtents(byte[] data, uint blockSize, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents)
    {
        if (data.Length < 4) throw new InvalidDataException("An HFS Plus attribute record is truncated.");
        switch (U32(data, 0))
        {
            case 0x10:
                return;
            case 0x20:
                if (data.Length < 88)
                    throw new InvalidDataException("An HFS Plus fork-data attribute is truncated.");
                ReadOnlySpan<byte> fork = data.AsSpan(8, 80);
                ulong logical = BinaryPrimitives.ReadUInt64BigEndian(fork);
                uint allocated = U32(fork, 12);
                if (logical > (ulong)allocated * blockSize)
                    throw new InvalidDataException("An HFS Plus fork-data attribute has insufficient extents.");
                uint covered = 0;
                for (int index = 0; index < 8; index++)
                {
                    uint start = U32(fork, 16 + index * 8);
                    uint count = U32(fork, 20 + index * 8);
                    if (count == 0) break;
                    if (count > allocated - covered)
                        throw new InvalidDataException("An HFS Plus fork-data attribute exceeds its allocated blocks.");
                    AddAllocationExtent(start, count, totalBlocks, allocationExtents,
                        "An HFS Plus fork-data attribute lies outside the allocation area.");
                    covered = checked(covered + count);
                }
                return;
            case 0x30:
                if (data.Length < 72)
                    throw new InvalidDataException("An HFS Plus attribute extension record is truncated.");
                AddExtentRecord(data.AsSpan(8, 64), totalBlocks, allocationExtents,
                    "An HFS Plus attribute extension extent lies outside the allocation area.");
                return;
            default:
                return;
        }
    }

    private static void AddExtentRecord(ReadOnlySpan<byte> extents, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage)
    {
        for (int index = 0; index < 8; index++)
        {
            uint start = U32(extents, index * 8);
            uint count = U32(extents, index * 8 + 4);
            if (count == 0) break;
            AddAllocationExtent(start, count, totalBlocks, allocationExtents, outOfRangeMessage);
        }
    }

    private static void AddAllocationExtent(uint start, uint count, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage)
    {
        if ((ulong)start + count > totalBlocks) throw new InvalidDataException(outOfRangeMessage);
        allocationExtents.Add((start, checked(start + count)));
    }

    private static void ValidateAllocationBitmap(byte[] bitmap, uint totalBlocks, uint blockSize,
        List<(uint Start, uint End)> extents)
    {
        ulong requiredBytes = ((ulong)totalBlocks + 7) / 8;
        if ((ulong)bitmap.Length < requiredBytes)
            throw new InvalidDataException("The HFS Plus allocation file is too short for the volume.");

        int wholeBitmapBytes = checked((int)(totalBlocks / 8));
        int remainingBits = checked((int)(totalBlocks % 8));
        if (remainingBits != 0)
        {
            int unusedBitMask = (1 << (8 - remainingBits)) - 1;
            if ((bitmap[wholeBitmapBytes] & unusedBitMask) != 0)
                throw new InvalidDataException("Unused HFS Plus allocation bitmap bits must be clear.");
            wholeBitmapBytes++;
        }
        for (int index = wholeBitmapBytes; index < bitmap.Length; index++)
            if (bitmap[index] != 0)
                throw new InvalidDataException("Unused HFS Plus allocation bitmap bits must be clear.");

        uint firstAreaEnd = checked((uint)Math.Min(totalBlocks, (1536UL + blockSize - 1) / blockSize));
        ulong volumeBytes = (ulong)totalBlocks * blockSize;
        uint lastAreaStart = checked((uint)((volumeBytes > 1024 ? volumeBytes - 1024 : 0) / blockSize));
        RequireAllocationRange(bitmap, 0, firstAreaEnd);
        RequireAllocationRange(bitmap, lastAreaStart, totalBlocks);
        foreach (var (start, end) in extents)
            RequireAllocationRange(bitmap, start, end);
    }

    private static void RequireAllocationRange(byte[] bitmap, uint start, uint end)
    {
        uint block = start;
        while (block < end)
        {
            uint blockInByte = block & 7;
            uint count = Math.Min(end - block, 8 - blockInByte);
            int lastBit = checked((int)(blockInByte + count - 1));
            int mask = (0xFF >> checked((int)blockInByte)) & (0xFF << (7 - lastBit));
            int byteIndex = checked((int)(block >> 3));
            if ((bitmap[byteIndex] & mask) != mask)
                throw new InvalidDataException("An HFS Plus allocation block is marked free in the allocation file.");
            block += count;
        }
    }

    private static uint CountFreeAllocationBlocks(byte[] bitmap, uint totalBlocks)
    {
        int wholeBytes = checked((int)(totalBlocks / 8));
        uint allocated = 0;
        for (int index = 0; index < wholeBytes; index++)
            allocated = checked(allocated + (uint)System.Numerics.BitOperations.PopCount((uint)bitmap[index]));

        int remainingBits = checked((int)(totalBlocks % 8));
        if (remainingBits != 0)
        {
            byte validBits = (byte)(0xFF << (8 - remainingBits));
            allocated = checked(allocated +
                (uint)System.Numerics.BitOperations.PopCount((uint)(bitmap[wholeBytes] & validBits)));
        }
        return totalBlocks - allocated;
    }

    private static void ValidateCatalogThreads(Dictionary<uint, CatalogNode> nodes,
        Dictionary<uint, CatalogThread> threads)
    {
        foreach (var (id, node) in nodes)
        {
            if (!threads.TryGetValue(id, out var thread))
                throw new InvalidDataException($"The HFS Plus catalog node {id} has no thread record.");
            if (thread.IsFolder != node.IsFolder || thread.Parent != node.Parent ||
                !string.Equals(thread.Name, node.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"The HFS Plus catalog thread for node {id} is inconsistent.");
        }
        if (threads.Count != nodes.Count)
            throw new InvalidDataException("The HFS Plus catalog has a thread for a missing node.");
    }

    private static ForkData ReadFork(ForkData image, ReadOnlySpan<byte> fork, uint blockSize, uint totalBlocks,
        Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>? overflow = null,
        byte forkType = 0, uint fileId = 0, List<(uint Start, uint End)>? allocationExtents = null)
    {
        ulong logical = BinaryPrimitives.ReadUInt64BigEndian(fork);
        if (logical > long.MaxValue) throw new InvalidDataException("An HFS Plus fork is too large.");
        uint allocatedBlocks = U32(fork, 12);
        if (allocatedBlocks == 0 && logical != 0)
            throw new InvalidDataException("A nonempty HFS Plus fork has no allocated blocks.");
        if (allocatedBlocks == 0) return ForkData.Empty;
        var ranges = new List<(long Offset, long Length)>();
        uint coveredBlocks = 0;
        void AddExtents(ReadOnlySpan<byte> extents, bool addToAllocationOwnership)
        {
            for (int index = 0; index < 8; index++)
            {
                uint start = U32(extents, index * 8);
                uint count = U32(extents, index * 8 + 4);
                if (count == 0) break;
                if (count > allocatedBlocks - coveredBlocks)
                    throw new InvalidDataException("An HFS Plus fork's extents exceed its allocated block count.");
                if ((ulong)start + count > totalBlocks)
                    throw new InvalidDataException("An HFS Plus extent lies outside the allocation area.");
                long offset = checked((long)start * blockSize);
                long length = checked((long)count * blockSize);
                if (offset > image.Length - length)
                    throw new InvalidDataException("An HFS Plus extent lies outside the image.");
                ranges.Add((offset, length));
                if (addToAllocationOwnership)
                    allocationExtents?.Add((start, checked(start + count)));
                coveredBlocks = checked(coveredBlocks + count);
            }
        }
        AddExtents(fork.Slice(16, 64), addToAllocationOwnership: true);
        if (coveredBlocks < allocatedBlocks && overflow is not null &&
            overflow.TryGetValue((forkType, fileId), out var entries))
            foreach (var entry in entries.OrderBy(e => e.Start))
            {
                if (coveredBlocks >= allocatedBlocks) break;
                if (entry.Start != coveredBlocks)
                    throw new InvalidDataException("An HFS Plus overflow extent is not contiguous with the fork.");
                AddExtents(entry.Extents, addToAllocationOwnership: false);
            }
        if (coveredBlocks != allocatedBlocks)
            throw new InvalidDataException("An HFS Plus fork's extent count differs from its allocated block count.");
        if ((ulong)coveredBlocks * blockSize < logical)
            throw new InvalidDataException("An HFS Plus fork has insufficient extents for its logical length.");
        if (logical == 0) return ForkData.Empty;
        return new ExtentForkData(image, ranges, checked((long)logical));
    }

    private static IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(byte[] tree, string name,
        bool isHfsX = false)
    {
        if (tree.Length < 512 || tree[8] != 1)
            throw new InvalidDataException($"The HFS Plus {name} tree has no B-tree header.");
        if (U32(tree, 4) != 0 || tree[9] != 0 || U16(tree, 10) != 3)
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node is invalid.");
        if (tree[14 + 36] != 0)
            throw new InvalidDataException($"The HFS Plus {name} B-tree has an invalid tree type.");
        uint attributes = U32(tree, 14 + 38);
        bool variableIndexKeys = (attributes & 0x00000004) != 0;
        bool hasVariableIndexKeys = name is "catalog" or "attributes";
        if ((attributes & 0x00000002) == 0 || variableIndexKeys != hasVariableIndexKeys)
            throw new InvalidDataException($"The HFS Plus {name} B-tree key-layout attributes are invalid.");
        bool caseSensitiveCatalog = false;
        bool caseFoldingCatalog = name == "catalog" && !isHfsX;
        if (name == "catalog" && isHfsX)
        {
            byte keyCompareType = tree[14 + 37];
            if (keyCompareType is not (0xBC or 0xCF))
                throw new InvalidDataException("The HFSX catalog has an unsupported key comparison type.");
            caseSensitiveCatalog = keyCompareType == 0xBC;
            caseFoldingCatalog = keyCompareType == 0xCF;
        }
        int nodeSize = U16(tree, 32);
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0 || tree.Length % nodeSize != 0)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is invalid.");
        int maxKeyLength = U16(tree, 34);
        int definedMaxKeyLength = name switch
        {
            "catalog" => 516,
            "extents-overflow" => 10,
            // HFSPlusAttrKey is 268 bytes including its 2-byte keyLength field (Apple hfs_format.h).
            "attributes" => 266,
            _ => maxKeyLength
        };
        if (maxKeyLength != definedMaxKeyLength)
            throw new InvalidDataException($"The HFS Plus {name} B-tree maximum key length is invalid.");
        ValidateHeaderNodeRecordLayout(tree.AsSpan(0, nodeSize), nodeSize, name);
        if ((name is "catalog" or "attributes") && nodeSize < 4096)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is below the 4 KiB minimum.");
        uint totalNodes = U32(tree, 36);
        if (totalNodes == 0 || totalNodes != tree.Length / nodeSize)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node count is invalid.");
        uint first = U32(tree, 24);
        uint last = U32(tree, 28);
        ushort depth = U16(tree, 14);
        uint root = U32(tree, 16);
        uint expectedRecords = U32(tree, 20);
        if (expectedRecords == 0)
        {
            if (depth != 0 || root != 0 || first != 0 || last != 0)
                throw new InvalidDataException($"The empty HFS Plus {name} B-tree has root or leaf nodes.");
            ValidateNodeMap(tree, nodeSize, totalNodes, []);
            yield break;
        }
        if (depth == 0 || root == 0 || root >= totalNodes || first == 0 || last == 0 ||
            first >= totalNodes || last >= totalNodes)
            throw new InvalidDataException($"The HFS Plus {name} B-tree root or leaf endpoints are invalid.");
        int rootOffset = checked((int)root * nodeSize);
        if (depth == 1)
        {
            if (root != first || first != last || tree[rootOffset + 8] != 0xFF || tree[rootOffset + 9] != 1)
                throw new InvalidDataException($"The single-leaf HFS Plus {name} B-tree has an invalid root.");
        }
        else if (tree[rootOffset + 8] != 0 || tree[rootOffset + 9] != depth)
        {
            throw new InvalidDataException($"The HFS Plus {name} B-tree root kind or height is invalid.");
        }
        (HashSet<uint> indexedLeaves, HashSet<uint> indexedNodes) =
            ValidateIndexGraph(tree, name, nodeSize, maxKeyLength, totalNodes, root, depth, caseSensitiveCatalog,
                caseFoldingCatalog);
        uint readRecords = 0;
        uint previous = 0;
        uint finalLeaf = 0;
        var seen = new HashSet<uint>();
        var keys = new HashSet<byte[]>(ByteArrayEqualityComparer.Instance);
        byte[]? previousExtentKey = null;
        byte[]? previousCatalogKey = null;
        byte[]? previousAttributeKey = null;
        for (uint node = first; node != 0; node = U32(tree, checked((int)node * nodeSize)))
        {
            if (!seen.Add(node) || node >= totalNodes)
                throw new InvalidDataException($"The HFS Plus {name} B-tree leaf chain is invalid.");
            int start = checked((int)node * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(tree.AsSpan(start, nodeSize), nodeSize, name);
            if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                throw new InvalidDataException($"An HFS Plus {name} B-tree linked leaf has an invalid type.");
            if (U32(tree, start + 4) != previous)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has an invalid backward link.");
            int count = U16(tree, start + 10);
            if (count > (nodeSize - 14) / 2)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has too many records.");
            for (int index = 0; index < count; index++)
            {
                int begin = U16(tree, start + nodeSize - 2 * (index + 1));
                int end = U16(tree, start + nodeSize - 2 * (index + 2));
                if (begin < 14 || end <= begin || end > nodeSize - 2 * (count + 1))
                    throw new InvalidDataException($"An HFS Plus {name} B-tree record offset is invalid.");
                int offset = start + begin;
                int keyLength = U16(tree, offset);
                if (keyLength < 6 || 2 + keyLength > end - begin)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree key is invalid.");
                int dataOffset = offset + 2 + keyLength;
                byte[] key = tree.AsSpan(offset, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    ValidateCatalogKey(key);
                    ReadOnlySpan<byte> recordData = tree.AsSpan(dataOffset, start + end - dataOffset);
                    if (U16(key, 6) == 0 && recordData.Length >= 2 && U16(recordData, 0) is 1 or 2)
                        throw new InvalidDataException("An HFS Plus file or folder catalog key has an empty name.");
                    if (caseSensitiveCatalog || caseFoldingCatalog)
                    {
                        if (previousCatalogKey is not null &&
                            CompareCatalogKeys(previousCatalogKey, key, caseFoldingCatalog) >= 0)
                            throw new InvalidDataException("The HFS Plus catalog keys are not strictly ordered.");
                        previousCatalogKey = key;
                    }
                    else if (!keys.Add(key))
                    {
                        throw new InvalidDataException("The HFS Plus catalog B-tree has a duplicate leaf key.");
                    }
                }
                else if (name == "extents-overflow")
                {
                    if (key.Length != 12 || keyLength != 10 || key[2] is not (0 or 0xFF))
                        throw new InvalidDataException("An HFS Plus extents-overflow key is invalid.");
                    if (previousExtentKey is not null && CompareExtentKeys(previousExtentKey, key) >= 0)
                        throw new InvalidDataException("The HFS Plus extents-overflow keys are not strictly ordered.");
                    previousExtentKey = key;
                }
                else if (name == "attributes")
                {
                    ValidateAttributeKey(key);
                    if (previousAttributeKey is not null && CompareAttributeKeys(previousAttributeKey, key) >= 0)
                        throw new InvalidDataException("The HFS Plus attributes B-tree keys are not strictly ordered.");
                    previousAttributeKey = key;
                }
                else if (!keys.Add(key))
                    throw new InvalidDataException($"The HFS Plus {name} B-tree has a duplicate leaf key.");
                yield return (key,
                    tree.AsSpan(dataOffset, start + end - dataOffset).ToArray());
                readRecords++;
            }
            finalLeaf = node;
            previous = node;
        }
        if (readRecords != expectedRecords)
            throw new InvalidDataException($"The HFS Plus {name} B-tree leaf-record count is inconsistent.");
        if (finalLeaf != last)
            throw new InvalidDataException($"The HFS Plus {name} B-tree ends at leaf {finalLeaf}, not {last}.");
        if (!seen.SetEquals(indexedLeaves))
            throw new InvalidDataException($"The HFS Plus {name} B-tree index and leaf chain disagree.");
        ValidateNodeMap(tree, nodeSize, totalNodes, indexedNodes);
    }

    private static void ValidateHeaderNodeRecordLayout(ReadOnlySpan<byte> headerNode, int nodeSize, string name)
    {
        int freeSpaceOffset = nodeSize - 8;
        if (U16(headerNode, nodeSize - 2) != 14 ||
            U16(headerNode, nodeSize - 4) != 14 + 106 ||
            U16(headerNode, nodeSize - 6) != 14 + 106 + 128 ||
            U16(headerNode, freeSpaceOffset) != freeSpaceOffset)
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node has an invalid record layout.");
    }

    private static void RequireFirstRecordStartsAtNodeDescriptorEnd(ReadOnlySpan<byte> node, int nodeSize,
        string name)
    {
        if (U16(node, 10) != 0 && U16(node, nodeSize - 2) != 14)
            throw new InvalidDataException($"An HFS Plus {name} B-tree record does not start after its node descriptor.");
    }

    private static (HashSet<uint> Leaves, HashSet<uint> Nodes) ValidateIndexGraph(byte[] tree, string name,
        int nodeSize, int maxKeyLength, uint totalNodes, uint root, ushort depth, bool caseSensitiveCatalog,
        bool caseFoldingCatalog)
    {
        var visitedNodes = new HashSet<uint>();
        var leafNodes = new HashSet<uint>();
        var nodesByHeight = new Dictionary<ushort, List<uint>>();
        var indexKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)>();
        var subtreeKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)?>();
        bool validateChildKeyRanges = name == "extents-overflow" || name == "attributes" ||
            (name == "catalog" && (caseSensitiveCatalog || caseFoldingCatalog));

        void AddAtHeight(uint nodeNumber, ushort height)
        {
            if (!nodesByHeight.TryGetValue(height, out var nodes)) nodesByHeight.Add(height, nodes = []);
            nodes.Add(nodeNumber);
        }

        (byte[] First, byte[] Last)? Visit(uint nodeNumber, ushort expectedHeight)
        {
            if (nodeNumber >= totalNodes || !visitedNodes.Add(nodeNumber))
                throw new InvalidDataException($"The HFS Plus {name} B-tree index graph is cyclic or out of range.");
            int start = checked((int)nodeNumber * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(tree.AsSpan(start, nodeSize), nodeSize, name);
            if (expectedHeight == 1)
            {
                if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index points to a non-leaf node.");
                AddAtHeight(nodeNumber, expectedHeight);
                leafNodes.Add(nodeNumber);
                var leafRange = LeafKeyRange(tree, start, nodeSize, maxKeyLength, name);
                subtreeKeyRanges.Add(nodeNumber, leafRange);
                return leafRange;
            }

            if (tree[start + 8] != 0 || tree[start + 9] != expectedHeight)
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid kind or height.");
            AddAtHeight(nodeNumber, expectedHeight);
            int count = U16(tree, start + 10);
            if (count < 2 || count > (nodeSize - 14) / 2)
                throw new InvalidDataException($"An HFS Plus {name} B-tree index node has an invalid record count.");
            int offsetTableStart = nodeSize - 2 * (count + 1);
            byte[]? previousIndexKey = null;
            byte[]? firstIndexKey = null;
            var childRanges = new List<(byte[] Key, (byte[] First, byte[] Last)? Range)>(count);
            for (int index = 0; index < count; index++)
            {
                int begin = U16(tree, start + nodeSize - 2 * (index + 1));
                int end = U16(tree, start + nodeSize - 2 * (index + 2));
                if (begin < 14 || end <= begin || end > offsetTableStart)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record offset is invalid.");
                int keyLength = U16(tree, start + begin);
                if (keyLength < 6 || keyLength > maxKeyLength)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index key length is invalid.");
                byte[] indexKey = tree.AsSpan(start + begin, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    ValidateCatalogKey(indexKey);
                    if ((caseSensitiveCatalog || caseFoldingCatalog) && previousIndexKey is not null &&
                        CompareCatalogKeys(previousIndexKey, indexKey, caseFoldingCatalog) >= 0)
                        throw new InvalidDataException("The HFS Plus catalog index keys are not strictly ordered.");
                }
                else if (name == "extents-overflow")
                {
                    if (keyLength != 10 || indexKey[2] is not (0 or 0xFF))
                        throw new InvalidDataException("An HFS Plus extents-overflow index key is invalid.");
                    if (previousIndexKey is not null && CompareExtentKeys(previousIndexKey, indexKey) >= 0)
                        throw new InvalidDataException("The HFS Plus extents-overflow index keys are not strictly ordered.");
                }
                else if (name == "attributes")
                {
                    ValidateAttributeKey(indexKey);
                    if (previousIndexKey is not null && CompareAttributeKeys(previousIndexKey, indexKey) >= 0)
                        throw new InvalidDataException("The HFS Plus attributes index keys are not strictly ordered.");
                }
                firstIndexKey ??= indexKey;
                previousIndexKey = indexKey;
                int storedKeyLength = (name is "catalog" or "attributes") ? keyLength : maxKeyLength;
                int childOffset = begin + 2 + storedKeyLength;
                if ((childOffset & 1) != 0) childOffset++;
                if (childOffset + 4 != end)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record has an invalid length.");
                var childRange = Visit(U32(tree, start + childOffset), checked((ushort)(expectedHeight - 1)));
                childRanges.Add((indexKey, childRange));
            }
            indexKeyRanges.Add(nodeNumber, (firstIndexKey!, previousIndexKey!));
            if (validateChildKeyRanges)
            {
                for (int index = 0; index < childRanges.Count; index++)
                {
                    var (separator, childRange) = childRanges[index];
                    if (childRange is not { } child) continue;
                    if (CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog, separator, child.First) > 0)
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree index key sorts after a key in its child subtree.");
                    if (index > 0 && childRanges[index - 1].Range is { } previousChild &&
                        CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog,
                            previousChild.Last, separator) >= 0)
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree child contains a key beyond its index range.");
                }
            }
            (byte[] First, byte[] Last)? subtreeRange = null;
            foreach (var child in childRanges)
            {
                if (child.Range is not { } range) continue;
                subtreeRange = subtreeRange is { } existing
                    ? (existing.First, range.Last)
                    : range;
            }
            subtreeKeyRanges.Add(nodeNumber, subtreeRange);
            return subtreeRange;
        }

        _ = Visit(root, depth);
        foreach (var (height, nodes) in nodesByHeight)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                int start = checked((int)nodes[index] * nodeSize);
                uint expectedForward = index + 1 < nodes.Count ? nodes[index + 1] : 0;
                uint expectedBackward = index > 0 ? nodes[index - 1] : 0;
                if (U32(tree, start) != expectedForward || U32(tree, start + 4) != expectedBackward)
                    throw new InvalidDataException(
                        $"The HFS Plus {name} B-tree height-{height} sibling links are invalid.");
                if (index > 0 && height > 1 && indexKeyRanges.TryGetValue(nodes[index - 1], out var previousRange))
                {
                    var currentRange = indexKeyRanges[nodes[index]];
                    int comparison = name switch
                    {
                        "catalog" when caseSensitiveCatalog || caseFoldingCatalog =>
                            CompareCatalogKeys(previousRange.Last, currentRange.First, caseFoldingCatalog),
                        "extents-overflow" => CompareExtentKeys(previousRange.Last, currentRange.First),
                        "attributes" => CompareAttributeKeys(previousRange.Last, currentRange.First),
                        _ => -1
                    };
                    if (comparison >= 0)
                        throw new InvalidDataException(
                            $"The HFS Plus {name} B-tree index keys are not strictly ordered across sibling nodes.");
                }
                if (index > 0 && height > 1 && validateChildKeyRanges &&
                    subtreeKeyRanges[nodes[index - 1]] is { } previousSubtree &&
                    subtreeKeyRanges[nodes[index]] is { } currentSubtree &&
                    CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog,
                        previousSubtree.Last, currentSubtree.First) >= 0)
                    throw new InvalidDataException(
                        $"The HFS Plus {name} B-tree child key ranges overlap across sibling nodes.");
            }
        }
        return (leafNodes, visitedNodes);
    }

    private static (byte[] First, byte[] Last)? LeafKeyRange(byte[] tree, int nodeStart, int nodeSize,
        int maxKeyLength, string name)
    {
        int count = U16(tree, nodeStart + 10);
        if (count == 0) return null;
        int offsetTableStart = nodeSize - 2 * (count + 1);

        byte[] ReadKey(int index)
        {
            int begin = U16(tree, nodeStart + nodeSize - 2 * (index + 1));
            int end = U16(tree, nodeStart + nodeSize - 2 * (index + 2));
            if (begin < 14 || end <= begin || end > offsetTableStart)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf record offset is invalid.");
            int keyLength = U16(tree, nodeStart + begin);
            if (keyLength < 6 || keyLength > maxKeyLength || begin + 2 + keyLength > end)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf key length is invalid.");
            byte[] key = tree.AsSpan(nodeStart + begin, 2 + keyLength).ToArray();
            if (name == "attributes") ValidateAttributeKey(key);
            return key;
        }

        return (ReadKey(0), ReadKey(count - 1));
    }

    private static int CompareTreeKeys(string name, bool caseSensitiveCatalog, bool caseFoldingCatalog,
        byte[] left, byte[] right) => name switch
    {
        "catalog" when caseSensitiveCatalog => CompareHfsXCatalogKeys(left, right),
        "catalog" when caseFoldingCatalog => CompareCatalogKeys(left, right, caseFolding: true),
        "extents-overflow" => CompareExtentKeys(left, right),
        "attributes" => CompareAttributeKeys(left, right),
        _ => throw new InvalidOperationException($"No key comparator is defined for the {name} B-tree.")
    };

    private static void ValidateAttributeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 14 || U16(key, 0) != key.Length - 2 || U16(key, 2) != 0)
            throw new InvalidDataException("The HFS Plus attributes B-tree key length or padding is invalid.");
        int nameLength = U16(key, 12);
        if (nameLength > 127 || key.Length != 14 + 2 * nameLength)
            throw new InvalidDataException("The HFS Plus attributes B-tree key name length is invalid.");
    }

    // Apple’s HFS comparator orders attribute keys by file ID, name length, binary UTF-16 name,
    // then start block (hfs_attrkeycompare in Apple’s HFS source).
    private static int CompareAttributeKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = U32(left, 4).CompareTo(U32(right, 4));
        if (comparison != 0) return comparison;

        int leftNameLength = U16(left, 12);
        int rightNameLength = U16(right, 12);
        comparison = leftNameLength.CompareTo(rightNameLength);
        if (comparison != 0) return comparison;

        for (int index = 0; index < leftNameLength; index++)
        {
            comparison = U16(left, 14 + index * 2).CompareTo(U16(right, 14 + index * 2));
            if (comparison != 0) return comparison;
        }

        return U32(left, 8).CompareTo(U32(right, 8));
    }

    private static void ValidateNodeMap(byte[] tree, int nodeSize, uint totalNodes,
        HashSet<uint> referencedNodes)
    {
        const int MapOffset = 14 + 106 + 128;
        int mapLength = nodeSize - 256;
        uint headerMapCapacity = checked((uint)(mapLength * 8));
        int mapNodeLength = nodeSize - 20;
        uint mapNodeCapacity = checked((uint)(mapNodeLength * 8));
        var mapNodes = new List<uint>();
        var seenMapNodes = new HashSet<uint>();
        uint nextMapNode = U32(tree, 0);
        ulong capacity = headerMapCapacity;
        while (nextMapNode != 0)
        {
            if (nextMapNode >= totalNodes || !seenMapNodes.Add(nextMapNode) ||
                referencedNodes.Contains(nextMapNode))
                throw new InvalidDataException("The HFS Plus B-tree map-node chain is cyclic or invalid.");

            int offset = checked((int)nextMapNode * nodeSize);
            RequireFirstRecordStartsAtNodeDescriptorEnd(tree.AsSpan(offset, nodeSize), nodeSize,
                "node map");
            if (tree[offset + 8] != 2 || tree[offset + 9] != 0 || U16(tree, offset + 10) != 1 ||
                U32(tree, offset + 4) != 0 || U16(tree, offset + nodeSize - 4) != nodeSize - 6)
                throw new InvalidDataException("An HFS Plus B-tree map node has an invalid descriptor or record layout.");
            mapNodes.Add(nextMapNode);
            capacity += mapNodeCapacity;
            nextMapNode = U32(tree, offset);
        }
        uint requiredMapNodes = totalNodes <= headerMapCapacity
            ? 0
            : checked((totalNodes - headerMapCapacity + mapNodeCapacity - 1) / mapNodeCapacity);
        if (mapNodes.Count != requiredMapNodes)
            throw new InvalidDataException("The HFS Plus B-tree map-node chain has an invalid length.");
        if (capacity < totalNodes)
            throw new InvalidDataException("The HFS Plus B-tree map nodes do not cover every node.");

        // [Code] Apple's HFS verifier's CmpBTM checks that unused bytes at the end of the final map record are zero.
        if (totalNodes <= headerMapCapacity)
        {
            int usedMapBytes = checked((int)((totalNodes + 7UL) / 8));
            RequireZeroMapPadding(tree.AsSpan(MapOffset, mapLength), usedMapBytes);
        }
        else
        {
            ulong precedingMapBits = (ulong)headerMapCapacity + (ulong)mapNodeCapacity * (uint)(mapNodes.Count - 1);
            int usedMapBytes = checked((int)((totalNodes - precedingMapBits + 7) / 8));
            int lastMapOffset = checked((int)mapNodes[^1] * nodeSize + 14);
            RequireZeroMapPadding(tree.AsSpan(lastMapOffset, mapNodeLength), usedMapBytes);
        }

        void RequireAllocated(uint nodeNumber)
        {
            if (!IsAllocated(nodeNumber))
                throw new InvalidDataException($"HFS Plus B-tree node {nodeNumber} is referenced but marked free.");
        }

        bool IsAllocated(uint nodeNumber)
        {
            int byteOffset;
            uint bitInByte;
            if (nodeNumber < headerMapCapacity)
            {
                byteOffset = MapOffset + checked((int)(nodeNumber / 8));
                bitInByte = nodeNumber % 8;
            }
            else
            {
                uint continuationBit = nodeNumber - headerMapCapacity;
                uint mapIndex = continuationBit / mapNodeCapacity;
                if (mapIndex >= mapNodes.Count)
                    throw new InvalidDataException("The HFS Plus B-tree node map is truncated.");
                byteOffset = checked((int)mapNodes[(int)mapIndex] * nodeSize + 14 +
                    (int)((continuationBit % mapNodeCapacity) / 8));
                bitInByte = continuationBit % 8;
            }
            byte mask = (byte)(0x80 >> (int)bitInByte);
            return (tree[byteOffset] & mask) != 0;
        }

        RequireAllocated(0);
        foreach (uint nodeNumber in referencedNodes) RequireAllocated(nodeNumber);
        foreach (uint nodeNumber in mapNodes) RequireAllocated(nodeNumber);

        var knownNodes = new HashSet<uint>(referencedNodes) { 0 };
        knownNodes.UnionWith(mapNodes);
        uint freeNodes = 0;
        for (uint nodeNumber = 0; nodeNumber < totalNodes; nodeNumber++)
        {
            if (IsAllocated(nodeNumber))
            {
                if (!knownNodes.Contains(nodeNumber))
                    throw new InvalidDataException(
                        $"HFS Plus B-tree node {nodeNumber} is allocated but not referenced by the tree.");
                continue;
            }
            freeNodes++;

            // [Code] Apple's HFS verifier's BTCheckUnusedNodes requires free B-tree nodes to be zero-filled.
            int offset = checked((int)nodeNumber * nodeSize);
            if (tree.AsSpan(offset, nodeSize).IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException($"HFS Plus B-tree free node {nodeNumber} is not zero-filled.");
        }
        if (freeNodes != U32(tree, 14 + 26))
            throw new InvalidDataException("The HFS Plus B-tree free-node count differs from its node map.");
    }

    private static void RequireZeroMapPadding(ReadOnlySpan<byte> mapRecord, int usedBytes)
    {
        for (int index = usedBytes; index < mapRecord.Length; index++)
            if (mapRecord[index] != 0)
                throw new InvalidDataException("The HFS Plus B-tree node map has nonzero unused bytes.");
    }

    private static string Name(ReadOnlySpan<byte> key)
    {
        if (key.Length < 8) throw new InvalidDataException("An HFS Plus catalog name is truncated.");
        int length = U16(key, 6);
        if (length > 255 || key.Length < 8 + 2 * length)
            throw new InvalidDataException("An HFS Plus catalog name is invalid.");
        return Encoding.BigEndianUnicode.GetString(key.Slice(8, 2 * length)).Normalize(NormalizationForm.FormC);
    }

    private static int CompareExtentKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = U32(left, 4).CompareTo(U32(right, 4));
        if (comparison != 0) return comparison;
        comparison = left[2].CompareTo(right[2]);
        return comparison != 0 ? comparison : U32(left, 8).CompareTo(U32(right, 8));
    }

    private static void ValidateCatalogKey(ReadOnlySpan<byte> key)
    {
        int nameLength = U16(key, 6);
        if (nameLength > 255 || key.Length != 8 + nameLength * 2)
            throw new InvalidDataException("An HFSX catalog key has an invalid name length.");
        if (!HfsPlusUnicodeNormalization.IsCanonical(key.Slice(8, nameLength * 2)))
            throw new InvalidDataException("An HFS Plus catalog name is not canonically decomposed.");
    }

    private static int CompareHfsXCatalogKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = U32(left, 2).CompareTo(U32(right, 2));
        if (comparison != 0) return comparison;
        int leftLength = U16(left, 6);
        int rightLength = U16(right, 6);
        for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
        {
            comparison = U16(left, 8 + index * 2).CompareTo(U16(right, 8 + index * 2));
            if (comparison != 0) return comparison;
        }
        return leftLength.CompareTo(rightLength);
    }

    private static int CompareCatalogKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, bool caseFolding)
    {
        int comparison = U32(left, 2).CompareTo(U32(right, 2));
        if (comparison != 0) return comparison;
        int leftLength = U16(left, 6);
        int rightLength = U16(right, 6);
        if (!caseFolding)
        {
            for (int index = 0; index < Math.Min(leftLength, rightLength); index++)
            {
                comparison = U16(left, 8 + index * 2).CompareTo(U16(right, 8 + index * 2));
                if (comparison != 0) return comparison;
            }
            return leftLength.CompareTo(rightLength);
        }

        return HfsPlusUnicodeComparison.CompareBigEndian(left.Slice(8, leftLength * 2),
            right.Slice(8, rightLength * 2));
    }

    private static IReadOnlyList<string> FolderPath(uint parent,
        Dictionary<uint, (uint Parent, string Name, uint Valence)> folders)
    {
        var path = new List<string>();
        var seen = new HashSet<uint>();
        while (parent != RootFolderId)
        {
            if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
                throw new InvalidDataException("An HFS Plus folder path is missing or cyclic.");
            path.Insert(0, folder.Name);
            parent = folder.Parent;
        }
        return path;
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private static string? ReadSymbolicLinkTarget(ushort fileMode, FinderInfo finderInfo,
        ForkData dataFork, ForkData resourceFork, long maxExpandedBytes)
    {
        const ushort fileTypeMask = 0xF000;
        const ushort symbolicLinkMode = 0xA000;
        if ((fileMode & fileTypeMask) != symbolicLinkMode) return null;

        if (finderInfo.Type != FourCC.FromString("slnk") || finderInfo.Creator != FourCC.FromString("rhap"))
            throw new InvalidDataException("An HFS Plus symbolic link has invalid Finder type or creator codes.");
        if (resourceFork.Length != 0)
            throw new InvalidDataException("An HFS Plus symbolic link has a nonempty resource fork.");

        byte[] target = dataFork.ToArray(maxExpandedBytes);
        if (Array.IndexOf(target, (byte)0) >= 0)
            throw new InvalidDataException("An HFS Plus symbolic-link path contains a null byte.");
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(target);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("An HFS Plus symbolic-link path is not valid UTF-8.", exception);
        }
    }

    private static bool IsHardLinkFile(FinderInfo finderInfo) =>
        finderInfo.Type == FourCC.FromString("hlnk") && finderInfo.Creator == FourCC.FromString("hfs+");

    private readonly record struct CatalogFileData(string Name, uint Parent, uint LinkReference, ushort Mode,
        FinderInfo FinderInfo, MacDate? Created, MacDate? Modified, ForkData DataFork, ForkData ResourceFork);
    private readonly record struct CatalogNode(uint Parent, string Name, bool IsFolder);
    private readonly record struct CatalogThread(uint Parent, string Name, bool IsFolder);
    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayEqualityComparer Instance { get; } = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] key)
        {
            var hash = new HashCode();
            foreach (byte value in key) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    private static MacString LegacyName(string name)
    {
        try { return MacString.FromMacRoman(name); }
        catch (ArgumentException) { return MacString.FromMacRoman("?"); }
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
