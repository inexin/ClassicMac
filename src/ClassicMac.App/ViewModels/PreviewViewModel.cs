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
    }

    /// <summary>One decoded image: PNG bytes, its size, and a caption (a list item's number, a cursor's hotspot).</summary>
    public sealed record PreviewImage(byte[] Png, int Width, int Height, string? Caption);

    /// <summary>
    /// The preview of a resource or file, made by the same decoders as <c>extract</c>: images (pictures, icons, cursors,
    /// patterns), text (strings, styled text), JSON (version resources, lone style runs) or sound (<c>snd </c>, drawn and
    /// played); otherwise a note to look at
    /// the hex view.
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

        public bool HasPreview => Kind is PreviewKind.Image or PreviewKind.Text or PreviewKind.Json or PreviewKind.Sound;

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
            var decoder = ResourceDecoders.Create(options).FirstOrDefault(d => d.CanDecode(resource.Type));
            if (decoder is null) return Nothing($"'{type}'");
            var files = decoder.Decode(new DecodeInput(resource, data, fork, readOptions, diagnostics));
            return FromFiles(files, $"'{type}'");
        }

        // A picture file (type PICT, after its 512-byte header) or a SimpleText document (TEXT, styled by its styl 128).
        private static PreviewViewModel ForFile(MacFile file, DecodeOptions options, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
        {
            var type = file.FinderInfo.Type.ToString();
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

        private static PreviewViewModel SoundPreview(SampledSound sampled)
        {
            var bits = sampled.Kind == SoundHeaderKind.Compressed ? $"'{sampled.Format}'" : $"{sampled.SampleSize}-bit";
            if (SoundSamples.Decode(sampled) is not { } sound)
                return new PreviewViewModel(PreviewKind.None, $"A {bits} sound, a format ClassicMac does not read; see Hex.");
            var channels = sound.Channels == 1 ? "mono" : sound.Channels == 2 ? "stereo" : $"{sound.Channels} channels";
            var loop = sampled.LoopEnd > sampled.LoopStart + 1 ? $", loop {sampled.LoopStart}–{sampled.LoopEnd}" : "";
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
