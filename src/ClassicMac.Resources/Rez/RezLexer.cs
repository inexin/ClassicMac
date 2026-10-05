using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Resources.Rez;

internal enum RezTokenKind
{
    End,
    Identifier,
    Integer,
    String,
    HexString,
    Literal,
    Function,
    Punctuation,
    Preprocessor,
}

// One token: its kind, its text (identifiers, functions, punctuation), its value (integers) or bytes (strings, hex strings,
// character literals), and the line it starts on.
internal sealed record RezToken(RezTokenKind Kind, string Text, long Value, byte[] Bytes, int Line);

/// <summary>Raised for source Rez refuses; the compile stops (rez.md §2).</summary>
internal sealed class RezSyntaxException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Rez's lexical layer (docs/formats/output/rez.md §1): comments, integers ($ hex, 0x, 0b, leading-0 octal), strings and
// character literals with MPW's escapes (or Retro68's \n and \r), hex strings, $$ functions and punctuation. The source
// is bytes: Mac OS Roman text goes into strings as it is.
internal sealed class RezLexer(byte[] source, bool retro68)
{
    private int at;
    private int line = 1;

    public RezToken Next()
    {
        while (true)
        {
            SkipSpace();
            if (at >= source.Length)
            {
                return Token(RezTokenKind.End, "", 0, []);
            }

            byte c = source[at];
            if (c == '/' && Peek(1) == '*')
            {
                int start = line;
                at += 2;
                while (at < source.Length && !(source[at] == '*' && Peek(1) == '/'))
                {
                    Advance();
                }

                if (at >= source.Length)
                {
                    throw Error("rez.syntax", $"line {start}: a comment is not closed.");
                }

                at += 2;
                continue;
            }

            if (c == '/' && Peek(1) == '/')
            {
                while (at < source.Length && source[at] is not ((byte)'\r' or (byte)'\n'))
                {
                    at++;
                }

                continue;
            }

            if (c == '#')
            {
                int start = line, from = at;
                while (at < source.Length && source[at] is not ((byte)'\r' or (byte)'\n'))
                {
                    at++;
                }

                return new RezToken(RezTokenKind.Preprocessor, Encoding.ASCII.GetString(source, from, at - from), 0, [], start);
            }

            break;
        }

        byte b = source[at];
        if (b == '"')
        {
            at++;
            return Token(RezTokenKind.String, "", 0, Quoted((byte)'"'));
        }

        if (b == '\'')
        {
            at++;
            return Token(RezTokenKind.Literal, "", 0, Quoted((byte)'\''));
        }

        if (b == '$' && Peek(1) == '"')
        {
            at += 2;
            return Token(RezTokenKind.HexString, "", 0, Hex());
        }

        if (b == '$' && Peek(1) == '$')
        {
            int from = at;
            at += 2;
            while (at < source.Length && IsWord(source[at]))
            {
                at++;
            }

            return Token(RezTokenKind.Function, Encoding.ASCII.GetString(source, from, at - from), 0, []);
        }

        if (b == '$' || char.IsAsciiDigit((char)b))
        {
            return Token(RezTokenKind.Integer, "", Integer(), []);
        }

        if (IsWord(b))
        {
            int from = at;
            while (at < source.Length && IsWord(source[at]))
            {
                at++;
            }

            return Token(RezTokenKind.Identifier, Encoding.ASCII.GetString(source, from, at - from), 0, []);
        }

        at++;
        return Token(RezTokenKind.Punctuation, ((char)b).ToString(), 0, []);
    }

    private RezToken Token(RezTokenKind kind, string text, long value, byte[] bytes) => new(kind, text, value, bytes, line);

    private static RezSyntaxException Error(string code, string message) => new(code, message);

    private int Peek(int offset) => at + offset < source.Length ? source[at + offset] : -1;

    private void Advance()
    {
        if (source[at] == '\n' || (source[at] == '\r' && Peek(1) != '\n'))
        {
            line++;
        }

        at++;
    }

