using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Behaviors;

// The sound preview's playhead (boards/sound.md, on A1): while a sound plays, a timer asks the model to follow the
// player's position about 30 times a second.
internal sealed class PlayheadFollower
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private SoundPlayback? playback;

    public PlayheadFollower(Window window)
    {
        timer.Tick += (_, _) => playback?.RefreshPlayhead();
        window.DataContextChanged += (_, _) =>
        {
            if (playback is not null)
            {
                playback.PropertyChanged -= OnPlaybackChanged;
            }

            playback = (window.DataContext as MainViewModel)?.SoundPlayback;
            if (playback is not null)
            {
                playback.PropertyChanged += OnPlaybackChanged;
            }
        };
        window.Closed += (_, _) => timer.Stop();
    }

    /// <summary>Whether the timer runs (while a sound plays).</summary>
    public bool IsFollowing => timer.IsEnabled;

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SoundPlayback.IsPlaying) || sender is not SoundPlayback model)
        {
            return;
        }

        if (model.IsPlaying)
        {
            timer.Start();
        }
        else
        {
            timer.Stop();
        }
    }
}
