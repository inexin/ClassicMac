using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>What First Aid's banner says (design/boards/volume-tools.md §5).</summary>
public enum FirstAidOutcome
{
    Ok,
    NeedsRepair,
    CannotRepair,
    Repaired,
}

/// <summary>A section of First Aid's log: its muted heading and Disk First Aid's lines, verbatim.</summary>
public sealed record FirstAidSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>
/// First Aid's dialog (design/boards/volume-tools.md §5, V3): it checks the volume as it opens (the progress state), then
/// says the verdict in a banner (headline and sentence) over one log in the order things happened: "Problems found",
/// then after Repair "Repaired" and "Checked again". Repair when it can repair, Extract All… when it can't, and Copy
/// report, which copies the volume, the verdict and every log line.
/// </summary>
public sealed partial class FirstAidViewModel(string volume,
    Func<IProgress<VolumeProgress>, CancellationToken, Task<FirstAidReport>> check,
    Func<IProgress<VolumeProgress>, CancellationToken, Task<FirstAidRepairResult>> repair) : VolumeOperation
{
    private int problems;

    public string Volume => volume;

    public string Title => $"First Aid · “{volume}”";

    /// <summary>The verdict, or null before the check has finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Headline), nameof(Sentence), nameof(HasOutcome), nameof(ReportText))]
    private FirstAidOutcome? outcome;

    /// <summary>Disk First Aid's last line ("The volume “X” appears to be OK.").</summary>
    [ObservableProperty]
    private string summary = "";

    /// <summary>The log's sections, in the order things happened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog), nameof(ReportText))]
    private IReadOnlyList<FirstAidSection> sections = [];

    /// <summary>Whether the repair changed the volume in the session.</summary>
    public bool Written { get; private set; }

    public bool HasOutcome => Outcome is not null;

    public bool HasLog => Sections.Count > 0;

    /// <summary>Extract All, offered when First Aid can't repair the volume.</summary>
    public Func<Task>? ExtractAll { get; set; }

    public string Headline => Outcome switch
    {
        FirstAidOutcome.Ok => "Appears to be OK",
        FirstAidOutcome.NeedsRepair => "Needs repair",
        FirstAidOutcome.CannotRepair => "Can't repair",
        FirstAidOutcome.Repaired => "Repaired · the volume appears to be OK",
        _ => "",
    };

    public string Sentence => Outcome switch
    {
        FirstAidOutcome.Ok => "First Aid found no problems.",
        FirstAidOutcome.NeedsRepair => string.Create(CultureInfo.InvariantCulture,
            $"First Aid found {problems:N0} {(problems == 1 ? "problem" : "problems")} it can repair."),
        FirstAidOutcome.CannotRepair => "Copy the files you need with Extract All, then make a new volume.",
        FirstAidOutcome.Repaired => "Changes stay in this session until you Save As.",
        _ => "",
    };

    public override string CancelNote => StepText == "Checking…" ? "Cancel stops the check; nothing has changed yet." : base.CancelNote;

    /// <summary>Copy report's text: the title, the verdict and every log line, each section under its heading.</summary>
    public string ReportText
    {
        get
        {
            var text = new StringBuilder().Append(Title).Append('\n').Append(Headline).Append('\n').Append(Sentence).Append('\n');
            foreach (var section in Sections)
            {
                text.Append('\n').Append(section.Heading).Append('\n');
                foreach (var line in section.Lines)
                {
                    text.Append(line).Append('\n');
                }
            }

            return text.ToString();
        }
    }

    private static FirstAidOutcome OutcomeOf(FirstAidReport report) => report.Verdict switch
    {
        FirstAidVerdict.AppearsOk => FirstAidOutcome.Ok,
        FirstAidVerdict.NeedsRepair => FirstAidOutcome.NeedsRepair,
        _ => FirstAidOutcome.CannotRepair,
    };

    private static List<string> Lines(FirstAidReport report) => [.. report.Problems.Select(p => p.ToString())];

    protected override void OnStateChanged()
    {
        OnPropertyChanged(nameof(CancelNote));
        CheckCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        ExtractAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutcomeChanged(FirstAidOutcome? value)
    {
        RepairCommand.NotifyCanExecuteChanged();
        ExtractAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanCheck() => IsReady && Outcome is null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        FirstAidReport? report = null;
        if (!await RunAsync("Checking…", async (progress, token) => report = await check(progress, token)) || report is null)
        {
            return;
        }

        problems = report.Problems.Count;
        Summary = report.Summary;
        Sections = problems > 0 ? [new FirstAidSection("Problems found", Lines(report))] : [];
        State = VolumeOperationState.Ready;
        Outcome = OutcomeOf(report);
    }

    private bool CanRepair() => IsReady && Outcome == FirstAidOutcome.NeedsRepair;

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task Repair()
    {
        FirstAidRepairResult? result = null;
        if (!await RunAsync("Repairing…", async (progress, token) => result = await repair(progress, token)) || result is null)
        {
            return;
        }

        Written = result.Written;
        Summary = result.Summary;
        Sections =
        [
            .. Sections,
            new FirstAidSection("Repaired", [.. result.Changes.Select(c => c.Detail)]),
            new FirstAidSection("Checked again", [.. Lines(result.After), result.After.Summary]),
        ];
        problems = result.After.Problems.Count;
        State = VolumeOperationState.Ready;
        Outcome = result.Written && result.After.Verdict == FirstAidVerdict.AppearsOk ? FirstAidOutcome.Repaired : OutcomeOf(result.After);
    }

    private bool CanExtractAll() => IsReady && Outcome == FirstAidOutcome.CannotRepair && ExtractAll is not null;

    [RelayCommand(CanExecute = nameof(CanExtractAll))]
    private Task ExtractAllAsync() => ExtractAll!();
}
