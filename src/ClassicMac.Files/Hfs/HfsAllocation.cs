using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;

namespace ClassicMac.Files.Hfs;

// HfsWriter's allocation: extents and overflow records, the volume bitmap, and forks read and written through their
// extents (hfs.md §1.4, §2.5).
internal static class HfsAllocation
{
    internal static void ValidateExtentOwnership(uint blockCount, byte[] bitmap,
        List<(ushort Start, ushort Count)> extentsFile, List<(ushort Start, ushort Count)> catalogFile,
        TreeRecord[] catalogRecords, Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow,
        TreeRecord target, uint targetFileId, int targetForkOffset, List<(ushort Start, ushort Count)> targetExtents)
    {
        var claimed = new HashSet<ushort>();
        Claim(extentsFile, "extents-overflow file");
        Claim(catalogFile, "catalog file");
        if (overflow.TryGetValue((0x00, 3), out var extentsOverflow))
        {
            foreach (var record in extentsOverflow.OrderBy(r => r.Start))
            {
                Claim(ParseExtents(new BigEndianReader(record.Extents)), "extents-overflow file");
            }
        }

        foreach (var record in catalogRecords)
        {
            if (record.Key.Length < 7 || record.Data.Length < 102 || record.Data[0] != 2)
            {
                continue;
            }

            var data = new BigEndianReader(record.Data);
            uint id = U32(data, 20);
            foreach (var (kind, offset) in new[] { ((byte)0, 74), ((byte)0xFF, 86) })
            {
                if (ReferenceEquals(record, target) && id == targetFileId && offset == targetForkOffset)
                {
                    continue;
                }

                var fileExtents = ParseExtents(data, offset);
                Claim(fileExtents, "file fork");
                if (overflow.TryGetValue((kind, id), out var fileOverflow))
                {
                    foreach (var extra in fileOverflow.OrderBy(r => r.Start))
                    {
                        Claim(ParseExtents(new BigEndianReader(extra.Extents)), "file fork");
                    }
                }
            }
        }
        Claim(targetExtents, "target fork");

        void Claim(IEnumerable<(ushort Start, ushort Count)> extents, string owner)
        {
            foreach (var (start, count) in extents)
            {
                if ((uint)start + count > blockCount)
                {
                    throw new InvalidDataException($"An extent of the {owner} lies outside the HFS allocation area.");
                }

                for (uint block = start; block < (uint)start + count; block++)
                {
                    ushort index = (ushort)block;
                    if (!IsAllocated(bitmap, index))
                    {
                        throw new InvalidDataException($"An extent of the {owner} points to a free allocation block.");
                    }

                    if (!claimed.Add(index))
                    {
                        throw new InvalidDataException($"An allocation block is shared by multiple HFS extents ({owner}).");
                    }
                }
            }
        }
    }

    internal static byte[] ReadFork(HfsVolume image, uint firstBlock, uint blockSize, uint blockCount,
        List<(ushort Start, ushort Count)> firstExtents, uint logicalLength,
        Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow,
        byte overflowFork, uint fileId, bool includeOverflow)
    {
        var extents = firstExtents.ToList();
        if (includeOverflow && overflow.TryGetValue((overflowFork, fileId), out var extra))
        {
            extents.AddRange(extra.OrderBy(x => x.Start)
                .SelectMany(x => ParseExtents(new BigEndianReader(x.Extents))));
        }

        ulong capacity = extents.Aggregate<(ushort Start, ushort Count), ulong>(0, (n, e) => n + (ulong)e.Count * blockSize);
        if (logicalLength > capacity || logicalLength > int.MaxValue)
        {
            throw new InvalidDataException("An HFS system fork has invalid length or extents.");
        }

        var output = new byte[(int)logicalLength];
        int written = 0;
        foreach (var (start, count) in extents)
        {
            if ((uint)start + count > blockCount)
            {
                throw new InvalidDataException("An HFS extent lies outside the allocation area.");
            }

            int take = (int)Math.Min((ulong)(output.Length - written), (ulong)count * blockSize);
            long sourceOffset = (long)firstBlock + (long)start * blockSize;
            if (sourceOffset < 0 || sourceOffset + take > image.Length)
            {
                throw new InvalidDataException("An HFS extent lies outside the image.");
            }

            image.Read(sourceOffset, output.AsSpan(written, take));
            written += take;
            if (written == output.Length)
            {
                break;
            }
        }
        return output;
    }

    internal static List<(ushort Start, ushort Count)> ParseExtents(BigEndianReader bytes, int offset = 0)
    {
        var result = new List<(ushort, ushort)>();
        for (int i = 0; i < 3; i++)
        {
            ushort start = U16(bytes, offset + i * 4), count = U16(bytes, offset + i * 4 + 2);
            if (count != 0)
            {
                result.Add((start, count));
            }
        }
        return result;
    }

