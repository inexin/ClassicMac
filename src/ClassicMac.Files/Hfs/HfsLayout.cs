using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsWriter;
using static ClassicMac.Files.Hfs.HfsCatalogEditing;
using static ClassicMac.Files.Hfs.HfsBTreeWriting;
using static ClassicMac.Files.Hfs.HfsRecords;
using static ClassicMac.Files.Hfs.HfsAllocation;

namespace ClassicMac.Files.Hfs;

// HfsReader.ReadLayout (hfs.md §5.7): each file record's forks in extents (its catalog record's three, then its overflow
// records'), the bitmap's runs of free blocks, and the smallest sizes: the least block count a shrink's moves fit in
// now (§3.3, the same plan Shrink makes), and the blocks in use.
internal static class HfsLayout
{
    internal static VolumeLayout? Measure(ForkData input)
    {
        CatalogEditState state;
        try
        {
            state = OpenCatalog(input, writable: false);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }

        var mdb = new BigEndianReader(state.Mdb);
        if (mdb.ReadUInt16At(0x7C) == 0x482B)
        {
            return null;                                                                // an HFS wrapper
        }

        var overflow = new Dictionary<(byte Fork, uint File), List<BlockRange>>();
        foreach (var record in LeafRecords(state.ExtentsTree))
        {
            var key = (record.Key[1], KeyId(record.Key));
            overflow[key] = [.. overflow.GetValueOrDefault(key) ?? [], .. Extents(record.Data, 0)];
        }

        int files = 0, splitFiles = 0, splitForks = 0, most = 0;
        var split = new List<BlockRange>();
        foreach (var (_, data) in state.Records)
        {
            if (data.Length < 0x62 || data[0] != 2)
            {
                continue;
            }

            files++;
            uint id = FileId(data);
            bool fragmented = false;
            foreach (var (fork, offset) in new[] { ((byte)0x00, 0x4A), ((byte)0xFF, 0x56) })
            {
                List<BlockRange> extents = [.. Extents(data, offset), .. overflow.GetValueOrDefault((fork, id)) ?? []];
                most = Math.Max(most, extents.Count);
                if (extents.Count > 1)
                {
                    splitForks++;
                    fragmented = true;
                    split.AddRange(extents);
                }
            }

            splitFiles += fragmented ? 1 : 0;
        }

        var free = HfsResizer.FreeRuns(state.Bitmap, state.BlockCount);
        long used = state.BlockCount - free.Sum(r => r.Count);

        // The smallest block count the shrink's plan fits: between the blocks in use and the volume's own.
        var catalog = state.Catalog.ToArray();
        var extentsTree = state.ExtentsTree.ToArray();
        var descriptors = HfsResizer.Descriptors(state.Mdb.ToArray(), catalog, extentsTree)
            .ConvertAll(d => (HfsResizer.Extent(d.Bytes, d.Offset), d.BadBlocks));
        long low = Math.Max(1, used), high = state.BlockCount;
        while (low < high)
        {
            long middle = (low + high) / 2;
            if (HfsResizer.PlanMoves(descriptors, free, middle) is not null)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        long Size(long blocks) => state.FirstBlock + blocks * state.BlockSize + 2L * BlockSize;
        return new VolumeLayout(state.BlockCount, state.BlockSize, files, splitFiles, splitForks, most, free,
            [.. split.OrderBy(e => e.Start)], Size(low), Size(Math.Max(1, used)));
    }

    // The extents with blocks among the three descriptors at offset.
    private static IEnumerable<BlockRange> Extents(byte[] data, int offset)
    {
        var reader = new BigEndianReader(data);
        for (var slot = 0; slot < 3; slot++)
        {
            int start = reader.ReadUInt16At(offset + slot * 4), count = reader.ReadUInt16At(offset + slot * 4 + 2);
            if (count > 0)
            {
                yield return new BlockRange(start, count);
            }
        }
    }
}
