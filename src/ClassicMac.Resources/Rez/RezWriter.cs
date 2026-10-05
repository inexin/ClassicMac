using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Resources.Rez;

/// <summary>Which Rez compiler the source is for (docs/formats/output/rez.md).</summary>
public enum RezDialect
{
    /// <summary>MPW's: DeRez's output byte for byte (Mac OS Roman, CR line ends).</summary>
    Mpw,

    /// <summary>What both MPW's and Retro68's Rez compile alike: ASCII, LF line ends, nothing either reads differently.</summary>
    Portable,
}

/// <summary>How <see cref="RezWriter"/> writes.</summary>
public sealed record RezOptions
{
    /// <summary>The compiler the source is for.</summary>
    public RezDialect Dialect { get; init; } = RezDialect.Mpw;

    /// <summary>DeRez's <c>-e</c>: control bytes in names and types as they are, only the quote, backslash and (in names) $0D escaped (MPW only).</summary>
    public bool RawNames { get; init; }
}

/// <summary>
/// A resource fork as Rez source: one <c>data</c> statement per resource, in the fork's order, as MPW's DeRez writes it
/// (rez.md §3), or in the subset both Rez compilers read alike (rez.md §5).
/// </summary>
public static class RezWriter
{
    private const int CommentColumn = 55;

    // The attribute keywords DeRez writes, highest bit first.
    private static readonly (ResourceAttributes Bit, string Name)[] Keywords =
    [
        (ResourceAttributes.SystemHeap, "sysheap"), (ResourceAttributes.Purgeable, "purgeable"), (ResourceAttributes.Locked, "locked"),
        (ResourceAttributes.Protected, "protected"), (ResourceAttributes.Preload, "preload"),
    ];

    /// <summary>The fork's Rez source, as bytes (Mac OS Roman for MPW, ASCII for the portable dialect).</summary>
    public static byte[] Write(ResourceFork fork, RezOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(fork);
        options ??= new RezOptions();
        bool portable = options.Dialect == RezDialect.Portable;
        string end = portable ? "\n" : "\r";
        var output = new List<byte>();
        void Text(string text) => output.AddRange(Encoding.ASCII.GetBytes(text));
        void Bytes(IEnumerable<byte> bytes) => output.AddRange(bytes);

        foreach (var resource in fork.Resources)
        {
            string where = string.Create(CultureInfo.InvariantCulture, $"'{resource.Type}' ({resource.Id})");
            Text("data '");
            Bytes(TypeBytes(resource.Type, options, diagnostics, where));
            Text(string.Create(CultureInfo.InvariantCulture, $"' ({resource.Id}"));
            if (resource.Name is { } name)
            {
                if (portable && name.Length == 0)
                {
                    diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "rez.empty-name",
                        $"{where}: an empty name cannot be written in source both compilers read (\"\" is no name); left out."));
                }
                else
                {
                    Text(", \"");
                    Bytes(NameBytes(name.Bytes, options));
                    Text("\"");
                }
            }

            Bytes(Encoding.ASCII.GetBytes(Attributes(resource.Attributes, portable, diagnostics, where)));
            Text(") {" + end);
            var data = resource.GetData().Span;
            for (int line = 0; line < data.Length; line += 16)
            {
                var chunk = data.Slice(line, Math.Min(16, data.Length - line));
                var hex = new StringBuilder("\t$\"");
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (i > 0 && i % 2 == 0)
                    {
                        hex.Append(' ');
                    }

                    hex.Append(chunk[i].ToString("X2", CultureInfo.InvariantCulture));
                }