    internal static int LastExtentSlot(BigEndianReader record)
    {
        int last = -1;
        bool emptySeen = false;
        for (int slot = 0; slot < 3; slot++)
        {
            ushort start = U16(record, slot * 4), count = U16(record, slot * 4 + 2);
            if (count == 0)
            {
                if (start != 0)
                {
                    throw new InvalidDataException("An empty HFS extent descriptor has a nonzero start block.");
                }

                emptySeen = true;
            }
            else
            {
                if (emptySeen)
                {
                    throw new InvalidDataException("An HFS extent record has a populated descriptor after an empty slot.");
                }

                last = slot;
            }
        }
        return last;
    }

    internal static void WriteExtent(Span<byte> record, int slot, ushort start, ushort count)
    {
        var extent = new BigEndianWriter(4);
        extent.WriteUInt16(start);
        extent.WriteUInt16(count);
        extent.WrittenSpan.CopyTo(record.Slice(slot * 4, 4));
    }

    internal static List<(ushort Start, ushort Count)> AllocateRuns(byte[] bitmap, ushort blockCount, ulong required, int maxRuns)
    {
        var freeRuns = new List<(ushort Start, ushort Count)>();
        int runStart = -1;
        for (int block = 0; block <= blockCount; block++)
        {
            bool free = block < blockCount && !IsAllocated(bitmap, (ushort)block);
            if (free && runStart < 0)
            {
                runStart = block;
            }

            if (!free && runStart >= 0)
            {
                freeRuns.Add((checked((ushort)runStart), checked((ushort)(block - runStart))));
                runStart = -1;
            }
        }
        freeRuns.Sort((left, right) => right.Count.CompareTo(left.Count));
        var chosen = new List<(ushort Start, ushort Count)>();
        ulong remaining = required;
        foreach (var run in freeRuns)
        {
            if (remaining == 0)
            {
                break;
            }

            if (chosen.Count == maxRuns)
            {
                break;
            }

            ushort take = (ushort)Math.Min((ulong)run.Count, remaining);
            chosen.Add((run.Start, take));
            remaining -= take;
        }
        if (remaining != 0)
        {
            throw new InvalidDataException("There are not enough free HFS allocation blocks in the fork's available extent slots.");
        }

        chosen.Sort((left, right) => left.Start.CompareTo(right.Start));
        return chosen;
    }

    internal static bool IsAllocated(byte[] bitmap, ushort block)
    {
        int index = block >> 3;
        if (index >= bitmap.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap is too short.");
        }

        return (bitmap[index] & (0x80 >> (block & 7))) != 0;
    }

    internal static void SetBitmap(byte[] bitmap, ushort block, bool allocated)
    {
        int index = block >> 3;
        if (index >= bitmap.Length)
        {
            throw new InvalidDataException("The HFS volume bitmap is too short.");
        }

        byte mask = (byte)(0x80 >> (block & 7));
        if (allocated)
        {
            bitmap[index] |= mask;
        }
        else
        {
            bitmap[index] &= (byte)~mask;
        }
    }

    internal static void WriteFork(HfsVolume image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, ReadOnlySpan<byte> data)
    {
        int at = 0;
        foreach (var (start, count) in extents)
        {
            int take = Math.Min(data.Length - at, checked((int)((uint)count * blockSize)));
            if (take <= 0)
            {
                break;
            }

            long offset = firstBlock + (long)start * blockSize;
            image.Write(offset, data.Slice(at, take));
            at += take;
        }
        if (at != data.Length)
        {
            throw new InvalidDataException("The HFS extents cannot hold the data to write.");
        }
    }

    internal static void ClearForkTail(HfsVolume image, uint firstBlock, uint blockSize, List<(ushort Start, ushort Count)> extents, int dataLength)
    {
        long skip = dataLength;
        foreach (var (start, count) in extents)
        {
            int bytes = checked((int)((uint)count * blockSize));
            long offset = firstBlock + (long)start * blockSize;
            int clearFrom = (int)Math.Clamp(skip, 0, bytes);
            if (clearFrom < bytes)
            {
                image.Write(offset + clearFrom, new byte[bytes - clearFrom]);
            }

            skip -= bytes;
        }
    }

    internal static List<(ushort Start, ushort Count)> WithOverflow(List<(ushort Start, ushort Count)> initial,
        Dictionary<(byte Fork, uint File), List<(ushort Start, byte[] Extents)>> overflow, byte fork, uint fileId)
    {
        var result = initial.ToList();
        if (overflow.TryGetValue((fork, fileId), out var values))
        {
            result.AddRange(values.OrderBy(x => x.Start)
                .SelectMany(x => ParseExtents(new BigEndianReader(x.Extents))));
        }

        return result;
    }

    internal static void ValidateBitmapFreeCount(uint blockCount, byte[] bitmap, ushort expectedFree)
    {
        uint actualFree = 0;
        for (uint block = 0; block < blockCount; block++)
        {
            if (!IsAllocated(bitmap, checked((ushort)block)))
            {
                actualFree++;
            }
        }

        if (actualFree != expectedFree)
        {
            throw new InvalidDataException("The HFS volume free-block count disagrees with its allocation bitmap.");
        }
    }
}
