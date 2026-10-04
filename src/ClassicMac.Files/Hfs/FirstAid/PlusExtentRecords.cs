using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// HFS Plus extent records as First Aid reads them (hfs-plus.md §1.3): eight extents of a 32-bit start block and count,
// and a fork's extents from its fork data and the extents tree, as ExtentRecords does for HFS.
internal static class PlusExtentRecords
{
    public const int ForkDataLength = 80, RecordLength = 64;

    /// <summary>
    /// #11, ending the check: each extent starts and ends within the volume, and none with blocks follows an empty one.
    /// </summary>
    public static bool Check(FirstAidRun run, ReadOnlyMemory<byte> record, long cnid, long node)
    {
        if (record.Length < RecordLength)
        {
            return run.Fatal(22, cnid, node);
        }

        var reader = new BigEndianReader(record);
        bool empty = false;
        for (var i = 0; i < 8; i++)
        {
            uint start = reader.ReadUInt32At(8 * i), count = reader.ReadUInt32At(8 * i + 4);
            if (count != 0 && (empty || (long)start + count > run.BlockCount))
            {
                return run.Fatal(11, cnid, node);
            }

            empty |= count == 0;
        }

        return true;
    }

    /// <summary>A record's extents, to the first empty one.</summary>
    public static IEnumerable<(uint Start, uint Count)> Of(ReadOnlyMemory<byte> record)
    {
        var reader = new BigEndianReader(record);
        for (var i = 0; i < 8; i++)
        {
            uint count = reader.ReadUInt32At(8 * i + 4);
            if (count == 0)
            {
                yield break;
            }

            yield return (reader.ReadUInt32At(8 * i), count);
        }
    }

    /// <summary>
    /// A fork's extents: its fork data's eight, then the overflow records for (fork, file ID) from the one starting at the
    /// blocks so far. Null when a record fails <see cref="Check"/> (the problem is recorded).
    /// </summary>
    public static List<(uint Start, uint Count)>? Fork(FirstAidRun run, ReadOnlyMemory<byte> forkData, byte fork, uint fileId, long node)
    {
        var record = forkData.Slice(16, RecordLength);
        if (!Check(run, record, fileId, node))
        {
            return null;
        }

        var extents = new List<(uint Start, uint Count)>(Of(record));
        long blocks = 0;
        foreach (var (_, count) in extents)
        {
            blocks += count;
        }

        if (run.Extents is not { } tree || fileId == 3)
        {
            return extents;
        }

        var records = tree.Records;
        int at = records.FindIndex(r => KeyIs(r.Key, fork, fileId) && new BigEndianReader(r.Key).ReadUInt32At(8) == blocks);
        for (; at >= 0 && at < records.Count && KeyIs(records[at].Key, fork, fileId); at++)
        {
            if (!Check(run, records[at].Data, fileId, node))
            {
                return null;
            }

            // Beyond Disk First Aid: each record's start block is the fork's blocks before it.
            if (new BigEndianReader(records[at].Key).ReadUInt32At(8) != blocks)
            {
                run.Problem("An overflow extents record's start block is not the fork's blocks before it", "firstaid.extent-start", FirstAidRepairs.ExtentStarts);
            }

            foreach (var extent in Of(records[at].Data))
            {
                extents.Add(extent);
                blocks += extent.Count;
            }
        }

        return extents;
    }

    private static bool KeyIs(byte[] key, byte fork, uint fileId) =>
        key.Length >= 12 && key[2] == fork && new BigEndianReader(key).ReadUInt32At(4) == fileId;
}
