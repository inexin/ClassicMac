using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A row of Defragment's figures: its label, the volume now, and after (predicted, then measured).</summary>
public sealed record DefragmentFigure(string Label, string Now, string After);

/// <summary>
/// Defragment's dialog (design/boards/volume-tools.md §3, V5): before, a sentence, the allocation map titled "Now" and
/// the Now and After figures (After predicted from the layout); running, the progress state; after, the same dialog
/// with the map titled "After", the figures measured and the time taken. A volume already in order says so.
/// </summary>
public sealed partial class DefragmentViewModel(string volume, VolumeLayout before,
    Func<IProgress<VolumeProgress>, CancellationToken, Task<VolumeLayout?>> defragment) : VolumeOperation
{
    private TimeSpan took;

    /// <summary>The clock the time taken is read from (a fixed one in tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public string Title => $"Defragment “{volume}”";

    /// <summary>The volume as it is before.</summary>
    public VolumeLayout Before { get; } = before;

    /// <summary>The volume as measured after, once it has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapLayout), nameof(Figures))]
    private VolumeLayout? after;

    /// <summary>Whether there is nothing to gain: no file in pieces, the free space in one run.</summary>
    public bool IsInOrder => !Before.CanDefragment;

    public string Lead => IsDone ? "Done. Every file is in one piece and the free space is one run."
        : IsInOrder ? "This volume is already in order."
        : "Moves every file into one piece and gathers the free space into one run at the end. Files and their contents don't change; only where they lie.";

    public string MapTitle => IsDone ? "After" : "Now";

    public VolumeLayout MapLayout => IsDone && After is { } measured ? measured : Before;

    public IReadOnlyList<DefragmentFigure> Figures
    {
        get
        {
            var then = After;
            return
            [
                new("Split files", SplitFiles(Before), then is null ? "None" : SplitFiles(then)),
                new("Free space", FreeRuns(Before), then is null ? FreeRuns(Before.FreeRuns.Count == 0 ? 0 : 1, Before.FreeBlocks) : FreeRuns(then)),
                new("Can shrink to", DetailsViewModel.ShortSize(Before.SmallestSize),
                    DetailsViewModel.ShortSize(then?.SmallestSize ?? Before.SmallestSizeDefragmented)),
            ];
        }
    }

    /// <summary>The muted line after it ran: the time taken, and that Save As writes it.</summary>
    public string DoneNote => string.Create(CultureInfo.InvariantCulture, $"Took {took.TotalSeconds:0.0} s. Save As ▸ HFS Volume Image writes the result.");

    private static string SplitFiles(VolumeLayout layout) =>
        layout.SplitFiles == 0 ? "None" : string.Create(CultureInfo.InvariantCulture, $"{layout.SplitFiles:N0} of {layout.Files:N0}");

    private static string FreeRuns(VolumeLayout layout) => FreeRuns(layout.FreeRuns.Count, layout.LargestFreeRun, largest: layout.FreeRuns.Count > 1);

    private static string FreeRuns(int runs, long blocks, bool largest = false) => string.Create(CultureInfo.InvariantCulture,
        $"{runs:N0} {(runs == 1 ? "run" : "runs")} · {(largest ? "largest " : "")}{blocks:N0} {(blocks == 1 ? "block" : "blocks")}");

    protected override void OnStateChanged()
    {
        OnPropertyChanged(nameof(Lead));
        OnPropertyChanged(nameof(MapTitle));
        OnPropertyChanged(nameof(MapLayout));
        StartCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => IsReady && !IsInOrder;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        long started = Time.GetTimestamp();
        VolumeLayout? measured = null;
        if (await RunAsync("Defragmenting…", async (progress, token) => measured = await defragment(progress, token)))
        {
            took = Time.GetElapsedTime(started);
            After = measured;
            State = VolumeOperationState.Done;
            OnPropertyChanged(nameof(DoneNote));
        }
    }
}