    private void SkipSpace()
    {
        while (at < source.Length && source[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0C)
        {
            Advance();
        }
    }

    private static bool IsWord(byte b) => char.IsAsciiLetterOrDigit((char)b) || b == '_';

    // $hex, 0x hex, 0b binary, a leading 0 octal, else decimal.
    private long Integer()
    {
        int from = at;
        int radix = 10;
        if (source[at] == '$')
        {
            radix = 16;
            at++;
        }
        else if (source[at] == '0' && Peek(1) is 'x' or 'X')
        {
            radix = 16;
            at += 2;
        }
        else if (source[at] == '0' && Peek(1) is 'b' or 'B')
        {
            radix = 2;
            at += 2;
        }
        else if (source[at] == '0' && Peek(1) is >= '0' and <= '7')
        {
            radix = 8;
            at++;
        }

        long value = 0;
        int digits = 0;
        while (at < source.Length && Digit(source[at], radix) is { } d)
        {
            value = value * radix + d;
            digits++;
            at++;
            if (value > uint.MaxValue)
            {
                throw Error("rez.syntax", $"line {line}: the number {Encoding.ASCII.GetString(source, from, at - from)} is too large.");
            }
        }

        if (digits == 0 && radix != 10 && !(radix == 8))
        {
            throw Error("rez.syntax", $"line {line}: a number has no digits.");
        }

        return value;
    }

    private static int? Digit(byte b, int radix)
    {
        int d = b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => 99,
        };
        return d < radix ? d : null;
    }

    // A string or character literal up to its closing quote, with escapes (none in Retro68's character literals).
    private byte[] Quoted(byte quote)
    {
        int start = line;
        var bytes = new List<byte>();
        while (true)
        {
            if (at >= source.Length)
            {
                throw Error("rez.syntax", $"line {start}: a {(quote == '"' ? "string" : "literal")} is not closed.");
            }

            byte c = source[at];
            if (c == quote)
            {
                at++;
                return [.. bytes];
            }

            if (c == '\\' && !(retro68 && quote == '\''))
            {
                at++;
                bytes.Add(Escape());
                continue;
            }

            bytes.Add(c);
            Advance();
        }
    }

    private byte Escape()
    {
        if (at >= source.Length)
        {
            throw Error("rez.syntax", $"line {line}: an escape is not finished.");
        }

        byte c = source[at++];
        switch (c)
        {
            case (byte)'n':
                return retro68 ? (byte)0x0A : (byte)0x0D;
            case (byte)'r':
                return retro68 ? (byte)0x0D : (byte)0x0A;
            case (byte)'t':
                return 0x09;
            case (byte)'b':
                return 0x08;
            case (byte)'v':
                return 0x0B;
            case (byte)'f':
                return 0x0C;
            case (byte)'?':
                return 0x7F;
            case (byte)'$':
                return (byte)Fixed(16, 2, "Bad syntax in hex escape. Format is: \\0Xnn or \\$nn.");
            case (byte)'0' when Peek(0) is 'x' or 'X':
                at++;
                return (byte)Fixed(16, 2, "Bad syntax in hex escape. Format is: \\0Xnn or \\$nn.");
            case (byte)'0' when Peek(0) is 'd' or 'D':
                at++;
                return (byte)Fixed(10, 3, "Bad syntax in decimal escape. Format is: \\0Dnnn.");
            case (byte)'0' when Peek(0) is 'b' or 'B':
                at++;
                return (byte)Fixed(2, 8, "Bad syntax in binary escape. Format is: \\0Bnnnnnnnn.");
            case >= (byte)'0' and <= (byte)'7':
                {
                    int value = c - '0';
                    for (int i = 0; i < 2 && at < source.Length && source[at] is >= (byte)'0' and <= (byte)'7'; i++)
                    {
                        value = value * 8 + (source[at++] - '0');
                    }

                    return (byte)value;
                }
            default:
                return c;                                                           // \' \" \\ and any other \c
        }
    }

    private int Fixed(int radix, int count, string message)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            if (at >= source.Length || Digit(source[at], radix) is not { } d)
            {
                throw Error("rez.syntax", string.Create(CultureInfo.InvariantCulture, $"line {line}: {message}"));
            }

            value = value * radix + d;
            at++;
        }

        return value;
    }

    // $"…": hex digits, white space between them ignored; an odd count is refused.
    private byte[] Hex()
    {
        int start = line;
        var digits = new List<int>();
        while (true)
        {
            if (at >= source.Length)
            {
                throw Error("rez.syntax", $"line {start}: a hex string is not closed.");
            }

            byte c = source[at];
            if (c == '"')
            {
                at++;
                break;
            }

            if (Digit(c, 16) is { } d)
            {
                digits.Add(d);
                at++;
            }
            else if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                Advance();
            }
            else
            {
                throw Error("rez.syntax", $"line {line}: '{(char)c}' is not a hex digit.");
            }
        }

        if (digits.Count % 2 != 0)
        {
            throw Error("rez.syntax", $"line {start}: a hex string has an odd number of digits.");
        }

        var bytes = new byte[digits.Count / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(digits[2 * i] << 4 | digits[2 * i + 1]);
        }

        return bytes;
    }
}
