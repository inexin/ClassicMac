using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Sound;

/// <summary>A marker of an AIFF file's <c>MARK</c> chunk: its ID, its sample frame and its name.</summary>
public sealed record AiffMarker(short Id, uint Position, string Name);

/// <summary>A loop of an AIFF file's <c>INST</c> chunk: its play mode (0 none, 1 forward, 2 forward and backward) and its markers.</summary>
public readonly record struct AiffLoop(short PlayMode, short Begin, short End);

/// <summary>An AIFF file's <c>INST</c> chunk.</summary>
public sealed record AiffInstrument(sbyte BaseNote, sbyte Detune, sbyte LowNote, sbyte HighNote, sbyte LowVelocity, sbyte HighVelocity,
    short Gain, AiffLoop SustainLoop, AiffLoop ReleaseLoop);

/// <summary>
/// An AIFF or AIFF-C file (docs/formats/documents/aiff.md): the <c>FORM</c> and its chunks, the sampled sound as the
/// <c>'snd '</c> decoders read it (<see cref="SampledSound"/>, so the same codecs decode it), the markers, the
/// instrument and the text chunks.
/// </summary>
public sealed class AiffFile
{
    private static readonly FourCC Form = FourCC.FromString("FORM"), Aiff = FourCC.FromString("AIFF"), Aifc = FourCC.FromString("AIFC"),
        None = FourCC.FromString("NONE"), Twos = FourCC.FromString("twos"), In24 = FourCC.FromString("in24"), In32 = FourCC.FromString("in32");

    private AiffFile(FourCC formType, SampledSound sound, double sampleRate, int sampleSize, FourCC compression, string compressionName)
    {
        FormType = formType;
        Sound = sound;
        SampleRate = sampleRate;
        SampleSize = sampleSize;
        Compression = compression;
        CompressionName = compressionName;
    }

    /// <summary><c>AIFF</c>, or <c>AIFC</c> for AIFF-C.</summary>
    public FourCC FormType { get; }

    /// <summary>Whether the file is AIFF-C.</summary>
    public bool IsCompressed => FormType == Aifc;

    /// <summary>The samples, channels, frames and format, as a sound resource's header gives them.</summary>
    public SampledSound Sound { get; }

    /// <summary>The sample rate, exactly as the 80-bit extended number says.</summary>
    public double SampleRate { get; }

    /// <summary>The COMM chunk's sample size in bits (1 to 32 for uncompressed samples).</summary>
    public int SampleSize { get; }

    /// <summary>The AIFF-C compression type (<c>NONE</c> for AIFF).</summary>
    public FourCC Compression { get; }

    /// <summary>The AIFF-C compression name ("not compressed"); empty for AIFF.</summary>
    public string CompressionName { get; }

    /// <summary>The MARK chunk's markers, in file order.</summary>
    public IReadOnlyList<AiffMarker> Markers { get; private set; } = [];

    /// <summary>The INST chunk, or null.</summary>
    public AiffInstrument? Instrument { get; private set; }

    /// <summary>The NAME chunk's text, or null.</summary>
    public string? Name { get; private set; }

    /// <summary>The AUTH chunk's text, or null.</summary>
    public string? Author { get; private set; }

    /// <summary>The <c>(c) </c> chunk's text, or null.</summary>
    public string? Copyright { get; private set; }

    /// <summary>The ANNO chunks' texts, one line each, or null.</summary>
    public string? Annotation { get; private set; }

