using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The sound preview (design/boards/sound.md, P3) on the player's position, seek and loop (A1): the detail chips, the
// playhead and its time, click-to-seek, Space, Repeat the loop, and the error state's Hex and Save raw data.
public sealed class SoundPlaybackTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-playback").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class Picker(string path) : IFilePicker
    {
        public List<(string Name, IReadOnlyList<string> Extensions)> Asked { get; } = [];

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions)
        {
            Asked.Add((suggestedName, extensions));
            return Task.FromResult<string?>(path);
        }
    }

    // 'snd ' 128 "Sine" (8-bit, 11127.27 Hz, 1,000 frames, loop 10–990, base note 72), 129 in format 3 (unknown).
    private async Task<(MainViewModel Model, ResourceNode Sine, ResourceNode Unknown, FakeAudioPlayer Player)> Open()
    {
        byte[] unknown = [0, 3, 0, 0];
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Sounds", [], PreviewTests.Fork(("snd ", 128, "Sine", SoundPreviewTests.Sound()), ("snd ", 129, null, unknown)));
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var player = new FakeAudioPlayer();
        var model = new MainViewModel();
        model.SoundPlayback.AudioPlayer = player;
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single();
        await file.EnsureLoadedAsync();
        var sounds = file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().ToList();
        model.Selected = sounds[0];
        await model.PreviewTask;
        return (model, sounds[0], sounds[1], player);
    }

    private const double Rate = 0x2B7745D1 / 65536.0;

    [Fact]
    public async Task The_details_are_chips_and_the_loop_is_in_seconds()
    {
        var (model, _, _, _) = await Open();
        var preview = model.Preview;
        Assert.Equal(
            [("Rate", "11,127.273 Hz"), ("Channels", "mono"), ("Sample", "8-bit"), ("Length", "0.09 s · 1,000 frames"), ("Loop", "10–990"),
             ("Base note", "72"), ("Format", "sampled, uncompressed")],
            preview.SoundFacts.Select(f => (f.Label, f.Value)));
        Assert.Equal(new SoundLoop(10 / Rate, 990 / Rate), preview.SoundLoop);
        Assert.Equal((10, 990), preview.SoundLoopFrames);
        Assert.True(model.SoundPlayback.HasSoundLoop);
        Assert.Equal(("0:00.00", "/ 0:00.09"), (model.SoundPlayback.PlayheadText, model.SoundPlayback.SoundDurationText));
    }

    [Fact]
    public async Task Play_starts_at_the_beginning_and_the_playhead_follows_the_player()
    {
        var (model, _, _, player) = await Open();
        model.SoundPlayback.PlaySoundCommand.Execute(null);
        Assert.Equal((0d, (SoundLoop?)null), (player.Played[0].Start, player.Played[0].Loop));

        player.Position = 0.05;
        model.SoundPlayback.RefreshPlayhead();
        Assert.Equal(0.05, model.SoundPlayback.Playhead);
        Assert.Equal("0:00.05", model.SoundPlayback.PlayheadText);

        player.Finish();                                                    // ended by itself: back to the start
        Assert.False(model.SoundPlayback.IsPlaying);
        Assert.Equal(0, model.SoundPlayback.Playhead);
        player.Position = 0.07;
        model.SoundPlayback.RefreshPlayhead();                                            // not playing: no change
        Assert.Equal(0, model.SoundPlayback.Playhead);

        model.SoundPlayback.PlaySoundCommand.Execute(null);
        player.Position = 0.03;
        model.SoundPlayback.RefreshPlayhead();
        model.SoundPlayback.StopSoundCommand.Execute(null);
        Assert.Equal(0, model.SoundPlayback.Playhead);
    }

    [Fact]
    public async Task A_click_on_the_waveform_seeks_or_starts_there()
    {
        var (model, _, _, player) = await Open();
        model.SoundPlayback.SeekSoundCommand.Execute(0.04);                                // stopped: plays from there
        Assert.True(model.SoundPlayback.IsPlaying);
        Assert.Equal(0.04, player.Played.Single().Start);
        Assert.Equal(0.04, model.SoundPlayback.Playhead);

        model.SoundPlayback.SeekSoundCommand.Execute(0.08);                                // playing: moves the playback
        Assert.Equal([0.08], player.Seeks);
        Assert.Equal(0.08, model.SoundPlayback.Playhead);
        Assert.Single(player.Played);

        model.SoundPlayback.SeekSoundCommand.Execute(-1.0);                               // clamped to the sound
        model.SoundPlayback.SeekSoundCommand.Execute(5.0);
        Assert.Equal([0.08, 0, 1000 / Rate], player.Seeks);
    }

    [Fact]
    public async Task Repeat_the_loop_loops_the_playback()
    {
        var (model, _, _, player) = await Open();
        var loop = model.Preview.SoundLoop;
        model.SoundPlayback.RepeatLoop = true;
        Assert.Empty(player.Loops);                                         // not playing: nothing to tell the player
        model.SoundPlayback.PlaySoundCommand.Execute(null);
        Assert.Equal(loop, player.Played[0].Loop);
        model.SoundPlayback.RepeatLoop = false;
        model.SoundPlayback.RepeatLoop = true;
        Assert.Equal([null, loop], player.Loops);
    }

    [Fact]
    public async Task Space_plays_and_stops()
    {
        var (model, _, _, player) = await Open();
        model.SoundPlayback.ToggleSoundCommand.Execute(null);
        Assert.True(model.SoundPlayback.IsPlaying);
        model.SoundPlayback.ToggleSoundCommand.Execute(null);
        Assert.False(model.SoundPlayback.IsPlaying);
        Assert.Equal(1, player.Stops);
        Assert.Single(player.Played);
    }

    [Fact]
    public async Task An_undecodable_sound_offers_its_hex_and_raw_data()
    {
        var (model, _, unknown, player) = await Open();
        model.Selected = unknown;
        await model.PreviewTask;
        var preview = model.Preview;
        Assert.Equal(PreviewKind.SoundError, preview.Kind);
        Assert.Equal("No preview for 'snd ' 129", preview.ErrorTitle);
        Assert.Contains("sound.unknown-format", preview.ErrorDetail);
        Assert.Equal("Format 3 is neither 1 nor 2. · sound.unknown-format", preview.ErrorDetail);
        Assert.False(model.SoundPlayback.PlaySoundCommand.CanExecute(null));
        Assert.False(model.SoundPlayback.HasSoundLoop);
        Assert.Equal(1, model.SelectedTab);

        model.SoundPlayback.ShowInHexCommand.Execute(null);
        Assert.Equal(2, model.SelectedTab);
        Assert.True(model.HasHex);
        Assert.Equal(new byte[] { 0, 3, 0, 0 }, model.HexSource!.Data.ToArray());

        var path = Path.Combine(folder, "raw.bin");
        var picker = new Picker(path);
        model.FilePicker = picker;
        await model.SoundPlayback.SaveRawDataCommand.ExecuteAsync(null);
        Assert.Equal(new byte[] { 0, 3, 0, 0 }, File.ReadAllBytes(path));
        Assert.Equal([".bin"], picker.Asked.Single().Extensions);
        _ = player;
    }
}
