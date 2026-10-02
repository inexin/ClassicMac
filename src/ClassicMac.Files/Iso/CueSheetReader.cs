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
    /// it. The disc comes out as one file whose data fork holds its data tracks' 2048-byte blocks (through
    /// <see cref="RawCdReader"/> for raw sectors), each at its absolute sector, for the volume readers to open next;
    /// audio tracks read as zeros. The start of the last session (<c>REM SESSION</c>, or the last data track) goes with
    /// the disc, as the Mac's CD driver reports it, so the ISO reader reads that session's descriptors
    /// (docs/formats/disk-images/cd-images.md §3). The referenced files are found among the cue sheet's siblings by
    /// name.
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
            if (!tracks.Any(t => t.Mode != "AUDIO"))
            {
                context.Report(DiagnosticSeverity.Info, "cue.audio-only", "The cue sheet lists audio tracks only.");
                return [];
            }
            var placed = Place(tracks, context.Siblings?.Invoke().ToList() ?? []);
            var origin = placed[0].Lba;

            // The last session's first track: the first after the last REM SESSION line or, in a sheet that does not
            // mark its sessions, the last data track (ClassicMac's reading).
            var lastSession = tracks.Max(t => t.Session);
            var first = lastSession > 1
                ? placed.First(p => p.Track.Session == lastSession)
                : placed.Last(p => p.Track.Mode != "AUDIO");
            var session = first.Lba - origin;

            // The track the descriptors come from must be readable: the last session's first data track, or the first
            // data track when that session has none (its D + 16 holds no descriptor, and sector 16 is read).
            var key = placed.FirstOrDefault(p => p.Track.Session == first.Track.Session && p.Track.Mode != "AUDIO" && p.Lba >= first.Lba)
                ?? placed.First(p => p.Track.Mode != "AUDIO");
            if (SectorSize(key.Track.Mode) is null)
                throw new InvalidDataException($"Track {key.Track.Number} has mode {key.Track.Mode}, which is not read.");
            if (key.File is null)
                throw new InvalidDataException($"\"{key.Track.File}\", which holds track {key.Track.Number}, is not beside the cue sheet.");
            if (key.Blocks <= 0)
                throw new InvalidDataException($"Track {key.Track.Number} starts past the end of \"{key.Track.File}\".");

            var segments = new List<CdDisc.Segment>();
            foreach (var p in placed.Where(p => p.Track.Mode != "AUDIO"))
            {
                if (p.File is null || SectorSize(p.Track.Mode) is not { } size || p.Blocks <= 0)
                {
                    context.Report(DiagnosticSeverity.Info, "cue.track-unread",
                        $"Data track {p.Track.Number} ({p.Track.Mode}, \"{p.Track.File}\") is not read; its sectors read as zeros.");
                    continue;
                }
                var slice = p.File.DataFork.Slice(p.Offset, p.Blocks * size);
                segments.Add(new CdDisc.Segment(p.Lba - origin, p.Blocks, size == 2048 ? slice : RawCdReader.Cooked(slice, size)));
            }
            var disc = new CdDisc(segments, session);

            // A partition map or an HFS volume at the start of a later session is read from there: the CD driver
            // applies the session base to the partition map (docs/formats/file-systems/partition-map.md §4); a bare HFS
            // volume is read the same way (ClassicMac's choice; the driver's handling of one was not traced).
            ForkData data = disc;
            if (session > 0 && SessionHoldsHfs(disc, session))
                data = disc.Slice(session * 2048, disc.Length - session * 2048);
            return [new MacFile { Name = RawCdReader.DiscName(context), DataFork = data }];
        }

        private static bool SessionHoldsHfs(CdDisc disc, long session)
        {
            var start = session * 2048;
            if (start > disc.Length - 2 * 2048) return false;
            var b = disc.Slice(start, 2 * 2048).ToArray();
            var map = (b.AsSpan(0, 2).SequenceEqual("ER"u8) || (b[0] == 0 && b[1] == 0))
                && (b.AsSpan(512, 2).SequenceEqual("PM"u8) || b.AsSpan(2048, 2).SequenceEqual("PM"u8));
            return map || b.AsSpan(1024, 2).SequenceEqual("BD"u8);
        }

        private sealed record Track(int Number, string Mode, string File, int Session, long? Pregap, long Start);

        // A track placed on the disc: its file (null when not beside the sheet), the byte offset of its INDEX 01 in that
        // file, its sectors up to the next track's pregap or the file's end, and its absolute sector.
        private sealed record Placed(Track Track, MacFile? File, long Offset, long Blocks, long Lba);

        // Files are laid end to end in the order the sheet names them, each holding its tracks' sectors (each track's
        // own size) from its pregap (INDEX 00) or INDEX 01. A raw 2352-byte data track whose first sector's header gives
        // a later address than that is placed at the header's address, and the tracks after it move with it: the image
        // left out the sectors between sessions (lead-out, lead-in). ClassicMac's rule; the Mac asks the drive.
        private static List<Placed> Place(List<Track> tracks, List<MacFile> siblings)
        {
            var placed = new List<Placed>();
            long fileBase = 0, shift = 0;
            foreach (var group in tracks.GroupBy(t => t.File))
            {
                var file = siblings.FirstOrDefault(f => string.Equals(f.Name.ToMacRoman(), group.Key, StringComparison.OrdinalIgnoreCase));
                var length = file?.DataFork.Length ?? 0;
                var inFile = group.ToList();
                long frame = 0, bytes = 0;
                var size = Layout(inFile[0].Mode);
                for (var i = 0; i < inFile.Count; i++)
                {
                    var t = inFile[i];
                    var region = Region(t);
                    bytes += Math.Max(0, region - frame) * size;
                    frame = region;
                    size = Layout(t.Mode);
                    var offset = bytes + (t.Start - region) * size;
                    var next = i + 1 < inFile.Count ? bytes + (Region(inFile[i + 1]) - region) * size : length;
                    var blocks = Math.Max(0, (Math.Min(next, length) - offset) / size);
                    var lba = fileBase + t.Start + shift;
                    if (file is not null && blocks > 0 && size == 2352 && t.Mode != "AUDIO"
                        && RawCdReader.HeaderLba(file.DataFork.Slice(offset, 2352).ToArray()) is { } header && header > lba)
                    {
                        shift += header - lba;
                        lba = header;
                    }
                    placed.Add(new Placed(t, file, offset, blocks, lba));
                }
                fileBase += frame + Math.Max(0, length - bytes) / size;
            }
            return placed;
        }

        private static long Region(Track t) => Math.Min(t.Pregap ?? t.Start, t.Start);

        private static List<Track> Parse(string text)
        {
            var tracks = new List<Track>();
            string? file = null;
            var session = 1;
            (int Number, string Mode, int Session)? pending = null;
            long? pregap = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (FileLine().Match(line) is { Success: true } f) file = f.Groups["name"].Value;
                else if (SessionLine().Match(line) is { Success: true } s)
                    session = int.Parse(s.Groups["number"].Value, CultureInfo.InvariantCulture);
                else if (TrackLine().Match(line) is { Success: true } t)
                {
                    pending = (int.Parse(t.Groups["number"].Value, CultureInfo.InvariantCulture), t.Groups["mode"].Value.ToUpperInvariant(), session);
                    pregap = null;
                }
                else if (IndexLine().Match(line) is { Success: true } i && pending is { } p && file is not null)
                {
                    long frames = (int.Parse(i.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                        + int.Parse(i.Groups["s"].Value, CultureInfo.InvariantCulture)) * 75
                        + int.Parse(i.Groups["f"].Value, CultureInfo.InvariantCulture);
                    if (int.Parse(i.Groups["index"].Value, CultureInfo.InvariantCulture) == 0)
                    {
                        pregap = frames;
                        continue;
                    }
                    tracks.Add(new Track(p.Number, p.Mode, file, p.Session, pregap, frames));
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

        // A track's sector size for laying out its file, whether or not it is read.
        private static int Layout(string mode) => SectorSize(mode) ?? mode switch
        {
            "CDG" => 2448,
            "CDI/2336" => 2336,
            _ => 2352,
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

        [GeneratedRegex("""^\s*REM\s+SESSION\s+(?<number>\d+)\s*$""", RegexOptions.IgnoreCase)]
        private static partial Regex SessionLine();

        [GeneratedRegex("""^\s*INDEX\s+(?<index>0*[01])\s+(?<m>\d+):(?<s>\d+):(?<f>\d+)\s*$""", RegexOptions.IgnoreCase)]
        private static partial Regex IndexLine();
    }
}
