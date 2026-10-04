using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsPlusReader;
using static ClassicMac.Files.Hfs.HfsPlusJournal;
using static ClassicMac.Files.Hfs.HfsPlusAttributes;
using static ClassicMac.Files.Hfs.HfsPlusLinks;
using static ClassicMac.Files.Hfs.HfsPlusBTree;

namespace ClassicMac.Files.Hfs;

// HFS Plus reading, for HfsPlusReader: extents, forks read through them, and the allocation bitmap checked against them.
internal static class HfsPlusAllocation
{
    internal static void ValidateAllocationExtents(List<(uint Start, uint End)> extents,
        List<(uint Start, uint End)> ordinaryForkExtents, uint blockSize, uint totalBlocks)
    {
        // TN1150's allocation-file consistency check assigns allocation blocks to fork extents. A block cannot
        // belong to two extents in a valid volume; this follows from that ownership model.
        uint primaryReservedEnd = checked((uint)Math.Min(totalBlocks, (1536UL + blockSize - 1) / blockSize));
        ulong volumeBytes = (ulong)totalBlocks * blockSize;
        uint alternateReservedStart = checked((uint)((volumeBytes > 1024 ? volumeBytes - 1024 : 0) / blockSize));
        foreach (var extent in ordinaryForkExtents)
        {
            if (extent.Start < primaryReservedEnd || extent.End > alternateReservedStart)
            {
                throw new InvalidDataException("HFS Plus forks claim blocks reserved for volume headers.");
            }
        }

        extents.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int index = 0; index < extents.Count; index++)
        {
            if (index > 0 && extents[index].Start < extents[index - 1].End)
            {
                throw new InvalidDataException("HFS Plus forks claim overlapping allocation blocks.");
            }
        }
    }

    internal static ExtentRecordInfo AddExtentRecord(ReadOnlyMemory<byte> extents, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        var reader = new BigEndianReader(extents);
        ValidateExtentDescriptorSequence(reader);
        ulong covered = 0;
        int extentCount = 0;
        for (int index = 0; index < 8; index++)
        {
            uint start = U32(reader, index * 8);
            uint count = U32(reader, index * 8 + 4);
            if (count == 0)
            {
                break;
            }

            AddAllocationExtent(start, count, totalBlocks, allocationExtents, outOfRangeMessage,
                ordinaryForkExtents);
            covered = checked(covered + count);
            extentCount++;
        }
        return new ExtentRecordInfo(covered, extentCount);
    }

    internal static bool HasExtentDescriptor(BigEndianReader extents)
    {
        for (int index = 0; index < 8; index++)
        {
            if (U32(extents, index * 8) != 0 || U32(extents, index * 8 + 4) != 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static void ValidateExtentDescriptorSequence(BigEndianReader extents)
    {
        bool unusedDescriptorSeen = false;
        for (int index = 0; index < 8; index++)
        {
            uint start = U32(extents, index * 8);
            uint count = U32(extents, index * 8 + 4);
            if (count == 0)
            {
                if (start != 0)
                {
                    throw new InvalidDataException("An unused HFS Plus extent descriptor must be zero.");
                }

                unusedDescriptorSeen = true;
            }
            else if (unusedDescriptorSeen)
            {
                throw new InvalidDataException("HFS Plus extent descriptors must not follow an unused descriptor.");
            }
        }
    }

    internal static void AddAllocationExtent(uint start, uint count, uint totalBlocks,
        List<(uint Start, uint End)> allocationExtents, string outOfRangeMessage,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        if ((ulong)start + count > totalBlocks)
        {
            throw new InvalidDataException(outOfRangeMessage);
        }

        var extent = (start, checked(start + count));
        allocationExtents.Add(extent);
        ordinaryForkExtents?.Add(extent);
    }

    internal static void ValidateAllocationBitmap(byte[] bitmap, uint totalBlocks, uint blockSize,
        List<(uint Start, uint End)> extents)
    {
        ulong requiredBytes = ((ulong)totalBlocks + 7) / 8;
        if ((ulong)bitmap.Length < requiredBytes)
        {
            throw new InvalidDataException("The HFS Plus allocation file is too short for the volume.");
        }

        int wholeBitmapBytes = checked((int)(totalBlocks / 8));
        int remainingBits = checked((int)(totalBlocks % 8));
        if (remainingBits != 0)
        {
            int unusedBitMask = (1 << (8 - remainingBits)) - 1;
            if ((bitmap[wholeBitmapBytes] & unusedBitMask) != 0)
            {
                throw new InvalidDataException("Unused HFS Plus allocation bitmap bits must be clear.");
            }

            wholeBitmapBytes++;
        }
        for (int index = wholeBitmapBytes; index < bitmap.Length; index++)
        {
            if (bitmap[index] != 0)
            {
                throw new InvalidDataException("Unused HFS Plus allocation bitmap bits must be clear.");
            }
        }

        uint firstAreaEnd = checked((uint)Math.Min(totalBlocks, (1536UL + blockSize - 1) / blockSize));
        ulong volumeBytes = (ulong)totalBlocks * blockSize;
        uint lastAreaStart = checked((uint)((volumeBytes > 1024 ? volumeBytes - 1024 : 0) / blockSize));
        RequireAllocationRange(bitmap, 0, firstAreaEnd);
        RequireAllocationRange(bitmap, lastAreaStart, totalBlocks);
        foreach (var (start, end) in extents)
        {
            RequireAllocationRange(bitmap, start, end);
        }
    }

    internal static void RequireAllocationRange(byte[] bitmap, uint start, uint end)
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
            {
                throw new InvalidDataException("An HFS Plus allocation block is marked free in the allocation file.");
            }

            block += count;
        }
    }

    internal static uint CountFreeAllocationBlocks(byte[] bitmap, uint totalBlocks)
    {
        int wholeBytes = checked((int)(totalBlocks / 8));
        uint allocated = 0;
        for (int index = 0; index < wholeBytes; index++)
        {
            allocated = checked(allocated + (uint)System.Numerics.BitOperations.PopCount((uint)bitmap[index]));
        }

        int remainingBits = checked((int)(totalBlocks % 8));
        if (remainingBits != 0)
        {
            byte validBits = (byte)(0xFF << (8 - remainingBits));
            allocated = checked(allocated +
                (uint)System.Numerics.BitOperations.PopCount((uint)(bitmap[wholeBytes] & validBits)));
        }
        return totalBlocks - allocated;
    }

    internal static ForkData ReadFork(ForkData image, ReadOnlyMemory<byte> fork, uint blockSize, uint totalBlocks,
        Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>? overflow = null,
        byte forkType = 0, uint fileId = 0, List<(uint Start, uint End)>? allocationExtents = null,
        List<(uint Start, uint End)>? ordinaryForkExtents = null)
    {
        var forkReader = new BigEndianReader(fork);
        ulong logical = U64(forkReader, 0);
        if (logical > long.MaxValue)
        {
            throw new InvalidDataException("An HFS Plus fork is too large.");
        }

        uint allocatedBlocks = U32(forkReader, 12);
        List<(uint Start, byte[] Extents)>? overflowEntries = null;
        if (overflow is not null)
        {
            overflow.TryGetValue((forkType, fileId), out overflowEntries);
        }

        if (allocatedBlocks == 0 && logical != 0)
        {
            throw new InvalidDataException("A nonempty HFS Plus fork has no allocated blocks.");
        }

        if (allocatedBlocks == 0)
        {
            if (HasExtentDescriptor(new BigEndianReader(fork.Slice(16, 64))))
            {
                throw new InvalidDataException("An empty HFS Plus fork has extent descriptors.");
            }

            if (overflowEntries is { Count: > 0 })
            {
                throw new InvalidDataException("An empty HFS Plus fork has overflow extents.");
            }

            return ForkData.Empty;
        }
        var ranges = new List<(long Offset, long Length)>();
        uint coveredBlocks = 0;
        int coveredExtents = 0;
        void AddExtents(BigEndianReader extents, bool addToAllocationOwnership)
        {
            ValidateExtentDescriptorSequence(extents);
            for (int index = 0; index < 8; index++)
            {
                uint start = U32(extents, index * 8);
                uint count = U32(extents, index * 8 + 4);
                if (count == 0)
                {
                    break;
                }

                if (count > allocatedBlocks - coveredBlocks)
                {
                    throw new InvalidDataException("An HFS Plus fork's extents exceed its allocated block count.");
                }

                if ((ulong)start + count > totalBlocks)
                {
                    throw new InvalidDataException("An HFS Plus extent lies outside the allocation area.");
                }

                long offset = checked((long)start * blockSize);
                long length = checked((long)count * blockSize);
                if (offset > image.Length - length)
                {
                    throw new InvalidDataException("An HFS Plus extent lies outside the image.");
                }

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
        AddExtents(new BigEndianReader(fork.Slice(16, 64)), addToAllocationOwnership: true);
        if (overflowEntries is { Count: > 0 })
        {
            if (coveredBlocks >= allocatedBlocks)
            {
                throw new InvalidDataException("An HFS Plus fork has unnecessary overflow extent records.");
            }

            if (coveredExtents != 8)
            {
                throw new InvalidDataException("An HFS Plus fork uses overflow before its first eight extents.");
            }

            var entries = overflowEntries.OrderBy(entry => entry.Start).ToArray();
            for (int index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.Start != coveredBlocks)
                {
                    throw new InvalidDataException("An HFS Plus overflow extent is not contiguous with the fork.");
                }

                int precedingExtentCount = coveredExtents;
                AddExtents(new BigEndianReader(entry.Extents), addToAllocationOwnership: false);
                int recordExtentCount = coveredExtents - precedingExtentCount;
                if (recordExtentCount == 0 || index < entries.Length - 1 && recordExtentCount != 8)
                {
                    throw new InvalidDataException("A non-final HFS Plus overflow record must contain eight extents.");
                }

                if (coveredBlocks > allocatedBlocks ||
                    coveredBlocks == allocatedBlocks && index < entries.Length - 1)
                {
                    throw new InvalidDataException("An HFS Plus fork has excess overflow extent records.");
                }
            }
        }
        if (coveredBlocks != allocatedBlocks)
        {
            throw new InvalidDataException("An HFS Plus fork's extent count differs from its allocated block count.");
        }

        if ((ulong)coveredBlocks * blockSize < logical)
        {
            throw new InvalidDataException("An HFS Plus fork has insufficient extents for its logical length.");
        }

        if (overflowEntries is { Count: > 0 })
        {
            overflow!.Remove((forkType, fileId));
        }

        if (logical == 0)
        {
            return ForkData.Empty;
        }

        return new ExtentForkData(image, ranges, checked((long)logical));
    }

    internal readonly record struct ExtentRecordInfo(ulong BlockCount, int ExtentCount);
}
