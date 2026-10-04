using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassicMac.Core;

/// <summary>
/// The Mac OS text encodings ClassicMac reads, numbered as the Text Encoding Converter's base encodings
/// (<c>kTextEncodingMacRoman</c> … in Apple's TextCommon.h).
/// </summary>
public enum MacTextEncoding
{
    /// <summary>Mac OS Roman (IANA <c>macintosh</c>).</summary>
    Roman = 0,

    /// <summary>Mac OS Japanese: Shift-JIS with Apple's additions.</summary>
    Japanese = 1,

    /// <summary>Mac OS Chinese Traditional: Big5 with Apple's additions.</summary>
    ChineseTraditional = 2,

    /// <summary>Mac OS Korean: EUC-KR (KS X 1001) with Apple's additions.</summary>
    Korean = 3,

    /// <summary>Mac OS Arabic.</summary>
    Arabic = 4,

    /// <summary>Mac OS Hebrew.</summary>
    Hebrew = 5,

    /// <summary>Mac OS Greek.</summary>
    Greek = 6,

    /// <summary>Mac OS Cyrillic.</summary>
    Cyrillic = 7,

    /// <summary>Mac OS Thai.</summary>
    Thai = 21,

    /// <summary>Mac OS Chinese Simplified: EUC-CN (GB 2312) with Apple's additions.</summary>
    ChineseSimplified = 25,

    /// <summary>Mac OS Central European (Mac OS CE).</summary>
    CentralEuropean = 29,

    /// <summary>Mac OS Turkish.</summary>
    Turkish = 35,

    /// <summary>Mac OS Croatian.</summary>
    Croatian = 36,

    /// <summary>Mac OS Icelandic.</summary>
    Icelandic = 37,

    /// <summary>Mac OS Romanian.</summary>
    Romanian = 38,

    /// <summary>Mac OS Ukrainian (Mac OS Cyrillic with the Ukrainian letters).</summary>
    Ukrainian = 152,
}

/// <summary>
/// Decoding and encoding the Mac OS text encodings (docs/formats/codecs/text-encodings.md). Mac OS Roman is
/// <see cref="MacRoman"/>; the others come from .NET's Mac code pages, read once into tables of every one- and
/// two-byte code, with Apple's mapping where the two differ.
/// </summary>
public static class MacEncodings
{
    private static readonly Dictionary<MacTextEncoding, int> CodePages = new()
    {
        [MacTextEncoding.Japanese] = 10001, [MacTextEncoding.ChineseTraditional] = 10002, [MacTextEncoding.Korean] = 10003,
        [MacTextEncoding.Arabic] = 10004, [MacTextEncoding.Hebrew] = 10005, [MacTextEncoding.Greek] = 10006,
        [MacTextEncoding.Cyrillic] = 10007, [MacTextEncoding.ChineseSimplified] = 10008, [MacTextEncoding.Romanian] = 10010,
        [MacTextEncoding.Ukrainian] = 10017, [MacTextEncoding.Thai] = 10021, [MacTextEncoding.CentralEuropean] = 10029,
        [MacTextEncoding.Icelandic] = 10079, [MacTextEncoding.Turkish] = 10081, [MacTextEncoding.Croatian] = 10082,
    };

    private static readonly ConcurrentDictionary<MacTextEncoding, Table> Tables = new();

    static MacEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding's IANA name: <c>macintosh</c>, <c>x-mac-japanese</c>, …</summary>
    public static string Name(MacTextEncoding encoding) =>
        encoding == MacTextEncoding.Roman ? "macintosh" : Encoding.GetEncoding(CodePage(encoding)).WebName;

