using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Documents;

/// <summary>What a link in a help page goes to.</summary>
public enum HelpLinkKind
{
    /// <summary>The page itself (a <c>data:</c> URI ClassicMac made, or <c>about:blank</c>): shown.</summary>
    Page,

    /// <summary>A file on the disk, by its path from the volume's root.</summary>
    File,

    /// <summary>A Help Viewer command (<c>help:</c>): open a book, search, look up an anchor.</summary>
    Help,

    /// <summary>A script: Help Viewer's <c>help:runscript=</c> (an AppleScript), or <c>javascript:</c>.</summary>
    Script,

    /// <summary>Anything else: a web or mail address, another scheme.</summary>
    External,
}

/// <summary>A link classified: what it goes to, the file's path for a file, its fragment, and a sentence for the status line.</summary>
public sealed record HelpLink(HelpLinkKind Kind, IReadOnlyList<string>? Path, string? Fragment, string Description);

/// <summary>A file a help page refers to: its data fork and Finder type.</summary>
public sealed record HelpFile(ReadOnlyMemory<byte> Data, FourCC Type);

/// <summary>
/// Apple Help pages (docs/formats/resources/help-pages.md): the HTML pages of Mac OS 8.5–9 help books, shown by Help
/// Viewer (<c>TEXT</c> files of creator <c>hbwr</c>), and other <c>.htm</c>/<c>.html</c> files. ClassicMac decodes a
/// page's bytes and makes it ready for a web view: the pictures, stylesheets and frames it refers to are read from
/// the disk and put into the page itself (no web server, no temporary files), and its links point at files of the
/// disk by an address the viewer recognises, so a click selects that file instead of following the link.
/// </summary>
public static partial class HelpPages
{
    /// <summary>The address a page's links to files of the disk are given: this origin, then the path from the volume's root.</summary>
    public const string Origin = "https://help.classicmac.invalid/";

    private const string Policy = """<meta http-equiv="Content-Security-Policy" content="default-src data: 'unsafe-inline'; script-src 'none'">""";

    private static readonly FourCC Text = FourCC.FromString("TEXT"), HelpViewer = FourCC.FromString("hbwr"), Pict = FourCC.FromString("PICT");

