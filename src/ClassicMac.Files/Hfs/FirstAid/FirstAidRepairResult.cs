using System.Collections.Generic;
using ClassicMac.Files.Editing;

namespace ClassicMac.Files.Hfs;

/// <summary>What a First Aid repair did (hfs.md §5.6): the verify before, the changes, the verify after, and the volume.</summary>
public sealed class FirstAidRepairResult
{
    private byte[]? bytes;

    internal FirstAidRepairResult(FirstAidReport before, FirstAidReport after, IReadOnlyList<PlannedChange> changes, HfsVolume? repaired)
    {
        Before = before;
        After = after;
        Changes = changes;
        Repaired = repaired;
    }

    /// <summary>The repaired volume as sectors over the input's; null when nothing was written.</summary>
    internal HfsVolume? Repaired { get; }

    /// <summary>The verify of the volume as it was.</summary>
    public FirstAidReport Before { get; }

    /// <summary>The verify of the repaired volume (the same as <see cref="Before"/> when nothing was repaired).</summary>
    public FirstAidReport After { get; }

    /// <summary>Each repair made, in the order made.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; }

    /// <summary>Whether the repair changed the volume (false when it appeared to be OK, or cannot be repaired).</summary>
    public bool Written => Repaired is not null;

    /// <summary>The repaired volume's bytes; null when nothing was written (the volume appeared to be OK, or cannot be repaired).</summary>
    public byte[]? Volume => bytes ??= Repaired?.ToArray();

    /// <summary>Disk First Aid's last line: that the volume was repaired, or what the verify after the repair says.</summary>
    public string Summary => Repaired is not null && After.Verdict == FirstAidVerdict.AppearsOk ? FirstAidMessages.Repaired(After.VolumeName) : After.Summary;
}
