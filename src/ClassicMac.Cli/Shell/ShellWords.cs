using System;
using System.Collections.Generic;
using System.Text;

namespace ClassicMac.Cli.Shell;

/// <summary>One word of a shell line: where it starts and its text with the double quotes taken off.</summary>
internal sealed record ShellWord(int Start, string Text);

/// <summary>
/// How the shell splits a line into words (docs/cli.md §5.2): spaces separate words; double quotes group a word
/// with spaces (and are taken off); a name that starts with a single quote runs to the next one, quotes kept (a
/// resource type, as in a Mac path); a backslash before a space or a double quote makes it part of the word, and
/// before anything else stays, for the Mac path's own escapes (docs/cli.md §1.2).
/// </summary>
internal static class ShellWords
{
    /// <summary>The words of a line.</summary>
    public static IReadOnlyList<ShellWord> Split(string line) => Scan(line, out _);

    /// <summary>The words of a line, and whether it ends inside double quotes.</summary>
    public static IReadOnlyList<ShellWord> Scan(string line, out bool openQuote)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = new List<ShellWord>();
        var word = new StringBuilder();
        var start = -1;
        var inDouble = false;
        var inSingle = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inDouble)
            {
                if (c == '"')
                {
                    inDouble = false;
                }
                else if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    word.Append(line[++i]);
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (inSingle)
            {
                word.Append(c);
                if (c == '\'')
                {
                    inSingle = false;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (start >= 0)
                {
                    words.Add(new ShellWord(start, word.ToString()));
                    word.Clear();
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            if (c == '"')
            {
                inDouble = true;
            }
            else if (c == '\'' && NameStart(word) && line.IndexOf('\'', i + 1) > 0)
            {
                word.Append(c);
                inSingle = true;
            }
            else if (c == '\\' && i + 1 < line.Length && (line[i + 1] == ' ' || line[i + 1] == '"'))
            {
                word.Append(line[++i]);
            }
            else
            {
                word.Append(c);
            }
        }

        if (start >= 0)
        {
            words.Add(new ShellWord(start, word.ToString()));
        }

        openQuote = inDouble;
        return words;
    }

    // Whether the next character starts a name: the word's start, or just after a separator.
    private static bool NameStart(StringBuilder word) => word.Length == 0 || word[^1] is ':' or '/';

    /// <summary>A word as it must be typed: in double quotes when it holds a space outside a resource type's quotes.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inSingle = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'' && (inSingle || i == 0 || text[i - 1] is ':' or '/'))
            {
                inSingle = !inSingle;
            }
            else if ((c == ' ' || c == '"') && !inSingle)
            {
                return "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            }
        }

        return text;
    }
}