    // Windows-1252's characters for 128–159; ISO 8859-1 pages read as it, as browsers read them [Reference: WHATWG Encoding].
    private const string Windows1252 = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>Whether a file is a help page: a <c>TEXT</c> file of Help Viewer, or one named <c>.htm</c> or <c>.html</c>.</summary>
    public static bool IsHelpPage(FourCC type, FourCC creator, string name) =>
        type == Text && (creator == HelpViewer || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".html", StringComparison.OrdinalIgnoreCase));

    /// <summary>The charset a page declares in a <c>meta</c> tag of its first 1,024 bytes, lower case; null when it declares none.</summary>
    public static string? Charset(ReadOnlySpan<byte> page)
    {
        var head = Encoding.Latin1.GetString(page[..Math.Min(page.Length, 1024)]);
        foreach (Match meta in MetaTag().Matches(head))
        {
            if (CharsetValue().Match(meta.Value) is { Success: true } charset)
            {
                return charset.Groups[1].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// A page's text: UTF-8 when it declares utf-8, ISO 8859-1 (as Windows-1252) when it declares that or
    /// Windows-1252 or US-ASCII, else Mac OS Roman, the Mac's own encoding [ClassicMac].
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> page)
    {
        switch (Charset(page))
        {
            case "utf-8" or "utf8":
                return Encoding.UTF8.GetString(page);
            case "iso-8859-1" or "latin1" or "windows-1252" or "us-ascii" or "iso_8859-1":
                var text = new StringBuilder(page.Length);
                foreach (var b in page)
                {
                    text.Append(b is >= 0x80 and <= 0x9F ? Windows1252[b - 0x80] : (char)b);
                }

                return text.ToString();
            default:
                return MacRoman.Decode(page);
        }
    }

    /// <summary>The page's <c>meta</c> tags (name or http-equiv, and content), in order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Meta(string html)
    {
        var meta = new List<KeyValuePair<string, string>>();
        foreach (var tag in Tags(html).Where(t => t.Name == "meta"))
        {
            if ((tag.Value("name") ?? tag.Value("http-equiv")) is { } name && tag.Value("content") is { } content)
            {
                meta.Add(new(name, content));
            }
        }

        return meta;
    }

    /// <summary>The page's title: its <c>AppleTitle</c> meta tag (Help Viewer's), else its <c>title</c>; null without either.</summary>
    public static string? Title(string html)
    {
        if (Meta(html).FirstOrDefault(m => m.Key.Equals("AppleTitle", StringComparison.OrdinalIgnoreCase)) is { Value: { } apple })
        {
            return apple;
        }

        var start = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        var open = start < 0 ? -1 : html.IndexOf('>', start);
        var end = open < 0 ? -1 : html.IndexOf("</title", open, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return null;
        }

        return string.Join(' ', WebUtility.HtmlDecode(html[(open + 1)..end]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A relative URL of a page in <paramref name="folder"/> (its path from the volume's root) as a file's path from
    /// the root [ClassicMac]: '/'-separated names, each percent-decoded (UTF-8, else Mac OS Roman bytes, so a name
    /// may hold a '/' as <c>%2F</c>), "." and ".." stepping as usual, the query and fragment dropped; a leading '/'
    /// starts at the root. Null for a URL with a scheme, one only of a fragment, or one above the root.
    /// </summary>
    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string> folder, string url)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(url);
        var end = url.IndexOfAny(['#', '?']);
        var path = end < 0 ? url : url[..end];
        var colon = path.IndexOf(':');
        if (path.Length == 0 || colon >= 0 && (path.IndexOf('/') < 0 || colon < path.IndexOf('/')))
        {
            return null;
        }

        var names = path.StartsWith('/') ? new List<string>() : [.. folder];
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (names.Count == 0)
                {
                    return null;
                }

                names.RemoveAt(names.Count - 1);
                continue;
            }

            names.Add(Unescape(segment));
        }

        return names.Count == 0 ? null : names;
    }

    // A URL segment's %XX escapes as bytes, read as UTF-8 when they are, else as Mac OS Roman.
    private static string Unescape(string segment)
    {
        if (!segment.Contains('%', StringComparison.Ordinal))
        {
            return segment;
        }

        var bytes = new List<byte>();
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' && i + 2 < segment.Length
                && byte.TryParse(segment.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(MacRoman.Encode(segment[i].ToString()));
            }
        }

        var array = bytes.ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(array);
        }
        catch (DecoderFallbackException)
        {
            return MacRoman.Decode(array);
        }
    }

    /// <summary>What a link of a rendered page goes to (a link the web view is asked to follow).</summary>
    public static HelpLink Classify(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[Origin.Length..];
            var hash = rest.IndexOf('#');
            var fragment = hash < 0 ? null : rest[(hash + 1)..];
            var path = (hash < 0 ? rest : rest[..hash]).Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
            return new HelpLink(HelpLinkKind.File, path, fragment, "Opens " + string.Join(':', path));
        }

        var scheme = url.IndexOf(':') is > 0 and var at ? url[..at].ToLowerInvariant() : "";
        return scheme switch
        {
            "data" or "about" => new HelpLink(HelpLinkKind.Page, null, null, "This page"),
            "help" when url.Contains("runscript", StringComparison.OrdinalIgnoreCase) =>
                new HelpLink(HelpLinkKind.Script, null, null, "Runs an AppleScript in Help Viewer (not run here)"),
            "help" => new HelpLink(HelpLinkKind.Help, null, null, $"A Help Viewer command (not followed here): {url}"),
            "javascript" => new HelpLink(HelpLinkKind.Script, null, null, "A script (not run here)"),
            _ => new HelpLink(HelpLinkKind.External, null, null, $"An address outside the disk (not followed): {url}"),
        };
    }

    /// <summary>A page's text as a <c>data:</c> URI for the web view (UTF-8).</summary>
    public static string DataUri(string html) => "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

    /// <summary>
    /// The page ready for a web view: a content security policy that lets it load only what it holds and run no
    /// script; its <c>base</c> dropped; its stylesheets (<c>link rel=stylesheet</c>) put in <c>style</c> elements; its
    /// pictures (<c>img</c>, <c>input type=image</c>, <c>background</c>) as <c>data:</c> URIs (GIF, JPEG, PNG as they
    /// are, a <c>PICT</c> file drawn to PNG); its frames as <c>data:</c> URIs of their pages, made ready the same way;
    /// links to files of the disk as <see cref="Origin"/> addresses; every link opening in the page's own window
    /// (target <c>_top</c>), so the viewer sees the click. A file it refers to that is missing is reported
    /// (<c>help.missing-file</c>) and left out.
    /// </summary>
    /// <param name="html">The page's text.</param>
    /// <param name="folder">Its folder's path from the volume's root.</param>
    /// <param name="resolve">Reads a file of the disk by its path from the root; null when there is none.</param>
    /// <param name="options">How a PICT file is drawn.</param>
    /// <param name="diagnostics">Where the files left out are reported.</param>
    public static string Render(string html, IReadOnlyList<string> folder, Func<IReadOnlyList<string>, HelpFile?> resolve,
        DecodeOptions? options = null, ICollection<Diagnostic>? diagnostics = null) =>
        Render(html, folder, resolve, options ?? DecodeOptions.Default, diagnostics ?? new List<Diagnostic>(), depth: 0);

    private static string Render(string html, IReadOnlyList<string> folder, Func<IReadOnlyList<string>, HelpFile?> resolve,
        DecodeOptions options, ICollection<Diagnostic> diagnostics, int depth)
    {
        var output = new StringBuilder(html.Length + 256);
        var copied = 0;
        var policy = false;
        HelpFile? Read(string url, out IReadOnlyList<string>? path) => ReadIn(folder, url, out path);

        HelpFile? ReadIn(IReadOnlyList<string> from, string url, out IReadOnlyList<string>? path)
        {
            path = Resolve(from, url);
            if (path is null)
            {
                return null;
            }

            var file = resolve(path);
            if (file is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "help.missing-file",
                    $"The page refers to {string.Join(':', path)}, which is not on the disk; left out."));
            }

            return file;
        }

        // CSS's pictures, url(...), as data: URIs, resolved against the folder the CSS is in.
        string Css(string css, IReadOnlyList<string> from) => CssUrl().Replace(css, m =>
        {
            var url = m.Groups[2].Value.Trim();
            if (url.Length == 0 || url.StartsWith('#') || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.Contains("://", StringComparison.Ordinal))
            {
                return m.Value;
            }

            var picture = Picture(ReadIn(from, url, out _), options, diagnostics);
            return picture.Length == 0 ? m.Value : $"url(\"{picture}\")";
        });

        foreach (var tag in Tags(html))
        {
            if (tag.Start < copied)
            {
                continue; // inside a <style> element already written
            }

            string? replacement = null;
            switch (tag.Name)
            {
                case "style" when html.IndexOf("</style", tag.End, StringComparison.OrdinalIgnoreCase) is var close and >= 0:
                    output.Append(html, copied, tag.End - copied).Append(Css(html[tag.End..close], folder));
                    copied = close;
                    continue;
                case "head" when !policy:
                    policy = true;
                    output.Append(html, copied, tag.End - copied).Append(Policy);
                    copied = tag.End;
                    continue;
                case "base":
                    replacement = "";
                    break;
                case "link" when (tag.Value("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase) && tag.Value("href") is { } css:
                    replacement = Read(css, out var sheetPath) is { } sheet && sheetPath is not null
                        ? "<style>" + Css(Decode(sheet.Data.Span), sheetPath.Take(sheetPath.Count - 1).ToList()) + "</style>"
                        : "";
                    break;
                case "img" or "input" when tag.Attribute("src") is { } src:
                    replacement = tag.With(src, Picture(Read(src.Value, out _), options, diagnostics));
                    break;
                case "body" or "table" or "td" or "th" or "tr" when tag.Attribute("background") is { } background:
                    replacement = tag.With(background, Picture(Read(background.Value, out _), options, diagnostics));
                    break;
                case "frame" or "iframe" when tag.Attribute("src") is { } frame:
                    var page = Read(frame.Value, out var framePath);
                    replacement = tag.With(frame, page is null || framePath is null || depth >= 2 ? ""
                        : DataUri(Render(Decode(page.Data.Span), framePath.Take(framePath.Count - 1).ToList(), resolve, options, diagnostics, depth + 1)));
                    break;
                case "a" or "area" when tag.Attribute("href") is { } href && href.Value.Length > 0 && !href.Value.StartsWith('#'):
                    var target = Resolve(folder, href.Value) is { } to ? Address(to, href.Value) : href.Value;
                    var stripped = tag.Without("target");
                    replacement = stripped.With(stripped.Attribute("href")!, target, append: " target=\"_top\"");
                    break;
            }

            if (replacement is null && tag.Attribute("style") is { } style && style.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                var rewritten = Css(style.Value, folder);
                if (rewritten != style.Value)
                {
                    replacement = tag.With(style, rewritten.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal));
                }
            }

            if (replacement is not null)
            {
                output.Append(html, copied, tag.Start - copied).Append(replacement);
                copied = tag.End;
            }
        }

        output.Append(html, copied, html.Length - copied);
        return policy ? output.ToString() : Policy + output;
    }

    // url(…) in CSS, quoted or not, any case: group 2 is the address.
    [GeneratedRegex("""url\(\s*(["']?)(.*?)\1\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    // A file's address: the origin, the path's names escaped, the fragment of the URL it came from.
    private static string Address(IReadOnlyList<string> path, string url)
    {
        var hash = url.IndexOf('#');
        return Origin + string.Join('/', path.Select(Uri.EscapeDataString)) + (hash < 0 ? "" : url[hash..]);
    }

    // A picture file as a data: URI: GIF, JPEG and PNG by their signatures, a PICT file (by its Finder type) drawn to
    // PNG; "" for none or anything else.
    private static string Picture(HelpFile? file, DecodeOptions options, ICollection<Diagnostic> diagnostics)
    {
        if (file is null)
        {
            return "";
        }

        var data = file.Data.Span;
        string? mime = data switch
        {
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "image/gif",
            [0xFF, 0xD8, ..] => "image/jpeg",
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            _ => null,
        };
        if (mime is not null)
        {
            return $"data:{mime};base64,{Convert.ToBase64String(data)}";
        }

        if (file.Type != Pict || data.Length <= 512 + 10)
        {
            return "";
        }

        try
        {
            var bitmap = PictureDecoder.Draw(data[512..].ToArray(), options);
            return "data:image/png;base64," + Convert.ToBase64String(PngEncoder.Instance.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels));
        }
        catch (Exception e) when (e is NotSupportedException or System.IO.EndOfStreamException or ArgumentException
            or OverflowException or System.IO.InvalidDataException or IndexOutOfRangeException)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "help.undrawable-picture", $"A PICT file the page shows cannot be drawn: {e.Message}"));
            return "";
        }
    }

    // The page's tags, in order: start tags with their attributes (comments, and the text of script, style and title,
    // skipped).
    private static IEnumerable<Tag> Tags(string html)
    {
        var i = 0;
        while ((i = html.IndexOf('<', i)) >= 0)
        {
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            if (i + 1 >= html.Length || !char.IsAsciiLetter(html[i + 1]))
            {
                i++;
                continue;
            }

            var tag = Tag.Read(html, i);
            yield return tag;
            i = tag.End;
            if (tag.Name is "script" or "style" or "title" or "textarea")
            {
                var close = html.IndexOf("</" + tag.Name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
            }
        }
    }

    [GeneratedRegex(@"<meta[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_.:\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetValue();

    // An attribute as written: its name (lower case), value (entities decoded), and where its value and itself are.
    private sealed record TagAttribute(string Name, string Value, int Start, int End, int ValueStart, int ValueEnd, char Quote);

    // A start tag as written in the page: its name (lower case), its attributes, and where it is.
    private sealed record Tag(string Html, string Name, IReadOnlyList<TagAttribute> Attributes, int Start, int End)
    {
        public TagAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);

        public string? Value(string name) => Attribute(name)?.Value;

        // The tag with one attribute's value replaced (in its own quotes; double quotes for a bare value), and text put
        // before its '>'.
        public string With(TagAttribute attribute, string value, string append = "")
        {
            var text = Html[Start..End];
            var from = attribute.ValueStart - Start;
            var to = attribute.ValueEnd - Start;
            // A value kept as it was is written as it was (entities and all).
            var written = value == attribute.Value ? text[from..to] : value;
            var replaced = text[..from] + (attribute.Quote == '\0' ? $"\"{written}\"" : written) + text[to..];
            var close = replaced.EndsWith("/>", StringComparison.Ordinal) ? replaced.Length - 2 : replaced.Length - 1;
            return replaced[..close].TrimEnd() + append + replaced[close..];
        }

        // The tag without an attribute (kept as the same tag, positions shifted).
        public Tag Without(string name)
        {
            if (Attribute(name) is not { } attribute)
            {
                return this;
            }

            var length = attribute.End - attribute.Start;
            var html = Html[..attribute.Start] + Html[attribute.End..];
            return new Tag(html, Name, [.. Attributes.Where(a => a != attribute).Select(a => a.Start > attribute.Start
                ? a with { Start = a.Start - length, End = a.End - length, ValueStart = a.ValueStart - length, ValueEnd = a.ValueEnd - length } : a)],
                Start, End - length);
        }

        public static Tag Read(string html, int start)
        {
            var at = start + 1;
            var nameStart = at;
            while (at < html.Length && (char.IsAsciiLetterOrDigit(html[at]) || html[at] is '-' or ':'))
            {
                at++;
            }

            var name = html[nameStart..at].ToLowerInvariant();
            var attributes = new List<TagAttribute>();
            while (at < html.Length)
            {
                var spaceStart = at;
                while (at < html.Length && char.IsWhiteSpace(html[at]))
                {
                    at++;
                }

                if (at >= html.Length || html[at] == '>' || html[at] == '/' && at + 1 < html.Length && html[at + 1] == '>')
                {
                    break;
                }

                var attributeStart = spaceStart;
                var keyStart = at;
                while (at < html.Length && !char.IsWhiteSpace(html[at]) && html[at] is not ('=' or '>'))
                {
                    at++;
                }

                var key = html[keyStart..at].ToLowerInvariant();
                var scan = at;
                while (scan < html.Length && char.IsWhiteSpace(html[scan]))
                {
                    scan++;
                }

                if (scan >= html.Length || html[scan] != '=')
                {
                    attributes.Add(new TagAttribute(key, "", attributeStart, at, at, at, '\0'));
                    continue;
                }

                at = scan + 1;
                while (at < html.Length && char.IsWhiteSpace(html[at]))
                {
                    at++;
                }

                if (at < html.Length && html[at] is '"' or '\'')
                {
                    var quote = html[at];
                    var close = html.IndexOf(quote, at + 1);
                    close = close < 0 ? html.Length - 1 : close;
                    attributes.Add(new TagAttribute(key, WebUtility.HtmlDecode(html[(at + 1)..close]), attributeStart, close + 1, at + 1, close, quote));
                    at = close + 1;
                }
                else
                {
                    var valueStart = at;
                    while (at < html.Length && !char.IsWhiteSpace(html[at]) && html[at] != '>')
                    {
                        at++;
                    }

                    attributes.Add(new TagAttribute(key, WebUtility.HtmlDecode(html[valueStart..at]), attributeStart, at, valueStart, at, '\0'));
                }
            }

            var end = html.IndexOf('>', Math.Min(at, html.Length - 1));
            return new Tag(html, name, attributes, start, end < 0 ? html.Length : end + 1);
        }
    }
}