                hex.Append('"');
                Text(hex.ToString().PadRight(CommentColumn) + "/* ");
                Bytes(CommentBytes(chunk, portable));
                Text(" */" + end);
            }

            Text("};" + end + end);
        }

        return [.. output];
    }

    // A type: escapes for ', \ and control bytes, Mac OS Roman above $7F as is; a leading NUL (Rez's right-justified
    // three-character literal) left out. The portable dialect needs printable ASCII; anything else is written as MPW's.
    private static List<byte> TypeBytes(FourCC type, RezOptions options, ICollection<Diagnostic>? diagnostics, string where)
    {
        Span<byte> raw = stackalloc byte[4];
        type.CopyTo(raw);
        int start = raw[0] == 0 ? 1 : 0;
        var bytes = new List<byte>();
        bool portable = options.Dialect == RezDialect.Portable;
        foreach (var b in raw[start..])
        {
            if (portable && (b < 0x20 || b >= 0x7F || b is (byte)'\'' or (byte)'\\'))
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "rez.type",
                    $"{where}: the type is not four printable characters, which only MPW's Rez reads; written in MPW's form."));
                portable = false;
                bytes.Clear();
                foreach (var c in raw[start..])
                {
                    bytes.AddRange(Escaped(c, options, isType: true));
                }

                return bytes;
            }

            bytes.AddRange(options.RawNames && !portable && b is not ((byte)'\'' or (byte)'\\') ? [b] : Escaped(b, options, isType: true));
        }

        return bytes;
    }

    private static List<byte> NameBytes(ReadOnlySpan<byte> name, RezOptions options)
    {
        var bytes = new List<byte>();
        foreach (var b in name)
        {
            if (options.RawNames && options.Dialect == RezDialect.Mpw)
            {
                // DeRez -e still escapes the quote, the backslash and $0D; other control bytes go in as they are.
                bytes.AddRange(b is (byte)'"' or (byte)'\\' or 0x0D ?Escaped(b, options, isType: false) : [b]);
            }
            else if (options.Dialect == RezDialect.Portable)
            {
                bytes.AddRange(b is < 0x20 or >= 0x7F or (byte)'"' or (byte)'\\' ? Hex(b) : [b]);
            }
            else
            {
                bytes.AddRange(Escaped(b, options, isType: false));
            }
        }

        return bytes;
    }

    // DeRez's escapes: \" or \' (the quote of the literal), \\, \n for $0D in a name, \0xHH for other control bytes.
    private static byte[] Escaped(byte b, RezOptions options, bool isType) => b switch
    {
        (byte)'"' when !isType => [(byte)'\\', (byte)'"'],
        (byte)'\'' when isType => [(byte)'\\', (byte)'\''],
        (byte)'\\' => [(byte)'\\', (byte)'\\'],
        0x0D when !isType => [(byte)'\\', (byte)'n'],
        < 0x20 or 0x7F => Hex(b),
        _ => [b],
    };

    private static byte[] Hex(byte b) => Encoding.ASCII.GetBytes("\\0x" + b.ToString("X2", CultureInfo.InvariantCulture));

    // Keywords when every bit set has one, else the whole byte as $XX. The portable dialect drops the other bits.
    private static string Attributes(ResourceAttributes attributes, bool portable, ICollection<Diagnostic>? diagnostics, string where)
    {
        const ResourceAttributes named = ResourceAttributes.SystemHeap | ResourceAttributes.Purgeable | ResourceAttributes.Locked
            | ResourceAttributes.Protected | ResourceAttributes.Preload;
        if (attributes == 0)
        {
            return "";
        }

        if ((attributes & ~named) != 0)
        {
            if (!portable)
            {
                return string.Create(CultureInfo.InvariantCulture, $", ${(byte)attributes:X2}");
            }

            diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "rez.attributes",
                string.Create(CultureInfo.InvariantCulture,
                    $"{where}: attribute bits ${(byte)(attributes & ~named):X2} (compressed, changed or reserved) have no keyword both compilers read; left out.")));
            attributes &= named;
            if (attributes == 0)
            {
                return "";
            }
        }

        var text = new StringBuilder();
        foreach (var (bit, keyword) in Keywords)
        {
            if ((attributes & bit) != 0)
            {
                text.Append(", ").Append(keyword);
            }
        }

        return text.ToString();
    }

    // The comment: printable ASCII and Mac OS Roman above $7F as they are, $09 as ∆ and $0D as ¬ (MPW's invisibles),
    // other control bytes and $7F as '.', a '/' after '*' as '.' so the comment cannot end early. The portable dialect
    // keeps to ASCII: every byte outside $20–$7E is '.'.
    private static List<byte> CommentBytes(ReadOnlySpan<byte> chunk, bool portable)
    {
        var bytes = new List<byte>(chunk.Length);
        for (int i = 0; i < chunk.Length; i++)
        {
            byte b = chunk[i];
            byte shown = b switch
            {
                (byte)'/' when i > 0 && chunk[i - 1] == (byte)'*' => (byte)'.',
                0x09 when !portable => 0xC6,
                0x0D when !portable => 0xC2,
                < 0x20 or 0x7F => (byte)'.',
                >= 0x80 when portable => (byte)'.',
                _ => b,
            };
            bytes.Add(shown);
        }

        return bytes;
    }
}
