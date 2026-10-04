using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

/// <summary>What a First Aid verify found (hfs.md §5.6): the stages it ran, the problems, the repairs needed, the verdict.</summary>
public sealed class FirstAidReport
{
    internal FirstAidReport(FirstAidRun run)
    {
        Stages = [.. run.Stages];
        Problems = [.. run.Problems];
        Repairs = run.Repairs;
        VolumeName = run.VolumeName;
        HfsPlus = run.Plus;
        Verdict = run.Ended ?? (run.Unrepairable ? FirstAidVerdict.CannotRepair
            : run.Repairs != FirstAidRepairs.None ? FirstAidVerdict.NeedsRepair : FirstAidVerdict.AppearsOk);
    }

    /// <summary>The stage lines, in the order they ran.</summary>
    public IReadOnlyList<string> Stages { get; }

    /// <summary>The problems, in the order they were found.</summary>
    public IReadOnlyList<FirstAidProblem> Problems { get; }

    /// <summary>The repairs the problems need.</summary>
    public FirstAidRepairs Repairs { get; }

    /// <summary>Whether the volume is HFS Plus (bare or in its HFS wrapper).</summary>
    public bool HfsPlus { get; }

    /// <summary>The volume's name (from its MDB).</summary>
    public string VolumeName { get; }

    /// <summary>What Disk First Aid would say of the volume.</summary>
    public FirstAidVerdict Verdict { get; }

    /// <summary>Disk First Aid's last line for the verdict.</summary>
    public string Summary => Verdict switch
    {
        FirstAidVerdict.AppearsOk => FirstAidMessages.AppearsOk(VolumeName),
        FirstAidVerdict.NeedsRepair => FirstAidMessages.NeedsRepair(VolumeName),
        FirstAidVerdict.CannotRepair => FirstAidMessages.CannotRepair,
        FirstAidVerdict.NotHfs => FirstAidMessages.NotHfs,
        _ => FirstAidMessages.NotChecked,
    };

    /// <summary>
    /// The problems as diagnostics: an Error for one repair cannot fix, a Warning for one it can; a disk that is not HFS is
    /// an Error.
    /// </summary>
    public IReadOnlyList<Diagnostic> ToDiagnostics()
    {
        var diagnostics = Problems.Select(p => new Diagnostic(
            p.Repairable ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
            p.Code, $"First Aid: {p.Message}, {p.Arg2}, {p.Arg3}")).ToList();
        if (Verdict == FirstAidVerdict.NotHfs)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "firstaid.not-hfs", $"First Aid: {Summary}"));
        }

        return diagnostics;
    }
}
