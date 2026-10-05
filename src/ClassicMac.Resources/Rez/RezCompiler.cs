using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Resources.Rez;

/// <summary>How <see cref="RezCompiler"/> reads its source.</summary>
public sealed record RezCompileOptions
{
    /// <summary>Retro68's escapes: <c>\n</c> $0A and <c>\r</c> $0D, and none in character literals (MPW's otherwise).</summary>
    public bool Retro68Escapes { get; init; }

    /// <summary>A file named by <c>read</c> or <c>$$Read</c>: its data fork. Null refuses them.</summary>
    public Func<string, byte[]>? ReadFile { get; init; }

    /// <summary>A file named by <c>include</c>: its resource fork. Null refuses it.</summary>
    public Func<string, ResourceFork>? ReadResourceFork { get; init; }
}

/// <summary>
/// Compiles Rez source into a resource fork (docs/formats/output/rez.md §2): <c>data</c>, <c>read</c> and
/// <c>include</c> statements, as MPW 3.6's Rez does. Typed <c>resource</c> statements are not compiled.
/// </summary>
public static class RezCompiler
{
    // The attribute keywords: the bit each sets or clears.
    private static readonly Dictionary<string, (ResourceAttributes Bit, bool Set)> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sysheap"] = (ResourceAttributes.SystemHeap, true), ["appheap"] = (ResourceAttributes.SystemHeap, false),
        ["purgeable"] = (ResourceAttributes.Purgeable, true), ["nonpurgeable"] = (ResourceAttributes.Purgeable, false),
        ["locked"] = (ResourceAttributes.Locked, true), ["unlocked"] = (ResourceAttributes.Locked, false),
        ["protected"] = (ResourceAttributes.Protected, true), ["unprotected"] = (ResourceAttributes.Protected, false),
        ["preload"] = (ResourceAttributes.Preload, true), ["nonpreload"] = (ResourceAttributes.Preload, false),
    };

    /// <summary>
    /// The resource fork the source makes, or null when it has an error (each in <paramref name="diagnostics"/>, with its
    /// line), as MPW's Rez writes no fork then.
    /// </summary>
    public static ResourceFork? Compile(ReadOnlySpan<byte> source, RezCompileOptions options, ICollection<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var parser = new Parser(new RezLexer(source.ToArray(), options.Retro68Escapes), options, diagnostics);
        try
        {
            return parser.Run();
        }
        catch (RezSyntaxException e)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, e.Code, e.Message));
            return null;
        }
    }

    private sealed class Parser(RezLexer lexer, RezCompileOptions options, ICollection<Diagnostic> diagnostics)
    {
        private readonly ResourceFork fork = new();
        private RezToken token = null!;

        public ResourceFork Run()
        {
            Advance();
            while (token.Kind != RezTokenKind.End)
            {
                if (token.Kind == RezTokenKind.Preprocessor)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "rez.preprocessor",
                        $"line {token.Line}: {token.Text.Trim()} is not read; ClassicMac's Rez has no preprocessor."));
                    Advance();
                    continue;
                }

                var word = Expect(RezTokenKind.Identifier, "a statement").Text.ToLowerInvariant();
                switch (word)
                {
                    case "data":
                        Data();
                        break;
                    case "read":
                        Read();
                        break;
                    case "include":
                        Include();
                        break;
                    case "type" or "resource" or "change" or "delete" or "enum" or "symbol":
                        throw Error("rez.typed", $"'{word}' statements (typed resources and their templates) are not compiled; use data statements.");
                    default:
                        throw Error("rez.syntax", $"expected a statement but got '{word}'.");
                }
            }

            return fork;
        }

        private RezSyntaxException Error(string code, string message) =>
            new(code, string.Create(CultureInfo.InvariantCulture, $"line {token.Line}: {message}"));

        private void Advance() => token = lexer.Next();

        private RezToken Expect(RezTokenKind kind, string what)
        {
            if (token.Kind != kind)
            {
                throw Error("rez.syntax", $"expected {what}.");
            }

            var t = token;
            Advance();
            return t;
        }

        private void Punct(char c)
        {
            if (token.Kind != RezTokenKind.Punctuation || token.Text[0] != c)
            {
                throw Error("rez.syntax", $"expected '{c}'.");
            }

            Advance();
        }

        private bool IsPunct(char c) => token.Kind == RezTokenKind.Punctuation && token.Text[0] == c;

        // 'TYPE' (id[, "name"][, attributes]): a hex-string type makes no resource (null), as MPW's Rez does.
        private (FourCC? Type, short Id, MacString? Name, ResourceAttributes Attributes) Spec()
        {
            FourCC? type = null;
            int line = token.Line;
            if (token.Kind == RezTokenKind.HexString)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "rez.hex-type",
                    $"line {line}: a hex string is not a type; MPW's Rez makes no resource of it and reports nothing."));
                Advance();
            }
            else
            {
                var literal = Expect(RezTokenKind.Literal, "a resource type ('TYPE')").Bytes;
                if (literal.Length > 4)
                {
                    throw Error("rez.syntax", "a resource type is at most four characters.");
                }

                var bytes = new byte[4];
                literal.CopyTo(bytes, 4 - literal.Length);                          // right-justified
                type = new FourCC(bytes);
            }

            Punct('(');
            short id = (short)Number(-32768, 32767, "a resource ID");
            MacString? name = null;
            ResourceAttributes set = 0, cleared = 0;
            bool first = true;
            while (IsPunct(','))
            {
                Advance();
                if (first && token.Kind is RezTokenKind.String or RezTokenKind.HexString)
                {
                    var bytes = token.Bytes;
                    if (bytes.Length > 255)
                    {
                        throw Error("rez.syntax", "a resource name is at most 255 bytes.");
                    }

                    name = bytes.Length == 0 ? null : new MacString(bytes);         // "" is no name
                    Advance();
                }
                else if (token.Kind == RezTokenKind.Identifier)
                {
                    if (!Keywords.TryGetValue(token.Text, out var keyword))
                    {
                        throw Error("rez.syntax", $"expected 'SYSHEAP', 'APPHEAP', 'PURGEABLE', 'NONPURGEABLE', 'LOCKED', 'UNLOCKED', 'PRELOAD', 'NONPRELOAD', 'PROTECTED', or 'UNPROTECTED', but got identifier ({token.Text}).");
                    }

                    if (((keyword.Set ? cleared : set) & keyword.Bit) != 0)
                    {
                        throw Error("rez.conflicting-attributes", "Too many or conflicting resource attributes.");
                    }

                    if (keyword.Set)
                    {
                        set |= keyword.Bit;
                    }
                    else
                    {
                        cleared |= keyword.Bit;
                    }

                    Advance();
                }
                else
                {
                    int value = (int)Number(0, 255, "resource attributes");
                    if ((value & 0x02) != 0)
                    {
                        throw Error("rez.invalid-attributes", string.Create(CultureInfo.InvariantCulture, $"Invalid resource attributes ({value})."));
                    }

                    if ((value & 0x81) != 0)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "rez.reserved-attributes",
                            $"line {token.Line}: Illegal or reserved resource attributes set."));
                    }

                    set |= (ResourceAttributes)value;
                }

                first = false;
            }

            Punct(')');
            return (type, id, name, set);
        }

        private long Number(long min, long max, string what)
        {
            bool negative = false;
            if (IsPunct('-'))
            {
                negative = true;
                Advance();
            }

            long value = Expect(RezTokenKind.Integer, what).Value * (negative ? -1 : 1);
            if (value < min || value > max)
            {
                throw Error("rez.syntax", string.Create(CultureInfo.InvariantCulture, $"{what} {value} is out of range."));
            }

            return value;
        }

        private void Add(FourCC? type, short id, MacString? name, ResourceAttributes attributes, byte[] data)
        {
            if (type is not { } t)
            {
                return;
            }

            if (fork.Find(t, id) is not null)
            {
                throw Error("rez.duplicate", string.Create(CultureInfo.InvariantCulture, $"resource '{t}' ({id}) is already defined."));
            }

            fork.Add(new Resource(t, id, data) { Name = name, Attributes = attributes });
        }

        // data 'TYPE' (…) { strings, hex strings, $$Read, $$Format … };
        private void Data()
        {
            var (type, id, name, attributes) = Spec();
            Punct('{');
            var data = new List<byte>();
            while (!IsPunct('}'))
            {
                data.AddRange(Value());
            }

            Advance();
            Punct(';');
            Add(type, id, name, attributes, [.. data]);
        }

        // One value of a data body: a string, a hex string or a function.
        private byte[] Value()
        {
            switch (token.Kind)
            {
                case RezTokenKind.String or RezTokenKind.HexString:
                    {
                        var bytes = token.Bytes;
                        Advance();
                        return bytes;
                    }

                case RezTokenKind.Function when token.Text.Equals("$$Read", StringComparison.OrdinalIgnoreCase):
                    {
                        Advance();
                        Punct('(');
                        var file = Expect(RezTokenKind.String, "a file name").Bytes;
                        Punct(')');
                        return ReadFile(file);
                    }

                case RezTokenKind.Function when token.Text.Equals("$$Format", StringComparison.OrdinalIgnoreCase):
                    return Format();
                default:
                    throw Error("rez.syntax", "expected a string, a hex string or a function in data.");
            }
        }

        // $$Format("format", values…): %d, %i, %ld, %x, %X, %c, %s and %%.
        private byte[] Format()
        {
            Advance();
            Punct('(');
            var format = Expect(RezTokenKind.String, "a format string").Bytes;
            var arguments = new List<RezToken>();
            while (IsPunct(','))
            {
                Advance();
                bool negative = IsPunct('-');
                if (negative)
                {
                    Advance();
                }

                var argument = token;
                if (argument.Kind is not (RezTokenKind.Integer or RezTokenKind.String))
                {
                    throw Error("rez.syntax", "a $$Format value is a number or a string.");
                }

                arguments.Add(negative ? argument with { Value = -argument.Value } : argument);
                Advance();
            }

            Punct(')');
            var output = new List<byte>();
            int next = 0;
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] != '%' || i + 1 >= format.Length)
                {
                    output.Add(format[i]);
                    continue;
                }

                char spec = (char)format[++i];
                if (spec == 'l' && i + 1 < format.Length)
                {
                    spec = (char)format[++i];
                }

                if (spec == '%')
                {
                    output.Add((byte)'%');
                    continue;
                }

                if (next >= arguments.Count)
                {
                    throw Error("rez.syntax", "$$Format has fewer values than its format asks for.");
                }

                var argument = arguments[next++];
                output.AddRange(spec switch
                {
                    'd' or 'i' => Encoding.ASCII.GetBytes(argument.Value.ToString(CultureInfo.InvariantCulture)),
                    'x' => Encoding.ASCII.GetBytes(argument.Value.ToString("x", CultureInfo.InvariantCulture)),
                    'X' => Encoding.ASCII.GetBytes(argument.Value.ToString("X", CultureInfo.InvariantCulture)),
                    'c' => [(byte)argument.Value],
                    's' => argument.Bytes,
                    _ => throw Error("rez.syntax", $"$$Format does not know %{spec}."),
                });
            }

            return [.. output];
        }

        // read 'TYPE' (…) "file";
        private void Read()
        {
            var (type, id, name, attributes) = Spec();
            var file = Expect(RezTokenKind.String, "a file name").Bytes;
            Punct(';');
            Add(type, id, name, attributes, ReadFile(file));
        }

        // include "file" ['TYPE' [(id)]]; — the resources copied as they are.
        private void Include()
        {
            var file = Expect(RezTokenKind.String, "a file name").Bytes;
            FourCC? only = null;
            short? onlyId = null;
            if (token.Kind == RezTokenKind.Literal)
            {
                var literal = token.Bytes;
                var bytes = new byte[4];
                literal.AsSpan(0, Math.Min(4, literal.Length)).CopyTo(bytes.AsSpan(4 - Math.Min(4, literal.Length)));
                only = new FourCC(bytes);
                Advance();
                if (IsPunct('('))
                {
                    Advance();
                    onlyId = (short)Number(-32768, 32767, "a resource ID");
                    Punct(')');
                }
            }

            Punct(';');
            if (options.ReadResourceFork is not { } readFork)
            {
                throw Error("rez.file", "include needs files, and none can be read here.");
            }

            ResourceFork other;
            try
            {
                other = readFork(MacRoman.Decode(file));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw Error("rez.file", $"{MacRoman.Decode(file)} cannot be read: {e.Message}");
            }

            foreach (var resource in other.Resources.Where(r => (only is null || r.Type == only) && (onlyId is null || r.Id == onlyId)))
            {
                if (fork.Find(resource.Type, resource.Id) is not null)
                {
                    throw Error("rez.duplicate", string.Create(CultureInfo.InvariantCulture, $"resource '{resource.Type}' ({resource.Id}) is already defined."));
                }

                fork.Add(new Resource(resource.Type, resource.Id, resource.GetData()) { Name = resource.Name, Attributes = resource.Attributes });
            }
        }

        private byte[] ReadFile(byte[] name)
        {
            if (options.ReadFile is not { } read)
            {
                throw Error("rez.file", "read needs files, and none can be read here.");
            }

            try
            {
                return read(MacRoman.Decode(name));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw Error("rez.file", $"{MacRoman.Decode(name)} cannot be read: {e.Message}");
            }
        }
    }
}
