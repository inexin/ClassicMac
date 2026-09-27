using System;
using ClassicMac.Resources.Decoders.Sound;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>Plays decoded sounds on the computer's output device; one at a time.</summary>
    public interface IAudioPlayer
    {
        /// <summary>Why sound cannot play (no device, no native library), known once a Play failed; null otherwise.</summary>
        string? Unavailable { get; }

        /// <summary>
        /// Plays <paramref name="sound"/>, stopping any other; <paramref name="ended"/> runs on the UI thread when it
        /// finishes by itself.
        /// </summary>
        void Play(DecodedSound sound, Action ended);

        /// <summary>Stops what is playing.</summary>
        void Stop();
    }

    // Play and Stop for the sound preview. Playback stops when the preview changes.
    public sealed partial class MainViewModel
    {
        private IAudioPlayer? audioPlayer;

        /// <summary>The player sounds play through; set by the window (none in tests unless given).</summary>
        public IAudioPlayer? AudioPlayer
        {
            get => audioPlayer;
            set
            {
                audioPlayer = value;
                OnPropertyChanged(nameof(PlaybackNote));
                PlaySoundCommand.NotifyCanExecuteChanged();
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PlaySoundCommand), nameof(StopSoundCommand))]
        private bool isPlaying;

        /// <summary>Why the Play button is off, if sound cannot play here.</summary>
        public string? PlaybackNote => AudioPlayer is null ? "No audio output." : AudioPlayer.Unavailable;

        private bool CanPlaySound() => !IsPlaying && Preview.Sound is not null && AudioPlayer is { Unavailable: null };

        [RelayCommand(CanExecute = nameof(CanPlaySound))]
        private void PlaySound()
        {
            if (Preview.Sound is not { } sound || AudioPlayer is not { } player) return;
            var playing = Preview;
            IsPlaying = true;
            player.Play(sound, () =>
            {
                if (ReferenceEquals(Preview, playing)) IsPlaying = false;
            });
            if (player.Unavailable is { } why)
            {
                IsPlaying = false;
                Status = why;
                OnPropertyChanged(nameof(PlaybackNote));
                PlaySoundCommand.NotifyCanExecuteChanged();
            }
        }

        [RelayCommand(CanExecute = nameof(IsPlaying))]
        private void StopSound()
        {
            AudioPlayer?.Stop();
            IsPlaying = false;
        }

        partial void OnPreviewChanging(PreviewViewModel value)
        {
            if (IsPlaying) StopSound();
        }
    }
}
