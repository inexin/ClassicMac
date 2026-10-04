using System;
using System.IO;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// Disk First Aid for HFS volumes (hfs.md §5.6): verify a volume in Disk First Aid 8.5.5's stages, naming each problem as
/// it does, and say whether the volume appears to be OK, needs to be repaired, or cannot be repaired.
/// </summary>
public static class HfsFirstAid
{
    /// <summary>Verifies the HFS volume in <paramref name="image"/> (a volume that fills it, as a disk's partition does).</summary>
    public static FirstAidReport Verify(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Verify(new HfsVolume(image));
    }

    internal static FirstAidReport Verify(HfsVolume volume) => Check(volume).Report;

    /// <summary>
    /// Repairs what a verify of the HFS volume in <paramref name="image"/> finds repairable, in Disk First Aid's order, then
    /// verifies it again; the volume is not written when nothing could be repaired.
    /// </summary>
    public static FirstAidRepairResult Repair(ForkData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return FirstAidRepairer.Repair(new HfsVolume(image));
    }

    internal static (FirstAidRun Run, FirstAidReport Report) Check(HfsVolume volume)
    {
        var run = new FirstAidRun(volume);
        try
        {
            _ = VolumeInfoCheck.Run(run)
                && BTreeSetupCheck.Run(run)
                && LockedNameCheck.Run(run)
                && Stage(run, FirstAidMessages.CheckingExtentsBTree) && BTreeCheck.Run(run, run.Extents!)
                && Stage(run, FirstAidMessages.CheckingExtentsFile)
                && Stage(run, FirstAidMessages.CheckingCatalogBTree) && BTreeCheck.Run(run, run.Catalog!)
                && CatalogScan.Run(run)
                && HierarchyCheck.Run(run)
                && BitmapCheck.Run(run)
                && MdbCompare.Run(run)
                && ClassicMacChecks.Run(run);
        }
        catch (EndOfStreamException)
        {
            run.End(FirstAidVerdict.CannotRepair);
        }

        return (run, new FirstAidReport(run));
    }

    private static bool Stage(FirstAidRun run, string line)
    {
        run.Stage(line);
        return true;
    }
}
