using ClassicMac.App.ViewModels;
using ClassicMac.Graphics;
using ClassicMac.Resources.Decoders.Sound;

namespace ClassicMac.App.Tests;

// The platform's services (pickers, dialogs, image reader, audio, shell) live on MainViewModel, attached by the window
// in one call that keeps any a test set first.
public class AppPlatformTests
{
    private sealed class Picker : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) =>
            Task.FromResult<string?>(null);
    }

    private sealed class Reader : IImageReader
    {
        public RgbaBitmap Read(string path) => new(1, 1);
    }

    private sealed class Player : IAudioPlayer
    {
        public string? Unavailable => null;

        public double? Position => null;

        public void Play(DecodedSound sound, double start, SoundLoop? loop, Action ended)
        {
        }

        public void Seek(double seconds)
        {
        }

        public void SetLoop(SoundLoop? loop)
        {
        }

        public void Stop()
        {
        }
    }

    [Fact]
    public void Attaching_a_platform_fills_only_the_services_not_set()
    {
        var mine = new Picker();
        var model = new MainViewModel { FilePicker = mine };
        var platform = new AppPlatform(FilePicker: new Picker(), ImageReader: new Reader(), AudioPlayer: new Player());

        model.AttachPlatform(platform);

        Assert.Same(mine, model.FilePicker);                          // a test's fake stays
        Assert.Same(platform.ImageReader, model.ImageReader);
        Assert.Same(platform.AudioPlayer, model.AudioPlayer);
        Assert.Null(model.Shell);
        Assert.Null(model.EditDialogs);
    }

    [Fact]
    public void A_new_audio_player_updates_the_sound_preview()
    {
        var model = new MainViewModel();
        Assert.Equal("No audio output.", model.SoundPlayback.PlaybackNote);
        var changed = new List<string?>();
        model.SoundPlayback.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.AudioPlayer = new Player();

        Assert.Null(model.SoundPlayback.PlaybackNote);
        Assert.Contains(nameof(SoundPlayback.PlaybackNote), changed);
    }
}
