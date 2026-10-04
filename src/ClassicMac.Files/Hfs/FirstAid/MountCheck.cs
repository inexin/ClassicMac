using System.Linq;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// Disk First Aid's copy of the File Manager's MountCheck, run at the end of the catalog scan (hfs.md §5.6): the
// catalog's valences and threads, each fork's PEOF against its blocks, and the free blocks the extents leave against the
// bitmap's. A used block the bitmap marks free is serious; anything else found is minor. Either is one line with no
// numbers [Code: Disk First Aid 8.5.5, CODE 1 $12D6A, $12E78, $14408] [Verified: C2, C4, C6, D1, D2].
internal static class MountCheck
{
    public static void Run(FirstAidRun run)
    {
        int valences = 0, dirs = 0, files = 0, withThreadFlag = 0, dirThreads = 0, fileThreads = 0;
        bool forkMismatch = false;
        foreach (var (key, data, _) in run.Catalog!.Records)
        {
            var reader = new BigEndianReader(data);
            switch (data[0])
            {
                case 1:
                    valences += reader.ReadUInt16At(4);
                    if (KeyId(key) != 1)
                    {
                        dirs++;
                    }

                    break;
                case 2:
                    files++;
                    withThreadFlag += (data[2] & 0x02) != 0 ? 1 : 0;
                    uint id = reader.ReadUInt32At(0x14);
                    forkMismatch |= Blocks(run, id, 0x00) * run.BlockSize != reader.ReadUInt32At(0x1E)
                        || Blocks(run, id, 0xFF) * run.BlockSize != reader.ReadUInt32At(0x28);
                    break;
                case 3:
                    dirThreads++;
                    break;
                case 4:
                    fileThreads++;
                    break;
            }
        }

        // The free blocks: what the extents leave, against the bitmap's clear bits.
        long computedFree = run.BlockCount - run.ForkExtents.Sum(e => (long)e.Count);
        long bitmapFree = BitmapFree(run);
        if (computedFree < bitmapFree)
        {
            run.Problem(FirstAidMessages.MountCheckSerious, "firstaid.mountcheck-serious", FirstAidRepairs.OrphanedExtents);
            return;
        }

        var repairs = FirstAidRepairs.None;
        if (forkMismatch)
        {
            repairs |= FirstAidRepairs.MountCheck | FirstAidRepairs.RebuildBTree;
        }

        if (files + dirs != valences || dirThreads > dirs + 1 || fileThreads > withThreadFlag)
        {
            repairs |= FirstAidRepairs.MountCheck;
        }

        if (dirThreads < dirs + 1 || fileThreads < withThreadFlag)
        {
            repairs |= FirstAidRepairs.MountCheck | FirstAidRepairs.MissingThreads;
        }

        if (computedFree > bitmapFree)
        {
            repairs |= FirstAidRepairs.MountCheck | FirstAidRepairs.OrphanedExtents;
        }

        if (repairs != FirstAidRepairs.None)
        {
            run.Problem(FirstAidMessages.MountCheckMinor, "firstaid.mountcheck-minor", repairs);
        }
    }

    private static long Blocks(FirstAidRun run, uint fileId, byte fork) =>
        run.ForkExtents.Where(e => e.FileId == fileId && e.Fork == fork).Sum(e => (long)e.Count);

    // The clear bits among the bitmap's first drNmAlBlks.
    private static long BitmapFree(FirstAidRun run)
    {
        var bitmap = new byte[(run.BlockCount + 7) / 8];
        run.Volume.Read((long)run.BitmapStart * FirstAidRun.SectorSize, bitmap);
        long used = 0;
        for (uint block = 0; block < run.BlockCount; block++)
        {
            used += (bitmap[block / 8] >> (7 - (int)(block % 8))) & 1;
        }

        return run.BlockCount - used;
    }
}
