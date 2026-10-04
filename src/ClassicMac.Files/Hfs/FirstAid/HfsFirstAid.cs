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

    internal static FirstAidReport Verify(HfsVolume volume)
    {
        var run = new FirstAidRun(volume);
        try
        {
            _ = VolumeInfoCheck.Run(run);
        }
        catch (EndOfStreamException)
        {
            run.End(FirstAidVerdict.CannotRepair);
        }

        return new FirstAidReport(run);
    }
}
