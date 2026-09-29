using System;
using System.Buffers.Binary;
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
    }

    /// <summary>One decoded image: PNG bytes, its size, and a caption (a list item's number, a cursor's hotspot).</summary>
    public sealed record PreviewImage(byte[] Png, int Width, int Height, string? Caption);

    /// <summary>
    /// The preview of a resource or file, made by the same decoders as <c>extract</c>: images (pictures, icons, cursors,
    /// patterns), text (strings, styled text), JSON (version resources, lone style runs), sound (<c>snd </c>, drawn and
    /// played) or a document (DOCMaker, SimpleText with pictures); otherwise a note to look at the hex view.
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

        public StyledText? Styled { get; private init; }

        public string Text { get; private init; } = "";

        /// <summary>A sound's samples, for the waveform and playback.</summary>
        public DecodedSound? Sound { get; private init; }

        /// <summary>A sound's rate, channels, sample size, length, format and loop.</summary>
        public string SoundDetails { get; private init; } = "";

        /// <summary>A DOCMaker or SimpleText document, a chapter at a time.</summary>
        public DocumentPreview? Document { get; private init; }

        /// <summary>A dialog, alert or item list, drawn by the viewer.</summary>
        public DialogPreview? Dialog { get; private init; }

        /// <summary>A menu, drawn pulled down.</summary>
        public MenuResource? Menu { get; private init; }

        public bool HasPreview => Kind is PreviewKind.Image or PreviewKind.Text or PreviewKind.Json or PreviewKind.Sound or PreviewKind.Document
            or PreviewKind.Dialog or PreviewKind.Menu;

        public bool IsDocument => Kind == PreviewKind.Document;

        public bool IsDialog => Kind == PreviewKind.Dialog;

        public bool IsMenu => Kind == PreviewKind.Menu;

        /// <summary>Whether the zoom applies (images, dialogs and menus).</summary>
        public bool IsZoomable => Kind is PreviewKind.Image or PreviewKind.Dialog or PreviewKind.Menu;

        public bool IsSound => Kind == PreviewKind.Sound;

        public bool IsImage => Kind == PreviewKind.Image;

        public bool IsStyledText => Kind == PreviewKind.Text && Styled is not null;

        public bool IsPlainText => Kind is PreviewKind.Text or PreviewKind.Json && Styled is null;

        public bool IsMessage => Kind is PreviewKind.None or PreviewKind.Loading;

        private static PreviewViewModel Nothing(string what) => new(PreviewKind.None, $"No preview for {what}; see Hex.");

        /// <summary>Makes the preview of <paramref name="node"/> off the UI thread; decoding problems go to <paramref name="diagnostics"/>.</summary>
        public static Task<PreviewViewModel> BuildAsync(NodeViewModel? node, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics, CancellationToken cancellation) =>
            Task.Run(() => Build(node, options, readOptions, diagnostics), cancellation);

        internal static PreviewViewModel Build(NodeViewModel? node, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics) =>
            node switch
            {
                ResourceNode resource => ForResource(resource.Resource, resource.Fork, options, readOptions, diagnostics),
                FileNode file => ForFile(file.File, options, readOptions, diagnostics),
                ContainerFileNode container => ForFile(container.File, options, readOptions, diagnostics),
                InputNode input when input.Root.Children.Count == 0 => ForFile(input.Root.File, options, readOptions, diagnostics),
                _ => None,
            };

        private static PreviewViewModel ForResource(Resource resource, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics)
        {
            var type = resource.Type.ToString();
            var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
            if (type == "TEXT")
            {
                var styl = fork.Find(FourCC.FromString("styl"), resource.Id) is { } s
                    ? ResourceDecompression.Default.GetData(s, fork, readOptions, diagnostics)
                    : ReadOnlyMemory<byte>.Empty;
                return StyledPreview(StyledText.Read(data.Span, styl.Span, options));
            }
            if (type == "snd " && SoundResource.Read(data, diagnostics, resource.ToString()) is { Sound: { } sampled })
                return SoundPreview(sampled);
            if (InterfacePreviews.Dialog(resource, data, fork, options, readOptions, diagnostics) is { } dialog)
                return new PreviewViewModel(PreviewKind.Dialog, "") { Dialog = dialog };
            if (type is "clut" or "pltt")
            {
                var entries = type == "clut" ? Resources.Decoders.Colors.Palettes.ReadColorTable(data.Span, out _, out _, out _)
                    : Resources.Decoders.Colors.Palettes.ReadPalette(data.Span, out _);
                return entries.Count == 0 ? Nothing($"'{type}'") : Swatches(entries);
            }
            if (type == "MENU")
                return new PreviewViewModel(PreviewKind.Menu, "") { Menu = InterfaceResources.ReadMenu(data.Span, options, diagnostics, resource.ToString()) };
            var decoder = ResourceDecoders.Create(options).FirstOrDefault(d => d.CanDecode(resource.Type));
            if (decoder is null) return Nothing($"'{type}'");
            var files = decoder.Decode(new DecodeInput(resource, data, fork, readOptions, diagnostics));
            var preview = FromFiles(files, $"'{type}'");
            // Icons: the suite of their ID as the Finder draws it, after the member itself.
            if (preview.Kind == PreviewKind.Image && FinderIcons.Applies(resource))
                preview = new PreviewViewModel(PreviewKind.Image, "") { Images = [.. preview.Images, .. FinderIcons.Draw(resource, fork, options, readOptions, diagnostics)] };
            return preview;
        }

        // A picture file (type PICT, after its 512-byte header), a document (DOCMaker, or SimpleText with pictures), or a
        // SimpleText document's styled text (TEXT, styled by its styl 128).
        private static PreviewViewModel ForFile(MacFile file, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            var type = file.FinderInfo.Type.ToString();
            if (DocumentOf(file, options, readOptions, diagnostics) is { } document)
                return new PreviewViewModel(PreviewKind.Document, "") { Document = DocumentPreview.Create(document, options, diagnostics) };
            if (type == "PICT" && file.DataFork.Length is > 512 + 10 and <= MaxPictureFile)
            {
                var picture = new Resource(FourCC.FromString("PICT"), 0, file.DataFork.Slice(512, file.DataFork.Length - 512).ToArray());
                var fork = new ResourceFork();
                fork.Add(picture);
                var decoder = ResourceDecoders.Create(options).First(d => d.CanDecode(picture.Type));
                return FromFiles(decoder.Decode(new DecodeInput(picture, picture.GetData(), fork, readOptions, diagnostics)), "this picture");
            }
            if (type == "TEXT" && file.DataFork.Length is > 0 and <= MaxTextFile)
            {
                var styl = ReadOnlyMemory<byte>.Empty;
                if (MacFileResources.Read(file, readOptions).Fork is { } fork && fork.Find(FourCC.FromString("styl"), 128) is { } s)
                    styl = ResourceDecompression.Default.GetData(s, fork, readOptions, diagnostics);
                return StyledPreview(StyledText.Read(file.DataFork.ToArray(), styl.Span, options));
            }
            return None;
        }

        // A DOCMaker document, or a SimpleText document that has pictures (one without is shown as styled text).
        private static StyledDocument? DocumentOf(MacFile file, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            if (MacFileResources.Read(file, readOptions).Fork is not { } fork) return null;
            var isText = file.FinderInfo.Type == FourCC.FromString("TEXT") || file.FinderInfo.Type == FourCC.FromString("ttro");
            if (isText && file.DataFork.Length > MaxTextFile) return null;
            var text = isText ? file.DataFork.ToArray() : ReadOnlyMemory<byte>.Empty;
            var document = StyledDocuments.Read(text, fork, file.FinderInfo.Type, file.Name.ToString(), options, readOptions, diagnostics);
            return document is { Kind: DocumentKind.DocMaker } || document?.Chapters.Any(c => c.Pictures.Count > 0) == true ? document : null;
        }

        // A palette as a grid of 16 × 16-pixel swatches, 16 to a row, in entry order, captioned with the count.
        private static PreviewViewModel Swatches(IReadOnlyList<Resources.Decoders.Colors.PaletteEntry> entries)
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
            return new PreviewViewModel(PreviewKind.Image, "") { Images = [new PreviewImage(png, width, height, $"{entries.Count} colours")] };
        }

        private static PreviewViewModel SoundPreview(SampledSound sampled)
        {
            var bits = sampled.Kind == SoundHeaderKind.Compressed ? $"'{sampled.Format}'" : $"{sampled.SampleSize}-bit";
            if (SoundSamples.Decode(sampled) is not { } sound)
                return new PreviewViewModel(PreviewKind.None, $"A {bits} sound, a format ClassicMac does not read; see Hex.");
            var channels = sound.Channels == 1 ? "mono" : sound.Channels == 2 ? "stereo" : $"{sound.Channels} channels";
            var loop = sampled.LoopEnd > sampled.LoopStart && sampled.LoopEnd - sampled.LoopStart > 2 ? $", loop {sampled.LoopStart}–{sampled.LoopEnd}" : "";
            var note = sampled.BaseNote is not (0 or 60) ? $", base note {sampled.BaseNote}" : "";
            return new PreviewViewModel(PreviewKind.Sound, "")
            {
                Sound = sound,
                SoundDetails = string.Create(CultureInfo.InvariantCulture,
                    $"{sound.SampleRate:0.###} Hz, {channels}, {bits}, {sound.Duration:0.00} s ({sound.Frames:N0} frames){loop}{note}"),
            };
        }

        private static PreviewViewModel StyledPreview(StyledText styled) => new(PreviewKind.Text, "") { Styled = styled, Text = styled.Text.Replace('\r', '\n') };

        private static PreviewViewModel FromFiles(IReadOnlyList<DecodedFile> files, string what)
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
                        var number = f.Extension.Length > 4 ? $"#{f.Extension[1..^4]}" : caption;
                        return new PreviewImage(png, width, height, number);
                    }).ToList(),
                };
            }
            if (files.FirstOrDefault(f => f.Extension == ".txt") is { } text)
                return new PreviewViewModel(PreviewKind.Text, "") { Text = Encoding.UTF8.GetString(text.Content.Span) };
            if (files.FirstOrDefault(f => f.Extension == ".json") is { } other)
            {
                using var document = JsonDocument.Parse(other.Content);
                // A string list reads best as numbered lines.
                if (document.RootElement.TryGetProperty("strings", out var strings))
                {
                    var lines = strings.EnumerateArray().Select((s, i) => $"{i + 1,4}  {s.GetString()}");
                    return new PreviewViewModel(PreviewKind.Text, "") { Text = string.Join('\n', lines) };
                }
                return new PreviewViewModel(PreviewKind.Json, "") { Text = Encoding.UTF8.GetString(other.Content.Span).TrimEnd() };
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
        private static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) =>
            png.Length >= 24 ? (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..])) : (0, 0);
    }
}
