using System.Collections.Generic;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Text
{
    /// <summary>
    /// <c>'STR '</c>: one Pascal string (<i>Inside Macintosh: Text</i>, "String Resource"), written as UTF-8 text.
    /// </summary>
    internal sealed class StringDecoder(DecodeOptions options) : IResourceDecoder, IBuiltInDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("STR ");

        public string Name => "text.string";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyCollection<FourCC> Types => [Type];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var data = input.Data.Span;
            var offset = 0;
            if (!MacText.TryReadPascal(data, ref offset, out var text))
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.string-short",
                    $"{input.Resource}: the string's length byte says more than the resource holds; cut."));
            }
            var decoded = MacText.Lines(MacText.Decode(text, options), options);
            return [new DecodedFile(".txt", MacText.Utf8(decoded), MacText.EncodingName(options.TextEncoding))];
        }
    }

    /// <summary>
    /// <c>'STR#'</c>: a count and that many Pascal strings (<i>Inside Macintosh: Text</i>, "String List Resource"),
    /// written as JSON (<c>{"strings": [...]}</c>).
    /// </summary>
    internal sealed class StringListDecoder(DecodeOptions options) : IResourceDecoder, IBuiltInDecoder
    {
        private static readonly FourCC Type = FourCC.FromString("STR#");

        public string Name => "text.string-list";

        public int Version => 1;

        public bool CanDecode(FourCC type) => type == Type;

        public IReadOnlyCollection<FourCC> Types => [Type];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var reader = new BigEndianReader(input.Data);
            if (!reader.TryReadUInt16(out ushort count)) return [];
            var strings = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                // A string cut short is kept as far as it goes; the list ends there.
                if (!reader.TryReadByte(out byte length))
                {
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.string-list-short",
                        $"{input.Resource}: the list counts {count} strings but the data ends after {strings.Count}."));
                    break;
                }
                if (!reader.TryReadBytes(length, out var text))
                {
                    strings.Add(MacText.Decode(reader.ReadBytes(reader.Remaining), options));
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "text.string-list-short",
                        $"{input.Resource}: the list counts {count} strings but the data ends after {strings.Count}."));
                    break;
                }
                strings.Add(MacText.Decode(text, options));
            }
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("strings");
                foreach (var s in strings) w.WriteStringValue(MacText.Lines(s, options));
                w.WriteEndArray();
                w.WriteEndObject();
            });
            return [new DecodedFile(".json", json, MacText.EncodingName(options.TextEncoding))];
        }
    }
}
