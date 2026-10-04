namespace ClassicMac.Files.Hfs;

// "Checking volume bit map." (hfs.md §5.6): a bitmap built from every extent First Aid walked, blocks claimed twice
// reported once, then compared with the volume's sector by sector; the whole last sector counts, so bits after the last
// block and bytes after the bitmap must be clear [Code: Disk First Aid 8.5.5, CODE 1 $20ADE, $2595C] [Verified: D1, D3,
// D4].
internal static class BitmapCheck
{
    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingBitmap);
        int sectors = (int)((run.BlockCount + 4095) / 4096);
        // HFS compares whole bitmap sectors; HFS Plus the allocation file's bytes that cover the blocks.
        var computed = new byte[run.Plus ? (run.BlockCount + 7) / 8 : sectors * FirstAidRun.SectorSize];
        bool overlapped = false;
        foreach (var (fileId, _, start, count) in run.ForkExtents)
        {
            for (long block = start; block < (long)start + count && block < run.BlockCount; block++)
            {
                int at = (int)(block / 8), bit = 0x80 >> (int)(block % 8);
                if ((computed[at] & bit) != 0 && !overlapped)
                {
                    overlapped = true;
                    run.Flag(12, FirstAidRepairs.OverlappingExtents, fileId);
                }

                computed[at] |= (byte)bit;
            }
        }

        var onDisk = new byte[computed.Length];
        if (run.AllocationFileExtents is { } allocation)
        {
            onDisk = run.ReadExtents(allocation, computed.Length);
            if (!System.MemoryExtensions.SequenceEqual(computed, onDisk))
            {
                run.Flag(60, FirstAidRepairs.Bitmap);
            }

            run.ComputedBitmap = computed;
            return true;
        }

        run.Volume.Read((long)run.BitmapStart * FirstAidRun.SectorSize, onDisk);
        for (var sector = 0; sector < sectors; sector++)
        {
            var span = System.MemoryExtensions.AsSpan(computed, sector * FirstAidRun.SectorSize, FirstAidRun.SectorSize);
            if (!System.MemoryExtensions.SequenceEqual(span, System.MemoryExtensions.AsSpan(onDisk, sector * FirstAidRun.SectorSize, FirstAidRun.SectorSize)))
            {
                run.Flag(60, FirstAidRepairs.Bitmap);
                break;
            }
        }

        run.ComputedBitmap = computed;
        return true;
    }
}
