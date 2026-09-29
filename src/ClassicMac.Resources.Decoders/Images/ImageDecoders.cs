using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;
using ClassicMac.Graphics;
using ClassicMac.Pict;

namespace ClassicMac.Resources.Decoders.Images
{
    // What the image decoders share: turning QuickDraw.Pict's bitmaps into image files, and its exceptions (it reports
    // problems only by throwing) into a diagnostic, after which the exporter writes the resource raw.
    internal abstract class ImageDecoder(DecodeOptions options, string name, params string[] types) : IResourceDecoder, IBuiltInDecoder
    {
        private readonly HashSet<FourCC> handled = types.Select(FourCC.FromString).ToHashSet();

        protected DecodeOptions Options { get; } = options;

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC type) => handled.Contains(type);

        public IReadOnlyCollection<FourCC> Types => handled;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            try
            {
                return DecodeImages(input);
            }
            catch (Exception e) when (e is NotSupportedException or EndOfStreamException or ArgumentException
                or OverflowException or InvalidDataException or IndexOutOfRangeException)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "image.undecodable",
                    $"{input.Resource}: {e.Message}"));
                return [];
            }
        }

        protected abstract IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input);

        protected DecodedFile Image(PictBitmap bitmap, string? suffix = null) =>
            new((suffix ?? "") + Options.ImageEncoder.Extension, Options.ImageEncoder.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels));

        // Lists (SICN, PAT#, ppt#): one file per item, ".1.png", ".2.png", ….
        protected IReadOnlyList<DecodedFile> Numbered(IReadOnlyList<PictBitmap> bitmaps) =>
            bitmaps.Select((b, i) => Image(b, "." + (i + 1).ToString(CultureInfo.InvariantCulture))).ToList();

        protected static string TypeName(DecodeInput input) => input.Resource.Type.ToString();
    }

    /// <summary><c>'PICT'</c>: QuickDraw pictures (version 1 and 2), drawn by QuickDraw.Pict at the chosen screen depth.</summary>
    internal sealed class PictureDecoder(DecodeOptions options) : ImageDecoder(options, "image.picture", "PICT")
    {
        protected override IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input)
        {
            var data = input.Data.ToArray();
            // The picture frame (top, left, bottom, right after the size word) says how big the canvas will be; refuse
            // pictures over the limit before anything is allocated.
            if (data.Length >= 10)
            {
                long height = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(6)) - BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(2));
                long width = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(8)) - BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(4));
                if (width * height > Options.MaxImagePixels)
                {
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "image.too-large",
                        $"{input.Resource}: its frame is {width} × {height} pixels, over the {Options.MaxImagePixels}-pixel limit."));
                    return [];
                }
            }
            return [Image(Draw(data, Options))];
        }

        // A picture drawn at the options' screen depth, as the Mac they choose draws it.
        internal static PictBitmap Draw(byte[] data, DecodeOptions options) =>
            PictReader.Decode(data, new PictDecodeOptions
            {
                ScreenDepth = options.ScreenDepth,
                QuickDraw = options.QuickDraw == ResourceManagerModel.Rom68k ? PictQuickDraw.MacRom : PictQuickDraw.MacOS9,
            });
    }

    /// <summary>
    /// Icons: <c>ICON</c>, the icon lists <c>ICN#</c>/<c>ics#</c>/<c>icm#</c> (mask as transparency), the 4- and 8-bit
    /// icons <c>icl4</c>/<c>icl8</c>/<c>ics4</c>/<c>ics8</c>/<c>icm4</c>/<c>icm8</c> (masked by the list of the same ID
    /// and size, as the Finder draws them), <c>cicn</c> and <c>SICN</c>.
    /// </summary>
    internal sealed class IconDecoder(DecodeOptions options)
        : ImageDecoder(options, "image.icon", "ICON", "ICN#", "ics#", "icm#", "icl4", "icl8", "ics4", "ics8", "icm4", "icm8", "cicn", "SICN")
    {
        protected override IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input)
        {
            var type = TypeName(input);
            var data = input.Data.ToArray();
            switch (type)
            {
                case "ICON":
                    return [Image(QuickDrawResources.DecodeIcon(data))];
                case "ICN#" or "ics#" or "icm#":
                    return [Image(QuickDrawResources.DecodeIconList(type, data))];
                case "cicn":
                    return [Image(QuickDrawResources.DecodeCicn(data))];
                case "SICN":
                    return Numbered(QuickDrawResources.DecodeSmallIcons(data));
            }
            var list = type[..3] switch { "icl" => "ICN#", "ics" => "ics#", _ => "icm#" };
            var mask = input.Find(FourCC.FromString(list), input.Resource.Id);
            if (mask is null)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "image.no-mask",
                    $"{input.Resource}: no '{list}' {input.Resource.Id} for its mask; drawn opaque."));
            }
            return [Image(QuickDrawResources.DecodeColorIcon(type, data, mask?.ToArray()))];
        }
    }

    /// <summary>
    /// Cursors (<c>CURS</c>, <c>crsr</c>): the image (mask as transparency) and a JSON file with the hotspot, the pixels
    /// the cursor inverts where its mask is clear, and, for colour cursors, any other colour it XORs there.
    /// </summary>
    internal sealed class CursorDecoder(DecodeOptions options) : ImageDecoder(options, "image.cursor", "CURS", "crsr")
    {
        protected override IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input)
        {
            var data = input.Data.ToArray();
            var cursor = TypeName(input) == "CURS" ? QuickDrawResources.DecodeCursor(data) : QuickDrawResources.DecodeColorCursor(data);
            var (width, height) = (cursor.Image.Width, cursor.Image.Height);
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("width", width);
                w.WriteNumber("height", height);
                w.WriteStartObject("hotspot");
                w.WriteNumber("h", cursor.HotspotH);
                w.WriteNumber("v", cursor.HotspotV);
                w.WriteEndObject();
                w.WriteStartArray("inverted");
                for (var y = 0; y < height; y++)
                {
                    var row = new StringBuilder(width);
                    for (var x = 0; x < width; x++) row.Append(y * width + x < cursor.Inverted.Length && cursor.Inverted[y * width + x] ? '1' : '0');
                    w.WriteStringValue(row.ToString());
                }
                w.WriteEndArray();
                if (cursor.Xor.Any(v => v is not (0 or 0xFFFFFF)))
                {
                    w.WriteStartArray("xor");
                    for (var y = 0; y < height; y++)
                        w.WriteStringValue(string.Join(" ", cursor.Xor.Skip(y * width).Take(width).Select(v => v.ToString("X6", CultureInfo.InvariantCulture))));
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            });
            return [Image(cursor.Image), new DecodedFile(".json", json)];
        }
    }

    /// <summary>Patterns: <c>PAT </c> and <c>ppat</c> at their size; <c>PAT#</c> and <c>ppt#</c> as one image each.</summary>
    internal sealed class PatternDecoder(DecodeOptions options) : ImageDecoder(options, "image.pattern", "PAT ", "ppat", "PAT#", "ppt#")
    {
        protected override IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input)
        {
            var data = input.Data.ToArray();
            return TypeName(input) switch
            {
                "PAT " => [Image(QuickDrawResources.DecodePattern(data))],
                "ppat" => [Image(QuickDrawResources.DecodePixelPattern(data))],
                "PAT#" => Numbered(QuickDrawResources.DecodePatternList(data)),
                _ => Numbered(QuickDrawResources.DecodePixelPatternList(data)),
            };
        }
    }
}
