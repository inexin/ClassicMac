using System;
using System.IO;
using System.Threading;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// Disk First Aid for HFS volumes (hfs.md §5.6): verify a volume in Disk First Aid 8.5.5's stages, naming each problem as
/// it does, and say whether the volume appears to be OK, needs to be repaired, or cannot be repaired.
/// </summary>
public static class HfsFirstAid
{
    /// <summary>Verifies the HFS volume in <paramref name="image"/> (a volume that fills it, as a disk's partition does).</summary>
    public static FirstAidReport Verify(ForkData image) => Verify(image, null, CancellationToken.None);

    /// <summary>
    /// Verifies the volume, each stage reported to <paramref name="progress"/> as a step of all (hfs.md §5.8); cancelling
    /// throws <see cref="OperationCanceledException"/> as the next stage begins.
    /// </summary>
    public static FirstAidReport Verify(ForkData image, IProgress<VolumeProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Check(new HfsVolume(image), progress, cancellationToken).Report;
    }

    internal static FirstAidReport Verify(HfsVolume volume) => Check(volume).Report;

    /// <summary>
    /// Repairs what a verify of the HFS volume in <paramref name="image"/> finds repairable, in Disk First Aid's order, then
    /// verifies it again; the volume is not written when nothing could be repaired.
    /// </summary>
    public static FirstAidRepairResult Repair(ForkData image) => Repair(image, null, CancellationToken.None);

    /// <summary>Repairs as the other overload does, each check's stages reported to <paramref name="progress"/>; cancelling throws.</summary>
    public static FirstAidRepairResult Repair(ForkData image, IProgress<VolumeProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return FirstAidRepairer.Repair(new HfsVolume(image), progress, cancellationToken);
    }

    // The stages a check has: HFS's ten; HFS Plus's eight, with one for an attributes tree and one for a journal.
    private const int HfsSteps = 10, PlusSteps = 8;

    internal static (FirstAidRun Run, FirstAidReport Report) Check(HfsVolume volume, IProgress<VolumeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // An HFS Plus volume's journal is replayed on a copy first, and the volume checked as it leaves it.
        var (plus, hfsx, offset, length) = PlusVolumeCheck.Find(volume);
        PlusJournal.Result? journal = null;
        if (plus)
        {
            var copy = volume.Fork();
            journal = PlusJournal.Replay(copy, offset);
            volume = journal is { Damage: null } ? copy : volume;
        }

        var run = new FirstAidRun(volume) { Progress = progress, Token = cancellationToken, Steps = plus ? PlusSteps + (journal is null ? 0 : 1) : HfsSteps };
        try
        {
            if (journal is { } replay)
            {
                run.Stage($"Replaying the journal ({replay.Transactions} transaction{(replay.Transactions == 1 ? "" : "s")}).");
                if (replay.Damage is { } damage)
                {
                    run.Problem($"The journal cannot be replayed: {damage}", "firstaid.journal-damaged", FirstAidRepairs.None);
                }
                else
                {
                    run.Problem("The journal holds transactions not yet written to the volume", "firstaid.journal-pending", FirstAidRepairs.Journal);
                }
            }

            if (plus)
            {
                _ = PlusVolumeCheck.Run(run, offset, length, hfsx)
                    && Counted(run)
                    && Stage(run, FirstAidMessages.CheckingExtentsBTree) && BTreeCheck.Run(run, run.Extents!)
                    && Stage(run, FirstAidMessages.CheckingCatalogBTree) && BTreeCheck.Run(run, run.Catalog!)
                    && (run.AttributesTree is not { } attributes || Stage(run, FirstAidMessages.CheckingAttributesBTree) && BTreeCheck.Run(run, attributes))
                    && PlusCatalogScan.Run(run)
                    && BitmapCheck.Run(run)
                    && PlusHeaderCompare.Run(run);
                return (run, new FirstAidReport(run));
            }

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

    // Once the volume header is read: one more step when there is an attributes tree to check.
    private static bool Counted(FirstAidRun run)
    {
        run.Steps += run.AttributesTree is null ? 0 : 1;
        return true;
    }

    private static bool Stage(FirstAidRun run, string line)
    {
        run.Stage(line);
        return true;
    }
}
