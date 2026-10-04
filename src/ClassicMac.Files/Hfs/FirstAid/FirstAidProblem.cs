namespace ClassicMac.Files.Hfs;

/// <summary>Who reports a First Aid problem.</summary>
public enum FirstAidOrigin
{
    /// <summary>One of Disk First Aid 8.5.5's own checks, made as it makes it.</summary>
    DiskFirstAid,

    /// <summary>One of ClassicMac's checks that Disk First Aid lacks; it never changes Disk First Aid's verdict.</summary>
    ClassicMac,
}

/// <summary>What First Aid makes of a volume.</summary>
public enum FirstAidVerdict
{
    /// <summary>"The volume … appears to be OK."</summary>
    AppearsOk,

    /// <summary>"The volume … needs to be repaired.": every problem found can be repaired.</summary>
    NeedsRepair,

    /// <summary>"Test done. Problems were found, but Disk First Aid cannot repair them.": a check stopped at a problem.</summary>
    CannotRepair,

    /// <summary>"This is not an HFS disk.": the alternate MDB is missing or not an HFS one.</summary>
    NotHfs,

    /// <summary>An HFS Plus volume, which ClassicMac's First Aid does not check yet.</summary>
    NotChecked,
}

/// <summary>
/// A problem First Aid found, as Disk First Aid prints it: "Problem:  <see cref="Message"/>, <see cref="Arg2"/>,
/// <see cref="Arg3"/>" (hfs.md §5.6).
/// </summary>
/// <param name="Number">Disk First Aid's problem number (1–71), or 0 for the MountCheck lines and ClassicMac's own problems.</param>
/// <param name="Message">The problem's text.</param>
/// <param name="Arg2">Disk First Aid's first number: a catalog record's CNID, or the detail of an MDB or bitmap problem.</param>
/// <param name="Arg3">Disk First Aid's second number: for a catalog record, its B-tree node.</param>
/// <param name="Stage">The stage line it was found under.</param>
/// <param name="Repairable">Whether repair fixes it; a problem that is not ends the check.</param>
/// <param name="Code">The diagnostic code (<c>firstaid.invalid-peof</c>).</param>
/// <param name="Origin">Whether it is Disk First Aid's check or ClassicMac's.</param>
public sealed record FirstAidProblem(int Number, string Message, long Arg2, long Arg3, string Stage, bool Repairable, string Code,
    FirstAidOrigin Origin = FirstAidOrigin.DiskFirstAid)
{
    /// <summary>The line Disk First Aid prints.</summary>
    public override string ToString() => $"Problem:  {Message}, {Arg2}, {Arg3}";
}
