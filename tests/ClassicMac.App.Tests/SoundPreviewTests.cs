using ClassicMac.App.Audio;
using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Sound;

namespace ClassicMac.App.Tests;

public class SoundPreviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-sound-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Records what it is asked to play; Finish ends the sound as the device would.
    private sealed class FakePlayer : IAudioPlayer
    {
        private Action? ended;

        public List<DecodedSound> Played { get; } = [];

        public int Stops { get; private set; }

        public string? Unavailable { get; set; }

        public void Play(DecodedSound sound, Action ended)
        {
            if (Unavailable is not null) return;
            Played.Add(sound);
            this.ended = ended;
        }

        public void Stop() => Stops++;

        public void Finish() => ended?.Invoke();
    }

    // A format 1 'snd ' with a standard header: 8-bit, 11127.27 Hz, a loop, base note 72; and a MACE one.
    internal static byte[] Sound(int length = 1000, byte encode = 0x00)
    {
        byte[] header = [0, 0, 0, 0, .. BigEndian((uint)length), 0x2B, 0x77, 0x45, 0xD1, 0, 0, 0, 10, .. BigEndian((uint)(length - 10)), encode, 72];
        var samples = Enumerable.Range(0, length).Select(i => (byte)(128 + 100 * Math.Sin(i / 8.0))).ToArray();
        return [0, 1, 0, 1, 0, 5, 0, 0, 0, 0x80, 0, 1, 0x80, 0x51, 0, 0, 0, 0, 0, 20, .. header, .. samples];
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private async Task<(MainViewModel Model, ResourceNode Sound)> Open(FakePlayer? player)
    {
        byte[] mace = [.. Sound(64)[..20], .. new byte[64]];
        mace[20 + 20] = 0xFE; // compressed header, compressionID 3
        mace[20 + 4 + 3] = 1; // one channel
        mace[20 + 56 + 1] = 3;
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Sounds", [], PreviewTests.Fork(("snd ", 128, "Sine", Sound()), ("snd ", 129, "Mace", mace)));
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var model = new MainViewModel { AudioPlayer = player };
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single();
        await file.EnsureLoadedAsync();
        var sound = file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().First(r => r.Resource.Id == 128);
        return (model, sound);
    }

    [Fact]
    public async Task A_sound_previews_with_its_details_and_plays()
    {
        var player = new FakePlayer();
        var (model, sound) = await Open(player);

        model.Selected = sound;
        await model.PreviewTask;

        Assert.Equal(PreviewKind.Sound, model.Preview.Kind);
        Assert.Equal(1, model.SelectedTab);
        Assert.Equal("11127.273 Hz, mono, 8-bit, 0.09 s (1,000 frames), loop 10–990, base note 72", model.Preview.SoundDetails);
        Assert.Equal(1000, model.Preview.Sound!.Frames);
        Assert.True(model.PlaySoundCommand.CanExecute(null));
        Assert.False(model.StopSoundCommand.CanExecute(null));

        model.PlaySoundCommand.Execute(null);

        Assert.Same(model.Preview.Sound, Assert.Single(player.Played));
        Assert.True(model.IsPlaying);
        Assert.False(model.PlaySoundCommand.CanExecute(null));
        Assert.True(model.StopSoundCommand.CanExecute(null));

        player.Finish();

        Assert.False(model.IsPlaying);
        Assert.True(model.PlaySoundCommand.CanExecute(null));
    }

    [Fact]
    public async Task Playback_stops_when_the_selection_changes()
    {
        var player = new FakePlayer();
        var (model, sound) = await Open(player);
        model.Selected = sound;
        await model.PreviewTask;
        model.PlaySoundCommand.Execute(null);

        model.Selected = sound.Parent;
        await model.PreviewTask;

        Assert.False(model.IsPlaying);
        Assert.Equal(1, player.Stops);
    }

    [Fact]
    public async Task Sounds_not_decoded_yet_say_so()
    {
        var (model, sound) = await Open(new FakePlayer());
        model.Selected = sound.Parent!.Children.OfType<ResourceNode>().Single(r => r.Resource.Id == 129);
        await model.PreviewTask;

        Assert.Equal(PreviewKind.None, model.Preview.Kind);
        Assert.Contains("'MAC3'", model.Preview.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_output_device_playback_is_off_with_a_note()
    {
        var player = new FakePlayer { Unavailable = "Sound cannot play here: no device." };
        var (model, sound) = await Open(player);
        model.Selected = sound;
        await model.PreviewTask;

        Assert.False(model.PlaySoundCommand.CanExecute(null));
        Assert.Equal("Sound cannot play here: no device.", model.PlaybackNote);
        Assert.Equal("No audio output.", (await Open(null)).Model.PlaybackNote);
    }

    [Fact]
    public void Sounds_are_converted_to_the_device_rate_and_stereo()
    {
        var sound = new DecodedSound([0f, 1f, 0f, -1f], 1, 24000);

        var converted = SoundFlowPlayer.Convert(sound);

        Assert.Equal(16, converted.Length); // 4 frames at 24 kHz = 8 at 48 kHz, two channels
        Assert.Equal([0f, 0f, 0.5f, 0.5f, 1f, 1f, 0.5f, 0.5f], converted[..8]);
        Assert.Equal([-1f, -1f], converted[^2..]);
    }
}
