using ClassicMac.App.ViewModels;
using ClassicMac.Resources.Decoders.Sound;

namespace ClassicMac.App.Tests;

// An audio player with no device: records what it is asked; Finish ends the sound as the device would, Position is
// where the test says playback is.
internal sealed class FakeAudioPlayer : IAudioPlayer
{
    private Action? ended;

    public List<(DecodedSound Sound, double Start, SoundLoop? Loop)> Played { get; } = [];

    public List<double> Seeks { get; } = [];

    public List<SoundLoop?> Loops { get; } = [];

    public int Stops { get; private set; }

    public string? Unavailable { get; set; }

    public double? Position { get; set; }

    public void Play(DecodedSound sound, double start, SoundLoop? loop, Action ended)
    {
        if (Unavailable is not null)
        {
            return;
        }

        Played.Add((sound, start, loop));
        Position = start;
        this.ended = ended;
    }

    public void Seek(double seconds)
    {
        Seeks.Add(seconds);
        Position = seconds;
    }

    public void SetLoop(SoundLoop? loop) => Loops.Add(loop);

    public void Stop()
    {
        Stops++;
        Position = null;
    }

    public void Finish()
    {
        Position = null;
        ended?.Invoke();
    }
}
