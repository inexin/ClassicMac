using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound
{
    /// <summary>A synthesizer (data format) entry of a format 1 <c>'snd '</c>.</summary>
    /// <param name="Id">The data format: 5 is sampled sound.</param>
    /// <param name="InitOptions">The channel's init options (<c>initMono</c> $80, <c>initStereo</c> $C0, …).</param>
    public readonly record struct SoundSynth(ushort Id, uint InitOptions);

    /// <summary>A sound command as stored.</summary>
    /// <param name="Command">The command word, with the data-offset flag ($8000).</param>
    /// <param name="Param1">The first parameter.</param>
    /// <param name="Param2">The second parameter; with the data-offset flag, an offset from the resource's start.</param>
    public readonly record struct SoundCommand(ushort Command, short Param1, int Param2)
    {
        /// <summary><c>soundCmd</c>.</summary>
        public const int SoundCmd = 80;

        /// <summary><c>bufferCmd</c>.</summary>
        public const int BufferCmd = 81;

        /// <summary>The command without the data-offset flag.</summary>
        public int Code => Command & 0x7FFF;

        /// <summary>Whether <see cref="Param2"/> is an offset into the resource (the sound header).</summary>
        public bool DataOffset => (Command & 0x8000) != 0;
    }

    /// <summary>The kind of sampled sound header (the <c>encode</c> byte).</summary>
    public enum SoundHeaderKind
    {
        /// <summary><c>stdSH</c> ($00): 8-bit mono.</summary>
        Standard = 0x00,

        /// <summary><c>cmpSH</c> ($FE): compressed, or any sample format named by <c>format</c>.</summary>
        Compressed = 0xFE,

        /// <summary><c>extSH</c> ($FF): several channels or 16-bit samples.</summary>
        Extended = 0xFF,
    }

    /// <summary>A sampled sound header and its samples.</summary>
    public sealed record SampledSound
    {
        /// <summary>The header's kind.</summary>
        public SoundHeaderKind Kind { get; init; }

        /// <summary>Where the header starts in the resource.</summary>
        public int Offset { get; init; }

        /// <summary>Channels (1 for a standard header).</summary>
        public int Channels { get; init; } = 1;

        /// <summary>The rate as stored, unsigned 16.16 fixed point.</summary>
        public uint SampleRateFixed { get; init; }

        /// <summary>The rate in hertz.</summary>
        public double SampleRate => SampleRateFixed / 65536.0;

        /// <summary>Loop start, as stored (the Sound Manager uses the loop only for instrument playback).</summary>
        public uint LoopStart { get; init; }

        /// <summary>Loop end, as stored.</summary>
        public uint LoopEnd { get; init; }

        /// <summary>The MIDI note the samples sound at (60 = middle C).</summary>
        public byte BaseNote { get; init; }

        /// <summary>Sample frames as the header counts them: for MACE and IMA4, packets per channel.</summary>
        public int Frames { get; init; }

        /// <summary>Bits per sample of the decoded sound.</summary>
        public int SampleSize { get; init; } = 8;

        /// <summary>
        /// The sample format: <c>raw </c> (offset binary), <c>twos</c>, <c>sowt</c>, <c>in24</c>, <c>in32</c>,
        /// <c>fl32</c>, <c>fl64</c>, or a codec (<c>MAC3</c>, <c>MAC6</c>, <c>ima4</c>, …).
        /// </summary>
        public FourCC Format { get; init; }

        /// <summary>A compressed header's <c>compressionID</c> (0 otherwise).</summary>
        public short CompressionId { get; init; }

        /// <summary>A compressed header's <c>packetSize</c> (0 otherwise).</summary>
        public ushort PacketSize { get; init; }

        /// <summary>The sample data, as stored.</summary>
        public ReadOnlyMemory<byte> Data { get; init; }
    }

    /// <summary>
    /// A <c>'snd '</c> resource (<i>Inside Macintosh: Sound</i>, "Sound Resources" and "Sound Header Records"): format 1
    /// (synthesizers, commands) or format 2 (reference count, commands), and the sampled sound a <c>bufferCmd</c> or
    /// <c>soundCmd</c> with the data-offset flag points to. Damage is reported, never thrown.
    /// </summary>
    public sealed class SoundResource
    {
        private const int HeaderLength = 22, LongHeaderLength = 64;

        private static readonly FourCC Raw = FourCC.FromString("raw "), Twos = FourCC.FromString("twos");

        private SoundResource(int format, IReadOnlyList<SoundSynth> synths, int referenceCount, IReadOnlyList<SoundCommand> commands,
            SampledSound? sound)
        {
            Format = format;
            Synths = synths;
            ReferenceCount = referenceCount;
            Commands = commands;
            Sound = sound;
        }

        /// <summary>1 or 2.</summary>
        public int Format { get; }

        /// <summary>A format 1 resource's synthesizers.</summary>
        public IReadOnlyList<SoundSynth> Synths { get; }

        /// <summary>A format 2 resource's reference count.</summary>
        public int ReferenceCount { get; }

        /// <summary>The commands, in order.</summary>
        public IReadOnlyList<SoundCommand> Commands { get; }

        /// <summary>The sampled sound the first buffer or sound command plays, or null when there is none.</summary>
        public SampledSound? Sound { get; }

        /// <summary>Reads a <c>'snd '</c>; null when it is not one (an unknown format, or too short to hold its lists).</summary>
        public static SoundResource? Read(ReadOnlyMemory<byte> resource, ICollection<Diagnostic> diagnostics, string source = "snd")
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var data = resource.Span;
            void Report(DiagnosticSeverity severity, string code, string message) => diagnostics.Add(new Diagnostic(severity, code, $"{source}: {message}"));
            if (data.Length < 4) return null;
            var reader = new BigEndianReader(resource);
            int format = reader.ReadUInt16At(0);
            var synths = new List<SoundSynth>();
            var referenceCount = 0;
            var at = 2;
            if (format == 1)
            {
                int count = reader.ReadUInt16At(at);
                at += 2;
                if (at + count * 6 + 2 > data.Length)
                {
                    Report(DiagnosticSeverity.Error, "sound.short", $"the resource ends inside its {count} synthesizers.");
                    return null;
                }
                for (var i = 0; i < count; i++, at += 6)
                    synths.Add(new SoundSynth(reader.ReadUInt16At(at), reader.ReadUInt32At(at + 2)));
            }
            else if (format == 2)
            {
                referenceCount = reader.ReadUInt16At(at);
                at += 2;
            }
            else
            {
                Report(DiagnosticSeverity.Error, "sound.unknown-format", $"format {format} is neither 1 nor 2.");
                return null;
            }

            if (at + 2 > data.Length)
            {
                Report(DiagnosticSeverity.Error, "sound.short", "the resource ends before its command count.");
                return null;
            }
            int commandCount = reader.ReadUInt16At(at);
            at += 2;
            var commands = new List<SoundCommand>();
            for (var i = 0; i < commandCount; i++, at += 8)
            {
                if (at + 8 > data.Length)
                {
                    Report(DiagnosticSeverity.Error, "sound.short", $"the resource ends after {i} of its {commandCount} commands.");
                    break;
                }
                commands.Add(new SoundCommand(reader.ReadUInt16At(at), reader.ReadInt16At(at + 2), reader.ReadInt32At(at + 4)));
            }

            // The sound the resource plays: the first buffer or sound command pointing into it. SndPlay finds a format 2
            // resource's header itself, right after the commands, and never reads the offset (Sound Manager 3.5.1,
            // disassembly); Realmz's format 2 sounds point to 20 but play from there.
            var players = commands.FindAll(c => c.DataOffset && c.Code is SoundCommand.BufferCmd or SoundCommand.SoundCmd);
            if (players.Count > 1)
                Report(DiagnosticSeverity.Info, "sound.several-sounds", $"{players.Count} commands point to sound headers; the first is decoded.");
            var offset = players.Count > 0 ? players[0].Param2 : -1;
            if (format == 2)
            {
                // SndPlay never reads a format 2 resource's commands: it plays the header after them, whatever they say.
                if (offset >= 0 && offset != at)
                {
                    Report(DiagnosticSeverity.Info, "sound.header-offset",
                        $"the command points to offset {offset}; as SndPlay does, the header after the commands (offset {at}) is read.");
                }
                offset = at;
            }
            var sound = offset >= 0 ? Header(resource, offset, Report) : null;
            return new SoundResource(format, synths, referenceCount, commands, sound);
        }

        private static SampledSound? Header(ReadOnlyMemory<byte> resource, int offset, Action<DiagnosticSeverity, string, string> report)
        {
            var data = resource.Span;
            if (offset < 0 || offset + HeaderLength > data.Length)
            {
                report(DiagnosticSeverity.Error, "sound.bad-offset", $"the sound header's offset {offset} lies outside the {data.Length}-byte resource.");
                return null;
            }
            var h = new BigEndianReader(resource[offset..]);
            var samplePtr = h.ReadUInt32();
            var lengthOrChannels = h.ReadUInt32();
            var rate = h.ReadUInt32();
            var loopStart = h.ReadUInt32();
            var loopEnd = h.ReadUInt32();
            var encode = h.ReadByte();
            var baseNote = h.ReadByte();
            if (samplePtr != 0)
                report(DiagnosticSeverity.Warning, "sound.sample-pointer", $"the header's samplePtr is ${samplePtr:X8}, not 0; the samples after the header are read.");
            if (rate == 0) report(DiagnosticSeverity.Warning, "sound.no-rate", "the sample rate is 0.");

            var sound = new SampledSound
            {
                Offset = offset,
                SampleRateFixed = rate,
                LoopStart = loopStart,
                LoopEnd = loopEnd,
                BaseNote = baseNote,
            };
            switch (encode)
            {
                case (byte)SoundHeaderKind.Standard:
                {
                    var start = offset + HeaderLength;
                    return sound with
                    {
                        Kind = SoundHeaderKind.Standard,
                        Frames = (int)Math.Min(lengthOrChannels, int.MaxValue),
                        Format = Raw,
                        Data = Samples(resource, start, lengthOrChannels, report),
                    };
                }
                case (byte)SoundHeaderKind.Extended or (byte)SoundHeaderKind.Compressed:
                {
                    if (offset + LongHeaderLength > data.Length)
                    {
                        report(DiagnosticSeverity.Error, "sound.short", "the resource ends inside the sound header.");
                        return null;
                    }
                    // The Sound Manager reads numChannels as the word at +6.
                    int channels = h.ReadUInt16At(6);
                    if (channels is 0 or > 64)
                    {
                        report(DiagnosticSeverity.Error, "sound.bad-header", $"the header gives {channels} channels.");
                        return null;
                    }
                    var frames = (int)Math.Min(h.ReadUInt32At(22), int.MaxValue);
                    var start = offset + LongHeaderLength;
                    // sampleSize: at +48 in an extended header (after the AIFF fields), at +62 in a compressed one.
                    int sampleSize = h.ReadUInt16At(encode == (byte)SoundHeaderKind.Extended ? 48 : 62);
                    if (encode == (byte)SoundHeaderKind.Extended)
                    {
                        // Extended header: 8-bit samples offset binary as in a standard header, 16-bit two's complement
                        // big-endian, channels interleaved. The Sound Manager reads every other size as 16-bit, so it
                        // misreads 24- and 32-bit samples: they are refused here.
                        if (sampleSize is not (8 or 16))
                        {
                            report(DiagnosticSeverity.Error, "sound.bad-header",
                                $"the header gives {sampleSize}-bit samples, which the Sound Manager reads as 16-bit.");
                            return null;
                        }
                        return sound with
                        {
                            Kind = SoundHeaderKind.Extended,
                            Channels = channels,
                            Frames = frames,
                            SampleSize = sampleSize,
                            Format = sampleSize == 8 ? Raw : Twos,
                            Data = Samples(resource, start, (long)frames * channels * (sampleSize / 8), report),
                        };
                    }

                    // Compressed header (Sound Manager 3.5.1): compressionID 0 is PCM whatever format says, 3 and 4 are
                    // MACE, -1 and -2 leave it to format; it refuses any other (-223). A codec's numFrames counts packets.
                    var format = h.ReadFourCCAt(40);
                    var compressionId = h.ReadInt16At(56);
                    var packetSize = h.ReadUInt16At(58);
                    switch (compressionId)
                    {
                        case 0: format = sampleSize == 8 ? Raw : Twos; break;
                        case 3: format = SoundCodecs.Mace3; break;
                        case 4: format = SoundCodecs.Mace6; break;
                        case -1 or -2: break;
                        default:
                            report(DiagnosticSeverity.Error, "sound.bad-header", $"compressionID {compressionId} is one the Sound Manager refuses.");
                            return null;
                    }
                    if ((format == Raw || format == Twos) && sampleSize is not (8 or 16))
                    {
                        report(DiagnosticSeverity.Error, "sound.bad-header",
                            $"the header gives {sampleSize}-bit '{format}' samples, which the Sound Manager reads as 16-bit.");
                        return null;
                    }
                    var pcm = Pcm.BytesPerSample(format, sampleSize);
                    var packet = SoundCodecs.Packet(format);
                    return sound with
                    {
                        Kind = SoundHeaderKind.Compressed,
                        Channels = channels,
                        Frames = frames,
                        SampleSize = pcm > 0 ? pcm * 8 : SoundCodecs.OutputSize(format),
                        Format = format,
                        CompressionId = compressionId,
                        PacketSize = packetSize,
                        Data = pcm > 0 ? Samples(resource, start, (long)frames * channels * pcm, report)
                            : packet is { } p ? Samples(resource, start, (long)frames * channels * p.Bytes, report)
                            : resource[Math.Min(start, resource.Length)..],
                    };
                }
                default:
                    report(DiagnosticSeverity.Error, "sound.bad-header", $"the header's encode byte is ${encode:X2}, not a sampled sound header.");
                    return null;
            }
        }

        private static ReadOnlyMemory<byte> Samples(ReadOnlyMemory<byte> resource, int start, long length, Action<DiagnosticSeverity, string, string> report)
        {
            var available = Math.Max(0, resource.Length - start);
            if (length > available)
            {
                report(DiagnosticSeverity.Warning, "sound.short", $"the header counts {length} bytes of samples but the resource holds {available}; cut.");
                length = available;
            }
            return resource.Slice(Math.Min(start, resource.Length), (int)length);
        }
    }
}
