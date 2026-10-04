using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Sound;

// AIFF and AIFF-C files (docs/formats/documents/aiff.md §5) to sound.wav and sound.json: the samples through the snd
// codecs, and what WAV cannot hold. Only files of type AIFF or AIFC are read.
internal sealed class AiffConverter : IDocumentConverter
{
    public const string ConverterName = "sound.aiff";

    public string Name => ConverterName;

    public int Version => 1;

    /// <summary>Whether files of <paramref name="type"/> are AIFF or AIFF-C sounds, whose data fork holds the sound.</summary>
    public static bool IsSound(FourCC type) => type == FourCC.FromString("AIFF") || type == FourCC.FromString("AIFC");

    public IReadOnlyList<DocumentFile> Convert(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsSound(input.Type))
        {
            return [];
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = input.ReadDataFork();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "aiff.unreadable", $"The sound (its data fork) cannot be read: {e.Message}"));
            return [];
        }

        if (AiffFile.Read(data, input.Diagnostics, input.Title) is not { } file)
        {
            return [];
        }

        var json = new DocumentFile("sound.json", Json(file));
        if (AiffFile.ToWav(file) is not { } wav)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "sound.codec",
                $"{input.Title}: the samples are in '{file.Compression}', which is not decoded; only the JSON is written."));
            return [json];
        }

        return [new DocumentFile("sound.wav", wav), json];
    }

    private static byte[] Json(AiffFile file) => MacText.Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("form", file.FormType.ToString());
        if (file.IsCompressed)
        {
            w.WriteString("compressionType", file.Compression.ToString());
            w.WriteString("compressionName", file.CompressionName);
        }

        w.WriteNumber("channels", file.Sound.Channels);
        w.WriteNumber("sampleSize", file.SampleSize);
        w.WriteNumber("frames", file.Sound.Frames);
        w.WriteNumber("sampleRate", Math.Round(file.SampleRate, 6));
        w.WriteString("sampleFormat", file.Sound.Format.ToString());
        foreach (var (key, text) in new[] { ("name", file.Name), ("author", file.Author), ("copyright", file.Copyright), ("annotation", file.Annotation) })
        {
            if (text is not null)
            {
                w.WriteString(key, text);
            }
        }

        if (file.Markers.Count > 0)
        {
            w.WriteStartArray("markers");
            foreach (var m in file.Markers)
            {
                w.WriteStartObject();
                w.WriteNumber("id", m.Id);
                w.WriteNumber("position", m.Position);
                w.WriteString("name", m.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (file.Instrument is { } i)
        {
            w.WriteStartObject("instrument");
            w.WriteNumber("baseNote", i.BaseNote);
            w.WriteNumber("detune", i.Detune);
            w.WriteNumber("lowNote", i.LowNote);
            w.WriteNumber("highNote", i.HighNote);
            w.WriteNumber("lowVelocity", i.LowVelocity);
            w.WriteNumber("highVelocity", i.HighVelocity);
            w.WriteNumber("gain", i.Gain);
            foreach (var (key, loop) in new[] { ("sustainLoop", i.SustainLoop), ("releaseLoop", i.ReleaseLoop) })
            {
                w.WriteStartObject(key);
                w.WriteNumber("playMode", loop.PlayMode);
                w.WriteNumber("begin", loop.Begin);
                w.WriteNumber("end", loop.End);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    });
}
