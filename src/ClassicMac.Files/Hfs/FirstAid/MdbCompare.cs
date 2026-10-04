using System;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// "Checking volume info." (hfs.md §5.6): the primary MDB against the one First Aid computes, which takes the geometry,
// creation date, cache sizes and B-tree extents from the alternate MDB, the counts and next CNID from the catalog, and
// keeps the primary's other fields when they are sound. The first difference is #58 with the detail of its group
// [Code: Disk First Aid 8.5.5, CODE 1 $1CBF6, $20D18] [Verified: A2, A3, B1–B3, B6–B8].
internal static class MdbCompare
{
    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingVolumeInfo);
        var computed = Computed(run);
        run.ComputedMdb = computed;
        var primary = run.Primary;
        int nameEnd = 0x25 + Math.Min((int)primary[0x24], 27);
        int detail =
            Differs(primary, computed, 0x00, 0x0C) || Differs(primary, computed, 0x0E, 0x10) || Differs(primary, computed, 0x12, 0x14)
            || Differs(primary, computed, 0x18, 0x22) || Differs(primary, computed, 0x24, nameEnd) ? 1
            : Differs(primary, computed, 0x40, 0x82) ? 2
            : Differs(primary, computed, 0x82, 0x86) ? 3
            : Differs(primary, computed, 0x86, 0x92) ? 4
            : Differs(primary, computed, 0x92, 0x96) ? 5
            : Differs(primary, computed, 0x96, 0xA2) ? 6
            : 0;
        if (detail != 0)
        {
            run.Flag(58, FirstAidRepairs.Mdb, detail);
        }

        return true;
    }

    // The MDB First Aid would write: the primary's, with what it computes or takes from the alternate.
    private static byte[] Computed(FirstAidRun run)
    {
        var mdb = (byte[])run.Primary.Clone();
        var writer = new BigEndianWriter(mdb);
        var primary = new BigEndianReader(run.Primary);
        var alternate = new BigEndianReader(run.Alternate);
        uint a = run.BlockSize, n = run.BlockCount;
        long maxClump = (long)(n / 4) * a;

        // From the alternate MDB: signature, creation date, bitmap start, block count, allocation start, cache sizes and
        // the B-tree files' sizes and extents.
        foreach (var (at, length) in new[] { (0x00, 2), (0x02, 4), (0x0E, 2), (0x12, 2), (0x1C, 2), (0x7C, 6), (0x82, 0x20) })
        {
            run.Alternate.AsSpan(at, length).CopyTo(mdb.AsSpan(at));
        }

        // drAtrb: kept unless one of its bits 0–6 is set.
        ushort attributes = primary.ReadUInt16At(0x0A);
        writer.WriteUInt16At(0x0A, (attributes & 0x7F) == 0 ? attributes : (ushort)0x0100);

        // drClpSiz: the primary's if sound, else the alternate's, else 4 blocks; over 1 MB, one block.
        uint clump = Clump(primary.ReadUInt32At(0x18), a, maxClump) ?? Clump(alternate.ReadUInt32At(0x18), a, maxClump) ?? 4 * a;
        writer.WriteUInt32At(0x18, clump > 1024 * 1024 ? a : clump);

        // drXTClpSiz, drCTClpSiz: the primary's if a multiple of the block size within a quarter of the volume, else the
        // alternate's, else its file's first extent.
        writer.WriteUInt32At(0x4A, TreeClump(primary.ReadUInt32At(0x4A), alternate.ReadUInt32At(0x4A), alternate.ReadUInt16At(0x88), a, maxClump));
        writer.WriteUInt32At(0x4E, TreeClump(primary.ReadUInt32At(0x4E), alternate.ReadUInt32At(0x4E), alternate.ReadUInt16At(0x98), a, maxClump));

        // drNxtCNID: the catalog's highest CNID + 1, or the primary's when it is above that by at most 4,096.
        uint next = run.NextCnid, declared = primary.ReadUInt32At(0x1E);
        writer.WriteUInt32At(0x1E, declared > next && declared <= next + 4096 ? declared : next);

        // drVN: the root folder's name.
        if (!run.RootName.AsSpan().SequenceEqual(run.Primary.AsSpan(0x25, Math.Min((int)run.Primary[0x24], 27))))
        {
            mdb.AsSpan(0x24, 28).Clear();
            mdb[0x24] = (byte)Math.Min(run.RootName.Length, 27);
            run.RootName.AsSpan(0, mdb[0x24]).CopyTo(mdb.AsSpan(0x25));
        }

        // The counts the catalog scan made. drNmFls and drFreeBks are not compared; repair writes them as well, one of
        // ClassicMac's extras (hfs.md §5.6).
        writer.WriteUInt16At(0x0C, (ushort)run.RootFileCount);
        if (run.ComputedBitmap.Length > 0)
        {
            long used = 0;
            for (uint block = 0; block < run.BlockCount; block++)
            {
                used += (run.ComputedBitmap[block / 8] >> (7 - (int)(block % 8))) & 1;
            }

            writer.WriteUInt16At(0x22, (ushort)(run.BlockCount - used));
        }

        writer.WriteUInt16At(0x52, (ushort)run.RootDirCount);
        writer.WriteUInt32At(0x54, (uint)run.FileCount);
        writer.WriteUInt32At(0x58, (uint)run.DirCount);
        return mdb;
    }

    private static uint? Clump(uint value, uint a, long maxClump) => value > 0 && value <= maxClump && value % a == 0 ? value : null;

    private static uint TreeClump(uint primary, uint alternate, uint firstExtentBlocks, uint a, long maxClump) =>
        primary % a == 0 && primary <= maxClump ? primary
        : alternate % a == 0 && alternate <= maxClump ? alternate
        : firstExtentBlocks * a;

    private static bool Differs(byte[] left, byte[] right, int from, int to) => !left.AsSpan(from, to - from).SequenceEqual(right.AsSpan(from, to - from));
}
