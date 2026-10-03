using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.App.ViewModels
{
    /// <summary>What the preview tab shows.</summary>
    public enum PreviewKind
    {
        None,
        Loading,
        Image,
        Text,
        Json,
        Sound,
        Document,
        Dialog,
        Menu,
        Folder,

        /// <summary>A sound that cannot be decoded: what it is, why, and the way to its bytes (boards/sound.md).</summary>
        SoundError,

        /// <summary>A font family: a sample from its strikes and its tables (boards/font-family.md).</summary>
        FontFamily,
    }

    /// <summary>One of a sound's detail chips: "Rate" "22,254.545 Hz".</summary>
    public sealed record SoundFact(string Label, string Value);

    /// <summary>One decoded image: PNG bytes, its size, and a caption (a list item's number, a cursor's hotspot).</summary>
    /// <param name="Title">The card's title: the resource type in quotes ("'icl8'", "'SICN' #3").</param>
    /// <param name="Detail">The card's detail: "32×32 · 8-bit".</param>
    public sealed record PreviewImage(byte[] Png, int Width, int Height, string? Caption, string? Title = null, string? Detail = null)
    {
        /// <summary>The card's second line: the detail, else the caption.</summary>
        public string? CardDetail => Detail ?? Caption;
    }

    /// <summary>
    /// The preview of a resource or file, made by the same decoders as <c>extract</c>: images (pictures, icons, cursors,
    /// patterns), text (strings, styled text), JSON (version resources, lone style runs), sound (<c>snd </c>, drawn and
    /// played) or a document (DOCMaker, SimpleText with pictures); a folder as the Finder's window shows it; otherwise a
    /// note to look at the hex view.
    /// </summary>
    public sealed class PreviewViewModel
    {
        private const int MaxTextFile = 4 * 1024 * 1024, MaxPictureFile = 64 * 1024 * 1024;

        private PreviewViewModel(PreviewKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public static PreviewViewModel None { get; } = new(PreviewKind.None, "");

        public static PreviewViewModel Loading { get; } = new(PreviewKind.Loading, "Decoding…");

        public PreviewKind Kind { get; }

        public string Message { get; }

        public IReadOnlyList<PreviewImage> Images { get; private init; } = [];

        /// <summary>An icon family's 1-bit masks, shown with "Show masks".</summary>
        public IReadOnlyList<PreviewImage> Masks { get; private init; } = [];

        /// <summary>An icon's suite as the Finder draws it: the five states, then the seven labels, each captioned.</summary>
        public IReadOnlyList<PreviewImage> FinderStates { get; private init; } = [];

        /// <summary>Whether the images are an icon family's members (the summary says "members").</summary>
        public bool IsFamily { get; private init; }

        public StyledText? Styled { get; private init; }

        public string Text { get; private init; } = "";

        /// <summary>A sound's samples, for the waveform and playback.</summary>
        public DecodedSound? Sound { get; private init; }

        /// <summary>A sound's rate, channels, sample size, length, format and loop.</summary>
        public string SoundDetails { get; private init; } = "";

        /// <summary>The same facts as chips: Rate, Channels, Sample, Length, Loop, Base note, Format.</summary>
        public IReadOnlyList<SoundFact> SoundFacts { get; private init; } = [];

        /// <summary>A sound's loop in seconds (for playback), or null when it has none.</summary>
        public SoundLoop? SoundLoop { get; private init; }

        /// <summary>A sound's loop in frames (for the waveform's band), or null.</summary>
        public (int Start, int End)? SoundLoopFrames { get; private init; }

        /// <summary>The error state's title: "No preview for 'snd ' 8192".</summary>
        public string ErrorTitle { get; private init; } = "";

        /// <summary>The error state's reason: "Unknown sound format 3 · sound.unknown-format".</summary>
        public string ErrorDetail { get; private init; } = "";

        public bool IsSoundError => Kind == PreviewKind.SoundError;

        /// <summary>The waveform card's title: "Channel 1", or "Channels 1–2" for a stereo sound.</summary>
        public string SoundLanesTitle => Sound is { Channels: > 1 } s ? $"Channels 1–{s.Channels}" : "Channel 1";

        /// <summary>A DOCMaker or SimpleText document, a chapter at a time.</summary>
        public DocumentPreview? Document { get; private init; }

        /// <summary>A dialog, alert or item list, drawn as Mac OS 9's Platinum appearance draws it.</summary>
        public DialogPreview? Dialog { get; private init; }

        /// <summary>A menu, drawn pulled down.</summary>
        public MenuResource? Menu { get; private init; }

        public bool HasPreview => Kind is PreviewKind.Image or PreviewKind.Text or PreviewKind.Json or PreviewKind.Sound or PreviewKind.Document
            or PreviewKind.Dialog or PreviewKind.Menu or PreviewKind.Folder or PreviewKind.SoundError or PreviewKind.FontFamily;

        /// <summary>A font family's sample and tables (P6); its JSON is <see cref="Text"/>.</summary>
        public FontFamilyPreview? FontFamily { get; private init; }

        public bool IsFontFamily => Kind == PreviewKind.FontFamily;

        public bool IsDocument => Kind == PreviewKind.Document;

        public bool IsDialog => Kind == PreviewKind.Dialog;

        public bool IsMenu => Kind == PreviewKind.Menu;

        /// <summary>Whether the zoom applies (images, folders, dialogs, menus and font samples).</summary>
        public bool IsZoomable => Kind is PreviewKind.Image or PreviewKind.Folder or PreviewKind.Dialog or PreviewKind.Menu or PreviewKind.FontFamily;

        public bool IsSound => Kind == PreviewKind.Sound;

        /// <summary>Whether the preview is images: decoded ones, or a folder's window.</summary>
        public bool IsImage => Kind is PreviewKind.Image or PreviewKind.Folder;

        public bool IsStyledText => Kind == PreviewKind.Text && Styled is not null;

        public bool IsPlainText => Kind is PreviewKind.Text && Styled is null;

        /// <summary>Whether the preview is a decoder's JSON: shown as properties, or as the JSON itself.</summary>
        public bool IsJson => Kind == PreviewKind.Json;

        /// <summary>The JSON read as labelled values in cards (P2); empty when it is not an object.</summary>
        public IReadOnlyList<PropertyCard> PropertyCards { get; private init; } = [];

        public bool HasProperties => PropertyCards.Count > 0;

        public bool IsMessage => Kind is PreviewKind.None or PreviewKind.Loading;

        private static PreviewViewModel Nothing(string what) => new(PreviewKind.None, $"No preview for {what}; see Hex.");

        /// <summary>Makes the preview of <paramref name="node"/> off the UI thread; decoding problems go to <paramref name="diagnostics"/>.</summary>
        public static Task<PreviewViewModel> BuildAsync(NodeViewModel? node, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics, CancellationToken cancellation, DialogSources? dialogSources = null) =>
            Task.Run(() => Build(node, options, readOptions, diagnostics, dialogSources), cancellation);

        internal static PreviewViewModel Build(NodeViewModel? node, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics,
            DialogSources? dialogSources = null) =>
            node switch
            {
                ResourceNode resource => ForResource(resource.Resource, resource.Fork, options, readOptions, diagnostics, dialogSources),
                FileNode file => ForFile(file.File, options, readOptions, diagnostics),
                ContainerFileNode container => ForFile(container.File, options, readOptions, diagnostics) is { Kind: not PreviewKind.None } shown
                    ? shown : ForFolder(container, options, readOptions, dialogSources),
                InputNode input when input.Root.Children.Count == 0 => ForFile(input.Root.File, options, readOptions, diagnostics),
                InputNode or FolderNode => ForFolder(node, options, readOptions, dialogSources),
                NoNameGroupNode group => ForFolder(group.Parent!, options, readOptions, dialogSources),
                _ => None,
            };

        /// <summary>The preview of resource data not in a file yet (what Import would make); decoding problems are dropped.</summary>
        internal static PreviewViewModel ForData(string type, byte[] data, DecodeOptions options, ReadOptions readOptions)
        {
            var resource = new Resource(FourCC.FromString(type), 128, data);
            var fork = new ResourceFork();
            fork.Add(resource);
            return ForResource(resource, fork, options, readOptions, new List<Diagnostic>());
        }

        // A folder, volume root or container's contents as the Finder's icon view of its window.
        private static PreviewViewModel ForFolder(NodeViewModel node, DecodeOptions options, ReadOptions readOptions, DialogSources? sources) =>
            FolderPreviews.Build(node, options, readOptions, sources) is { } image
                ? new PreviewViewModel(PreviewKind.Folder, "") { Images = [image] }
                : None;

        private static PreviewViewModel ForResource(Resource resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics, DialogSources? dialogSources = null)
        {
            var type = resource.Type.ToString();
            var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
            if (type == "TEXT")
            {
                var styl = fork.Find(FourCC.FromString("styl"), resource.Id) is { } s
                    ? ResourceDecompression.Default.GetData(s, fork, readOptions, diagnostics)
                    : ReadOnlyMemory<byte>.Empty;
                return StyledPreview(StyledText.Read(data.Span, styl, options));
            }
            if (type == "snd ")
            {
                var found = new List<Diagnostic>();
                var read = SoundResource.Read(data, found, resource.ToString());
                foreach (var d in found)
                {
                    diagnostics.Add(d);
                }

                var what = $"'{resource.Type}' {resource.Id}";
                if (read is { Sound: { } sampled })
                {
                    return SoundPreview(sampled, what);
                }

                if (read is null || found.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var problem = found.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                    return SoundError(what, problem is null ? "The sound cannot be read" : $"{Reason(problem.Message, resource)} · {problem.Code}");
                }
            }

            if (InterfacePreviews.Dialog(resource, data, fork, options, readOptions, diagnostics, dialogSources) is { } dialog)
            {
                return new PreviewViewModel(PreviewKind.Dialog, "") { Dialog = dialog };
            }

            if (type is "clut" or "pltt")
            {
                var entries = type == "clut" ? Resources.Decoders.Colors.Palettes.ReadColorTable(data, out _, out _, out _)
                    : Resources.Decoders.Colors.Palettes.ReadPalette(data, out _);
                return entries.Count == 0 ? Nothing($"'{type}'") : Swatches(entries, type);
            }
            if (type == "MENU")
            {
                return new PreviewViewModel(PreviewKind.Menu, "") { Menu = InterfaceResources.ReadMenu(data, options, diagnostics, resource.ToString()) };
            }

            var decoder = ResourceDecoders.Create(options).FirstOrDefault(d => d.CanDecode(resource.Type));
            if (decoder is null)
            {
                return Nothing($"'{type}'");
            }

            var files = decoder.Decode(new DecodeInput(resource, data, fork, readOptions, diagnostics));
            var preview = FromFiles(files, $"'{type}'", type);
            // A font family: its sample and tables, the JSON a click away.
            if (type == "FOND" && preview.IsJson && FontFamilyPreview.Create(resource, data, fork, readOptions) is { } font)
            {
                return new PreviewViewModel(PreviewKind.FontFamily, "") { Text = preview.Text, FontFamily = font };
            }

            // Icons: every member of their ID's family, its masks, and the suite as the Finder draws it.
            if (preview.Kind == PreviewKind.Image && FinderIcons.Applies(resource))
            {
                preview = new PreviewViewModel(PreviewKind.Image, "")
                {
                    Images = type == "icns" ? preview.Images : Members(resource, fork, options, readOptions, diagnostics),
                    Masks = FinderIcons.Masks(resource, fork, readOptions, diagnostics),
                    FinderStates = FinderIcons.FinderStates(resource, fork, options, readOptions, diagnostics),
                    IsFamily = true,
                };
            }

            return preview;
        }

        // A picture file (type PICT, after its 512-byte header), a document (DOCMaker, or SimpleText with pictures), or a
        // SimpleText document's styled text (TEXT, styled by its styl 128).
        private static PreviewViewModel ForFile(MacFile file, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            var type = file.FinderInfo.Type.ToString();
            if (DocumentOf(file, options, readOptions, diagnostics) is { } document)
            {
                return new PreviewViewModel(PreviewKind.Document, "") { Document = DocumentPreview.Create(document, options, diagnostics) };
            }

            if (type == "PICT" && file.DataFork.Length is > 512 + 10 and <= MaxPictureFile)
            {
                var picture = new Resource(FourCC.FromString("PICT"), 0, file.DataFork.Slice(512, file.DataFork.Length - 512).ToArray());
                var fork = new ResourceFork();
                fork.Add(picture);
                var decoder = ResourceDecoders.Create(options).First(d => d.CanDecode(picture.Type));
                return FromFiles(decoder.Decode(new DecodeInput(picture, picture.GetData(), fork, readOptions, diagnostics)), "this picture", "PICT");
            }
            if (type == "TEXT" && file.DataFork.Length is > 0 and <= MaxTextFile)
            {
                var styl = ReadOnlyMemory<byte>.Empty;
                if (MacFileResources.Read(file, readOptions).Fork is { } fork && fork.Find(FourCC.FromString("styl"), 128) is { } s)
                {
                    styl = ResourceDecompression.Default.GetData(s, fork, readOptions, diagnostics);
                }

                return StyledPreview(StyledText.Read(file.DataFork.ToArray(), styl, options));
            }
            return None;
        }

        // A Word document, a DOCMaker document, or a SimpleText document that has pictures (one without is shown as styled text).
        private static StyledDocument? DocumentOf(MacFile file, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            // A Word document is its data fork.
            if (StyledDocuments.IsWord(file.FinderInfo.Type))
            {
                return file.DataFork.Length <= MaxPictureFile
                    ? StyledDocuments.Read(file.DataFork.ToArray(), null, file.FinderInfo.Type, file.Name.ToString(), options, readOptions, diagnostics)
                    : null;
            }

            if (MacFileResources.Read(file, readOptions).Fork is not { } fork)
            {
                return null;
            }

            var isText = file.FinderInfo.Type == FourCC.FromString("TEXT") || file.FinderInfo.Type == FourCC.FromString("ttro");
            if (isText && file.DataFork.Length > MaxTextFile)
            {
                return null;
            }

            var text = isText ? file.DataFork.ToArray() : ReadOnlyMemory<byte>.Empty;
            var document = StyledDocuments.Read(text, fork, file.FinderInfo.Type, file.Name.ToString(), options, readOptions, diagnostics);
            return document is { Kind: DocumentKind.DocMaker } || document?.Chapters.Any(c => c.Pictures.Count > 0) == true ? document : null;
        }

        // A palette as a grid of 16 × 16-pixel swatches, 16 to a row, in entry order, captioned with the count.
        private static PreviewViewModel Swatches(IReadOnlyList<Resources.Decoders.Colors.PaletteEntry> entries, string type)
        {
            const int Cell = 16, Columns = 16;
            var width = Math.Min(entries.Count, Columns) * Cell;
            var height = (entries.Count + Columns - 1) / Columns * Cell;
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < entries.Count; i++)
            {
                var (x0, y0) = (i % Columns * Cell, i / Columns * Cell);
                for (var y = y0; y < y0 + Cell; y++)
                {
                    for (var x = x0; x < x0 + Cell; x++)
                    {
                        var at = (y * width + x) * 4;
                        var edge = x == x0 + Cell - 1 || y == y0 + Cell - 1; // a white gap between swatches
                        rgba[at] = edge ? (byte)255 : (byte)(entries[i].Red >> 8);
                        rgba[at + 1] = edge ? (byte)255 : (byte)(entries[i].Green >> 8);
                        rgba[at + 2] = edge ? (byte)255 : (byte)(entries[i].Blue >> 8);
                        rgba[at + 3] = 255;
                    }
                }
            }
            var png = Resources.Decoders.Images.PngEncoder.Instance.Encode(width, height, rgba);
            return new PreviewViewModel(PreviewKind.Image, "") { Images = [new PreviewImage(png, width, height, $"{entries.Count} colours", $"'{type}'")] };
        }

        // A diagnostic's message without the resource it names (the card's title names it): "Format 3 is neither 1 nor 2."
        private static string Reason(string message, Resource resource)
        {
            var prefix = resource + ": ";
            var reason = message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
            return reason.Length > 0 ? char.ToUpperInvariant(reason[0]) + reason[1..] : reason;
        }

        private static PreviewViewModel SoundError(string what, string detail) =>
            new(PreviewKind.SoundError, "") { ErrorTitle = $"No preview for {what}", ErrorDetail = detail };

        private static PreviewViewModel SoundPreview(SampledSound sampled, string what)
        {
            var compressed = sampled.Kind == SoundHeaderKind.Compressed;
            var bits = compressed ? $"'{sampled.Format}'" : $"{sampled.SampleSize}-bit";
            if (SoundSamples.Decode(sampled) is not { } sound)
            {
                return SoundError(what, compressed
                    ? $"Compressed as '{sampled.Format}', which ClassicMac does not decode"
                    : $"{sampled.SampleSize}-bit samples, which ClassicMac does not decode");
            }

            var channels = sound.Channels == 1 ? "mono" : sound.Channels == 2 ? "stereo" : $"{sound.Channels} channels";
            var hasLoop = sampled.LoopEnd > sampled.LoopStart && sampled.LoopEnd - sampled.LoopStart > 2;
            var loop = hasLoop ? $", loop {sampled.LoopStart}–{sampled.LoopEnd}" : "";
            var note = sampled.BaseNote is not (0 or 60) ? $", base note {sampled.BaseNote}" : "";
            var facts = new List<SoundFact>
            {
                new("Rate", string.Create(CultureInfo.InvariantCulture, $"{sound.SampleRate:N3} Hz")),
                new("Channels", channels),
                new("Sample", bits),
                new("Length", string.Create(CultureInfo.InvariantCulture, $"{sound.Duration:0.00} s · {sound.Frames:N0} frames")),
            };
            (int Start, int End)? loopFrames = null;
            if (hasLoop)
            {
                facts.Add(new("Loop", string.Create(CultureInfo.InvariantCulture, $"{sampled.LoopStart:N0}–{sampled.LoopEnd:N0}")));
                loopFrames = ((int)Math.Min(sampled.LoopStart, (uint)sound.Frames), (int)Math.Min(sampled.LoopEnd, (uint)sound.Frames));
            }

            if (sampled.BaseNote is not (0 or 60))
            {
                facts.Add(new("Base note", sampled.BaseNote.ToString(CultureInfo.InvariantCulture)));
            }

            facts.Add(new("Format", compressed ? $"sampled, compressed ('{sampled.Format}')" : "sampled, uncompressed"));
            return new PreviewViewModel(PreviewKind.Sound, "")
            {
                Sound = sound,
                SoundDetails = string.Create(CultureInfo.InvariantCulture,
                    $"{sound.SampleRate:0.###} Hz, {channels}, {bits}, {sound.Duration:0.00} s ({sound.Frames:N0} frames){loop}{note}"),
                SoundFacts = facts,
                SoundLoopFrames = loopFrames,
                SoundLoop = loopFrames is { } frames && sound.SampleRate > 0
                    ? new SoundLoop(frames.Start / sound.SampleRate, frames.End / sound.SampleRate)
                    : null,
            };
        }

        private static PreviewViewModel StyledPreview(StyledText styled) => new(PreviewKind.Text, "") { Styled = styled, Text = styled.Text.Replace('\r', '\n') };

        // An icon's family: the first image of each member of its ID, as their decoders draw them.
        private static IReadOnlyList<PreviewImage> Members(Resource resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics)
        {
            var decoders = ResourceDecoders.Create(options);
            var members = new List<PreviewImage>();
            foreach (var type in FinderIcons.MemberTypes)
            {
                var fourCC = FourCC.FromString(type);
                if (fork.Find(fourCC, resource.Id) is not { } member || decoders.FirstOrDefault(d => d.CanDecode(fourCC)) is not { } decoder)
                {
                    continue;
                }

                var data = ResourceDecompression.Default.GetData(member, fork, readOptions, diagnostics);
                if (FromFiles(decoder.Decode(new DecodeInput(member, data, fork, readOptions, diagnostics)), $"'{type}'", type).Images.FirstOrDefault() is { } image)
                {
                    members.Add(image);
                }
            }

            return members;
        }

        // How many bits a pixel has in an image type (empty for types whose depth varies or is not a pixel depth).
        private static string Depth(string? type) => type switch
        {
            "ICN#" or "ics#" or "icm#" or "ICON" or "SICN" or "CURS" or "PAT " or "PAT#" => " · 1-bit",
            "icl4" or "ics4" or "icm4" => " · 4-bit",
            "icl8" or "ics8" or "icm8" => " · 8-bit",
            _ => "",
        };

        private static PreviewViewModel FromFiles(IReadOnlyList<DecodedFile> files, string what, string? type = null)
        {
            var images = files.Where(f => f.Extension.EndsWith(".png", StringComparison.Ordinal)).ToList();
            if (images.Count > 0)
            {
                var caption = files.FirstOrDefault(f => f.Extension == ".json") is { } json ? CursorCaption(json) : null;
                return new PreviewViewModel(PreviewKind.Image, "")
                {
                    Images = images.Select(f =>
                    {
                        var png = f.Content.ToArray();
                        var (width, height) = PngSize(png);
                        var listed = f.Extension.Length > 4;
                        var number = listed ? $"#{f.Extension[1..^4]}" : caption;
                        var title = type is null ? null : listed ? $"'{type}' {number}" : $"'{type}'";
                        var detail = string.Create(CultureInfo.InvariantCulture, $"{width}×{height}{Depth(type)}")
                            + (!listed && caption is not null ? " · " + caption : "");
                        return new PreviewImage(png, width, height, number, title, detail);
                    }).ToList(),
                };
            }
            // Code: its listing.
            if (files.FirstOrDefault(f => f.Extension == ".s") is { } listing)
            {
                return new PreviewViewModel(PreviewKind.Text, "") { Text = Encoding.UTF8.GetString(listing.Content.Span).TrimEnd('\n') };
            }

            if (files.FirstOrDefault(f => f.Extension == ".txt") is { } text)
            {
                return new PreviewViewModel(PreviewKind.Text, "") { Text = Encoding.UTF8.GetString(text.Content.Span) };
            }

            if (files.FirstOrDefault(f => f.Extension == ".json") is { } other)
            {
                using var document = JsonDocument.Parse(other.Content);
                // A string list reads best as numbered lines.
                if (document.RootElement.TryGetProperty("strings", out var strings))
                {
                    var lines = strings.EnumerateArray().Select((s, i) => $"{i + 1,4}  {s.GetString()}");
                    return new PreviewViewModel(PreviewKind.Text, "") { Text = string.Join('\n', lines) };
                }
                var json = Encoding.UTF8.GetString(other.Content.Span).TrimEnd();
                return new PreviewViewModel(PreviewKind.Json, "")
                {
                    Text = json,
                    PropertyCards = PropertyView.FromJson(json, type is null ? "Properties" : InspectorHeader.TypeName(type)),
                };
            }
            return Nothing(what);
        }

        private static string? CursorCaption(DecodedFile json)
        {
            using var document = JsonDocument.Parse(json.Content);
            return document.RootElement.TryGetProperty("hotspot", out var hotspot)
                ? string.Create(CultureInfo.InvariantCulture, $"hotspot {hotspot.GetProperty("h").GetInt32()}, {hotspot.GetProperty("v").GetInt32()}")
                : null;
        }

        // Width and height from the PNG's IHDR (which directly follows the signature).
        private static (int Width, int Height) PngSize(ReadOnlyMemory<byte> png)
        {
            if (png.Length < 24)
            {
                return (0, 0);
            }

            var reader = new BigEndianReader(png);
            return (reader.ReadInt32At(16), reader.ReadInt32At(20));
        }
    }
}
