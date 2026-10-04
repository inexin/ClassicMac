using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusReader;
using static ClassicMac.Files.Hfs.HfsPlusJournal;
using static ClassicMac.Files.Hfs.HfsPlusAllocation;
using static ClassicMac.Files.Hfs.HfsPlusLinks;
using static ClassicMac.Files.Hfs.HfsPlusBTree;

namespace ClassicMac.Files.Hfs;

// HFS Plus reading, for HfsPlusReader: the attributes B-tree: fork records, security attributes and attribute keys.
internal static class HfsPlusAttributes
{
    internal static void CollectAttributeForkRecord(BigEndianReader key, byte[] data, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, List<(uint Start, uint End)> ordinaryForkExtents,
        Dictionary<(uint FileId, string Name), AttributeForkState> attributeForks,
        Dictionary<uint, byte[]> directoryFirstLinkIds, HashSet<uint> attributeFileIds)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("An HFS Plus attribute record is truncated.");
        }

        var dataReader = new BigEndianReader(data);
        attributeFileIds.Add(U32(key, 4));
        uint recordType = U32(dataReader, 0);
        if (recordType == 0x10)
        {
            const int inlineAttributeHeaderSize = 16;
            if (data.Length < inlineAttributeHeaderSize)
            {
                throw new InvalidDataException("An HFS Plus inline attribute record is truncated.");
            }

            ulong declaredLength = (ulong)inlineAttributeHeaderSize + U32(dataReader, 12);
            ulong alignedLength = (declaredLength + 1) & ~1UL;
            if (alignedLength != (ulong)data.Length)
            {
                throw new InvalidDataException(
                    $"An HFS Plus inline attribute has an invalid length ({data.Length} bytes for " +
                    $"{U32(dataReader, 12)} data bytes).");
            }

            byte[] firstLinkName = Encoding.BigEndianUnicode.GetBytes("com.apple.system.hfs.firstlink");
            int attributeNameLength = U16(key, 12);
            if (U32(key, 8) == 0 && key.Source.Span.Slice(14, attributeNameLength * 2).SequenceEqual(firstLinkName))
            {
                directoryFirstLinkIds[U32(key, 4)] = data.AsSpan(inlineAttributeHeaderSize,
                    checked((int)U32(dataReader, 12))).ToArray();
            }

            return;
        }
        if (recordType is not (0x20 or 0x30))
        {
            return;
        }

        var identity = (FileId: U32(key, 4), Name: Convert.ToHexString(key.Source.Span.Slice(14, U16(key, 12) * 2)));
        if (!attributeForks.TryGetValue(identity, out AttributeForkState? state))
        {
            attributeForks.Add(identity, state = new AttributeForkState());
        }

        switch (recordType)
        {
            case 0x20:
                if (data.Length != 88)
                {
                    throw new InvalidDataException("An HFS Plus fork-data attribute has an invalid length.");
                }

                if (U32(key, 8) != 0 || state.HasForkData)
                {
                    throw new InvalidDataException("An HFS Plus attribute fork-data record has an invalid key.");
                }

                ReadOnlyMemory<byte> fork = data.AsMemory(8, 80);
                var forkReader = new BigEndianReader(fork);
                state.HasForkData = true;
                state.LogicalSize = U64(forkReader, 0);
                state.TotalBlocks = U32(forkReader, 12);
                ExtentRecordInfo initialRecord = AddExtentRecord(fork[16..], totalBlocks, allocationExtents,
                    "An HFS Plus fork-data attribute lies outside the allocation area.", ordinaryForkExtents);
                state.InitialBlocks = initialRecord.BlockCount;
                state.InitialExtentCount = initialRecord.ExtentCount;
                if (state.InitialBlocks > state.TotalBlocks)
                {
                    throw new InvalidDataException("An HFS Plus fork-data attribute exceeds its allocated blocks.");
                }

                return;
            case 0x30:
                if (data.Length != 72)
                {
                    throw new InvalidDataException("An HFS Plus attribute extension record has an invalid length.");
                }

                ExtentRecordInfo extension = AddExtentRecord(data.AsMemory(8, 64), totalBlocks, allocationExtents,
                    "An HFS Plus attribute extension extent lies outside the allocation area.", ordinaryForkExtents);
                state.Extensions.Add((U32(key, 8), extension));
                return;
        }
    }

    internal static void ValidateSecurityAttribute(ReadOnlyMemory<byte> record, ContainerContext context)
    {
        const uint inlineDataRecord = 0x10;
        const uint fileSecurityMagic = 0x012CC16D;
        const uint noAclEntryCount = uint.MaxValue;
        const uint maximumAclEntries = 128;
        const int inlineHeaderSize = 16;
        const int fileSecurityHeaderSize = 44;
        const int accessControlEntrySize = 24;

        var recordReader = new BigEndianReader(record);
        bool invalid = record.Length < inlineHeaderSize || U32(recordReader, 0) != inlineDataRecord;
        if (!invalid)
        {
            uint valueLength = U32(recordReader, 12);
            if (valueLength > int.MaxValue || inlineHeaderSize + (ulong)valueLength > (ulong)record.Length)
            {
                invalid = true;
            }
            else
            {
                ReadOnlyMemory<byte> value = record.Slice(inlineHeaderSize, (int)valueLength);
                var valueReader = new BigEndianReader(value);
                invalid = value.Length < fileSecurityHeaderSize || U32(valueReader, 0) != fileSecurityMagic;
                if (!invalid)
                {
                    uint entryCount = U32(valueReader, 36);
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
                            uint kind = U32(valueReader, entryOffset + 16) & 0xF;
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
        {
            context.Report(DiagnosticSeverity.Warning, "hfs.plus-acl-invalid",
                "An HFS Plus com.apple.system.Security attribute has an invalid file-security header, ACL entry layout, or ACE kind.");
        }
    }

    internal static void ValidateAttributeForks(
        Dictionary<(uint FileId, string Name), AttributeForkState> attributeForks, uint blockSize)
    {
        foreach (AttributeForkState fork in attributeForks.Values)
        {
            if (!fork.HasForkData)
            {
                throw new InvalidDataException("An HFS Plus attribute extension has no corresponding fork-data record.");
            }

            var extensions = fork.Extensions.OrderBy(extension => extension.StartBlock).ToArray();
            if (extensions.Length > 0 && fork.InitialExtentCount != 8)
            {
                throw new InvalidDataException("An HFS Plus attribute fork uses overflow before its first eight extents.");
            }

            ulong covered = fork.InitialBlocks;
            for (int index = 0; index < extensions.Length; index++)
            {
                var extension = extensions[index];
                if (extension.Record.ExtentCount == 0 || extension.StartBlock != covered)
                {
                    throw new InvalidDataException("An HFS Plus attribute extension does not continue its fork extents.");
                }

                if (index < extensions.Length - 1 && extension.Record.ExtentCount != 8)
                {
                    throw new InvalidDataException("A non-final HFS Plus attribute extension must contain eight extents.");
                }

                covered = checked(covered + extension.Record.BlockCount);
                if (covered > fork.TotalBlocks)
                {
                    throw new InvalidDataException("An HFS Plus attribute extension exceeds its allocated blocks.");
                }
            }

            if (covered != fork.TotalBlocks || fork.LogicalSize > covered * blockSize)
            {
                throw new InvalidDataException("An HFS Plus attribute fork's extents disagree with its declared size.");
            }
        }
    }

    internal static void ValidateAttributeKey(ReadOnlyMemory<byte> key)
    {
        var reader = new BigEndianReader(key);
        if (key.Length < 14 || U16(reader, 0) != key.Length - 2 || U16(reader, 2) != 0)
        {
            throw new InvalidDataException("The HFS Plus attributes B-tree key length or padding is invalid.");
        }

        int nameLength = U16(reader, 12);
        if (nameLength > 127 || key.Length != 14 + 2 * nameLength)
        {
            throw new InvalidDataException("The HFS Plus attributes B-tree key name length is invalid.");
        }
    }

    // Apple’s HFS comparator orders attribute keys by file ID, name length, binary UTF-16 name,
    // then start block (hfs_attrkeycompare in Apple’s HFS source).
    internal static int CompareAttributeKeys(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        var leftReader = new BigEndianReader(left);
        var rightReader = new BigEndianReader(right);
        int comparison = U32(leftReader, 4).CompareTo(U32(rightReader, 4));
        if (comparison != 0)
        {
            return comparison;
        }

        int leftNameLength = U16(leftReader, 12);
        int rightNameLength = U16(rightReader, 12);
        comparison = leftNameLength.CompareTo(rightNameLength);
        if (comparison != 0)
        {
            return comparison;
        }

        for (int index = 0; index < leftNameLength; index++)
        {
            comparison = U16(leftReader, 14 + index * 2).CompareTo(U16(rightReader, 14 + index * 2));
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return U32(leftReader, 8).CompareTo(U32(rightReader, 8));
    }

    internal sealed class AttributeForkState
    {
        public ulong LogicalSize { get; set; }
        public uint TotalBlocks { get; set; }
        public ulong InitialBlocks { get; set; }
        public int InitialExtentCount { get; set; }
        public bool HasForkData { get; set; }
        public List<(uint StartBlock, ExtentRecordInfo Record)> Extensions { get; } = [];
    }
}
