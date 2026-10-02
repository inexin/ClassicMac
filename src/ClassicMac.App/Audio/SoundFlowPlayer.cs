using System;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources.Decoders.Sound;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace ClassicMac.App.Audio
{
    /// <summary>
    /// Playback through SoundFlow (miniaudio). The device opens on the first Play at 48 kHz stereo float; sounds are converted
    /// to that (linear interpolation, mono copied to both sides) so any rate Mac sounds use plays at its true pitch.
    /// </summary>
    internal sealed class SoundFlowPlayer : IAudioPlayer, IDisposable
    {
        private static readonly AudioFormat DeviceFormat = new()
        {
            Format = SampleFormat.F32,
            Channels = 2,
            SampleRate = 48000,
            Layout = AudioFormat.GetLayoutFromChannels(2),
        };

        private MiniAudioEngine? engine;
        private AudioPlaybackDevice? device;
        private SoundPlayer? player;
        private RawDataProvider? provider;
        private string? unavailable;

        public string? Unavailable => unavailable;

        // The sound is converted to the device's rate, so its seconds are the player's: position, seek and loop points
        // pass through as they are.
        public double? Position => player?.Time;

        public void Play(DecodedSound sound, double start, SoundLoop? loop, Action ended)
        {
            ArgumentNullException.ThrowIfNull(sound);
            ArgumentNullException.ThrowIfNull(ended);
            Stop();
            if (!Open())
            {
                return;
            }

            provider = new RawDataProvider(Convert(sound), DeviceFormat.SampleRate);
            var current = player = new SoundPlayer(engine!, DeviceFormat, provider);
            if (start > 0)
            {
                current.Seek((float)start);
            }

            SetLoop(loop);
            current.PlaybackEnded += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(player, current))
                {
                    return;
                }

                Stop();
                ended();
            });
            device!.MasterMixer.AddComponent(current);
            current.Play();
        }

        public void Seek(double seconds) => player?.Seek((float)seconds);

        public void SetLoop(SoundLoop? loop)
        {
            if (player is not { } p)
            {
                return;
            }

            p.IsLooping = loop is not null;
            if (loop is not null)
            {
                p.SetLoopPoints((float)loop.Start, (float)loop.End);
            }
        }

        public void Stop()
        {
            if (player is { } p)
            {
                player = null;
                p.Stop();
                device?.MasterMixer.RemoveComponent(p);
                p.Dispose();
            }
            provider?.Dispose();
            provider = null;
        }

        public void Dispose()
        {
            Stop();
            device?.Dispose();
            engine?.Dispose();
            device = null;
            engine = null;
        }

        private bool Open()
        {
            if (device is not null)
            {
                return true;
            }

            if (unavailable is not null)
            {
                return false;
            }

            try
            {
                engine = new MiniAudioEngine();
                device = engine.InitializePlaybackDevice(null, DeviceFormat);
                device.Start();
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or InvalidOperationException or SoundFlow.Exceptions.BackendException)
            {
                unavailable = $"Sound cannot play here: {e.Message}";
                engine?.Dispose();
                engine = null;
                return false;
            }
        }

        // The sound at the device's rate and channels.
        internal static float[] Convert(DecodedSound sound)
        {
            var rate = DeviceFormat.SampleRate;
            var inFrames = sound.Frames;
            if (inFrames == 0 || sound.SampleRate <= 0)
            {
                return [];
            }

            var outFrames = (int)Math.Min(int.MaxValue / 2, (long)Math.Ceiling(inFrames * rate / sound.SampleRate));
            var output = new float[outFrames * 2];
            var step = sound.SampleRate / rate;
            for (var i = 0; i < outFrames; i++)
            {
                var position = i * step;
                var index = (int)position;
                var fraction = (float)(position - index);
                var next = Math.Min(index + 1, inFrames - 1);
                for (var c = 0; c < 2; c++)
                {
                    var channel = Math.Min(c, sound.Channels - 1);
                    var a = sound.Samples[index * sound.Channels + channel];
                    var b = sound.Samples[next * sound.Channels + channel];
                    output[i * 2 + c] = a + (b - a) * fraction;
                }
            }
            return output;
        }
    }
}
