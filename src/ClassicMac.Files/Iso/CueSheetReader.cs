using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;

namespace ClassicMac.Files.Iso
{
    /// <summary>
    /// Cue sheets (<c>.cue</c>): the text that lays out a CD image's tracks over one or more <c>.bin</c> files beside
    /// it. The disc's first data track comes out as one file whose data fork holds its 2048-byte blocks (through
    /// <see cref="RawCdReader"/> for raw sectors), for the volume readers to open next; audio tracks are skipped. The
    /// referenced files are found among the cue sheet's siblings by name. Later data tracks (other sessions) are
    /// reported, not read.
    /// </summary>
    public sealed partial class CueSheetReader : IContainerReader
    {
        private const int MaxLength = 64 * 1024;

        /// <summary>The reader.</summary>
        public static CueSheetReader Instance { get; } = new();

        private CueSheetReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "cue sheet";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            if (input.Length is 0 or > MaxLength) return false;
            var text = Text(input);
            return text is not null && FileLine().IsMatch(text) && TrackLine().IsMatch(text);
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var text = (input.Length <= MaxLength ? Text(input) : null) ?? throw new InvalidDataException("Not a cue sheet.");
            var tracks = Parse(text);
            var data = tracks.Where(t => t.Mode != "AUDIO").ToList();
            if (data.Count == 0)
            {
                context.Report(DiagnosticSeverity.Info, "cue.audio-only", "The cue sheet lists audio tracks only.");
                return [];
            }
            if (data.Count > 1)
            {
                context.Report(DiagnosticSeverity.Info, "cue.more-tracks",
                    $"The disc has {data.Count} data tracks; only track {data[0].Number} is read.");
            }

            var track = data[0];
            var sectorSize = SectorSize(track.Mode);
            if (sectorSize is null)
                throw new InvalidDataException($"Track {track.Number} has mode {track.Mode}, which is not read.");
            var file = context.Siblings?.Invoke()
                .FirstOrDefault(f => string.Equals(f.Name.ToMacRoman(), track.File, StringComparison.OrdinalIgnoreCase));
            if (file is null)
                throw new InvalidDataException($"\"{track.File}\", which holds track {track.Number}, is not beside the cue sheet.");

            // From the track's INDEX 01 to the next track in the same file, or the file's end.
            var start = track.Start * sectorSize.Value;
            var next = tracks.FirstOrDefault(t => t.File == track.File && t.Start > track.Start);
            var end = next is null ? file.DataFork.Length : next.Start * sectorSize.Value;
            if (start >= file.DataFork.Length)
                throw new InvalidDataException($"Track {track.Number} starts past the end of \"{track.File}\".");
            end = Math.Min(end, file.DataFork.Length);
            end = start + (end - start) / sectorSize.Value * sectorSize.Value;
            var slice = file.DataFork.Slice(start, end - start);
            var disc = sectorSize == 2048 ? slice : RawCdReader.Cooked(slice, sectorSize.Value);
            return [new MacFile { Name = RawCdReader.DiscName(context), DataFork = disc }];
        }

        private sealed record Track(int Number, string Mode, string File, long Start);

        private static List<Track> Parse(string text)
        {
            var tracks = new List<Track>();
            string? file = null;
            (int Number, string Mode)? pending = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (FileLine().Match(line) is { Success: true } f) file = f.Groups["name"].Value;
                else if (TrackLine().Match(line) is { Success: true } t)
                    pending = (int.Parse(t.Groups["number"].Value, CultureInfo.InvariantCulture), t.Groups["mode"].Value.ToUpperInvariant());
                else if (IndexLine().Match(line) is { Success: true } i && pending is { } p && file is not null)
                {
                    long frames = (int.Parse(i.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                        + int.Parse(i.Groups["s"].Value, CultureInfo.InvariantCulture)) * 75
                        + int.Parse(i.Groups["f"].Value, CultureInfo.InvariantCulture);
                    tracks.Add(new Track(p.Number, p.Mode, file, frames));
                    pending = null;
                }
            }
            return tracks;
        }

        private static int? SectorSize(string mode) => mode switch
        {
            "MODE1/2048" or "MODE2/2048" => 2048,
            "MODE1/2352" or "MODE2/2352" => 2352,
            "MODE2/2336" => 2336,
            _ => null,
        };

        private static string? Text(ForkData input)
        {
            var bytes = input.ToArray(MaxLength);
            if (bytes.Any(b => b == 0)) return null;
            var text = Encoding.Latin1.GetString(bytes).TrimStart('﻿', 'ï', '»', '¿');
            return text.Replace("\r", "\n", StringComparison.Ordinal);
        }

        [GeneratedRegex("""^\s*FILE\s+(?:"(?<name>[^"]+)"|(?<name>\S+))\s+\w+\s*$""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
        private static partial Regex FileLine();

        [GeneratedRegex("""^\s*TRACK\s+(?<number>\d+)\s+(?<mode>\S+)\s*$""", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
        private static partial Regex TrackLine();

        [GeneratedRegex("""^\s*INDEX\s+0*1\s+(?<m>\d+):(?<s>\d+):(?<f>\d+)\s*$""", RegexOptions.IgnoreCase)]
        private static partial Regex IndexLine();
    }
}
