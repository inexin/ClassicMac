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
    private const ushort HasAttributesMask = 0x0004;
    private const ushort HasSecurityMask = 0x0008;
    private const ushort HasLinkChainMask = 0x0020;
    private const ushort HasChildLinkMask = 0x0040;

    public static IReadOnlyList<MacFile> Read(ForkData image, ContainerContext context)
    {
        byte[] header = image.Slice(HeaderOffset, HeaderLength).ToArray();
        ushort signature = U16(header, 0);
        ushort version = U16(header, 2);
        bool isHfsX = signature == 0x4858;
        if (signature is not (0x482B or 0x4858))
            throw new InvalidDataException($"Unknown HFS Plus volume signature 0x{signature:X4}.");
        if ((signature == 0x482B && version != 4) || (signature == 0x4858 && version != 5))
            throw new InvalidDataException($"Unsupported HFS Plus version {version}.");
        ReportAlternateHeaderProblem(image, signature, version, context);
        uint volumeAttributes = U32(header, 4);
        const uint volumeUnmountedBit = 1u << 8;
        const uint bootVolumeInconsistentBit = 1u << 11;
        const uint volumeJournaledBit = 1u << 13;
        const uint volumeInconsistentBit = 1u << 14; // kHFSVolumeInconsistentBit in Apple's HFS format header.
        if ((volumeAttributes & volumeUnmountedBit) == 0 ||
            (volumeAttributes & (bootVolumeInconsistentBit | volumeInconsistentBit)) != 0)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-volume-inconsistent",
                "The HFS Plus volume's attributes indicate an unclean unmount or an inconsistent volume state.");
        if ((volumeAttributes & volumeJournaledBit) != 0)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-journal-not-replayed",
                "The HFS Plus volume is journaled; ClassicMac reads the recorded structures without replaying the journal.");
        uint blockSize = U32(header, 40);
        uint totalBlocks = U32(header, 44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 ||
            totalBlocks == 0 || (ulong)blockSize * totalBlocks > (ulong)image.Length)
            throw new InvalidDataException("The HFS Plus allocation area is invalid.");
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(112, 8)) == 0)
            throw new InvalidDataException("The HFS Plus volume has no allocation file.");

        var overflow = new Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>();
        var allocationExtents = new List<(uint Start, uint End)>();
        var ordinaryForkExtents = new List<(uint Start, uint End)>();
        var directoryFirstLinkIds = new Dictionary<uint, byte[]>();
        var attributeFileIds = new HashSet<uint>();
        var securityAttributeFileIds = new HashSet<uint>();
        if (U32(header, 192 + 12) == 0)
            throw new InvalidDataException("The HFS Plus volume has no extents-overflow B-tree.");
        ForkData extentsFork = ReadFork(image, header.AsSpan(192, 80), blockSize, totalBlocks,
            allocationExtents: allocationExtents, ordinaryForkExtents: ordinaryForkExtents);
        if (U32(header, 192 + 12) != 0)
        {
            foreach (var (key, data) in LeafRecords(
                extentsFork.ToArray(context.Options.MaxExpandedBytesPerInput), "extents-overflow", context: context))
            {
                if (key.Length != 12 || U16(key, 0) != 10 || data.Length != 64 || key[2] is not (0 or 0xFF))
                    throw new InvalidDataException("An HFS Plus extents-overflow record is invalid.");
                var id = (Fork: key[2], File: U32(key, 4));
                if (id.File == BadBlockFileId && id.Fork != 0)
                    throw new InvalidDataException("An HFS Plus bad-block extent must use the data fork.");
                uint start = U32(key, 8);
                _ = AddExtentRecord(data.AsSpan(0, 64), totalBlocks, allocationExtents,
                    "An HFS Plus overflow extent lies outside the allocation area.",
                    id.File == BadBlockFileId ? null : ordinaryForkExtents);
                if (!overflow.TryGetValue(id, out var entries)) overflow[id] = entries = [];
                entries.Add((start, data.AsSpan(0, 64).ToArray()));
            }
        }
        bool hasBadBlockRecords = overflow.ContainsKey((0, BadBlockFileId));
        bool sparedBlocksFlag = (volumeAttributes & (1u << 9)) != 0;
        if (hasBadBlockRecords != sparedBlocksFlag)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-spared-blocks",
                "The HFS Plus spared-blocks attribute disagrees with its bad-block extent records.");

        ForkData allocationFork = ReadFork(image, header.AsSpan(112, 80), blockSize, totalBlocks,
            overflow, 0, 6, allocationExtents, ordinaryForkExtents);
        byte[] allocationBitmap = allocationFork.ToArray(context.Options.MaxExpandedBytesPerInput);
        _ = ReadFork(image, header.AsSpan(432, 80), blockSize, totalBlocks, overflow, 0, 7,
            allocationExtents, ordinaryForkExtents);
        ForkData attributesFork = ReadFork(image, header.AsSpan(352, 80), blockSize, totalBlocks,
            overflow, 0, 8, allocationExtents, ordinaryForkExtents);
        if (U32(header, 352 + 12) != 0)
        {
            byte[] attributes = attributesFork.ToArray(context.Options.MaxExpandedBytesPerInput);
            var attributeForks = new Dictionary<(uint FileId, string Name), AttributeForkState>();
            byte[] securityAttributeName = Encoding.BigEndianUnicode.GetBytes("com.apple.system.Security");
            foreach (var (key, data) in LeafRecords(attributes, "attributes", context: context))
            {
                if (U16(key, 12) == securityAttributeName.Length / 2 &&
                    key.AsSpan(14).SequenceEqual(securityAttributeName))
                {
                    securityAttributeFileIds.Add(U32(key, 4));
                    ValidateSecurityAttribute(data, context);
                }
                CollectAttributeForkRecord(key, data, totalBlocks, allocationExtents, ordinaryForkExtents,
                    attributeForks, directoryFirstLinkIds, attributeFileIds);
            }
            ValidateAttributeForks(attributeForks, blockSize);
        }

        var catalogFork = ReadFork(image, header.AsSpan(272, 80), blockSize, totalBlocks, overflow, 0, 4,
            allocationExtents, ordinaryForkExtents);
        byte[] catalog = catalogFork.ToArray(context.Options.MaxExpandedBytesPerInput);
        var records = new List<(byte[] Key, byte[] Data)>();
        int volumeEntries = 0;
        foreach (var record in LeafRecords(catalog, "catalog", isHfsX, context))
        {
            if (record.Data.Length >= 2 && (U16(record.Data, 0) is 1 or 2) &&
                ++volumeEntries > context.Options.MaxVolumeEntries)
                throw new InvalidDataException(
                    $"The HFS Plus catalog holds more than {context.Options.MaxVolumeEntries} files and folders.");
            records.Add(record);
        }
        var folders = new Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)>();
        var folderHardLinkCounts = new Dictionary<uint, uint>();
        var folderSecurity = new Dictionary<uint, (byte OwnerFlags, ushort Mode)>();
        var catalogIds = new HashSet<uint>();
        var catalogNodes = new Dictionary<uint, CatalogNode>();
        var catalogThreads = new Dictionary<uint, CatalogThread>();
        var catalogAttributeFlags = new Dictionary<uint, bool>();
        var catalogSecurityFlags = new Dictionary<uint, bool>();
        ulong requiredEncodingBitmap = 0;
        foreach (var (key, data) in records)
        {
            if (key.Length < 8 || data.Length < 2)
                throw new InvalidDataException("An HFS Plus catalog record is truncated.");
            switch (U16(data, 0))
            {
                case 1:
                    if (data.Length != 88)
                        throw new InvalidDataException("An HFS Plus folder record must be exactly 88 bytes.");
                    AddCatalogTextEncoding(data, ref requiredEncodingBitmap);
                    if ((U16(data, 2) & 0x0003) != 0)
                        throw new InvalidDataException("An HFS Plus folder record sets file-only flags.");
                    uint id = U32(data, 8);
                    if (id < 16 && id != RootFolderId)
                        throw new InvalidDataException($"HFS Plus folder catalog ID {id} is reserved.");
                    ReportInvalidBsdMode(U16(data, 42), isFolder: true, key, context);
                    if (!catalogIds.Add(id))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    catalogAttributeFlags.Add(id, (U16(data, 2) & HasAttributesMask) != 0);
                    catalogSecurityFlags.Add(id, (U16(data, 2) & HasSecurityMask) != 0);
                    uint parent = U32(key, 2);
                    ValidateCatalogObjectName(key);
                    string name = Name(key);
                    if (!folders.TryAdd(id, (parent, name, U32(data, 4), U32(data, 84), U16(data, 2))))
                        throw new InvalidDataException("Duplicate HFS Plus folder ID.");
                    folderHardLinkCounts.Add(id, U32(data, 44));
                    folderSecurity.Add(id, (data[41], U16(data, 42)));
                    catalogNodes.Add(id, new CatalogNode(parent, name, IsFolder: true));
                    break;
                case 2:
                    if (data.Length != 248)
                        throw new InvalidDataException("An HFS Plus file record must be exactly 248 bytes.");
                    AddCatalogTextEncoding(data, ref requiredEncodingBitmap);
                    uint fileId = U32(data, 8);
                    if (fileId < 16)
                        throw new InvalidDataException($"HFS Plus file catalog ID {fileId} is reserved.");
                    ReportInvalidBsdMode(U16(data, 42), isFolder: false, key, context);
                    if (!catalogIds.Add(fileId))
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    catalogAttributeFlags.Add(fileId, (U16(data, 2) & HasAttributesMask) != 0);
                    catalogSecurityFlags.Add(fileId, (U16(data, 2) & HasSecurityMask) != 0);
                    if ((U16(data, 2) & 0x0002) == 0)
                        throw new InvalidDataException("An HFS Plus file is missing its required thread flag.");
                    ValidateCatalogObjectName(key);
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
                    if (!HfsPlusUnicodeNormalization.IsCanonical(data.AsSpan(10, threadNameLength * 2), isHfsX))
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
        foreach (uint fileId in attributeFileIds)
        {
            if (!catalogIds.Contains(fileId) && fileId is not (>= 3 and <= 8))
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-attribute-orphan",
                    $"The HFS Plus attributes B-tree refers to missing catalog object {fileId}.");
        }
        foreach (var (fileId, hasAttributesFlag) in catalogAttributeFlags)
        {
            if (hasAttributesFlag != attributeFileIds.Contains(fileId))
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-attribute-flag-mismatch",
                    $"The HFS Plus catalog object's HasAttributes flag disagrees with its attribute records (CNID {fileId}).");
        }
        foreach (var (fileId, hasSecurityFlag) in catalogSecurityFlags)
        {
            if (hasSecurityFlag != securityAttributeFileIds.Contains(fileId))
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-security-flag-mismatch",
                    $"The HFS Plus catalog object's HasSecurity flag disagrees with its ACL attribute (CNID {fileId}).");
        }
        ulong declaredEncodingBitmap = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(72, 8));
        if ((requiredEncodingBitmap & declaredEncodingBitmap) != requiredEncodingBitmap)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-encoding-bitmap",
                "The HFS Plus volume encoding bitmap omits an encoding used by a catalog file or folder.");
        if (!folders.ContainsKey(RootFolderId))
            throw new InvalidDataException("The HFS Plus root folder is missing.");
        if (folders[RootFolderId].Parent != RootParentId)
            throw new InvalidDataException("The HFS Plus root folder does not use the reserved root parent ID.");
        foreach (var (id, node) in catalogNodes)
            if (id != RootFolderId && !folders.ContainsKey(node.Parent))
                throw new InvalidDataException("An HFS Plus catalog item's parent is not an existing folder.");
        const uint catalogNodeIdsReused = 1u << 12;
        uint nextCatalogId = U32(header, 64);
        if (nextCatalogId < 16)
            throw new InvalidDataException("The HFS Plus next catalog ID is reserved.");
        if ((volumeAttributes & catalogNodeIdsReused) == 0 && nextCatalogId <= catalogIds.Max())
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

        const string privateFileDataFolderName = "\0\0\0\0HFS+ Private Data";
        const string privateDirectoryDataFolderName = ".HFS+ Private Directory Data\r";
        uint? privateDataFolderId = folders
            .Where(folder => folder.Value.Parent == RootFolderId && folder.Value.Name == privateFileDataFolderName)
            .Select(folder => (uint?)folder.Key)
            .SingleOrDefault();
        uint? privateDirectoryDataFolderId = folders
            .Where(folder => folder.Value.Parent == RootFolderId && folder.Value.Name == privateDirectoryDataFolderName)
            .Select(folder => (uint?)folder.Key)
            .SingleOrDefault();
        var privateDataFolderIds = folders
            .Where(folder => folder.Value.Parent == RootFolderId &&
                folder.Value.Name is privateFileDataFolderName or privateDirectoryDataFolderName)
            .Select(folder => folder.Key)
            .ToHashSet();
        if (privateDataFolderIds.Count > 0)
        {
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

        if (isHfsX)
        {
            FourCC aliasType = FourCC.FromString("alis");
            FourCC aliasCreator = FourCC.FromString("MACS");
            var folderCounts = new Dictionary<uint, uint>(folders.Count);
            foreach (uint folderId in folders.Keys) folderCounts.Add(folderId, 0);
            foreach (var (id, folder) in folders)
                if (folderCounts.TryGetValue(folder.Parent, out uint count))
                    folderCounts[folder.Parent] = checked(count + 1);
            foreach (var (key, data) in records)
            {
                if (U16(data, 0) != 2 || (U16(data, 2) & HasLinkChainMask) == 0 ||
                    privateDataFolderIds.Contains(U32(key, 2)) ||
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(48, 4)) != aliasType.Value ||
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(52, 4)) != aliasCreator.Value)
                    continue;
                uint parentId = U32(key, 2);
                if (folderCounts.TryGetValue(parentId, out uint count))
                    folderCounts[parentId] = checked(count + 1);
            }
            foreach (var (folderId, folder) in folders)
            {
                if (folder.FolderCount != folderCounts[folderId])
                    context.Report(DiagnosticSeverity.Info, "hfs.plus-folder-count",
                        $"HFSX folder {folderId} records {folder.FolderCount} enclosed folders, but {folderCounts[folderId]} are present.");
            }
        }

        JournalInfo? journalInfo = null;
        var catalogFiles = new Dictionary<uint, CatalogFileData>();
        var journalFiles = new Dictionary<string, JournalCatalogFile>(StringComparer.Ordinal);
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            byte[] info = [.. data.AsSpan(48, 16), .. data.AsSpan(64, 16)];
            uint fileId = U32(data, 8);
            uint special = U32(data, 44);
            FinderInfo finderInfo = FinderInfo.Read(info);
            if (HasHardLinkMarker(finderInfo) && !IsHardLinkFile(finderInfo))
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-signature-invalid",
                    $"The HFS Plus file '{Name(key)}' has only part of the required hard-link Finder signature.");
            if (IsHardLinkFile(finderInfo) && special == 0)
                throw new InvalidDataException("An HFS Plus hard link has the reserved zero link reference.");
            var catalogFile = new CatalogFileData(
                fileId, Name(key), U32(key, 2), special, U32(data, 32), U32(data, 36), U16(data, 2),
                U16(data, 42), finderInfo,
                Date(U32(data, 12)), Date(U32(data, 16)),
                ReadFork(image, data.AsSpan(88, 80), blockSize, totalBlocks, overflow, 0, fileId,
                    allocationExtents, ordinaryForkExtents),
                ReadFork(image, data.AsSpan(168, 80), blockSize, totalBlocks, overflow, 0xFF, fileId,
                    allocationExtents, ordinaryForkExtents));
            if (IsHardLinkFile(finderInfo) && U32(data, 88 + 12) != 0)
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-alias-has-data",
                    $"The HFS Plus hard-link alias '{catalogFile.Name}' has allocated data-fork blocks.");
            uint parentId = U32(key, 2);
            bool isDirectoryHardLink = IsDirectoryHardLinkAliasCandidate(catalogFile) &&
                !privateDataFolderIds.Contains(parentId);
            bool isJournalFile = (volumeAttributes & volumeJournaledBit) != 0 && parentId == RootFolderId &&
                (catalogFile.Name is ".journal" or ".journal_info_block");
            const ushort regularFileMode = 0x8000;
            if (!IsHardLinkFile(finderInfo) && !isDirectoryHardLink && !isJournalFile &&
                (catalogFile.Mode & 0xF000) == regularFileMode && catalogFile.Special > 1 &&
                parentId != privateDataFolderId)
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-file-link-count-invalid",
                    $"The HFS Plus regular file '{catalogFile.Name}' has a BSD hard-link count greater than one.");
            catalogFiles.Add(fileId, catalogFile);
            uint parentIdForJournal = U32(key, 2);
            if ((volumeAttributes & volumeJournaledBit) != 0 && parentIdForJournal == RootFolderId &&
                (catalogFile.Name is ".journal" or ".journal_info_block"))
                journalFiles.Add(catalogFile.Name, ReadJournalCatalogFile(data.AsSpan(88, 80)));
        }
        var directoryInodeFolderIds = new HashSet<uint>();
        if (privateDirectoryDataFolderId is { } directoryDataFolderId)
            foreach (var (folderId, folder) in folders)
                if (folder.Parent == directoryDataFolderId &&
                    (folder.Flags & HasLinkChainMask) != 0 &&
                    TryParseDirectoryInodeName(folder.Name, out uint inodeId) && inodeId == folderId)
                    directoryInodeFolderIds.Add(folderId);

        var directoryAliasesByInode = new Dictionary<uint, List<DirectoryHardLinkAlias>>();
        var directoryAliasFileIds = new HashSet<uint>();
        foreach (CatalogFileData file in catalogFiles.Values)
        {
            if ((file.RecordFlags & HasLinkChainMask) != 0 &&
                !IsHardLinkFile(file.FinderInfo) && !IsDirectoryHardLinkAliasCandidate(file) &&
                !privateDataFolderIds.Contains(file.Parent))
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-chain-flag-unexpected",
                    $"The HFS Plus file '{file.Name}' has the directory hard-link chain flag but is not a hard link.");

            if (!IsDirectoryHardLinkAliasCandidate(file)) continue;
            if (privateDataFolderIds.Contains(file.Parent)) continue;
            if (!HasValidDirectoryHardLinkAliasSignature(file))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-signature-invalid",
                    $"The HFS Plus directory hard link '{file.Name}' has an incomplete Finder alias signature.");
                continue;
            }

            if (!directoryInodeFolderIds.Contains(file.Special))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-target-missing",
                    $"The HFS Plus directory hard link '{file.Name}' has no valid directory inode target.");
                continue;
            }

            if (!directoryAliasesByInode.TryGetValue(file.Special, out List<DirectoryHardLinkAlias>? aliases))
                directoryAliasesByInode.Add(file.Special, aliases = []);
            aliases.Add(new DirectoryHardLinkAlias(file.FileId, file.Name, file.Parent));
            directoryAliasFileIds.Add(file.FileId);
        }
        ValidateDirectoryHardLinkChains(directoryInodeFolderIds, directoryAliasesByInode,
            directoryAliasFileIds, directoryFirstLinkIds, folderHardLinkCounts, folderSecurity,
            privateDirectoryDataFolderId, folders, catalogFiles, context);

        if (overflow.Keys.Any(key => key.File != BadBlockFileId))
            throw new InvalidDataException("An HFS Plus overflow extent has no corresponding fork.");

        var hardLinkCounts = new Dictionary<uint, uint>();
        foreach (CatalogFileData file in catalogFiles.Values)
        {
            if (!IsHardLinkFile(file.FinderInfo)) continue;
            hardLinkCounts.TryGetValue(file.Special, out uint count);
            hardLinkCounts[file.Special] = checked(count + 1);
        }

        var hardLinkTargets = new Dictionary<uint, CatalogFileData>();
        if (privateDataFolderId is { } dataFolderId)
        {
            foreach (CatalogFileData file in catalogFiles.Values)
            {
                if (file.Parent != dataFolderId || !file.Name.StartsWith("iNode", StringComparison.Ordinal))
                    continue;

                if (!TryParseHardLinkReference(file.Name, out uint reference))
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-indirect-name-invalid",
                        $"The HFS Plus hard-link indirect node '{file.Name}' does not use a canonical decimal reference.");
                    continue;
                }

                if (!hardLinkTargets.TryAdd(reference, file))
                    throw new InvalidDataException("Duplicate HFS Plus hard-link indirect node reference.");
            }

            foreach (var (_, folder) in folders)
            {
                if (folder.Parent != dataFolderId || !TryParseHardLinkReference(folder.Name, out uint reference) ||
                    !hardLinkCounts.ContainsKey(reference) || hardLinkTargets.ContainsKey(reference))
                    continue;

                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-indirect-not-file",
                    $"The HFS Plus hard-link reference {reference} names a folder instead of an indirect node file.");
            }
        }

        foreach (var (reference, target) in hardLinkTargets)
        {
            uint actualCount = hardLinkCounts.GetValueOrDefault(reference);
            if (actualCount == 0)
            {
                context.Report(DiagnosticSeverity.Info, "hfs.plus-hardlink-indirect-orphan",
                    $"The HFS Plus hard-link indirect node '{target.Name}' has no catalog hard links referring to it.");
                continue;
            }

            if (target.Special != actualCount)
                context.Report(DiagnosticSeverity.Info, "hfs.plus-hardlink-count-mismatch",
                    $"The HFS Plus hard-link indirect node '{target.Name}' records an estimated link count of " +
                    $"{target.Special}, but {actualCount} catalog hard links refer to it.");
        }

        var result = new List<MacFile>();
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            string name = Name(key);
            uint parent = U32(key, 2);
            uint fileId = U32(data, 8);
            CatalogFileData file = catalogFiles[fileId];
            if (directoryAliasFileIds.Contains(fileId)) continue;
            bool isHardLink = IsHardLinkFile(file.FinderInfo);
            CatalogFileData target = default;
            bool hasHardLinkTarget = isHardLink && hardLinkTargets.TryGetValue(file.Special, out target);
            if (isHardLink && !hasHardLinkTarget)
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-target-missing",
                    $"The HFS Plus hard link '{name}' has no matching indirect node.");
            CatalogFileData content = hasHardLinkTarget ? target : file;
            IReadOnlyList<IReadOnlyList<string>> folderPaths = ResolveFolderPaths(parent, folders,
                privateDataFolderIds, directoryAliasesByInode);
            if (folderPaths.Count == 0) continue;
            foreach (IReadOnlyList<string> path in folderPaths)
            {
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
                    HardLinkReference = isHardLink ? file.Special : null,
                });
            }
        }
        ValidateAllocationExtents(allocationExtents, ordinaryForkExtents, blockSize, totalBlocks);
        ValidateAllocationBitmap(allocationBitmap, totalBlocks, blockSize, allocationExtents);
        if ((volumeAttributes & volumeJournaledBit) != 0)
        {
            journalInfo = ReportJournalInfoBlockProblem(image, header, blockSize, totalBlocks, allocationBitmap,
                context);
            ValidateJournalCatalogFiles(journalInfo, journalFiles, U32(header, 12), blockSize, context);
            if (journalInfo is { } info) ValidateJournalHeader(image, info, context);
        }
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

    private static JournalInfo? ReportJournalInfoBlockProblem(ForkData image, ReadOnlySpan<byte> volumeHeader,
        uint blockSize, uint totalBlocks, byte[] allocationBitmap, ContainerContext context)
    {
        const uint journalInVolumeFlag = 0x00000001;
        const uint journalOnOtherDeviceFlag = 0x00000002;
        const uint journalNeedsInitializationFlag = 0x00000004;
        const int journalInfoBlockLength = 180;
        uint allocationBlock = U32(volumeHeader, 12);
        if (allocationBlock >= totalBlocks ||
            (allocationBitmap[allocationBlock / 8] & (0x80 >> (int)(allocationBlock & 7))) == 0)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus journalInfoBlock points outside the allocation area or to an unallocated block.");
            return null;
        }

        long blockOffset = checked((long)allocationBlock * blockSize);
        byte[] journalInfo = image.Slice(blockOffset, journalInfoBlockLength).ToArray();
        uint flags = U32(journalInfo, 0);
        ulong volumeBytes = (ulong)blockSize * totalBlocks;
        ulong journalOffset = BinaryPrimitives.ReadUInt64BigEndian(journalInfo.AsSpan(36, 8));
        ulong journalSize = BinaryPrimitives.ReadUInt64BigEndian(journalInfo.AsSpan(44, 8));
        if ((flags & journalInVolumeFlag) == 0 || (flags & journalOnOtherDeviceFlag) != 0 ||
            journalSize == 0 || journalOffset > volumeBytes || journalSize > volumeBytes - journalOffset)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus JournalInfoBlock has unsupported storage flags or a journal range outside the volume.");
            return null;
        }

        ulong journalEnd = journalOffset + journalSize;
        uint firstJournalBlock = checked((uint)(journalOffset / blockSize));
        uint lastJournalBlock = checked((uint)((journalEnd - 1) / blockSize));
        for (uint block = firstJournalBlock; block <= lastJournalBlock; block++)
        {
            if ((allocationBitmap[block / 8] & (0x80 >> (int)(block & 7))) != 0) continue;
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus journal range includes an allocation block marked free.");
            return null;
        }
        return new JournalInfo(journalOffset, journalSize, (flags & journalNeedsInitializationFlag) != 0);
    }

    private static void ValidateJournalCatalogFiles(JournalInfo? journalInfo,
        IReadOnlyDictionary<string, JournalCatalogFile> journalFiles, uint journalInfoBlock, uint blockSize,
        ContainerContext context)
    {
        if (!journalFiles.TryGetValue(".journal_info_block", out JournalCatalogFile infoFile) ||
            !journalFiles.TryGetValue(".journal", out JournalCatalogFile journalFile))
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The journaled HFS Plus volume is missing its root .journal or .journal_info_block file.");
            return;
        }

        if (!infoFile.IsSingleExtent || infoFile.LogicalSize != 180 || infoFile.TotalBlocks != 1 ||
            infoFile.ExtentStart != journalInfoBlock)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus .journal_info_block file does not match the volume header's journalInfoBlock extent.");

        if (journalInfo is not { } info) return;
        ulong expectedJournalBlocks = (info.Size + blockSize - 1) / blockSize;
        ulong expectedOffset = (ulong)journalFile.ExtentStart * blockSize;
        if (!journalFile.IsSingleExtent || journalFile.LogicalSize != info.Size ||
            journalFile.TotalBlocks != expectedJournalBlocks || info.Offset != expectedOffset)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
                "The HFS Plus JournalInfoBlock range does not match the root .journal file's single extent.");
    }

    private static void ValidateJournalHeader(ForkData image, JournalInfo journalInfo, ContainerContext context)
    {
        const int journalHeaderLength = 44;
        const uint journalHeaderMagic = 0x4A4E4C78;
        const uint endianMagic = 0x12345678;
        if (journalInfo.NeedsInitialization) return;

        if (journalInfo.Size < journalHeaderLength || journalInfo.Offset > long.MaxValue ||
            journalInfo.Offset > (ulong)image.Length ||
            journalInfo.Size > (ulong)(image.Length - (long)journalInfo.Offset))
        {
            ReportInvalidJournalHeader(context);
            return;
        }

        byte[] fields = image.Slice((long)journalInfo.Offset, journalHeaderLength).ToArray(journalHeaderLength);
        uint magic = U32(fields, 0);
        uint endian = U32(fields, 4);
        ulong start = BinaryPrimitives.ReadUInt64BigEndian(fields.AsSpan(8, 8));
        ulong end = BinaryPrimitives.ReadUInt64BigEndian(fields.AsSpan(16, 8));
        ulong declaredSize = BinaryPrimitives.ReadUInt64BigEndian(fields.AsSpan(24, 8));
        uint blockListHeaderSize = U32(fields, 32);
        uint expectedChecksum = U32(fields, 36);
        uint headerSize = U32(fields, 40);

        if (magic != journalHeaderMagic || endian != endianMagic || declaredSize != journalInfo.Size ||
            headerSize < journalHeaderLength || headerSize > journalInfo.Size || blockListHeaderSize < 32 ||
            start < headerSize || start >= journalInfo.Size || end < headerSize || end > journalInfo.Size ||
            CalculateJournalHeaderChecksum(image, journalInfo.Offset, headerSize) != expectedChecksum)
            ReportInvalidJournalHeader(context);
    }

    private static uint CalculateJournalHeaderChecksum(ForkData image, ulong journalOffset, uint headerSize)
    {
        uint checksum = 0;
        byte[] buffer = new byte[8192];
        using Stream stream = image.Slice(checked((long)journalOffset), headerSize).Open();
        long position = 0;
        while (position < headerSize)
        {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, headerSize - position));
            if (count == 0) throw new EndOfStreamException("The HFS Plus journal header is truncated.");
            for (int index = 0; index < count; index++, position++)
            {
                byte value = position is >= 36 and < 40 ? (byte)0 : buffer[index];
                checksum = unchecked((checksum << 8) ^ (checksum + value));
            }
        }
        return ~checksum;
    }

    private static void ReportInvalidJournalHeader(ContainerContext context) =>
        context.Report(DiagnosticSeverity.Warning, "hfs.plus-journal-info-invalid",
            "The HFS Plus journal header has invalid fields, offsets, size, or checksum.");

    private static JournalCatalogFile ReadJournalCatalogFile(ReadOnlySpan<byte> fork)
    {
        ulong logicalSize = BinaryPrimitives.ReadUInt64BigEndian(fork);
        uint totalBlocks = U32(fork, 12);
        uint extentStart = U32(fork, 16);
        uint extentBlocks = U32(fork, 20);
        bool singleExtent = extentBlocks != 0 && extentBlocks == totalBlocks;
        for (int index = 1; index < 8; index++)
            if (U32(fork, 16 + index * 8) != 0 || U32(fork, 20 + index * 8) != 0)
                singleExtent = false;
        return new JournalCatalogFile(logicalSize, totalBlocks, extentStart, singleExtent);
    }

    private static void ValidateAllocationExtents(List<(uint Start, uint End)> extents,
        List<(uint Start, uint End)> ordinaryForkExtents, uint blockSize, uint totalBlocks)
    {
        // TN1150's allocation-file consistency check assigns allocation blocks to fork extents. A block cannot
        // belong to two extents in a valid volume; this follows from that ownership model.
        uint primaryReservedEnd = checked((uint)Math.Min(totalBlocks, (1536UL + blockSize - 1) / blockSize));
        ulong volumeBytes = (ulong)totalBlocks * blockSize;
        uint alternateReservedStart = checked((uint)((volumeBytes > 1024 ? volumeBytes - 1024 : 0) / blockSize));
        foreach (var extent in ordinaryForkExtents)
            if (extent.Start < primaryReservedEnd || extent.End > alternateReservedStart)
                throw new InvalidDataException("HFS Plus forks claim blocks reserved for volume headers.");
        extents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int index = 0; index < extents.Count; index++)
        {
            if (index > 0 && extents[index].Start < extents[index - 1].End)
                throw new InvalidDataException("HFS Plus forks claim overlapping allocation blocks.");
        }
    }

    private static void CollectAttributeForkRecord(byte[] key, byte[] data, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, List<(uint Start, uint End)> ordinaryForkExtents,
        Dictionary<(uint FileId, string Name), AttributeForkState> attributeForks,
        Dictionary<uint, byte[]> directoryFirstLinkIds, HashSet<uint> attributeFileIds)
    {
        if (data.Length < 4) throw new InvalidDataException("An HFS Plus attribute record is truncated.");
        attributeFileIds.Add(U32(key, 4));
        uint recordType = U32(data, 0);
        if (recordType == 0x10)
        {
            const int inlineAttributeHeaderSize = 16;
            if (data.Length < inlineAttributeHeaderSize)
                throw new InvalidDataException("An HFS Plus inline attribute record is truncated.");

            ulong declaredLength = (ulong)inlineAttributeHeaderSize + U32(data, 12);
            ulong alignedLength = (declaredLength + 1) & ~1UL;
            if (alignedLength != (ulong)data.Length)
                throw new InvalidDataException(
                    $"An HFS Plus inline attribute has an invalid length ({data.Length} bytes for {U32(data, 12)} data bytes).");
            byte[] firstLinkName = Encoding.BigEndianUnicode.GetBytes("com.apple.system.hfs.firstlink");
            int attributeNameLength = U16(key, 12);
            if (U32(key, 8) == 0 && key.AsSpan(14, attributeNameLength * 2).SequenceEqual(firstLinkName))
                directoryFirstLinkIds[U32(key, 4)] = data.AsSpan(inlineAttributeHeaderSize,
                    checked((int)U32(data, 12))).ToArray();
            return;
        }
        if (recordType is not (0x20 or 0x30)) return;

        var identity = (FileId: U32(key, 4), Name: Convert.ToHexString(key.AsSpan(14, U16(key, 12) * 2)));
        if (!attributeForks.TryGetValue(identity, out AttributeForkState? state))
            attributeForks.Add(identity, state = new AttributeForkState());

        switch (recordType)
        {
            case 0x20:
                if (data.Length != 88)
                    throw new InvalidDataException("An HFS Plus fork-data attribute has an invalid length.");
                if (U32(key, 8) != 0 || state.HasForkData)
                    throw new InvalidDataException("An HFS Plus attribute fork-data record has an invalid key.");
                ReadOnlySpan<byte> fork = data.AsSpan(8, 80);
                state.HasForkData = true;
                state.LogicalSize = BinaryPrimitives.ReadUInt64BigEndian(fork);
                state.TotalBlocks = U32(fork, 12);
                ExtentRecordInfo initialRecord = AddExtentRecord(fork[16..], totalBlocks, allocationExtents,
                    "An HFS Plus fork-data attribute lies outside the allocation area.", ordinaryForkExtents);
                state.InitialBlocks = initialRecord.BlockCount;
                state.InitialExtentCount = initialRecord.ExtentCount;
                if (state.InitialBlocks > state.TotalBlocks)
                    throw new InvalidDataException("An HFS Plus fork-data attribute exceeds its allocated blocks.");
                return;
            case 0x30:
                if (data.Length != 72)
                    throw new InvalidDataException("An HFS Plus attribute extension record has an invalid length.");
                ExtentRecordInfo extension = AddExtentRecord(data.AsSpan(8, 64), totalBlocks, allocationExtents,
                    "An HFS Plus attribute extension extent lies outside the allocation area.", ordinaryForkExtents);
                state.Extensions.Add((U32(key, 8), extension));
                return;
        }
    }

    private static void ValidateSecurityAttribute(ReadOnlySpan<byte> record, ContainerContext context)
    {
        const uint inlineDataRecord = 0x10;
        const uint fileSecurityMagic = 0x012CC16D;
        const uint noAclEntryCount = uint.MaxValue;
        const uint maximumAclEntries = 128;
        const int inlineHeaderSize = 16;
        const int fileSecurityHeaderSize = 44;
        const int accessControlEntrySize = 24;

        bool invalid = record.Length < inlineHeaderSize || U32(record, 0) != inlineDataRecord;
        if (!invalid)
        {
            uint valueLength = U32(record, 12);
            if (valueLength > int.MaxValue || inlineHeaderSize + (ulong)valueLength > (ulong)record.Length)
                invalid = true;
            else
            {
                ReadOnlySpan<byte> value = record.Slice(inlineHeaderSize, (int)valueLength);
                invalid = value.Length < fileSecurityHeaderSize || U32(value, 0) != fileSecurityMagic;
                if (!invalid)
                {
                    uint entryCount = U32(value, 36);
                    ulong expectedLength = entryCount == noAclEntryCount
                        ? fileSecurityHeaderSize
                        : entryCount <= maximumAclEntries
                            ? (ulong)fileSecurityHeaderSize + (ulong)entryCount * accessControlEntrySize
                            : ulong.MaxValue;
                    invalid = expectedLength != (ulong)value.Length;
                    if (!invalid && entryCount != noAclEntryCount)
                    {
                        for (uint index = 0; index < entryCount; index++)
                        {
                            int entryOffset = fileSecurityHeaderSize + (int)index * accessControlEntrySize;
                            uint kind = U32(value, entryOffset + 16) & 0xF;
                            if (kind is < 1 or > 4)
                            {
                                invalid = true;
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (invalid)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-acl-invalid",
                "An HFS Plus com.apple.system.Security attribute has an invalid file-security header, ACL entry layout, or ACE kind.");
    }

    private static void ValidateAttributeForks(
        Dictionary<(uint FileId, string Name), AttributeForkState> attributeForks, uint blockSize)
    {
        foreach (AttributeForkState fork in attributeForks.Values)
        {
            if (!fork.HasForkData)
                throw new InvalidDataException("An HFS Plus attribute extension has no corresponding fork-data record.");

            var extensions = fork.Extensions.OrderBy(extension => extension.StartBlock).ToArray();
            if (extensions.Length > 0 && fork.InitialExtentCount != 8)
                throw new InvalidDataException("An HFS Plus attribute fork uses overflow before its first eight extents.");

            ulong covered = fork.InitialBlocks;
            for (int index = 0; index < extensions.Length; index++)
            {
                var extension = extensions[index];
                if (extension.Record.ExtentCount == 0 || extension.StartBlock != covered)
                    throw new InvalidDataException("An HFS Plus attribute extension does not continue its fork extents.");
                if (index < extensions.Length - 1 && extension.Record.ExtentCount != 8)
                    throw new InvalidDataException("A non-final HFS Plus attribute extension must contain eight extents.");
                covered = checked(covered + extension.Record.BlockCount);
                if (covered > fork.TotalBlocks)
                    throw new InvalidDataException("An HFS Plus attribute extension exceeds its allocated blocks.");
            }

            if (covered != fork.TotalBlocks || fork.LogicalSize > covered * blockSize)
                throw new InvalidDataException("An HFS Plus attribute fork's extents disagree with its declared size.");
        }
    }

    private static void ValidateDirectoryHardLinkChains(
        HashSet<uint> directoryInodeFolderIds,
        Dictionary<uint, List<DirectoryHardLinkAlias>> directoryAliasesByInode,
        HashSet<uint> directoryAliasFileIds,
        Dictionary<uint, byte[]> directoryFirstLinkIds,
        Dictionary<uint, uint> folderHardLinkCounts,
        Dictionary<uint, (byte OwnerFlags, ushort Mode)> folderSecurity,
        uint? privateDirectoryDataFolderId,
        Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)> folders,
        Dictionary<uint, CatalogFileData> catalogFiles,
        ContainerContext context)
    {
        if (privateDirectoryDataFolderId is { } privateDirectoryId &&
            folderSecurity.TryGetValue(privateDirectoryId, out var privateDirectory) &&
            ((privateDirectory.OwnerFlags & 0x02) == 0 || (privateDirectory.Mode & 0x0200) == 0))
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-private-directory-invalid",
                "The HFS Plus private directory for directory hard links is missing its immutable flag or sticky bit.");

        var reportedAncestors = new HashSet<uint>();
        foreach (DirectoryHardLinkAlias alias in directoryAliasesByInode.Values.SelectMany(aliases => aliases))
        {
            uint parentId = alias.Parent;
            var visitedAncestors = new HashSet<uint>();
            while (parentId != RootFolderId && parentId != privateDirectoryDataFolderId)
            {
                if (!visitedAncestors.Add(parentId) || !folders.TryGetValue(parentId, out var parentFolder))
                    break;

                if ((parentFolder.Flags & HasChildLinkMask) == 0 && reportedAncestors.Add(parentId))
                    context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-ancestor-flag-missing",
                        $"The HFS Plus directory hard-link ancestor folder '{parentFolder.Name}' is missing its HasChildLink flag.");

                parentId = parentFolder.Parent;
            }
        }

        foreach (uint inodeId in directoryInodeFolderIds)
        {
            uint currentLinkId = 0;
            bool invalid = !directoryAliasesByInode.TryGetValue(inodeId,
                out List<DirectoryHardLinkAlias>? aliases) || aliases.Count == 0;
            if (!invalid && (!directoryFirstLinkIds.TryGetValue(inodeId, out byte[]? firstLinkValue) ||
                !TryParseDirectoryFirstLinkId(firstLinkValue, out currentLinkId)))
            {
                invalid = true;
            }

            uint previousLinkId = 0;
            var visited = new HashSet<uint>();
            while (!invalid && currentLinkId != 0)
            {
                if (!visited.Add(currentLinkId) || !directoryAliasFileIds.Contains(currentLinkId) ||
                    !catalogFiles.TryGetValue(currentLinkId, out CatalogFileData link) ||
                    link.Special != inodeId || link.PreviousLinkId != previousLinkId)
                {
                    invalid = true;
                    break;
                }

                previousLinkId = currentLinkId;
                currentLinkId = link.NextLinkId;
            }

            if (!invalid && (visited.Count != aliases!.Count ||
                !folderHardLinkCounts.TryGetValue(inodeId, out uint expectedCount) ||
                expectedCount != visited.Count))
                invalid = true;

            if (invalid)
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-chain-invalid",
                    $"The HFS Plus directory hard-link chain for inode {inodeId} is missing or inconsistent.");
        }
    }

    private static bool TryParseDirectoryFirstLinkId(byte[] value, out uint linkId)
    {
        linkId = 0;
        if (value.Length < 2 || value[^1] != 0) return false;
        ReadOnlySpan<byte> digits = value.AsSpan(0, value.Length - 1);
        if (digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0 ||
            !ulong.TryParse(Encoding.ASCII.GetString(digits), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong parsed) ||
            parsed < 16 || parsed > uint.MaxValue)
            return false;
        linkId = (uint)parsed;
        return true;
    }

    private static ExtentRecordInfo AddExtentRecord(ReadOnlySpan<byte> extents, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        ValidateExtentDescriptorSequence(extents);
        ulong covered = 0;
        int extentCount = 0;
        for (int index = 0; index < 8; index++)
        {
            uint start = U32(extents, index * 8);
            uint count = U32(extents, index * 8 + 4);
            if (count == 0) break;
            AddAllocationExtent(start, count, totalBlocks, allocationExtents, outOfRangeMessage,
                ordinaryForkExtents);
            covered = checked(covered + count);
            extentCount++;
        }
        return new ExtentRecordInfo(covered, extentCount);
    }

    private static bool HasExtentDescriptor(ReadOnlySpan<byte> extents)
    {
        for (int index = 0; index < 8; index++)
            if (U32(extents, index * 8) != 0 || U32(extents, index * 8 + 4) != 0)
                return true;
        return false;
    }

    private static void ValidateExtentDescriptorSequence(ReadOnlySpan<byte> extents)
    {
        bool unusedDescriptorSeen = false;
        for (int index = 0; index < 8; index++)
        {
            uint start = U32(extents, index * 8);
            uint count = U32(extents, index * 8 + 4);
            if (count == 0)
            {
                if (start != 0)
                    throw new InvalidDataException("An unused HFS Plus extent descriptor must be zero.");
                unusedDescriptorSeen = true;
            }
            else if (unusedDescriptorSeen)
            {
                throw new InvalidDataException("HFS Plus extent descriptors must not follow an unused descriptor.");
            }
        }
    }

    private static void AddAllocationExtent(uint start, uint count, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        if ((ulong)start + count > totalBlocks) throw new InvalidDataException(outOfRangeMessage);
        var extent = (start, checked(start + count));
        allocationExtents.Add(extent);
        ordinaryForkExtents?.Add(extent);
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
        byte forkType = 0, uint fileId = 0, List<(uint Start, uint End)>? allocationExtents = null,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        ulong logical = BinaryPrimitives.ReadUInt64BigEndian(fork);
        if (logical > long.MaxValue) throw new InvalidDataException("An HFS Plus fork is too large.");
        uint allocatedBlocks = U32(fork, 12);
        List<(uint Start, byte[] Extents)>? overflowEntries = null;
        if (overflow is not null) overflow.TryGetValue((forkType, fileId), out overflowEntries);
        if (allocatedBlocks == 0 && logical != 0)
            throw new InvalidDataException("A nonempty HFS Plus fork has no allocated blocks.");
        if (allocatedBlocks == 0)
        {
            if (HasExtentDescriptor(fork.Slice(16, 64)))
                throw new InvalidDataException("An empty HFS Plus fork has extent descriptors.");
            if (overflowEntries is { Count: > 0 })
                throw new InvalidDataException("An empty HFS Plus fork has overflow extents.");
            return ForkData.Empty;
        }
        var ranges = new List<(long Offset, long Length)>();
        uint coveredBlocks = 0;
        int coveredExtents = 0;
        void AddExtents(ReadOnlySpan<byte> extents, bool addToAllocationOwnership)
        {
            ValidateExtentDescriptorSequence(extents);
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
                {
                    allocationExtents?.Add((start, checked(start + count)));
                    ordinaryForkExtents?.Add((start, checked(start + count)));
                }
                coveredBlocks = checked(coveredBlocks + count);
                coveredExtents++;
            }
        }
        AddExtents(fork.Slice(16, 64), addToAllocationOwnership: true);
        if (overflowEntries is { Count: > 0 })
        {
            if (coveredBlocks >= allocatedBlocks)
                throw new InvalidDataException("An HFS Plus fork has unnecessary overflow extent records.");
            if (coveredExtents != 8)
                throw new InvalidDataException("An HFS Plus fork uses overflow before its first eight extents.");

            var entries = overflowEntries.OrderBy(entry => entry.Start).ToArray();
            for (int index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.Start != coveredBlocks)
                    throw new InvalidDataException("An HFS Plus overflow extent is not contiguous with the fork.");
                int precedingExtentCount = coveredExtents;
                AddExtents(entry.Extents, addToAllocationOwnership: false);
                int recordExtentCount = coveredExtents - precedingExtentCount;
                if (recordExtentCount == 0 || index < entries.Length - 1 && recordExtentCount != 8)
                    throw new InvalidDataException("A non-final HFS Plus overflow record must contain eight extents.");
                if (coveredBlocks > allocatedBlocks ||
                    coveredBlocks == allocatedBlocks && index < entries.Length - 1)
                    throw new InvalidDataException("An HFS Plus fork has excess overflow extent records.");
            }
        }
        if (coveredBlocks != allocatedBlocks)
            throw new InvalidDataException("An HFS Plus fork's extent count differs from its allocated block count.");
        if ((ulong)coveredBlocks * blockSize < logical)
            throw new InvalidDataException("An HFS Plus fork has insufficient extents for its logical length.");
        if (overflowEntries is { Count: > 0 }) overflow!.Remove((forkType, fileId));
        if (logical == 0) return ForkData.Empty;
        return new ExtentForkData(image, ranges, checked((long)logical));
    }

    private static IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(byte[] tree, string name,
        bool isHfsX = false, ContainerContext? context = null)
    {
        if (tree.Length < 512 || tree[8] != 1)
            throw new InvalidDataException($"The HFS Plus {name} tree has no B-tree header.");
        if (U32(tree, 4) != 0 || tree[9] != 0 || U16(tree, 10) != 3)
            throw new InvalidDataException($"The HFS Plus {name} B-tree header node is invalid.");
        byte treeType = tree[14 + 36];
        if (treeType != 0)
        {
            if (name == "attributes" && treeType == 0xFF)
                context?.Report(DiagnosticSeverity.Warning, "hfs.plus-btree-type",
                    "The HFS Plus attributes B-tree uses a reserved tree type written by Mac OS X; it was read for compatibility.");
            else
                throw new InvalidDataException($"The HFS Plus {name} B-tree has an invalid tree type.");
        }
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
            if (depth == 0 && root == 0 && first == 0 && last == 0)
            {
                ValidateNodeMap(tree, nodeSize, totalNodes, [], name);
                yield break;
            }

            if (depth != 1 || root == 0 || root >= totalNodes || root != first || root != last ||
                U16(tree, checked((int)root * nodeSize + 10)) != 0)
                throw new InvalidDataException($"The empty HFS Plus {name} B-tree has invalid root or leaf fields.");

            var (_, emptyRootNodes) = ValidateIndexGraph(tree, name, nodeSize, maxKeyLength, totalNodes,
                root, depth, caseSensitiveCatalog, caseFoldingCatalog, isHfsX);
            ValidateNodeMap(tree, nodeSize, totalNodes, emptyRootNodes, name);
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
                caseFoldingCatalog, isHfsX);
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
                if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin ||
                    end > nodeSize - 2 * (count + 1))
                    throw new InvalidDataException($"An HFS Plus {name} B-tree record offset is invalid.");
                int offset = start + begin;
                int keyLength = U16(tree, offset);
                if (keyLength < 6 || 2 + keyLength > end - begin)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree key is invalid.");
                int dataOffset = offset + 2 + keyLength;
                byte[] key = tree.AsSpan(offset, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    ValidateCatalogKey(key, isHfsX);
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
        ValidateNodeMap(tree, nodeSize, totalNodes, indexedNodes, name);
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
        bool caseFoldingCatalog, bool isHfsX)
    {
        var visitedNodes = new HashSet<uint>();
        var leafNodes = new HashSet<uint>();
        var nodesByHeight = new Dictionary<ushort, List<uint>>();
        var indexKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)>();
        var subtreeKeyRanges = new Dictionary<uint, (byte[] First, byte[] Last)?>();
        bool validateChildKeyRanges = name is "catalog" or "extents-overflow" or "attributes";

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
                if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin || end > offsetTableStart)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index record offset is invalid.");
                int keyLength = U16(tree, start + begin);
                if (keyLength < 6 || keyLength > maxKeyLength)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree index key length is invalid.");
                byte[] indexKey = tree.AsSpan(start + begin, 2 + keyLength).ToArray();
                if (name == "catalog")
                {
                    ValidateCatalogKey(indexKey, isHfsX);
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
                    if (childRange is not { } child)
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree index points to an empty child subtree.");
                    if (CompareTreeKeys(name, caseSensitiveCatalog, caseFoldingCatalog, separator, child.First) != 0)
                        throw new InvalidDataException(
                            $"An HFS Plus {name} B-tree index key does not match the first key in its child subtree.");
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
            if ((begin & 1) != 0 || (end & 1) != 0 || begin < 14 || end <= begin || end > offsetTableStart)
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
        HashSet<uint> referencedNodes, string name)
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

            // [Code] Apple's verifier calls BTCheckUnusedNodes for the catalog tree; it does not impose this
            // check on the extents-overflow or attributes trees.
            if (name == "catalog")
            {
                int offset = checked((int)nodeNumber * nodeSize);
                if (tree.AsSpan(offset, nodeSize).IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidDataException($"HFS Plus catalog B-tree free node {nodeNumber} is not zero-filled.");
            }
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

    private static void AddCatalogTextEncoding(ReadOnlySpan<byte> record, ref ulong requiredBitmap)
    {
        uint textEncoding = U32(record, 80);
        uint bit = textEncoding switch
        {
            140 => 49, // TN1150 assigns MacFarsi bitmap bit 49.
            152 => 48, // TN1150 assigns MacUkrainian bitmap bit 48.
            <= 63 => textEncoding,
            _ => uint.MaxValue,
        };
        if (bit < 64) requiredBitmap |= 1UL << checked((int)bit);
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

    private static void ValidateCatalogKey(ReadOnlySpan<byte> key, bool isHfsX)
    {
        int nameLength = U16(key, 6);
        if (nameLength > 255 || key.Length != 8 + nameLength * 2)
            throw new InvalidDataException("An HFSX catalog key has an invalid name length.");
        if (!HfsPlusUnicodeNormalization.IsCanonical(key.Slice(8, nameLength * 2), isHfsX))
            throw new InvalidDataException("An HFS Plus catalog name is not canonically decomposed.");
    }

    private static void ValidateCatalogObjectName(ReadOnlySpan<byte> key)
    {
        int nameLength = U16(key, 6);
        if (nameLength == 1 && U16(key, 8) == '.')
            throw new InvalidDataException("An HFS Plus catalog object is named '.'.");
        if (nameLength == 2 && U16(key, 8) == '.' && U16(key, 10) == '.')
            throw new InvalidDataException("An HFS Plus catalog object is named '..'.");
    }

    private static void ReportInvalidBsdMode(ushort mode, bool isFolder, ReadOnlySpan<byte> key,
        ContainerContext context)
    {
        if (mode == 0) return; // Apple fsck_hfs treats a zero BSD info record as uninitialized.

        const ushort fileTypeMask = 0xF000;
        ushort fileType = (ushort)(mode & fileTypeMask);
        bool valid = isFolder
            ? fileType == 0x4000
            : fileType is 0x1000 or 0x2000 or 0x6000 or 0x8000 or 0xA000 or 0xC000;
        if (!valid)
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-invalid-bsd-mode",
                $"The HFS Plus {(isFolder ? "folder" : "file")} '{Name(key)}' has an invalid BSD file type " +
                $"in mode 0x{mode:X4}.");
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
        Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)> folders)
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

    private static IReadOnlyList<IReadOnlyList<string>> ResolveFolderPaths(uint folderId,
        Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)> folders,
        HashSet<uint> privateDataFolderIds, Dictionary<uint, List<DirectoryHardLinkAlias>> directoryAliasesByInode,
        HashSet<uint>? activeFolderIds = null)
    {
        if (folderId == RootFolderId) return [Array.Empty<string>()];
        activeFolderIds ??= [];
        if (!activeFolderIds.Add(folderId)) return [];
        if (directoryAliasesByInode.TryGetValue(folderId, out List<DirectoryHardLinkAlias>? aliases))
        {
            var paths = new List<IReadOnlyList<string>>();
            foreach (DirectoryHardLinkAlias alias in aliases)
                foreach (IReadOnlyList<string> parentPath in ResolveFolderPaths(alias.Parent, folders,
                    privateDataFolderIds, directoryAliasesByInode, activeFolderIds))
                    paths.Add([.. parentPath, alias.Name]);
            activeFolderIds.Remove(folderId);
            return paths;
        }

        if (!folders.TryGetValue(folderId, out var folder) ||
            (privateDataFolderIds.Contains(folderId) && folder.Parent == RootFolderId))
        {
            activeFolderIds.Remove(folderId);
            return [];
        }

        IReadOnlyList<IReadOnlyList<string>> parentPaths = ResolveFolderPaths(folder.Parent, folders,
            privateDataFolderIds, directoryAliasesByInode, activeFolderIds);
        activeFolderIds.Remove(folderId);
        return parentPaths.Select(path => (IReadOnlyList<string>)[.. path, folder.Name]).ToArray();
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

    private static bool IsDirectoryHardLinkAliasCandidate(CatalogFileData file) =>
        (file.RecordFlags & 0x0020) != 0 &&
        (file.FinderInfo.Type == FourCC.FromString("alis") || file.FinderInfo.Creator == FourCC.FromString("MACS"));

    private static bool HasValidDirectoryHardLinkAliasSignature(CatalogFileData file) =>
        file.FinderInfo.Type == FourCC.FromString("alis") && file.FinderInfo.Creator == FourCC.FromString("MACS") &&
        (file.FinderInfo.Flags & FinderFlags.IsAlias) != 0;

    private static bool HasHardLinkMarker(FinderInfo finderInfo) =>
        finderInfo.Type == FourCC.FromString("hlnk") || finderInfo.Creator == FourCC.FromString("hfs+");

    private static bool TryParseHardLinkReference(string name, out uint reference)
    {
        reference = 0;
        if (!name.StartsWith("iNode", StringComparison.Ordinal) ||
            !uint.TryParse(name.AsSpan(5), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out reference))
            return false;

        string canonicalReference = reference.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return name.AsSpan(5).SequenceEqual(canonicalReference.AsSpan());
    }

    private static bool TryParseDirectoryInodeName(string name, out uint inodeId)
    {
        inodeId = 0;
        if (!name.StartsWith("dir_", StringComparison.Ordinal) ||
            !uint.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out inodeId))
            return false;
        return name.AsSpan(4).SequenceEqual(inodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private readonly record struct CatalogFileData(uint FileId, string Name, uint Parent, uint Special,
        uint PreviousLinkId, uint NextLinkId, ushort RecordFlags, ushort Mode,
        FinderInfo FinderInfo, MacDate? Created, MacDate? Modified, ForkData DataFork, ForkData ResourceFork);
    private readonly record struct DirectoryHardLinkAlias(uint FileId, string Name, uint Parent);
    private readonly record struct CatalogNode(uint Parent, string Name, bool IsFolder);
    private readonly record struct CatalogThread(uint Parent, string Name, bool IsFolder);
    private readonly record struct JournalInfo(ulong Offset, ulong Size, bool NeedsInitialization);
    private readonly record struct JournalCatalogFile(ulong LogicalSize, uint TotalBlocks, uint ExtentStart,
        bool IsSingleExtent);
    private sealed class AttributeForkState
    {
        public ulong LogicalSize { get; set; }
        public uint TotalBlocks { get; set; }
        public ulong InitialBlocks { get; set; }
        public int InitialExtentCount { get; set; }
        public bool HasForkData { get; set; }
        public List<(uint StartBlock, ExtentRecordInfo Record)> Extensions { get; } = [];
    }
    private readonly record struct ExtentRecordInfo(ulong BlockCount, int ExtentCount);
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