    /// <summary>Whether <paramref name="data"/> starts as an AIFF or AIFF-C file does.</summary>
    public static bool IsAiff(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("FORM"u8) && (data[8..12].SequenceEqual("AIFF"u8) || data[8..12].SequenceEqual("AIFC"u8));

    /// <summary>
    /// Reads an AIFF or AIFF-C file; null, with a diagnostic, when it is no such file or has no COMM chunk. Problems
    /// with the samples are reported and the samples that are there kept.
    /// </summary>
    public static AiffFile? Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics, string source)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!IsAiff(data.Span))
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "aiff.header", $"{source}: not an AIFF or AIFF-C file (no FORM of type AIFF or AIFC)."));
            return null;
        }

        var reader = new BigEndianReader(data);
        var formType = reader.ReadFourCCAt(8);
        long end = Math.Min(data.Length, 8L + reader.ReadUInt32At(4));
        BigEndianReader? comm = null, ssnd = null, mark = null, inst = null;
        var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        bool clipped = false;
        for (long position = 12; position + 8 <= end;)
        {
            var id = reader.ReadFourCCAt((int)position);
            long size = reader.ReadUInt32At((int)position + 4);
            long available = Math.Min(size, end - position - 8);
            clipped |= available < size;
            var chunk = reader.ReadSubReaderAt((int)position + 8, (int)available);
            switch (id.ToString())
            {
                case "COMM":
                    comm ??= chunk;
                    break;
                case "SSND":
                    ssnd ??= chunk;
                    break;
                case "MARK":
                    mark ??= chunk;
                    break;
                case "INST":
                    inst ??= chunk;
                    break;
                case "NAME" or "AUTH" or "(c) " or "ANNO":
                    if (!texts.TryGetValue(id.ToString(), out var list))
                    {
                        texts[id.ToString()] = list = [];
                    }

                    list.Add(MacRoman.Decode(chunk.Source.Span).TrimEnd('\0'));
                    break;
            }

            position += 8 + size + (size & 1);
        }

        if (clipped)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "aiff.truncated", $"{source}: a chunk runs past the end of the file; read as far as it goes."));
        }

        if (comm is null || comm.Source.Length < 18)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "aiff.no-comm", $"{source}: no COMM chunk, so there is no sound to read."));
            return null;
        }

        short channels = comm.ReadInt16();
        uint frames = comm.ReadUInt32();
        short sampleSize = comm.ReadInt16();
        double rate = Extended(comm.ReadBytes(10));
        var compression = None;
        string compressionName = "";
        if (formType == Aifc && comm.TryReadFourCC(out var type))
        {
            compression = type;
            if (comm.TryReadByte(out var length) && comm.TryReadBytes(Math.Min(length, comm.Source.Length - comm.Position), out var name))
            {
                compressionName = MacRoman.Decode(name);
            }
        }

        // Uncompressed samples are left-justified in whole bytes: 1 to 8 bits in one, up to 16 in two, and so on.
        int bytes = (Math.Clamp((int)sampleSize, 1, 32) + 7) / 8;
        var format = compression == None || compression == Twos ? bytes switch { 3 => In24, 4 => In32, _ => Twos } : compression;
        int frameBytes = Pcm.BytesPerSample(format, bytes * 8) * channels;
        if (frameBytes == 0 && SoundCodecs.Packet(format) is { } packet)
        {
            frameBytes = packet.Bytes * channels;
        }

        var samples = ReadOnlyMemory<byte>.Empty;
        if (ssnd is not null && ssnd.Source.Length >= 8)
        {
            long offset = 8L + ssnd.ReadUInt32At(0);
            samples = offset <= ssnd.Source.Length ? ssnd.Source[(int)offset..] : ReadOnlyMemory<byte>.Empty;
        }

        long wanted = frameBytes > 0 ? (long)frames * frameBytes : samples.Length;
        if (frames > 0 && samples.Length < wanted)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, ssnd is null ? "aiff.no-ssnd" : "aiff.short",
                ssnd is null
                    ? $"{source}: COMM counts {frames} sample frames but there is no SSND chunk."
                    : $"{source}: COMM counts {frames} sample frames but SSND holds fewer; read as far as it goes."));
        }

        samples = samples[..(int)Math.Min(samples.Length, wanted)];
        var markers = mark is null ? [] : ReadMarkers(mark);
        var instrument = inst is { Source.Length: >= 20 } ? ReadInstrument(inst) : null;
        var sound = new SampledSound
        {
            Kind = SoundHeaderKind.Extended,
            Channels = Math.Max(1, (int)channels),
            SampleRateFixed = (uint)Math.Clamp(rate * 65536, 0, uint.MaxValue),
            SampleSize = bytes * 8,
            Frames = (int)Math.Min(frames, int.MaxValue),
            Format = format,
            Data = samples,
            BaseNote = (byte)(instrument?.BaseNote ?? 60),
        };
        if (instrument is { SustainLoop: { PlayMode: not 0 } loop }
            && markers.FirstOrDefault(m => m.Id == loop.Begin) is { } begin && markers.FirstOrDefault(m => m.Id == loop.End) is { } finish)
        {
            sound = sound with { LoopStart = begin.Position, LoopEnd = finish.Position };
        }

        string? Text(string id) => texts.TryGetValue(id, out var list) ? string.Join("\n", list) : null;
        return new AiffFile(formType, sound, rate, sampleSize, compression, compressionName)
        {
            Markers = markers,
            Instrument = instrument,
            Name = Text("NAME"),
            Author = Text("AUTH"),
            Copyright = Text("(c) "),
            Annotation = Text("ANNO"),
        };
    }

    /// <summary>The samples as a WAV file (the loop and base note in a <c>smpl</c> chunk), or null for a codec not read.</summary>
    public static byte[]? ToWav(AiffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sound = file.Sound;
        if (SoundCodecs.ToWav(sound) is not { } samples)
        {
            return null;
        }

        int frames = samples.Data.Length / (samples.Width * sound.Channels);
        return WavWriter.Write(samples.Data, sound.Channels, samples.Width, samples.IsFloat, file.SampleRate, SoundDecoder.Sampler(sound, frames));
    }

    // An IEEE 754 80-bit extended number: a sign and 15-bit exponent, then a 64-bit mantissa with its integer bit.
    private static double Extended(ReadOnlySpan<byte> bytes)
    {
        var reader = new BigEndianReader(bytes.ToArray());
        ushort head = reader.ReadUInt16();
        ulong mantissa = reader.ReadUInt64();
        int exponent = head & 0x7FFF;
        if (exponent == 0 && mantissa == 0)
        {
            return 0;
        }

        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (head & 0x8000) != 0 ? -value : value;
    }

    private static List<AiffMarker> ReadMarkers(BigEndianReader mark)
    {
        var markers = new List<AiffMarker>();
        if (!mark.TryReadUInt16(out var count))
        {
            return markers;
        }

        for (var i = 0; i < count; i++)
        {
            if (!mark.TryReadInt16(out var id) || !mark.TryReadUInt32(out var position) || !mark.TryReadByte(out var length)
                || !mark.TryReadBytes(length, out var name))
            {
                break;
            }

            markers.Add(new AiffMarker(id, position, MacRoman.Decode(name)));
            if (length % 2 == 0)
            {
                mark.TrySkip(1);                                                     // the pstring padded to even
            }
        }

        return markers;
    }

    private static AiffInstrument ReadInstrument(BigEndianReader inst)
    {
        sbyte Signed() => (sbyte)inst.ReadByte();
        AiffLoop Loop() => new(inst.ReadInt16(), inst.ReadInt16(), inst.ReadInt16());
        return new AiffInstrument(Signed(), Signed(), Signed(), Signed(), Signed(), Signed(), inst.ReadInt16(), Loop(), Loop());
    }
}
