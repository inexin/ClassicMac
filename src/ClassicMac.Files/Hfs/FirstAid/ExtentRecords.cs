using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// Extent records as First Aid reads them: ChkExtRec, and a fork's extents from its catalog record and the extents
// tree (hfs.md §5.6) [Code: Disk First Aid 8.5.5, CODE 1 $1FDC2 ChkExtRec, $1DF42 fork walk].
internal static class ExtentRecords
{
    /// <summary>
    /// ChkExtRec (#11, ending the check): each of the record's three extents starts and counts below the volume's block
    /// count, and none with blocks follows an empty one; a start plus count past the end is not checked.
    /// </summary>
    public static bool Check(FirstAidRun run, ReadOnlySpan<byte> record, long cnid, long node)
    {
        if (record.Length < 12)
        {
            return run.Fatal(22, cnid, node);
        }

        uint previousCount = 1;
        for (var i = 0; i < 3; i++)
        {
            uint start = (uint)(record[4 * i] << 8 | record[4 * i + 1]), count = (uint)(record[4 * i + 2] << 8 | record[4 * i + 3]);
            if (start >= run.BlockCount || count >= run.BlockCount || previousCount == 0 && count != 0)
            {
                return run.Fatal(11, cnid, node);
            }

            previousCount = count;
        }

        return true;
    }

    /// <summary>A record's extents, to the first empty one.</summary>
    public static IEnumerable<(uint Start, uint Count)> Of(ReadOnlyMemory<byte> record)
    {
        var reader = new BigEndianReader(record);
        for (var i = 0; i < 3; i++)
        {
            uint count = reader.ReadUInt16At(4 * i + 2);
            if (count == 0)
            {
                yield break;
            }

            yield return (reader.ReadUInt16At(4 * i), count);
        }
    }

    /// <summary>
    /// A fork's extents: its catalog record's, then the overflow records for (fork, file ID, start block = the blocks so
    /// far) and those after it with the same file and fork; their start blocks are not checked. Null when an extent
    /// record fails ChkExtRec (the problem is recorded).
    /// </summary>
    public static List<(uint Start, uint Count)>? Fork(FirstAidRun run, ReadOnlyMemory<byte> catalogRecord, byte fork, uint fileId, long node)
    {
        if (!Check(run, catalogRecord.Span, fileId, node))
        {
            return null;
        }

        var extents = new List<(uint Start, uint Count)>(Of(catalogRecord));
        uint blocks = 0;
        foreach (var (_, count) in extents)
        {
            blocks += count;
        }

        var records = run.Extents!.Records;
        int at = records.FindIndex(r => KeyIs(r.Key, fork, fileId) && (r.Key[6] << 8 | r.Key[7]) == blocks);
        for (; at >= 0 && at < records.Count && KeyIs(records[at].Key, fork, fileId); at++)
        {
            if (!Check(run, records[at].Data, fileId, node))
            {
                return null;
            }

            extents.AddRange(Of(records[at].Data));
        }

        return extents;
    }

    private static bool KeyIs(byte[] key, byte fork, uint fileId) =>
        key.Length >= 8 && key[1] == fork && (uint)(key[2] << 24 | key[3] << 16 | key[4] << 8 | key[5]) == fileId;
}
