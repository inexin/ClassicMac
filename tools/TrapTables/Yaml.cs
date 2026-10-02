namespace TrapTables;

/// <summary>One top-level item of a Multiversal definition file: its kind and its scalar fields.</summary>
sealed class Item(string kind, string file)
{
    public string Kind { get; } = kind;
    public string File { get; } = file;
    public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    public List<Dictionary<string, string>> Args { get; } = [];
    public List<(string Name, string Value)> Values { get; } = [];
    public string this[string key] => Fields.TryGetValue(key, out var v) ? v : throw new InvalidDataException($"{File}: {Kind} has no {key}");
    public bool Has(string key) => Fields.ContainsKey(key);
}

/// <summary>
/// Reads the subset of YAML that Multiversal's defs use: a list of single-key maps (<c>- function:</c>) whose
/// fields are indented four spaces, with block scalars (<c>|</c>) and lists of maps (<c>args</c>, <c>values</c>).
/// Items marked <c>- api:</c> (Carbon-only variants) are skipped.
/// </summary>
static class Yaml
{
    public static IEnumerable<Item> Read(string file)
    {
        var result = new List<Item>();
        Item? item = null;
        string? list = null;
        Dictionary<string, string>? entry = null;
        int blockIndent = -1;
        foreach (string raw in System.IO.File.ReadLines(file))
        {
            string line = raw.TrimEnd();
            int indent = line.Length - line.TrimStart().Length;
            if (blockIndent >= 0)
            {
                if (line.Length == 0 || indent >= blockIndent)
                {
                    continue;
                }

                blockIndent = -1;
            }
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (indent == 0)
            {
                item = null;
                list = null;
                if (line.StartsWith("- ", StringComparison.Ordinal) && line.EndsWith(':') && !line.StartsWith("- api", StringComparison.Ordinal))
                {
                    item = new Item(line[2..^1], file);
                    result.Add(item);
                }
                continue;
            }
            if (item == null)
            {
                continue;
            }

            string text = line.TrimStart();
            if (indent == 4 && !text.StartsWith("- ", StringComparison.Ordinal))
            {
                list = null;
                var (key, value) = Split(text);
                if (value == "|" || value == ">")
                {
                    blockIndent = indent + 1;
                }
                else if (value.Length == 0)
                {
                    list = key;
                }
                else
                {
                    item.Fields[key] = Unquote(value);
                }

                continue;
            }
            if (list == null)
            {
                continue;
            }

            if (text.StartsWith("- ", StringComparison.Ordinal))
            {
                entry = new Dictionary<string, string>(StringComparer.Ordinal);
                if (list == "args")
                {
                    item.Args.Add(entry);
                }

                text = text[2..];
                if (!text.Contains(':'))
                {
                    continue;   // a scalar list item (variants)
                }
            }
            if (entry == null)
            {
                continue;
            }

            var (k, v) = Split(text);
            if (v == "|" || v == ">")
            {
                blockIndent = indent + 1;
                continue;
            }
            entry[k] = Unquote(v);
            if (list == "values" && entry.TryGetValue("name", out var n) && entry.TryGetValue("value", out var val) && (k == "name" || k == "value"))
            {
                item.Values.Add((n, val));
            }
        }
        return result;
    }

    private static (string Key, string Value) Split(string text)
    {
        int colon = text.IndexOf(':');
        return colon < 0 ? (text, "") : (text[..colon].Trim(), text[(colon + 1)..].Trim());
    }

    // Keeps the quotes of an OSType ('abcd'), which TryParseOSType reads.
    private static string Unquote(string v) => v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;
}
