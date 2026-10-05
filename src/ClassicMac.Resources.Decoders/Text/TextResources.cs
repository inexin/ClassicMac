using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Text;

/// <summary>
/// Text resources as values to edit and write back: <c>'STR '</c> (one Pascal string), <c>'STR#'</c> (a count, then
/// Pascal strings), <c>'vers'</c> and <c>'TEXT'</c> with its <c>'styl'</c>. Text is Mac OS Roman; a line break is a
/// carriage return in the resource and <c>\n</c> here. Formats: docs/formats/resources/strings.md,
/// styled-text.md and version.md.
/// </summary>
public static class TextResources
{
    /// <summary>The text of a <c>'STR '</c> (its Pascal string; bytes after it are ignored).</summary>
    public static string ReadString(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        MacText.TryReadPascal(data, ref offset, out var text);
        return FromMac(text);
    }

    /// <summary>A <c>'STR '</c> holding <paramref name="text"/>.</summary>
    /// <exception cref="ArgumentException">The text is not Mac OS Roman, or is over 255 bytes.</exception>
    public static byte[] WriteString(string text) => Pascal(text);

    /// <summary>A Pascal string of <paramref name="text"/> in <paramref name="encoding"/>.</summary>
    /// <exception cref="ArgumentException">The encoding cannot hold the text, or it is over 255 bytes.</exception>
    public static byte[] WriteString(string text, MacTextEncoding encoding) => Pascal(text, encoding);

    /// <summary>The strings of a <c>'STR#'</c>, as many as its data holds.</summary>
    public static IReadOnlyList<string> ReadStringList(ReadOnlyMemory<byte> data)
    {
        var strings = new List<string>();
        var reader = new BigEndianReader(data);
        if (!reader.TryReadUInt16(out ushort count))
        {
            return strings;
        }

        for (var i = 0; i < count && reader.TryReadByte(out byte length); i++)
        {
            if (!reader.TryReadBytes(length, out var text))
            {
                break;
            }

            strings.Add(FromMac(text));
        }
        return strings;
    }

    /// <summary>A <c>'STR#'</c> holding <paramref name="strings"/>.</summary>
    /// <exception cref="ArgumentException">A string is not Mac OS Roman or is over 255 bytes, or there are over 65535.</exception>
    public static byte[] WriteStringList(IReadOnlyList<string> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (strings.Count > ushort.MaxValue)
        {
            throw new ArgumentException("A string list holds at most 65535 strings.", nameof(strings));
        }

        var writer = new BigEndianWriter();
        writer.WriteUInt16(strings.Count);
        foreach (var s in strings)
        {
            writer.WriteBytes(Pascal(s));
        }

        return writer.ToArray();
    }

    /// <summary>The text of a <c>'TEXT'</c> (Mac OS Roman, carriage returns as <c>\n</c>).</summary>
    public static string ReadText(ReadOnlySpan<byte> data) => FromMac(data);

    /// <summary>
    /// A <c>'TEXT'</c> changed to <paramref name="newText"/>, and its <c>'styl'</c> (when there is one) kept in step: the
    /// text the two share at the start and at the end keeps its styles; new text in between takes the style of the run
    /// the change starts in, as typing in TextEdit does [ClassicMac]. Runs left empty are dropped.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not Mac OS Roman.</exception>
    public static (byte[] Text, byte[]? Styl) WriteText(ReadOnlySpan<byte> oldText, ReadOnlyMemory<byte> styl, string newText, bool hasStyl)
    {
        var text = ToMac(newText);
        if (!hasStyl)
        {
            return (text, null);
        }

        int prefix = 0, suffix = 0;
        while (prefix < oldText.Length && prefix < text.Length && oldText[prefix] == text[prefix])
        {
            prefix++;
        }

        while (suffix < oldText.Length - prefix && suffix < text.Length - prefix
               && oldText[oldText.Length - 1 - suffix] == text[text.Length - 1 - suffix])
        {
            suffix++;
        }

        int oldEnd = oldText.Length - suffix, newEnd = text.Length - suffix;
        var runs = StyleRuns.Read(styl, out _);
        StyleRun? Covering(int at) => runs.LastOrDefault(r => r.Start <= at) ?? runs.FirstOrDefault();
        // Before the change: as it was (the run the change starts in also styles the new text). After it: the text that
        // was there keeps its style, whether its run began before the change or after.
        var kept = runs.Where(r => r.Start <= prefix).ToList();
        if (suffix > 0 && Covering(oldEnd) is { } tail && tail.Start > prefix && tail.Start < oldEnd)
        {
            kept.Add(tail with { Start = newEnd });
        }

        kept.AddRange(runs.Where(r => r.Start >= oldEnd && r.Start > prefix).Select(r => r with { Start = r.Start - oldEnd + newEnd }));
        // One run per start (the later wins); none past the end but the first.
        kept = kept.GroupBy(r => r.Start).Select(g => g.Last()).OrderBy(r => r.Start).ToList();
        kept = kept.Where((r, i) => i == 0 || r.Start < text.Length).ToList();
        return (text, StyleRuns.Write(kept));
    }

