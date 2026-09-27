using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Text
{
    /// <summary>
    /// <c>'TEXT'</c>: plain text (<i>Inside Macintosh: Text</i>), written as UTF-8. When the fork has a <c>'styl'</c> of
    /// the same ID — as SimpleText and TextEdit keep them — the styled text is also written as RTF.
    /// </summary>
    internal sealed class TextDecoder(DecodeOptions options) : IResourceDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("TEXT"), Styl = FourCC.FromString("styl");

        public string Name => "text.text";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var encoding = MacText.EncodingName(options.TextEncoding);
            var text = MacText.Decode(input.Data.Span, options);
            var files = new List<DecodedFile> { new(".txt", MacText.Utf8(MacText.Lines(text, options)), encoding) };
            if (input.Find(Styl, input.Resource.Id) is { } styl)
            {
                var styled = StyledText.Read(input.Data.Span, styl.Span, options);
                if (!styled.Complete)
                {
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.styl-short",
                        $"{input.Resource}: its 'styl' ends before its last style run; the runs there are used."));
                }
                files.Add(new DecodedFile(".rtf", Encoding.ASCII.GetBytes(Rtf.Write(styled)), encoding));
            }
            return files;
        }
    }

    /// <summary><c>'styl'</c> on its own: the style runs as JSON.</summary>
    internal sealed class StyleDecoder : IResourceDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("styl");

        public string Name => "text.style";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var runs = StyleRuns.Read(input.Data.Span, out var complete);
            if (!complete)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.styl-short",
                    $"{input.Resource}: the data ends before the last style run."));
            }
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("runs");
                foreach (var r in runs)
                {
                    w.WriteStartObject();
                    w.WriteNumber("start", r.Start);
                    w.WriteNumber("height", r.Height);
                    w.WriteNumber("ascent", r.Ascent);
                    w.WriteNumber("font", r.Font);
                    w.WriteString("fontName", StyleRuns.FontName(r.Font));
                    w.WriteNumber("face", r.Face);
                    w.WriteNumber("size", r.Size);
                    w.WriteStartArray("color");
                    w.WriteNumberValue(r.Red);
                    w.WriteNumberValue(r.Green);
                    w.WriteNumberValue(r.Blue);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            return [new DecodedFile(".json", json)];
        }
    }

    /// <summary>
    /// <c>'vers'</c> (<i>Inside Macintosh: Macintosh Toolbox Essentials</i>, "Version Resource"): the version as
    /// binary-coded decimal (major; minor and bug fix as nibbles), the release stage ($20 development, $40 alpha, $60
    /// beta, $80 final) and non-release number, a region code, and short and long version strings — as JSON, with the
    /// number displayed as the Finder does ("1.0.2b3").
    /// </summary>
    internal sealed class VersionDecoder(DecodeOptions options) : IResourceDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("vers");

        public string Name => "text.version";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var data = input.Data.Span;
            if (data.Length < 7)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.vers-short",
                    $"{input.Resource}: {data.Length} bytes is too short for a version resource."));
                return [];
            }
            int major = Bcd(data[0]), minor = data[1] >> 4, bugFix = data[1] & 0x0F;
            var stage = data[2];
            var nonRelease = Bcd(data[3]);
            var region = BinaryPrimitives.ReadInt16BigEndian(data[4..]);
            var offset = 6;
            var complete = MacText.TryReadPascal(data, ref offset, out var shortText);
            var shortVersion = MacText.Decode(shortText, options);
            var longVersion = "";
            if (complete && offset < data.Length)
            {
                complete = MacText.TryReadPascal(data, ref offset, out var longText);
                longVersion = MacText.Decode(longText, options);
            }
            if (!complete)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.vers-short",
                    $"{input.Resource}: the version strings run past the data; cut."));
            }

            var (stageName, letter) = stage switch
            {
                0x20 => ("development", "d"),
                0x40 => ("alpha", "a"),
                0x60 => ("beta", "b"),
                0x80 => ("final", "f"),
                _ => ($"unknown (${stage:X2})", "?"),
            };
            var display = $"{major}.{minor}" + (bugFix > 0 ? $".{bugFix}" : "") + (stage != 0x80 || nonRelease > 0 ? $"{letter}{nonRelease}" : "");
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("display", display);
                w.WriteNumber("major", major);
                w.WriteNumber("minor", minor);
                w.WriteNumber("bugFix", bugFix);
                w.WriteString("stage", stageName);
                w.WriteNumber("nonRelease", nonRelease);
                w.WriteNumber("region", region);
                w.WriteString("shortVersion", shortVersion);
                w.WriteString("longVersion", MacText.Lines(longVersion, options));
                w.WriteEndObject();
            });
            return [new DecodedFile(".json", json, MacText.EncodingName(options.TextEncoding))];
        }

        private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
    }
}