    /// <summary>Reads an encoding's name: the enum's name (case and hyphens ignored, "chinese-traditional") or its IANA name.</summary>
    public static bool TryParse(string? name, out MacTextEncoding encoding)
    {
        encoding = MacTextEncoding.Roman;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var plain = name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<MacTextEncoding>())
        {
            if (string.Equals(plain, candidate.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, Name(candidate), StringComparison.OrdinalIgnoreCase))
            {
                encoding = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text <paramref name="bytes"/> hold. A code the encoding does not define, and a lead byte without a valid second
    /// byte, become U+FFFD; the byte after such a lead byte is read on its own.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, MacTextEncoding encoding)
    {
        if (encoding == MacTextEncoding.Roman)
        {
            return MacRoman.Decode(bytes);
        }

        var table = TableFor(encoding);
        var text = new StringBuilder(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (table.Lead[b])
            {
                if (i + 1 < bytes.Length && table.Double.TryGetValue(b << 8 | bytes[i + 1], out var pair))
                {
                    text.Append(pair);
                    i++;
                }
                else
                {
                    text.Append('�');
                }

                continue;
            }

            text.Append(table.Single[b] ?? "�");
        }

        return text.ToString();
    }

    /// <summary>The bytes of <paramref name="text"/> in the encoding.</summary>
    /// <exception cref="ArgumentException">A character the encoding cannot hold.</exception>
    public static byte[] Encode(string text, MacTextEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (encoding == MacTextEncoding.Roman)
        {
            return MacRoman.Encode(text);
        }

        var table = TableFor(encoding);
        var bytes = new List<byte>(text.Length * 2);
        for (int i = 0; i < text.Length;)
        {
            int length = Math.Min(table.LongestText, text.Length - i);
            for (; length > 0; length--)
            {
                if (table.Reverse.TryGetValue(text.Substring(i, length), out var code))
                {
                    if (code > 0xFF)
                    {
                        bytes.Add((byte)(code >> 8));
                    }

                    bytes.Add((byte)code);
                    break;
                }
            }

            if (length == 0)
            {
                throw new ArgumentException($"{Name(encoding)} cannot hold U+{(int)text[i]:X4}.", nameof(text));
            }

            i += length;
        }

        return [.. bytes];
    }

    private static int CodePage(MacTextEncoding encoding) =>
        CodePages.TryGetValue(encoding, out var page) ? page : throw new ArgumentOutOfRangeException(nameof(encoding));

    private static Table TableFor(MacTextEncoding encoding) => Tables.GetOrAdd(encoding, Build);

    // Every code of the code page: a byte the decoder holds back is a lead byte, and each of its pairs is tried; a code
    // the code page refuses is undefined.
    private static Table Build(MacTextEncoding encoding)
    {
        var codePage = Encoding.GetEncoding(CodePage(encoding), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var table = new Table();
        for (int b = 0; b < 256; b++)
        {
            var decoder = codePage.GetDecoder();
            try
            {
                var chars = new char[4];
                int count = decoder.GetChars([(byte)b], 0, 1, chars, 0, flush: false);
                if (count == 0)
                {
                    table.Lead[b] = true;
                    continue;
                }

                table.Single[b] = new string(chars, 0, count);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        for (int lead = 0; lead < 256; lead++)
        {
            if (!table.Lead[lead])
            {
                continue;
            }

            for (int trail = 0; trail < 256; trail++)
            {
                try
                {
                    var text = codePage.GetString([(byte)lead, (byte)trail]);
                    if (text.Length is 1 or 2 && !(text.Length == 2 && text[1] == table.Single[trail]?.FirstOrDefault()))
                    {
                        table.Double[lead << 8 | trail] = text;
                    }
                }
                catch (DecoderFallbackException)
                {
                }
            }
        }

        // Apple's mapping where it differs (MacEncodingCorrections, generated from Apple's tables): a two-byte code makes
        // its first byte a lead byte; a one-byte code is a character of its own.
        foreach (var (code, text) in MacEncodingCorrections.All.GetValueOrDefault(encoding, []))
        {
            if (code > 0xFF)
            {
                table.Lead[code >> 8] = true;
                table.Double[code] = text;
            }
            else
            {
                table.Lead[code] = false;
                table.Single[code] = text;
            }
        }

        foreach (var (code, text) in table.Single.Select((t, c) => (c, t)).Where(p => p.t is not null))
        {
            table.Reverse.TryAdd(text!, code);
        }

        foreach (var (code, text) in table.Double.OrderBy(p => p.Key))
        {
            table.Reverse.TryAdd(text, code);
        }

        table.LongestText = table.Reverse.Keys.Max(k => k.Length);
        return table;
    }

    private sealed class Table
    {
        public string?[] Single { get; } = new string?[256];

        public bool[] Lead { get; } = new bool[256];

        public Dictionary<int, string> Double { get; } = [];

        public Dictionary<string, int> Reverse { get; } = new(StringComparer.Ordinal);

        public int LongestText { get; set; }
    }
}
