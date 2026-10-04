using System;
using System.ComponentModel;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The sound preview's playhead (boards/sound.md, on A1): while a sound plays, a timer asks the model to follow the
// player's position about 30 times a second.
internal sealed partial class MainWindow
{
    private readonly DispatcherTimer playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private MainViewModel? soundModel;

    /// <summary>Whether the playhead timer runs (while a sound plays).</summary>
    internal bool IsFollowingPlayhead => playheadTimer.IsEnabled;

    private void BindSound()
    {
        playheadTimer.Tick += (_, _) => soundModel?.RefreshPlayhead();
        DataContextChanged += (_, _) =>
        {
            if (soundModel is not null)
            {
                soundModel.PropertyChanged -= OnSoundModelChanged;
            }

            soundModel = DataContext as MainViewModel;
            if (soundModel is not null)
            {
                soundModel.PropertyChanged += OnSoundModelChanged;
            }
        };
        Closed += (_, _) => playheadTimer.Stop();
    }

    private void OnSoundModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsPlaying) || sender is not MainViewModel model)
        {
            return;
        }

        if (model.IsPlaying)
        {
            playheadTimer.Start();
        }
        else
        {
            playheadTimer.Stop();
        }
    }
}
