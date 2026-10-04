using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusJournal;
using static ClassicMac.Files.Hfs.HfsPlusAllocation;
using static ClassicMac.Files.Hfs.HfsPlusAttributes;
using static ClassicMac.Files.Hfs.HfsPlusLinks;
using static ClassicMac.Files.Hfs.HfsPlusBTree;

namespace ClassicMac.Files.Hfs;

// HFS Plus structures and offsets follow Apple Technical Note TN1150.
internal static class HfsPlusReader
{
    private const int HeaderOffset = 1024;
    private const int HeaderLength = 512;
    private const uint RootParentId = 1;
    internal const uint RootFolderId = 2;
    private const uint BadBlockFileId = 5;
    private const ushort HasAttributesMask = 0x0004;
    private const ushort HasSecurityMask = 0x0008;
    private const ushort HasLinkChainMask = 0x0020;
    internal const ushort HasChildLinkMask = 0x0040;

    // The volume's files; its folders (but the private hard-link folders) go to folderList when one is given.
    public static IReadOnlyList<MacFile> Read(ForkData image, ContainerContext context, List<MacFolder>? folderList = null)
    {
        byte[] header = image.Slice(HeaderOffset, HeaderLength).ToArray();
        var headerReader = new BigEndianReader(header);
        ushort signature = U16(headerReader, 0);
        ushort version = U16(headerReader, 2);
        bool isHfsX = signature == 0x4858;
        if (signature is not (0x482B or 0x4858))
        {
            throw new InvalidDataException($"Unknown HFS Plus volume signature 0x{signature:X4}.");
        }

        if ((signature == 0x482B && version != 4) || (signature == 0x4858 && version != 5))
        {
            throw new InvalidDataException($"Unsupported HFS Plus version {version}.");
        }

        ReportAlternateHeaderProblem(image, signature, version, context);
        uint volumeAttributes = U32(headerReader, 4);
        const uint volumeUnmountedBit = 1u << 8;
        const uint bootVolumeInconsistentBit = 1u << 11;
        const uint volumeJournaledBit = 1u << 13;
        const uint volumeInconsistentBit = 1u << 14; // kHFSVolumeInconsistentBit in Apple's HFS format header.
        if ((volumeAttributes & volumeUnmountedBit) == 0 ||
            (volumeAttributes & (bootVolumeInconsistentBit | volumeInconsistentBit)) != 0)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-volume-inconsistent",
                "The HFS Plus volume's attributes indicate an unclean unmount or an inconsistent volume state.");
        }

        if ((volumeAttributes & volumeJournaledBit) != 0)
        {
            context.Report(DiagnosticSeverity.Info, "hfs.plus-journal-not-replayed",
                "The HFS Plus volume is journaled; ClassicMac reads the recorded structures without replaying the journal.");
        }

        uint blockSize = U32(headerReader, 40);
        uint totalBlocks = U32(headerReader, 44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 ||
            totalBlocks == 0 || (ulong)blockSize * totalBlocks > (ulong)image.Length)
        {
            throw new InvalidDataException("The HFS Plus allocation area is invalid.");
        }

        if (U64(headerReader, 112) == 0)
        {
            throw new InvalidDataException("The HFS Plus volume has no allocation file.");
        }

        var overflow = new Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>();
        var allocationExtents = new List<(uint Start, uint End)>();
        var ordinaryForkExtents = new List<(uint Start, uint End)>();
        var directoryFirstLinkIds = new Dictionary<uint, byte[]>();
        var attributeFileIds = new HashSet<uint>();
        var securityAttributeFileIds = new HashSet<uint>();
        if (U32(headerReader, 192 + 12) == 0)
        {
            throw new InvalidDataException("The HFS Plus volume has no extents-overflow B-tree.");
        }

        ForkData extentsFork = ReadFork(image, header.AsMemory(192, 80), blockSize, totalBlocks,
            allocationExtents: allocationExtents, ordinaryForkExtents: ordinaryForkExtents);
        if (U32(headerReader, 192 + 12) != 0)
        {
            foreach (var (key, data) in LeafRecords(
                extentsFork.ToArray(context.Options.MaxExpandedBytesPerInput), "extents-overflow", context: context))
            {
                var keyReader = new BigEndianReader(key);
                if (key.Length != 12 || U16(keyReader, 0) != 10 || data.Length != 64 || key[2] is not (0 or 0xFF))
                {
                    throw new InvalidDataException("An HFS Plus extents-overflow record is invalid.");
                }

                var id = (Fork: key[2], File: U32(keyReader, 4));
                if (id.File == BadBlockFileId && id.Fork != 0)
                {
                    throw new InvalidDataException("An HFS Plus bad-block extent must use the data fork.");
                }

                uint start = U32(keyReader, 8);
                _ = AddExtentRecord(data.AsMemory(0, 64), totalBlocks, allocationExtents,
                    "An HFS Plus overflow extent lies outside the allocation area.",
                    id.File == BadBlockFileId ? null : ordinaryForkExtents);
                if (!overflow.TryGetValue(id, out var entries))
                {
                    overflow[id] = entries = [];
                }

                entries.Add((start, data.AsSpan(0, 64).ToArray()));
            }
        }
        bool hasBadBlockRecords = overflow.ContainsKey((0, BadBlockFileId));
        bool sparedBlocksFlag = (volumeAttributes & (1u << 9)) != 0;
        if (hasBadBlockRecords != sparedBlocksFlag)
        {
            context.Report(DiagnosticSeverity.Info, "hfs.plus-spared-blocks",
                "The HFS Plus spared-blocks attribute disagrees with its bad-block extent records.");
        }

        ForkData allocationFork = ReadFork(image, header.AsMemory(112, 80), blockSize, totalBlocks,
            overflow, 0, 6, allocationExtents, ordinaryForkExtents);
        byte[] allocationBitmap = allocationFork.ToArray(context.Options.MaxExpandedBytesPerInput);
        _ = ReadFork(image, header.AsMemory(432, 80), blockSize, totalBlocks, overflow, 0, 7,
            allocationExtents, ordinaryForkExtents);
        ForkData attributesFork = ReadFork(image, header.AsMemory(352, 80), blockSize, totalBlocks,
            overflow, 0, 8, allocationExtents, ordinaryForkExtents);
        if (U32(headerReader, 352 + 12) != 0)
        {
            byte[] attributes = attributesFork.ToArray(context.Options.MaxExpandedBytesPerInput);
            var attributeForks = new Dictionary<(uint FileId, string Name), AttributeForkState>();
            byte[] securityAttributeName = Encoding.BigEndianUnicode.GetBytes("com.apple.system.Security");
            foreach (var (key, data) in LeafRecords(attributes, "attributes", context: context))
            {
                var keyReader = new BigEndianReader(key);
                if (U16(keyReader, 12) == securityAttributeName.Length / 2 &&
                    key.AsSpan(14).SequenceEqual(securityAttributeName))
                {
                    securityAttributeFileIds.Add(U32(keyReader, 4));
                    ValidateSecurityAttribute(data, context);
                }
                CollectAttributeForkRecord(keyReader, data, totalBlocks, allocationExtents, ordinaryForkExtents,
                    attributeForks, directoryFirstLinkIds, attributeFileIds);
            }
            ValidateAttributeForks(attributeForks, blockSize);
        }

        var catalogFork = ReadFork(image, header.AsMemory(272, 80), blockSize, totalBlocks, overflow, 0, 4,
            allocationExtents, ordinaryForkExtents);
        byte[] catalog = catalogFork.ToArray(context.Options.MaxExpandedBytesPerInput);
        var records = new List<(byte[] Key, byte[] Data)>();
        int volumeEntries = 0;
        foreach (var record in LeafRecords(catalog, "catalog", isHfsX, context))
        {
            if (record.Data.Length >= 2 && (U16(new BigEndianReader(record.Data), 0) is 1 or 2) &&
                ++volumeEntries > context.Options.MaxVolumeEntries)
            {
                throw new InvalidDataException(
                    $"The HFS Plus catalog holds more than {context.Options.MaxVolumeEntries} files and folders.");
            }

            records.Add(record);
        }
        var folders = new Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)>();
        var folderRecords = new Dictionary<uint, byte[]>();
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
            var keyReader = new BigEndianReader(key);
            var dataReader = new BigEndianReader(data);
            if (key.Length < 8 || data.Length < 2)
            {
                throw new InvalidDataException("An HFS Plus catalog record is truncated.");
            }

            switch (U16(dataReader, 0))
            {
                case 1:
                    if (data.Length != 88)
                    {
                        throw new InvalidDataException("An HFS Plus folder record must be exactly 88 bytes.");
                    }

                    AddCatalogTextEncoding(dataReader, ref requiredEncodingBitmap);
                    if ((U16(dataReader, 2) & 0x0003) != 0)
                    {
                        throw new InvalidDataException("An HFS Plus folder record sets file-only flags.");
                    }

                    uint id = U32(dataReader, 8);
                    if (id < 16 && id != RootFolderId)
                    {
                        throw new InvalidDataException($"HFS Plus folder catalog ID {id} is reserved.");
                    }

                    ReportInvalidBsdMode(U16(dataReader, 42), isFolder: true, keyReader, context);
                    if (!catalogIds.Add(id))
                    {
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    }

                    catalogAttributeFlags.Add(id, (U16(dataReader, 2) & HasAttributesMask) != 0);
                    catalogSecurityFlags.Add(id, (U16(dataReader, 2) & HasSecurityMask) != 0);
                    uint parent = U32(keyReader, 2);
                    ValidateCatalogObjectName(keyReader);
                    string name = Name(keyReader);
                    if (!folders.TryAdd(id,
                        (parent, name, U32(dataReader, 4), U32(dataReader, 84), U16(dataReader, 2))))
                    {
                        throw new InvalidDataException("Duplicate HFS Plus folder ID.");
                    }

                    folderHardLinkCounts.Add(id, U32(dataReader, 44));
                    folderRecords.Add(id, data);
                    folderSecurity.Add(id, (data[41], U16(dataReader, 42)));
                    catalogNodes.Add(id, new CatalogNode(parent, name, IsFolder: true));
                    break;
                case 2:
                    if (data.Length != 248)
                    {
                        throw new InvalidDataException("An HFS Plus file record must be exactly 248 bytes.");
                    }

                    AddCatalogTextEncoding(dataReader, ref requiredEncodingBitmap);
                    uint fileId = U32(dataReader, 8);
                    if (fileId < 16)
                    {
                        throw new InvalidDataException($"HFS Plus file catalog ID {fileId} is reserved.");
                    }

                    ReportInvalidBsdMode(U16(dataReader, 42), isFolder: false, keyReader, context);
                    if (!catalogIds.Add(fileId))
                    {
                        throw new InvalidDataException("Duplicate HFS Plus catalog ID.");
                    }

                    catalogAttributeFlags.Add(fileId, (U16(dataReader, 2) & HasAttributesMask) != 0);
                    catalogSecurityFlags.Add(fileId, (U16(dataReader, 2) & HasSecurityMask) != 0);
                    if ((U16(dataReader, 2) & 0x0002) == 0)
                    {
                        throw new InvalidDataException("An HFS Plus file is missing its required thread flag.");
                    }

                    ValidateCatalogObjectName(keyReader);
                    catalogNodes.Add(fileId, new CatalogNode(U32(keyReader, 2), Name(keyReader), IsFolder: false));
                    break;
                case 3 or 4:
                    if (key.Length != 8 || U16(keyReader, 0) != 6 || U16(keyReader, 6) != 0)
                    {
                        throw new InvalidDataException("An HFS Plus catalog thread key is invalid.");
                    }

                    if (data.Length < 10)
                    {
                        throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    }

                    if (data.Length > 520)
                    {
                        throw new InvalidDataException("An HFS Plus catalog thread record exceeds 520 bytes.");
                    }

                    ushort threadNameLength = U16(dataReader, 8);
                    if (threadNameLength > 255 || data.Length < 10 + 2 * threadNameLength)
                    {
                        throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    }

                    if (!HfsPlusUnicodeNormalization.IsCanonical(data.AsSpan(10, threadNameLength * 2), isHfsX))
                    {
                        throw new InvalidDataException(
                            "An HFS Plus catalog thread name is not canonically decomposed.");
                    }

                    uint threadId = U32(keyReader, 2);
                    var thread = new CatalogThread(U32(dataReader, 4),
                        Encoding.BigEndianUnicode.GetString(data, 10, threadNameLength * 2)
                            .Normalize(NormalizationForm.FormC),
                        IsFolder: U16(dataReader, 0) == 3);
                    if (!catalogThreads.TryAdd(threadId, thread))
                    {
                        throw new InvalidDataException("Duplicate HFS Plus catalog thread ID.");
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unknown HFS Plus catalog record type {U16(dataReader, 0)}.");
            }
        }
        foreach (uint fileId in attributeFileIds)
        {
            if (!catalogIds.Contains(fileId) && fileId is not (>= 3 and <= 8))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-attribute-orphan",
                    $"The HFS Plus attributes B-tree refers to missing catalog object {fileId}.");
            }
        }
        foreach (var (fileId, hasAttributesFlag) in catalogAttributeFlags)
        {
            if (hasAttributesFlag != attributeFileIds.Contains(fileId))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-attribute-flag-mismatch",
                    $"The HFS Plus catalog object's HasAttributes flag disagrees with its attribute records (CNID {fileId}).");
            }
        }
        foreach (var (fileId, hasSecurityFlag) in catalogSecurityFlags)
        {
            if (hasSecurityFlag != securityAttributeFileIds.Contains(fileId))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-security-flag-mismatch",
                    $"The HFS Plus catalog object's HasSecurity flag disagrees with its ACL attribute (CNID {fileId}).");
            }
        }
        ulong declaredEncodingBitmap = U64(headerReader, 72);
        if ((requiredEncodingBitmap & declaredEncodingBitmap) != requiredEncodingBitmap)
        {
            context.Report(DiagnosticSeverity.Info, "hfs.plus-encoding-bitmap",
                "The HFS Plus volume encoding bitmap omits an encoding used by a catalog file or folder.");
        }

        if (!folders.TryGetValue(RootFolderId, out var root))
        {
            throw new InvalidDataException("The HFS Plus root folder is missing.");
        }

        if (root.Parent != RootParentId)
        {
            throw new InvalidDataException("The HFS Plus root folder does not use the reserved root parent ID.");
        }

        foreach (var (id, node) in catalogNodes)
        {
            if (id != RootFolderId && !folders.ContainsKey(node.Parent))
            {
                throw new InvalidDataException("An HFS Plus catalog item's parent is not an existing folder.");
            }
        }

        const uint catalogNodeIdsReused = 1u << 12;
        uint nextCatalogId = U32(headerReader, 64);
        if (nextCatalogId < 16)
        {
            throw new InvalidDataException("The HFS Plus next catalog ID is reserved.");
        }

        if ((volumeAttributes & catalogNodeIdsReused) == 0 && nextCatalogId <= catalogIds.Max())
        {
            throw new InvalidDataException("The HFS Plus next catalog ID is not greater than all catalog IDs.");
        }

        ValidateCatalogThreads(catalogNodes, catalogThreads);
        var childCounts = new Dictionary<uint, uint>(folders.Count);
        foreach (uint folderId in folders.Keys)
        {
            childCounts.Add(folderId, 0);
        }

        foreach (CatalogNode node in catalogNodes.Values)
        {
            if (childCounts.TryGetValue(node.Parent, out uint childCount))
            {
                childCounts[node.Parent] = checked(childCount + 1);
            }
        }

        foreach (var (folderId, folder) in folders)
        {
            uint childCount = childCounts[folderId];
            if (folder.Valence != childCount)
            {
                throw new InvalidDataException(
                    $"HFS Plus folder {folderId} has valence {folder.Valence}, but {childCount} catalog children.");
            }

            if (folderId != RootFolderId)
            {
                _ = FolderPath(folder.Parent, folders);
            }
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
                {
                    if (privateDataFolderIds.Contains(folder.Parent) && privateDataFolderIds.Add(id))
                    {
                        added = true;
                    }
                }
            }
            while (added);
        }

        if (isHfsX)
        {
            FourCC aliasType = FourCC.FromString("alis");
            FourCC aliasCreator = FourCC.FromString("MACS");
            var folderCounts = new Dictionary<uint, uint>(folders.Count);
            foreach (uint folderId in folders.Keys)
            {
                folderCounts.Add(folderId, 0);
            }

            foreach (var (id, folder) in folders)
            {
                if (folderCounts.TryGetValue(folder.Parent, out uint count))
                {
                    folderCounts[folder.Parent] = checked(count + 1);
                }
            }

            foreach (var (key, data) in records)
            {
                var keyReader = new BigEndianReader(key);
                var dataReader = new BigEndianReader(data);
                if (U16(dataReader, 0) != 2 || (U16(dataReader, 2) & HasLinkChainMask) == 0 ||
                    privateDataFolderIds.Contains(U32(keyReader, 2)) ||
                    U32(dataReader, 48) != aliasType.Value ||
                    U32(dataReader, 52) != aliasCreator.Value)
                {
                    continue;
                }

                uint parentId = U32(keyReader, 2);
                if (folderCounts.TryGetValue(parentId, out uint count))
                {
                    folderCounts[parentId] = checked(count + 1);
                }
            }
            foreach (var (folderId, folder) in folders)
            {
                if (folder.FolderCount != folderCounts[folderId])
                {
                    context.Report(DiagnosticSeverity.Info, "hfs.plus-folder-count",
                        $"HFSX folder {folderId} records {folder.FolderCount} enclosed folders, but {folderCounts[folderId]} are present.");
                }
            }
        }

        JournalInfo? journalInfo = null;
        var catalogFiles = new Dictionary<uint, CatalogFileData>();
        var journalFiles = new Dictionary<string, JournalCatalogFile>(StringComparer.Ordinal);
        foreach (var (key, data) in records)
        {
            var keyReader = new BigEndianReader(key);
            var dataReader = new BigEndianReader(data);
            if (U16(dataReader, 0) != 2)
            {
                continue;
            }

            byte[] info = [.. data.AsSpan(48, 16), .. data.AsSpan(64, 16)];
            uint fileId = U32(dataReader, 8);
            uint special = U32(dataReader, 44);
            FinderInfo finderInfo = FinderInfo.Read(info);
            if (HasHardLinkMarker(finderInfo) && !IsHardLinkFile(finderInfo))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-signature-invalid",
                    $"The HFS Plus file '{Name(keyReader)}' has only part of the required hard-link Finder signature.");
            }

            if (IsHardLinkFile(finderInfo) && special == 0)
            {
                throw new InvalidDataException("An HFS Plus hard link has the reserved zero link reference.");
            }

            var catalogFile = new CatalogFileData(
                fileId, Name(keyReader), U32(keyReader, 2), special, U32(dataReader, 32), U32(dataReader, 36),
                U16(dataReader, 2), U16(dataReader, 42), finderInfo,
                Date(U32(dataReader, 12)), Date(U32(dataReader, 16)),
                ReadFork(image, data.AsMemory(88, 80), blockSize, totalBlocks, overflow, 0, fileId,
                    allocationExtents, ordinaryForkExtents),
                ReadFork(image, data.AsMemory(168, 80), blockSize, totalBlocks, overflow, 0xFF, fileId,
                    allocationExtents, ordinaryForkExtents));
            if (IsHardLinkFile(finderInfo) && U32(dataReader, 88 + 12) != 0)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-alias-has-data",
                    $"The HFS Plus hard-link alias '{catalogFile.Name}' has allocated data-fork blocks.");
            }

            uint parentId = U32(keyReader, 2);
            bool isDirectoryHardLink = IsDirectoryHardLinkAliasCandidate(catalogFile) &&
                !privateDataFolderIds.Contains(parentId);
            bool isJournalFile = (volumeAttributes & volumeJournaledBit) != 0 && parentId == RootFolderId &&
                (catalogFile.Name is ".journal" or ".journal_info_block");
            const ushort regularFileMode = 0x8000;
            if (!IsHardLinkFile(finderInfo) && !isDirectoryHardLink && !isJournalFile &&
                (catalogFile.Mode & 0xF000) == regularFileMode && catalogFile.Special > 1 &&
                parentId != privateDataFolderId)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-file-link-count-invalid",
                    $"The HFS Plus regular file '{catalogFile.Name}' has a BSD hard-link count greater than one.");
            }

            catalogFiles.Add(fileId, catalogFile);
            uint parentIdForJournal = U32(keyReader, 2);
            if ((volumeAttributes & volumeJournaledBit) != 0 && parentIdForJournal == RootFolderId &&
                (catalogFile.Name is ".journal" or ".journal_info_block"))
            {
                journalFiles.Add(catalogFile.Name, ReadJournalCatalogFile(new BigEndianReader(data.AsMemory(88, 80))));
            }
        }
        var directoryInodeFolderIds = new HashSet<uint>();
        if (privateDirectoryDataFolderId is { } directoryDataFolderId)
        {
            foreach (var (folderId, folder) in folders)
            {
                if (folder.Parent == directoryDataFolderId &&
                    (folder.Flags & HasLinkChainMask) != 0 &&
                    TryParseDirectoryInodeName(folder.Name, out uint inodeId) && inodeId == folderId)
                {
                    directoryInodeFolderIds.Add(folderId);
                }
            }
        }

        var directoryAliasesByInode = new Dictionary<uint, List<DirectoryHardLinkAlias>>();
        var directoryAliasFileIds = new HashSet<uint>();
        foreach (CatalogFileData file in catalogFiles.Values)
        {
            if ((file.RecordFlags & HasLinkChainMask) != 0 &&
                !IsHardLinkFile(file.FinderInfo) && !IsDirectoryHardLinkAliasCandidate(file) &&
                !privateDataFolderIds.Contains(file.Parent))
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-chain-flag-unexpected",
                    $"The HFS Plus file '{file.Name}' has the directory hard-link chain flag but is not a hard link.");
            }

            if (!IsDirectoryHardLinkAliasCandidate(file))
            {
                continue;
            }

            if (privateDataFolderIds.Contains(file.Parent))
            {
                continue;
            }

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
            {
                directoryAliasesByInode.Add(file.Special, aliases = []);
            }

            aliases.Add(new DirectoryHardLinkAlias(file.FileId, file.Name, file.Parent));
            directoryAliasFileIds.Add(file.FileId);
        }
        ValidateDirectoryHardLinkChains(directoryInodeFolderIds, directoryAliasesByInode,
            directoryAliasFileIds, directoryFirstLinkIds, folderHardLinkCounts, folderSecurity,
            privateDirectoryDataFolderId, folders, catalogFiles, context);

        if (overflow.Keys.Any(key => key.File != BadBlockFileId))
        {
            throw new InvalidDataException("An HFS Plus overflow extent has no corresponding fork.");
        }

        var hardLinkCounts = new Dictionary<uint, uint>();
        foreach (CatalogFileData file in catalogFiles.Values)
        {
            if (!IsHardLinkFile(file.FinderInfo))
            {
                continue;
            }

            hardLinkCounts.TryGetValue(file.Special, out uint count);
            hardLinkCounts[file.Special] = checked(count + 1);
        }

        var hardLinkTargets = new Dictionary<uint, CatalogFileData>();
        if (privateDataFolderId is { } dataFolderId)
        {
            foreach (CatalogFileData file in catalogFiles.Values)
            {
                if (file.Parent != dataFolderId || !file.Name.StartsWith("iNode", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseHardLinkReference(file.Name, out uint reference))
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-indirect-name-invalid",
                        $"The HFS Plus hard-link indirect node '{file.Name}' does not use a canonical decimal reference.");
                    continue;
                }

                if (!hardLinkTargets.TryAdd(reference, file))
                {
                    throw new InvalidDataException("Duplicate HFS Plus hard-link indirect node reference.");
                }
            }

            foreach (var (_, folder) in folders)
            {
                if (folder.Parent != dataFolderId || !TryParseHardLinkReference(folder.Name, out uint reference) ||
                    !hardLinkCounts.ContainsKey(reference) || hardLinkTargets.ContainsKey(reference))
                {
                    continue;
                }

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
            {
                context.Report(DiagnosticSeverity.Info, "hfs.plus-hardlink-count-mismatch",
                    $"The HFS Plus hard-link indirect node '{target.Name}' records an estimated link count of " +
                    $"{target.Special}, but {actualCount} catalog hard links refer to it.");
            }
        }

        var result = new List<MacFile>();
        foreach (var (key, data) in records)
        {
            var keyReader = new BigEndianReader(key);
            var dataReader = new BigEndianReader(data);
            if (U16(dataReader, 0) != 2)
            {
                continue;
            }

            string name = Name(keyReader);
            uint parent = U32(keyReader, 2);
            uint fileId = U32(dataReader, 8);
            CatalogFileData file = catalogFiles[fileId];
            if (directoryAliasFileIds.Contains(fileId))
            {
                continue;
            }

            bool isHardLink = IsHardLinkFile(file.FinderInfo);
            CatalogFileData target = default;
            bool hasHardLinkTarget = isHardLink && hardLinkTargets.TryGetValue(file.Special, out target);
            if (isHardLink && !hasHardLinkTarget)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-target-missing",
                    $"The HFS Plus hard link '{name}' has no matching indirect node.");
            }

            CatalogFileData content = hasHardLinkTarget ? target : file;
            IReadOnlyList<IReadOnlyList<string>> folderPaths = ResolveFolderPaths(parent, folders,
                privateDataFolderIds, directoryAliasesByInode);
            if (folderPaths.Count == 0)
            {
                continue;
            }

            foreach (IReadOnlyList<string> path in folderPaths)
            {
                result.Add(new MacFile
                {
                    Name = LegacyName(name),
                    UnicodeName = name,
                    FolderPath = path.Select(LegacyName).ToArray(),
                    UnicodeFolderPath = path,
                    FinderInfo = content.FinderInfo,
                    IsLocked = (content.RecordFlags & 0x0001) != 0, // kHFSFileLockedMask (TN1150)
                    Created = content.Created,
                    Modified = content.Modified,
                    DataFork = content.DataFork,
                    ResourceFork = content.ResourceFork,
                    SymbolicLinkTarget = ReadSymbolicLinkTarget(content.Mode, content.FinderInfo,
                        content.DataFork, content.ResourceFork,
                        context.Options.MaxExpandedBytesPerInput),
                    HardLinkReference = isHardLink ? file.Special : null,
                    CatalogId = fileId,
                    ParentId = parent,
                });
            }
        }
        ValidateAllocationExtents(allocationExtents, ordinaryForkExtents, blockSize, totalBlocks);
        ValidateAllocationBitmap(allocationBitmap, totalBlocks, blockSize, allocationExtents);
        if ((volumeAttributes & volumeJournaledBit) != 0)
        {
            journalInfo = ReportJournalInfoBlockProblem(image, headerReader, blockSize, totalBlocks, allocationBitmap,
                context);
            ValidateJournalCatalogFiles(journalInfo, journalFiles, U32(headerReader, 12), blockSize, context);
            if (journalInfo is { } info)
            {
                ValidateJournalHeader(image, info, context);
            }
        }
        uint freeBlocks = CountFreeAllocationBlocks(allocationBitmap, totalBlocks);
        uint declaredFreeBlocks = U32(headerReader, 48);
        if (freeBlocks != declaredFreeBlocks)
        {
            context.Report(DiagnosticSeverity.Info, "hfs.plus-free-blocks",
                $"The allocation bitmap has {freeBlocks} free blocks; the volume header says {declaredFreeBlocks}.");
        }

        if (folderList is not null)
        {
            // HFSPlusCatalogFolder (TN1150): dates at +12 and +16, userInfo (DInfo) at +48, finderInfo (DXInfo) at +64.
            foreach (var (id, folder) in folders)
            {
                if (privateDataFolderIds.Contains(id))
                {
                    continue;
                }

                var record = folderRecords[id];
                var recordReader = new BigEndianReader(record);
                folderList.Add(new MacFolder
                {
                    Name = LegacyName(folder.Name),
                    IsRoot = id == RootFolderId,
                    CatalogId = id,
                    FreeBytes = id == RootFolderId ? (long)U32(headerReader, 48) * blockSize : null,
                    FolderPath = id == RootFolderId ? [] : FolderPath(folder.Parent, folders).Select(LegacyName).ToArray(),
                    FinderInfo = FolderFinderInfo.Read(record.AsSpan(48, FolderFinderInfo.Length)),
                    Created = Date(U32(recordReader, 12)),
                    Modified = Date(U32(recordReader, 16)),
                });
            }
        }
        uint expectedFiles = U32(headerReader, 32);
        uint expectedFolders = U32(headerReader, 36);
        if (catalogFiles.Count != expectedFiles || folders.Count - 1 != expectedFolders)
        {
            context.Report(DiagnosticSeverity.Info, "hfs.plus-counts",
                $"The HFS Plus catalog has {catalogFiles.Count} files and {folders.Count - 1} folders; " +
                $"the volume header says {expectedFiles} and {expectedFolders}.");
        }

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
        var alternateReader = new BigEndianReader(alternate);
        if (U16(alternateReader, 0) != signature || U16(alternateReader, 2) != version)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-alternate-header",
                "The HFS Plus alternate volume header is missing or has an invalid signature or version.", offset);
        }
    }

    private static void ValidateCatalogThreads(Dictionary<uint, CatalogNode> nodes,
        Dictionary<uint, CatalogThread> threads)
    {
        foreach (var (id, node) in nodes)
        {
            if (!threads.TryGetValue(id, out var thread))
            {
                throw new InvalidDataException($"The HFS Plus catalog node {id} has no thread record.");
            }

            if (thread.IsFolder != node.IsFolder || thread.Parent != node.Parent ||
                !string.Equals(thread.Name, node.Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The HFS Plus catalog thread for node {id} is inconsistent.");
            }
        }
        if (threads.Count != nodes.Count)
        {
            throw new InvalidDataException("The HFS Plus catalog has a thread for a missing node.");
        }
    }

    private static void AddCatalogTextEncoding(BigEndianReader record, ref ulong requiredBitmap)
    {
        uint textEncoding = U32(record, 80);
        uint bit = textEncoding switch
        {
            140 => 49, // TN1150 assigns MacFarsi bitmap bit 49.
            152 => 48, // TN1150 assigns MacUkrainian bitmap bit 48.
            <= 63 => textEncoding,
            _ => uint.MaxValue,
        };
        if (bit < 64)
        {
            requiredBitmap |= 1UL << checked((int)bit);
        }
    }

    private static string Name(BigEndianReader key)
    {
        if (key.Source.Length < 8)
        {
            throw new InvalidDataException("An HFS Plus catalog name is truncated.");
        }

        int length = U16(key, 6);
        if (length > 255 || key.Source.Length < 8 + 2 * length)
        {
            throw new InvalidDataException("An HFS Plus catalog name is invalid.");
        }

        return Encoding.BigEndianUnicode.GetString(key.Source.Span.Slice(8, 2 * length))
            .Normalize(NormalizationForm.FormC);
    }

    internal static void ValidateCatalogKey(BigEndianReader key, bool isHfsX)
    {
        int nameLength = U16(key, 6);
        if (nameLength > 255 || key.Source.Length != 8 + nameLength * 2)
        {
            throw new InvalidDataException("An HFSX catalog key has an invalid name length.");
        }

        if (!HfsPlusUnicodeNormalization.IsCanonical(key.Source.Span.Slice(8, nameLength * 2), isHfsX))
        {
            throw new InvalidDataException("An HFS Plus catalog name is not canonically decomposed.");
        }
    }

    private static void ValidateCatalogObjectName(BigEndianReader key)
    {
        int nameLength = U16(key, 6);
        if (nameLength == 1 && U16(key, 8) == '.')
        {
            throw new InvalidDataException("An HFS Plus catalog object is named '.'.");
        }

        if (nameLength == 2 && U16(key, 8) == '.' && U16(key, 10) == '.')
        {
            throw new InvalidDataException("An HFS Plus catalog object is named '..'.");
        }
    }

    private static void ReportInvalidBsdMode(ushort mode, bool isFolder, BigEndianReader key,
        ContainerContext context)
    {
        if (mode == 0)
        {
            return; // Apple fsck_hfs treats a zero BSD info record as uninitialized.
        }

        const ushort fileTypeMask = 0xF000;
        ushort fileType = (ushort)(mode & fileTypeMask);
        bool valid = isFolder
            ? fileType == 0x4000
            : fileType is 0x1000 or 0x2000 or 0x6000 or 0x8000 or 0xA000 or 0xC000;
        if (!valid)
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-invalid-bsd-mode",
                $"The HFS Plus {(isFolder ? "folder" : "file")} '{Name(key)}' has an invalid BSD file type " +
                $"in mode 0x{mode:X4}.");
        }
    }

    private static List<string> FolderPath(uint parent,
        Dictionary<uint, (uint Parent, string Name, uint Valence, uint FolderCount, ushort Flags)> folders)
    {
        var path = new List<string>();
        var seen = new HashSet<uint>();
        while (parent != RootFolderId)
        {
            if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
            {
                throw new InvalidDataException("An HFS Plus folder path is missing or cyclic.");
            }

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
        if (folderId == RootFolderId)
        {
            return [Array.Empty<string>()];
        }

        activeFolderIds ??= [];
        if (!activeFolderIds.Add(folderId))
        {
            return [];
        }

        if (directoryAliasesByInode.TryGetValue(folderId, out List<DirectoryHardLinkAlias>? aliases))
        {
            var paths = new List<IReadOnlyList<string>>();
            foreach (DirectoryHardLinkAlias alias in aliases)
            {
                foreach (IReadOnlyList<string> parentPath in ResolveFolderPaths(alias.Parent, folders,
                    privateDataFolderIds, directoryAliasesByInode, activeFolderIds))
                {
                    paths.Add([.. parentPath, alias.Name]);
                }
            }

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

    internal readonly record struct CatalogFileData(uint FileId, string Name, uint Parent, uint Special,
        uint PreviousLinkId, uint NextLinkId, ushort RecordFlags, ushort Mode,
        FinderInfo FinderInfo, MacDate? Created, MacDate? Modified, ForkData DataFork, ForkData ResourceFork);
    private readonly record struct CatalogNode(uint Parent, string Name, bool IsFolder);
    private readonly record struct CatalogThread(uint Parent, string Name, bool IsFolder);
    private static MacString LegacyName(string name)
    {
        try
        { return MacString.FromMacRoman(name); }
        catch (ArgumentException) { return MacString.FromMacRoman("?"); }
    }
    // An offset outside the data throws ArgumentOutOfRangeException.
    internal static ushort U16(BigEndianReader data, int offset) =>
        data.TryReadUInt16At(offset, out ushort value) ? value : throw new ArgumentOutOfRangeException(nameof(offset));
    internal static uint U32(BigEndianReader data, int offset) =>
        data.TryReadUInt32At(offset, out uint value) ? value : throw new ArgumentOutOfRangeException(nameof(offset));
    internal static ulong U64(BigEndianReader data, int offset) =>
        data.TryReadUInt64At(offset, out ulong value) ? value : throw new ArgumentOutOfRangeException(nameof(offset));
}
