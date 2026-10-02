using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Sound;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A stretch of a sound, in seconds from its start (a sampled sound's loop).</summary>
    public sealed record SoundLoop(double Start, double End);

    /// <summary>Plays decoded sounds on the computer's output device; one at a time.</summary>
    public interface IAudioPlayer
    {
        /// <summary>Why sound cannot play (no device, no native library), known once a Play failed; null otherwise.</summary>
        string? Unavailable { get; }

        /// <summary>Where playback is, in seconds from the sound's start; null when nothing plays.</summary>
        double? Position { get; }

        /// <summary>
        /// Plays <paramref name="sound"/> from <paramref name="start"/> seconds, stopping any other; with a
        /// <paramref name="loop"/>, that stretch repeats until Stop. <paramref name="ended"/> runs on the UI thread when it
        /// finishes by itself.
        /// </summary>
        void Play(DecodedSound sound, double start, SoundLoop? loop, Action ended);

        /// <summary>Moves the playing sound to <paramref name="seconds"/> from its start.</summary>
        void Seek(double seconds);

        /// <summary>Repeats <paramref name="loop"/> of the playing sound from now on; null plays on to the end.</summary>
        void SetLoop(SoundLoop? loop);

        /// <summary>Stops what is playing.</summary>
        void Stop();
    }

    // The sound preview's transport (design/boards/sound.md, P3, on A1): Play and Stop (Space), the playhead and its time,
    // a click on the waveform seeking, and Repeat the loop. Playback stops when the preview changes.
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
                NotifySoundCommands();
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PlaySoundCommand), nameof(StopSoundCommand))]
        private bool isPlaying;

        /// <summary>Where the playhead is, in seconds from the sound's start.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayheadText))]
        private double playhead;

        /// <summary>Whether playback repeats the sound's loop ("Repeat the loop").</summary>
        [ObservableProperty]
        private bool repeatLoop;

        /// <summary>Why the Play button is off, if sound cannot play here.</summary>
        public string? PlaybackNote => AudioPlayer is null ? "No audio output." : AudioPlayer.Unavailable;

        /// <summary>The playhead's time: "0:00.62".</summary>
        public string PlayheadText => Time(Playhead);

        /// <summary>The sound's length after the playhead's time: "/ 0:01.48".</summary>
        public string SoundDurationText => "/ " + Time(Preview.Sound?.Duration ?? 0);

        /// <summary>Whether the sound has a loop (the "Repeat the loop" box shows).</summary>
        public bool HasSoundLoop => Preview.SoundLoop is not null;

        private static string Time(double seconds)
        {
            var minutes = (int)(seconds / 60);
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds - minutes * 60:00.00}");
        }

        private bool CanPlay() => Preview.Sound is not null && AudioPlayer is { Unavailable: null };

        private bool CanPlaySound() => !IsPlaying && CanPlay();

        private void NotifySoundCommands()
        {
            PlaySoundCommand.NotifyCanExecuteChanged();
            ToggleSoundCommand.NotifyCanExecuteChanged();
            SeekSoundCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(CanPlaySound))]
        private void PlaySound() => PlayFrom(0);

        // Plays the preview's sound from `start` seconds, its loop repeating when Repeat the loop is on.
        private void PlayFrom(double start)
        {
            if (Preview.Sound is not { } sound || AudioPlayer is not { } player)
            {
                return;
            }

            var playing = Preview;
            Playhead = start;
            IsPlaying = true;
            player.Play(sound, start, RepeatLoop ? Preview.SoundLoop : null, () =>
            {
                if (ReferenceEquals(Preview, playing))
                {
                    IsPlaying = false;
                    Playhead = 0;
                }
            });
            if (player.Unavailable is { } why)
            {
                IsPlaying = false;
                Status = why;
                OnPropertyChanged(nameof(PlaybackNote));
                NotifySoundCommands();
            }
        }

        [RelayCommand(CanExecute = nameof(IsPlaying))]
        private void StopSound()
        {
            AudioPlayer?.Stop();
            IsPlaying = false;
            Playhead = 0;
        }

        /// <summary>Space: plays the sound, or stops it.</summary>
        [RelayCommand(CanExecute = nameof(CanPlay))]
        private void ToggleSound()
        {
            if (IsPlaying)
            {
                StopSound();
            }
            else
            {
                PlayFrom(0);
            }
        }

        /// <summary>A click on the waveform: playback moves there, or starts there.</summary>
        [RelayCommand(CanExecute = nameof(CanPlay))]
        private void SeekSound(double seconds)
        {
            if (Preview.Sound is not { } sound)
            {
                return;
            }

            seconds = Math.Clamp(seconds, 0, sound.Duration);
            if (IsPlaying && AudioPlayer is { } player)
            {
                player.Seek(seconds);
                Playhead = seconds;
            }
            else
            {
                PlayFrom(seconds);
            }
        }

        /// <summary>The playhead follows the player (the view calls this while playing).</summary>
        public void RefreshPlayhead()
        {
            if (IsPlaying && AudioPlayer?.Position is { } position)
            {
                Playhead = position;
            }
        }

        partial void OnRepeatLoopChanged(bool value)
        {
            if (IsPlaying)
            {
                AudioPlayer?.SetLoop(value ? Preview.SoundLoop : null);
            }
        }

        partial void OnPreviewChanging(PreviewViewModel value)
        {
            if (IsPlaying)
            {
                StopSound();
            }
        }

        // A new preview: the playhead back at the start, the transport's state for its sound.
        private void OnSoundPreviewChanged()
        {
            Playhead = 0;
            OnPropertyChanged(nameof(SoundDurationText));
            OnPropertyChanged(nameof(HasSoundLoop));
            NotifySoundCommands();
        }

        /// <summary>The error state's Show in Hex: the resource's bytes in the Hex tab.</summary>
        [RelayCommand]
        private void ShowInHex()
        {
            if (Selected is not ResourceNode node)
            {
                return;
            }

            Hex = HexViewModel.For(node);
            HexSource = Hex.Sources[0];
            SelectedTab = 2;
        }

        /// <summary>The error state's Save raw data…: the resource's bytes as they are, to a .bin file.</summary>
        [RelayCommand]
        private async Task SaveRawData()
        {
            if (Selected is not ResourceNode node || FilePicker is null)
            {
                return;
            }

            var stem = HostNames.ToHostName(MacString.FromMacRoman($"{node.Resource.Type.ToString().Trim()} {node.Resource.Id}"), 200);
            var path = await FilePicker.PickSaveFileAsync($"Save raw data of {node.Resource}", stem + ".bin", [".bin"]);
            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, node.Resource.GetData().ToArray());
            Status = $"Saved the raw data of {node.Resource} to {path}.";
        }
    }
}
