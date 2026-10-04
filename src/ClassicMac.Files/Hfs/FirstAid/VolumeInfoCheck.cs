using System;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// The first stage, "Checking disk volume." (IVChk; hfs.md §5.6): an MDB decides what the disk is, and its geometry is
// checked; every geometry problem ends the check [Code: Disk First Aid 8.5.5, CODE 1 $19A76, $19D4E–$19F34]. Disk First
// Aid goes by the alternate MDB at sector S − 2 alone [Verified: a volume whose alternate MDB is zeroed is "not an HFS
// disk" though Mac OS mounts it]; here the primary, which Mac OS mounts by, is used instead when the alternate is
// missing or differs from it and the primary is sound, and repair writes the alternate from it.
internal static class VolumeInfoCheck
{
    private const ushort Hfs = 0x4244;          // 'BD'
    private const ushort HfsPlus = 0x482B;      // 'H+'

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingDiskVolume);
        if (run.Sectors < 3)
        {
            return run.End(FirstAidVerdict.NotHfs);
        }

        run.Primary = run.ReadSector(2);
        run.Alternate = run.ReadSector(run.Sectors - 2);
        var primary = new BigEndianReader(run.Primary);
        var alternate = new BigEndianReader(run.Alternate);
        bool primaryHfs = primary.ReadUInt16At(0) == Hfs, alternateHfs = alternate.ReadUInt16At(0) == Hfs;
        if (IsHfsPlus(primary) || !alternateHfs && IsHfsPlus(alternate))
        {
            return run.End(FirstAidVerdict.NotChecked);                       // HFS Plus, bare or in its HFS wrapper
        }

        if (primaryHfs && !alternateHfs)
        {
            run.Problem("The alternate MDB is missing", "firstaid.alternate-mdb-missing", FirstAidRepairs.AlternateMdb);
            run.Alternate = (byte[])run.Primary.Clone();
        }
        else if (primaryHfs && Differs(run.Primary, run.Alternate) && Sound(run.Sectors, primary))
        {
            run.Problem("The alternate MDB differs from the primary in the volume's layout or B-tree files", "firstaid.alternate-mdb-stale",
                FirstAidRepairs.AlternateMdb);
            run.Alternate = (byte[])run.Primary.Clone();
        }
        else if (!alternateHfs)
        {
            return run.End(FirstAidVerdict.NotHfs);
        }

        run.Stage(FirstAidMessages.CheckingStandardVolume);
        var mdb = new BigEndianReader(run.Alternate);
        int problem = GeometryProblem(run.Sectors, mdb);
        if (problem != 0)
        {
            return run.Fatal(problem);
        }

        run.BlockSize = mdb.ReadUInt32At(0x14);
        run.BlockCount = mdb.ReadUInt16At(0x12);
        run.BitmapStart = mdb.ReadUInt16At(0x0E);
        run.AllocationStart = mdb.ReadInt16At(0x1C);
        return true;
    }

    private static bool IsHfsPlus(BigEndianReader mdb) =>
        mdb.ReadUInt16At(0) == HfsPlus || mdb.ReadUInt16At(0) == Hfs && mdb.ReadUInt16At(0x7C) == HfsPlus;

    // drVBMSt, drNmAlBlks, drAlBlkSiz, drAlBlSt and the B-tree files' sizes and extents.
    private static bool Differs(byte[] primary, byte[] alternate)
    {
        foreach (var (at, length) in new[] { (0x0E, 2), (0x12, 6), (0x1C, 2), (0x82, 0x20) })
        {
            if (!primary.AsSpan(at, length).SequenceEqual(alternate.AsSpan(at, length)))
            {
                return true;
            }
        }

        return false;
    }

    // An MDB to check the volume by: its geometry passes, the extents file's extents are its size, and the catalog's
    // are at most its size (the rest in overflow extents).
    private static bool Sound(long sectors, BigEndianReader mdb)
    {
        if (GeometryProblem(sectors, mdb) != 0)
        {
            return false;
        }

        long blockSize = mdb.ReadUInt32At(0x14);
        return Blocks(mdb, 0x86) * blockSize == mdb.ReadUInt32At(0x82) && Blocks(mdb, 0x96) * blockSize <= mdb.ReadUInt32At(0x92);
    }

    private static long Blocks(BigEndianReader mdb, int record) =>
        mdb.ReadUInt16At(record + 2) + mdb.ReadUInt16At(record + 6) + mdb.ReadUInt16At(record + 10);

    // The geometry's problem number, or 0, with V = S − 2 sectors, A = drAlBlkSiz and N = drNmAlBlks.
    private static int GeometryProblem(long sectors, BigEndianReader mdb)
    {
        long v = sectors - 2;
        uint a = mdb.ReadUInt32At(0x14);
        uint n = mdb.ReadUInt16At(0x12);
        int bitmapStart = mdb.ReadUInt16At(0x0E);
        int allocationStart = mdb.ReadInt16At(0x1C);

        // #7: the block size is a multiple of 512, at most $7FFFFE00, and large enough that the volume has at most
        // 65,535 blocks: at least 512·k for the smallest k with V ÷ k ≤ 65,535.
        long k = 1;
        while (v / k > ushort.MaxValue)
        {
            k++;
        }

        if (a < 512 * k || a > 0x7FFFFE00 || a % 512 != 0)
        {
            return 7;
        }

        // #8: the blocks and the bitmap fit: N ≤ (V − 3 − B) ÷ (A ÷ 512), B the bitmap's sectors.
        long sectorsPerBlock = a / 512;
        long bitmapSectors = (v / sectorsPerBlock + 4095) >> 12;
        if (n > (v - 3 - bitmapSectors) / sectorsPerBlock)
        {
            return 8;
        }

        // #9: the bitmap starts after the MDB; #10: the allocation blocks start after the bitmap.
        if (bitmapStart <= 2)
        {
            return 9;
        }

        return bitmapStart + bitmapSectors > allocationStart ? 10 : 0;
    }
}
