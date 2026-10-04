using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>Where a volume operation's dialog is: before it runs, running, or showing what it did.</summary>
public enum VolumeOperationState
{
    Ready,
    Running,
    Done,
}

/// <summary>
/// A long volume operation in its dialog (design/boards/volume-tools.md §2, V2): the step line, a determinate bar and a
/// detail line while it runs off the UI thread, and Cancel, which drops the work: it runs on a copy of the volume, so
/// the session's volume is left as it was.
/// </summary>
public abstract partial class VolumeOperation : ObservableObject
{
    private CancellationTokenSource? cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsRunning), nameof(IsDone))]
    private VolumeOperationState state;

    /// <summary>How far it has come, from 0 to 1.</summary>
    [ObservableProperty]
    private double progress;

    /// <summary>The bold line while it runs ("Defragmenting…").</summary>
    [ObservableProperty]
    private string stepText = "";

    /// <summary>The muted line under the bar ("File 12 of 21", "Step 3 of 10 · Checking catalog file.").</summary>
    [ObservableProperty]
    private string detailText = "";

    /// <summary>Why the last run failed (the writer refused the volume), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? error;

    public bool HasError => Error is not null;

    public bool IsReady => State == VolumeOperationState.Ready;

    public bool IsRunning => State == VolumeOperationState.Running;

    public bool IsDone => State == VolumeOperationState.Done;

    /// <summary>What Cancel does while it runs.</summary>
    public virtual string CancelNote => "Cancel stops it; nothing has changed yet.";

    /// <summary>A report as the bar's share and the detail line: a file count as it is, a step with its number.</summary>
    public static (double Share, string Detail) Describe(VolumeProgress report)
    {
        double share = report.Steps <= 0 ? 0 : Math.Clamp((double)report.Step / report.Steps, 0, 1);
        return report.Text.StartsWith("File ", StringComparison.Ordinal)
            ? (share, report.Text)
            : (share, $"Step {report.Step} of {report.Steps} · {report.Text}");
    }

    /// <summary>
    /// Runs the work with a progress that updates the bar and a token Cancel cancels; true when it finished, false when
    /// it was cancelled (the state back at Ready).
    /// </summary>
    protected async Task<bool> RunAsync(string step, Func<IProgress<VolumeProgress>, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var source = new CancellationTokenSource();
        cancellation = source;
        StepText = step;
        DetailText = "";
        Progress = 0;
        Error = null;
        State = VolumeOperationState.Running;
        var reports = new Progress<VolumeProgress>(report => (Progress, DetailText) = Describe(report));
        try
        {
            await work(reports, source.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            State = VolumeOperationState.Ready;
            return false;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
        {
            Error = e.Message;
            State = VolumeOperationState.Ready;
            return false;
        }
        finally
        {
            cancellation = null;
        }
    }

    /// <summary>Called when <see cref="State"/> changes, for what a dialog derives from it.</summary>
    protected virtual void OnStateChanged()
    {
    }

    partial void OnStateChanged(VolumeOperationState value) => OnStateChanged();

    [RelayCommand]
    private void Cancel() => cancellation?.Cancel();
}
