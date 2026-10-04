using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClassicMac.Cli.Shell;

/// <summary>A shell command line that does not make sense: a usage error (exit 2).</summary>
internal sealed class ShellUsage(string message) : Exception(message);

/// <summary>A shell command's words: its arguments in order, its flags, and its options with values.</summary>
internal sealed class ShellOptions
{
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    private ShellOptions()
    {
    }

    /// <summary>The words that are not options.</summary>
    public List<string> Positional { get; } = [];

    /// <summary>
    /// Reads a command's words: <paramref name="flagNames"/> stand alone, <paramref name="valueNames"/> take the next
    /// word; any other word starting with <c>--</c> is refused, and so is a count of arguments outside
    /// <paramref name="min"/>…<paramref name="max"/>.
    /// </summary>
    public static ShellOptions Parse(IReadOnlyList<string> words, string[] flagNames, string[] valueNames, int min, int max)
    {
        var parsed = new ShellOptions();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (flagNames.Contains(word))
            {
                parsed.flags.Add(word);
            }
            else if (valueNames.Contains(word))
            {
                if (i + 1 >= words.Count)
                {
                    throw new ShellUsage($"{word} needs a value.");
                }

                parsed.values[word] = words[++i];
            }
            else if (word.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ShellUsage($"There is no option {word} here.");
            }
            else
            {
                parsed.Positional.Add(word);
            }
        }

        if (parsed.Positional.Count < min || parsed.Positional.Count > max)
        {
            throw new ShellUsage(min == max
                ? $"Give {min} argument{(min == 1 ? "" : "s")}; help shows how."
                : $"Give {min} to {max} arguments; help shows how.");
        }

        return parsed;
    }

    /// <summary>Whether a flag was given.</summary>
    public bool Flag(string name) => flags.Contains(name);

    /// <summary>An option's value, or null.</summary>
    public string? Value(string name) => values.GetValueOrDefault(name);

    /// <summary>A whole-number option, or <paramref name="fallback"/>.</summary>
    public long Long(string name, long fallback) => Value(name) switch
    {
        null => fallback,
        var text when long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        var text => throw new ShellUsage($"{name} '{text}' is not a whole number from 1."),
    };
}
