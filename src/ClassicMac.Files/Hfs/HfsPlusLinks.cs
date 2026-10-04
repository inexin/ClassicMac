using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusReader;
using static ClassicMac.Files.Hfs.HfsPlusJournal;
using static ClassicMac.Files.Hfs.HfsPlusAllocation;
using static ClassicMac.Files.Hfs.HfsPlusAttributes;
using static ClassicMac.Files.Hfs.HfsPlusBTree;

namespace ClassicMac.Files.Hfs;

// HFS Plus reading, for HfsPlusReader: file and directory hard links and symbolic links.
internal static class HfsPlusLinks
{
    internal static void ValidateDirectoryHardLinkChains(
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
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-private-directory-invalid",
                "The HFS Plus private directory for directory hard links is missing its immutable flag or sticky bit.");
        }

        var reportedAncestors = new HashSet<uint>();
        foreach (DirectoryHardLinkAlias alias in directoryAliasesByInode.Values.SelectMany(aliases => aliases))
        {
            uint parentId = alias.Parent;
            var visitedAncestors = new HashSet<uint>();
            while (parentId != RootFolderId && parentId != privateDirectoryDataFolderId)
            {
                if (!visitedAncestors.Add(parentId) || !folders.TryGetValue(parentId, out var parentFolder))
                {
                    break;
                }

                if ((parentFolder.Flags & HasChildLinkMask) == 0 && reportedAncestors.Add(parentId))
                {
                    context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-ancestor-flag-missing",
                        $"The HFS Plus directory hard-link ancestor folder '{parentFolder.Name}' is missing its HasChildLink flag.");
                }

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
            {
                invalid = true;
            }

            if (invalid)
            {
                context.Report(DiagnosticSeverity.Warning, "hfs.plus-hardlink-chain-invalid",
                    $"The HFS Plus directory hard-link chain for inode {inodeId} is missing or inconsistent.");
            }
        }
    }

    internal static bool TryParseDirectoryFirstLinkId(byte[] value, out uint linkId)
    {
        linkId = 0;
        if (value.Length < 2 || value[^1] != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> digits = value.AsSpan(0, value.Length - 1);
        if (digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0 ||
            !ulong.TryParse(Encoding.ASCII.GetString(digits), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong parsed) ||
            parsed < 16 || parsed > uint.MaxValue)
        {
            return false;
        }

        linkId = (uint)parsed;
        return true;
    }

    internal static string? ReadSymbolicLinkTarget(ushort fileMode, FinderInfo finderInfo,
        ForkData dataFork, ForkData resourceFork, long maxExpandedBytes)
    {
        const ushort fileTypeMask = 0xF000;
        const ushort symbolicLinkMode = 0xA000;
        if ((fileMode & fileTypeMask) != symbolicLinkMode)
        {
            return null;
        }

        if (finderInfo.Type != FourCC.FromString("slnk") || finderInfo.Creator != FourCC.FromString("rhap"))
        {
            throw new InvalidDataException("An HFS Plus symbolic link has invalid Finder type or creator codes.");
        }

        if (resourceFork.Length != 0)
        {
            throw new InvalidDataException("An HFS Plus symbolic link has a nonempty resource fork.");
        }

        byte[] target = dataFork.ToArray(maxExpandedBytes);
        if (Array.IndexOf(target, (byte)0) >= 0)
        {
            throw new InvalidDataException("An HFS Plus symbolic-link path contains a null byte.");
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(target);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("An HFS Plus symbolic-link path is not valid UTF-8.", exception);
        }
    }

    internal static bool IsHardLinkFile(FinderInfo finderInfo) =>
        finderInfo.Type == FourCC.FromString("hlnk") && finderInfo.Creator == FourCC.FromString("hfs+");

    internal static bool IsDirectoryHardLinkAliasCandidate(CatalogFileData file) =>
        (file.RecordFlags & 0x0020) != 0 &&
        (file.FinderInfo.Type == FourCC.FromString("alis") || file.FinderInfo.Creator == FourCC.FromString("MACS"));

    internal static bool HasValidDirectoryHardLinkAliasSignature(CatalogFileData file) =>
        file.FinderInfo.Type == FourCC.FromString("alis") && file.FinderInfo.Creator == FourCC.FromString("MACS") &&
        (file.FinderInfo.Flags & FinderFlags.IsAlias) != 0;

    internal static bool HasHardLinkMarker(FinderInfo finderInfo) =>
        finderInfo.Type == FourCC.FromString("hlnk") || finderInfo.Creator == FourCC.FromString("hfs+");

    internal static bool TryParseHardLinkReference(string name, out uint reference)
    {
        reference = 0;
        if (!name.StartsWith("iNode", StringComparison.Ordinal) ||
            !uint.TryParse(name.AsSpan(5), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out reference))
        {
            return false;
        }

        string canonicalReference = reference.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return name.AsSpan(5).SequenceEqual(canonicalReference.AsSpan());
    }

    internal static bool TryParseDirectoryInodeName(string name, out uint inodeId)
    {
        inodeId = 0;
        if (!name.StartsWith("dir_", StringComparison.Ordinal) ||
            !uint.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out inodeId))
        {
            return false;
        }

        return name.AsSpan(4).SequenceEqual(inodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal readonly record struct DirectoryHardLinkAlias(uint FileId, string Name, uint Parent);
}