    private static byte[] Pascal(string text, MacTextEncoding encoding = MacTextEncoding.Roman)
    {
        var bytes = ToMac(text, encoding);
        if (bytes.Length > 255)
        {
            throw new ArgumentException($"A Pascal string holds at most 255 bytes; this is {bytes.Length}.", nameof(text));
        }

        return [(byte)bytes.Length, .. bytes];
    }

    private static string FromMac(ReadOnlySpan<byte> bytes) => MacRoman.Decode(bytes).Replace('\r', '\n');

    private static byte[] ToMac(string text, MacTextEncoding encoding = MacTextEncoding.Roman)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mac = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (encoding != MacTextEncoding.Roman)
        {
            return MacEncodings.Encode(mac, encoding);
        }

        return MacRoman.TryEncode(mac, out var bytes)
            ? bytes
            : throw new ArgumentException("The text has characters Mac OS Roman cannot hold.", nameof(text));
    }
}

/// <summary>
/// A <c>'vers'</c> resource's fields: the version (major 0–99, minor and bug fix 0–15), the release stage ($20
/// development, $40 alpha, $60 beta, $80 final) and non-release number (0–99), a region code, and the short and long
/// version strings.
/// </summary>
public sealed record VersionResource(int Major, int Minor, int BugFix, byte Stage, int NonRelease, short Region, string ShortVersion, string LongVersion)
{
    /// <summary>Reads one (null when it is shorter than its fixed part).</summary>
    public static VersionResource? Read(ReadOnlyMemory<byte> data)
    {
        var bytes = data.Span;
        if (bytes.Length < 7)
        {
            return null;
        }

        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        var nonRelease = (bytes[3] >> 4) <= 9 && (bytes[3] & 0x0F) <= 9 ? Bcd(bytes[3]) : bytes[3];
        var offset = 6;
        MacText.TryReadPascal(bytes, ref offset, out var shortText);
        var reader = new BigEndianReader(data);
        var encoding = MacScripts.EncodingOfRegion(reader.ReadInt16At(4));                 // the region's system's encoding
        var shortVersion = MacEncodings.Decode(shortText, encoding);
        var longVersion = "";
        if (offset < bytes.Length && MacText.TryReadPascal(bytes, ref offset, out var longText))
        {
            longVersion = MacEncodings.Decode(longText, encoding).Replace('\r', '\n');
        }

        return new VersionResource(Bcd(bytes[0]), bytes[1] >> 4, bytes[1] & 0x0F, bytes[2], nonRelease, reader.ReadInt16At(4),
            shortVersion, longVersion);
    }

    /// <summary>The resource: BCD numbers (the non-release number BCD too, as Apple writes it), then the two Pascal strings.</summary>
    /// <exception cref="ArgumentException">A number is out of range, or a string is not in the region's encoding or over 255 bytes.</exception>
    public byte[] Write()
    {
        if (Major is < 0 or > 99 || Minor is < 0 or > 15 || BugFix is < 0 or > 15 || NonRelease is < 0 or > 99)
        {
            throw new ArgumentException("The version is major 0–99, minor and bug fix 0–15, non-release 0–99.");
        }

        static byte Bcd(int v) => (byte)((v / 10 << 4) | (v % 10));
        var writer = new BigEndianWriter();
        writer.WriteByte(Bcd(Major));
        writer.WriteByte((byte)((Minor << 4) | BugFix));
        writer.WriteByte(Stage);
        writer.WriteByte(Bcd(NonRelease));
        writer.WriteInt16(Region);
        var encoding = MacScripts.EncodingOfRegion(Region);                                 // the region's system's encoding
        writer.WriteBytes(TextResources.WriteString(ShortVersion, encoding));
        writer.WriteBytes(TextResources.WriteString(LongVersion, encoding));
        return writer.ToArray();
    }
}
