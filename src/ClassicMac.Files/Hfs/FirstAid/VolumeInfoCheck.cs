using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// Disk First Aid's first stage, "Checking disk volume." (IVChk; hfs.md §5.6): the alternate MDB at sector S − 2 decides
// what the disk is, and its geometry is checked; every geometry problem ends the check [Code: Disk First Aid 8.5.5,
// CODE 1 $19A76, $19D4E–$19F34] [Verified: a volume whose alternate MDB is zeroed is "not an HFS disk" though Mac OS
// mounts it].
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
        var alternate = new BigEndianReader(run.Alternate);
        var signature = alternate.ReadUInt16At(0);
        if (signature == HfsPlus || signature == Hfs && alternate.ReadUInt16At(0x7C) == HfsPlus)
        {
            return run.End(FirstAidVerdict.NotChecked);                       // HFS Plus, bare or in its HFS wrapper
        }

        var primary = new BigEndianReader(run.Primary);
        bool primaryHfs = primary.ReadUInt16At(0) == Hfs && primary.ReadUInt16At(0x7C) != HfsPlus;
        if (signature != Hfs)
        {
            if (primaryHfs)
            {
                run.Extra("The alternate MDB is missing; the primary MDB is an HFS one", "firstaid.alternate-mdb-missing", FirstAidRepairs.AlternateMdb);
            }

            return run.End(FirstAidVerdict.NotHfs);
        }

        // ClassicMac's: the alternate's layout and B-tree files against the primary's, which Mac OS mounts by.
        if (primaryHfs && Stale(run.Primary, run.Alternate))
        {
            run.Extra("The alternate MDB differs from the primary in the volume's layout or B-tree files", "firstaid.alternate-mdb-stale", FirstAidRepairs.AlternateMdb);
        }

        run.Stage(FirstAidMessages.CheckingStandardVolume);
        return Geometry(run, alternate);
    }

    // drVBMSt, drNmAlBlks, drAlBlkSiz, drAlBlSt and the B-tree files' sizes and extents.
    private static bool Stale(byte[] primary, byte[] alternate)
    {
        foreach (var (at, length) in new[] { (0x0E, 2), (0x12, 6), (0x1C, 2), (0x82, 0x20) })
        {
            if (!System.MemoryExtensions.SequenceEqual(System.MemoryExtensions.AsSpan(primary, at, length), System.MemoryExtensions.AsSpan(alternate, at, length)))
            {
                return true;
            }
        }

        return false;
    }

    // The alternate MDB's geometry, with V = S − 2 sectors, A = drAlBlkSiz and N = drNmAlBlks.
    private static bool Geometry(FirstAidRun run, BigEndianReader mdb)
    {
        long v = run.Sectors - 2;
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
            return run.Fatal(7);
        }

        // #8: the blocks and the bitmap fit: N ≤ (V − 3 − B) ÷ (A ÷ 512), B the bitmap's sectors.
        long sectorsPerBlock = a / 512;
        long bitmapSectors = (v / sectorsPerBlock + 4095) >> 12;
        if (n > (v - 3 - bitmapSectors) / sectorsPerBlock)
        {
            return run.Fatal(8);
        }

        // #9: the bitmap starts after the MDB; #10: the allocation blocks start after the bitmap.
        if (bitmapStart <= 2)
        {
            return run.Fatal(9);
        }

        if (bitmapStart + bitmapSectors > allocationStart)
        {
            return run.Fatal(10);
        }

        run.BlockSize = a;
        run.BlockCount = n;
        run.BitmapStart = bitmapStart;
        run.AllocationStart = allocationStart;
        return true;
    }
}
