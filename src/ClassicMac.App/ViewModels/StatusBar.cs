using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// The status bar's summary of an opened file (design/boards/main-window.md, S4): its name, format and file count,
    /// with its error and warning counts shown apart in their colours.
    /// </summary>
    public sealed record StatusSummary(string Name, string Format, int Files, int Errors, int Warnings)
    {
        /// <summary>"Mac OS 9.hfv · HFS+ · 4,982 files".</summary>
        public string Text => string.Create(CultureInfo.InvariantCulture, $"{Name} · {Format} · {Files:N0} {(Files == 1 ? "file" : "files")}");

        /// <summary>"2 errors", or null when there are none.</summary>
        public string? ErrorText => Count(Errors, "error");

        /// <summary>"3 warnings", or null when there are none.</summary>
        public string? WarningText => Count(Warnings, "warning");

        private static string? Count(int count, string what) =>
            count == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{count:N0} {what}{(count == 1 ? "" : "s")}");
    }

    // The status bar: the summary of the selected input on the left; on the right, while work runs, what it does and how
    // far it is, else the status text.
    public sealed partial class MainViewModel
    {
        /// <summary>What runs now ("Extracting Disk…"), or null when nothing does.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWorking), nameof(ProgressCount), nameof(IsProgressIndeterminate))]
        private string? progressText;

        /// <summary>How many of <see cref="ProgressMaximum"/> are done.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCount))]
        private int progressValue;

        /// <summary>How many there are to do; 0 when the work has no count.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressCount), nameof(IsProgressIndeterminate))]
        private int progressMaximum;

        /// <summary>Whether work runs (the progress shows instead of the status text).</summary>
        public bool IsWorking => ProgressText is not null;

        /// <summary>Work without a count: the bar runs without a fill level.</summary>
        public bool IsProgressIndeterminate => IsWorking && ProgressMaximum == 0;

        /// <summary>"1,240 of 3,906" while counted work runs; else null.</summary>
        public string? ProgressCount => IsWorking && ProgressMaximum > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ProgressValue:N0} of {ProgressMaximum:N0}")
            : null;

        /// <summary>The summary of the selected node's input (the first input when nothing is selected); null with none open.</summary>
        public StatusSummary? Summary
        {
            get
            {
                var input = Selected?.Input ?? Roots.FirstOrDefault();
                if (input is null)
                {
                    return null;
                }

                var (errors, warnings) = DiagnosticsPanel.CountsFor(d => d.Node?.Input == input || d.Node is null && d.Source == input.BaseTitle);
                var format = input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "resource fork";
                return new StatusSummary(input.BaseTitle, format, input.Root.Leaves().Count(), errors, warnings);
            }
        }

        // The summary changes with the selection, the open files and the diagnostics.
        private void WatchSummary()
        {
            Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
            DiagnosticsPanel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DiagnosticsPanel.ErrorCount) or nameof(DiagnosticsPanel.WarningCount))
                {
                    OnPropertyChanged(nameof(Summary));
                }
            };
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selected))
                {
                    OnPropertyChanged(nameof(Summary));
                }
            };
        }

        /// <summary>Shows work starting: <paramref name="text"/> and a bar of <paramref name="maximum"/> steps (0: no count).</summary>
        internal WorkProgress BeginProgress(string text, int maximum)
        {
            ProgressValue = 0;
            ProgressMaximum = maximum;
            ProgressText = text;
            return new WorkProgress(this);
        }

        /// <summary>
        /// The progress of one piece of work. <see cref="Progress{T}"/> posts the reports (to the UI thread), so one can
        /// arrive after the work has finished: the end is set under the same lock, and reports after it are dropped.
        /// </summary>
        internal sealed class WorkProgress : IProgress<int>
        {
            private readonly object gate = new();
            private readonly MainViewModel model;
            private readonly Progress<int> inner;
            private bool finished;

            public WorkProgress(MainViewModel model)
            {
                this.model = model;
                inner = new Progress<int>(Apply);
            }

            public void Report(int value) => ((IProgress<int>)inner).Report(value);

            /// <summary>Shows <paramref name="done"/> steps done, unless the work has finished.</summary>
            public void Apply(int done)
            {
                lock (gate)
                {
                    if (!finished)
                    {
                        model.ProgressValue = done;
                    }
                }
            }

            /// <summary>Ends the work: the progress goes, and <paramref name="status"/> (when not null) becomes the status text.</summary>
            public void Finish(string? status)
            {
                lock (gate)
                {
                    if (finished)
                    {
                        return;
                    }

                    finished = true;
                    model.ProgressText = null;
                    if (status is not null)
                    {
                        model.Status = status;
                    }
                }
            }
        }
    }
}
