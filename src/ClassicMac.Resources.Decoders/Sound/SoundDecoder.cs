using System;
using System.Collections.Generic;
using System.Globalization;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>
    /// <c>'snd '</c>: the sampled sound as a WAV (the rate rounded to whole hertz; a loop and a base note other than
    /// middle C in its <c>smpl</c> chunk), and a JSON file with what WAV cannot hold: the exact rate, the header, the
    /// synthesizers and the commands. A resource with commands only (no sampled sound) gives the JSON alone; one whose
    /// samples are in a format not read is exported raw. MACE
    /// comes out 8-bit, as the Sound Manager gives it; IMA4 and µ-law 16-bit.
    /// </summary>
    internal sealed class SoundDecoder : IResourceDecoder, IBuiltInDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("snd ");

        // Sound.h's command numbers, for the JSON.
        private static readonly Dictionary<int, string> CommandNames = new()
        {
            [0] = "nullCmd", [3] = "quietCmd", [4] = "flushCmd", [5] = "reInitCmd", [10] = "waitCmd", [11] = "pauseCmd",
            [12] = "resumeCmd", [13] = "callBackCmd", [14] = "syncCmd", [24] = "availableCmd", [25] = "versionCmd",
            [26] = "totalLoadCmd", [27] = "loadCmd", [40] = "freqDurationCmd", [41] = "restCmd", [42] = "freqCmd",
            [43] = "ampCmd", [44] = "timbreCmd", [45] = "getAmpCmd", [46] = "volumeCmd", [47] = "getVolumeCmd",
            [60] = "waveTableCmd", [61] = "phaseCmd", [80] = "soundCmd", [81] = "bufferCmd", [82] = "rateCmd",
            [83] = "continueCmd", [84] = "doubleBufferCmd", [85] = "getRateCmd", [86] = "rateMultiplierCmd",
            [87] = "getRateMultiplierCmd", [90] = "sizeCmd", [91] = "convertCmd",
        };

        public string Name => "sound.snd";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyCollection<FourCC> Types => [Type];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var resource = SoundResource.Read(input.Data, input.Diagnostics, input.Resource.ToString());
            if (resource is null) return [];
            if (resource.Sound is not { } sound) return [new DecodedFile(".json", Json(resource))];

            if (SoundCodecs.ToWav(sound) is not { } samples)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "sound.codec",
                    $"{input.Resource}: the samples are in '{sound.Format}', which is not decoded; exported raw."));
                return [];
            }
            var frames = samples.Data.Length / (samples.Width * sound.Channels);
            var wav = WavWriter.Write(samples.Data, sound.Channels, samples.Width, samples.IsFloat, sound.SampleRate, Sampler(sound, frames));
            return [new DecodedFile(".wav", wav), new DecodedFile(".json", Json(resource))];
        }

        // A smpl chunk when there is a loop inside the sound or a base note other than middle C (0 is taken as 60).
        private static SamplerInfo? Sampler(SampledSound sound, int frames)
        {
            var note = sound.BaseNote is 0 ? 60 : sound.BaseNote;
            var loop = sound.LoopEnd > sound.LoopStart && sound.LoopEnd - sound.LoopStart > 2 && sound.LoopEnd <= frames;
            if (!loop && note == 60) return null;
            return new SamplerInfo(note, loop ? sound.LoopStart : null, loop ? sound.LoopEnd - 1 : 0);
        }

        private static byte[] Json(SoundResource resource) => MacText.Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("format", resource.Format);
            if (resource.Format == 1)
            {
                w.WriteStartArray("synthesizers");
                foreach (var s in resource.Synths)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", s.Id);
                    w.WriteString("initOptions", Hex(s.InitOptions));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            else
            {
                w.WriteNumber("referenceCount", resource.ReferenceCount);
            }
            w.WriteStartArray("commands");
            foreach (var c in resource.Commands)
            {
                w.WriteStartObject();
                w.WriteNumber("command", c.Code);
                if (CommandNames.TryGetValue(c.Code, out var name)) w.WriteString("name", name);
                if (c.DataOffset) w.WriteBoolean("dataOffset", true);
                w.WriteNumber("param1", c.Param1);
                w.WriteNumber("param2", c.Param2);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (resource.Sound is { } sound)
            {
                w.WriteStartObject("sound");
                w.WriteString("header", sound.Kind switch
                {
                    SoundHeaderKind.Standard => "standard",
                    SoundHeaderKind.Extended => "extended",
                    _ => "compressed",
                });
                w.WriteNumber("headerOffset", sound.Offset);
                w.WriteNumber("sampleRate", Math.Round(sound.SampleRate, 6));
                w.WriteString("sampleRateFixed", Hex(sound.SampleRateFixed));
                w.WriteNumber("channels", sound.Channels);
                w.WriteNumber("sampleSize", sound.SampleSize);
                w.WriteString("sampleFormat", sound.Format.ToString());
                if (sound.Kind == SoundHeaderKind.Compressed)
                {
                    w.WriteNumber("compressionId", sound.CompressionId);
                    w.WriteNumber("packetSize", sound.PacketSize);
                }
                w.WriteNumber("frames", sound.Frames);
                w.WriteNumber("loopStart", sound.LoopStart);
                w.WriteNumber("loopEnd", sound.LoopEnd);
                w.WriteNumber("baseNote", sound.BaseNote);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });

        private static string Hex(uint value) => "$" + value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
