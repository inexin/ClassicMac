using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Text;

// Shared by the text decoders: bytes to Unicode in the chosen encoding, line endings, and small JSON documents.
internal static class MacText
{
    public static string EncodingName(MacTextEncoding encoding) => encoding switch
    {
        _ => "macintosh",
    };

    public static string Decode(ReadOnlySpan<byte> bytes, DecodeOptions options) => options.TextEncoding switch
    {
        _ => MacRoman.Decode(bytes),
    };

    public static string Lines(string text, DecodeOptions options) =>
        options.LineEndings == LineEndings.Lf ? text.Replace('\r', '\n') : text;

    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        // Relaxed escaping keeps non-ASCII text readable; the output is a file, never embedded in HTML. LF on every
        // platform, so outputs and their hashes are the same everywhere.
        var options = new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            write(writer);
        }

        return [.. buffer.WrittenSpan, (byte)'\n'];
    }

    // A Pascal string at offset, cut to what is there; false when its length byte runs past the data.
    public static bool TryReadPascal(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> text)
    {
        text = default;
        if (offset >= data.Length)
        {
            return false;
        }

        var length = data[offset];
        var available = Math.Min(length, data.Length - offset - 1);
        text = data.Slice(offset + 1, available);
        offset += 1 + length;
        return available == length;
    }
}
