using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// One labelled value of the property view: the value in plain words, the raw value beside it (mono, muted), a
/// derived note after it ("500 × 282 pixels"), and for a boolean the dot's state.
/// </summary>
/// <param name="Decimal">The value to copy as decimal; null when it is not an integer.</param>
/// <param name="Hex">The value to copy as hexadecimal ("0x780A"); null when it is not an integer.</param>
/// <param name="Json">The value to copy as JSON.</param>
public sealed record PropertyRow(string Label, string Value, bool IsMono = false, string? Raw = null, string? Note = null, bool? Flag = null,
    string? Decimal = null, string? Hex = null, string Json = "")
{
    public bool HasRaw => Raw is not null;

    public bool HasNote => Note is not null;

    public bool IsFlag => Flag is not null;

    public bool IsYes => Flag == true;

    public bool IsNumber => Decimal is not null;
}

/// <summary>A card of the property view: a caption, then its rows.</summary>
public sealed record PropertyCard(string Caption, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// The property view (design/boards/property-view.md, P2): a decoder's JSON read as labelled values in cards. The
/// object's own values make the first card; each nested object, and each object in a list, a card of its own. A
/// number beside a name of it (<c>font</c> and <c>fontName</c>) reads as the name with the number raw; rectangles and
/// points read as coordinates.
/// </summary>
public static class PropertyView
{
    /// <summary>The most cards a list of objects shows; the rest are counted, to be read in the JSON.</summary>
    public const int MaxCards = 100;

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["id"] = "ID", ["ids"] = "IDs", ["json"] = "JSON", ["url"] = "URL", ["rgb"] = "RGB", ["os"] = "OS",
    };

    /// <summary>The cards for <paramref name="json"/>, the first captioned <paramref name="caption"/>; none when it is not a JSON object.</summary>
    public static IReadOnlyList<PropertyCard> FromJson(string json, string caption)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var cards = new List<PropertyCard>();
            AddObject(document.RootElement, caption, null, cards);
            return cards;
        }
    }

    // The object's values as a card (when it has any), then its objects' and lists' cards.
    // Nested objects' captions follow the path below the top card: "Style", "Style › Extra".
    private static void AddObject(JsonElement element, string caption, string? path, List<PropertyCard> cards)
    {
        var rows = new List<PropertyRow>();
        var nested = new List<(string Caption, JsonElement Value)>();
        var names = element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var label = Label(property.Name);
            var value = property.Value;
            // fontName beside font: the name goes on font's row.
            if (property.Name.EndsWith("Name", StringComparison.Ordinal) && names.Contains(property.Name[..^4])
                && element.GetProperty(property.Name[..^4]).ValueKind == JsonValueKind.Number)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && element.TryGetProperty(property.Name + "Name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                rows.Add(Number(label, value) with { Value = name.GetString()!, IsMono = false, Raw = value.GetRawText() });
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (Coordinates(label, value) is { } coordinates)
                {
                    rows.Add(coordinates);
                }
                else
                {
                    nested.Add((path is null ? label : $"{path} › {label}", value));
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Object))
            {
                nested.Add((Singular(label), value));
                continue;
            }

            rows.Add(Scalar(label, value));
        }

        if (rows.Count > 0)
        {
            cards.Add(new PropertyCard(caption, rows));
        }

        foreach (var (title, value) in nested)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                AddObject(value, title, title, cards);
            }
            else
            {
                AddList(value, title, cards);
            }
        }
    }

    private static void AddList(JsonElement list, string singular, List<PropertyCard> cards)
    {
        var items = list.EnumerateArray().ToList();
        for (var i = 0; i < Math.Min(items.Count, MaxCards); i++)
        {
            var caption = string.Create(CultureInfo.InvariantCulture, $"{singular} {i + 1}");
            if (items[i].ValueKind == JsonValueKind.Object)
            {
                var before = cards.Count;
                AddObject(items[i], caption, caption, cards);
                if (cards.Count == before)
                {
                    cards.Add(new PropertyCard(caption, [new PropertyRow("", "Empty")]));
                }
            }
            else
            {
                cards.Add(new PropertyCard(caption, [Scalar("Value", items[i])]));
            }
        }

        if (items.Count > MaxCards)
        {
            var more = string.Create(CultureInfo.InvariantCulture, $"{items.Count - MaxCards} more {singular.ToLowerInvariant()}s; see JSON.");
            cards.Add(new PropertyCard($"{singular}s", [new PropertyRow("", more)]));
        }
    }

    private static PropertyRow Scalar(string label, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Number(label, value),
        JsonValueKind.String => new PropertyRow(label, value.GetString()!, Json: value.GetRawText()),
        JsonValueKind.True => new PropertyRow(label, "Yes", Flag: true, Json: "true"),
        JsonValueKind.False => new PropertyRow(label, "No", Flag: false, Json: "false"),
        JsonValueKind.Array when value.GetArrayLength() == 0 => new PropertyRow(label, "None", Json: "[]"),
        JsonValueKind.Array => new PropertyRow(label, string.Join(", ", value.EnumerateArray().Select(Plain)),
            value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number), Json: value.GetRawText()),
        _ => new PropertyRow(label, "—", Json: "null"),
    };

    private static string Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Null => "—",
        _ => value.GetRawText(),
    };

    private static PropertyRow Number(string label, JsonElement value)
    {
        var text = value.GetRawText();
        if (!value.TryGetInt64(out var n))
        {
            return new PropertyRow(label, text, true, Json: text);
        }

        var hex = n < 0
            ? string.Create(CultureInfo.InvariantCulture, $"-0x{-n:X}")
            : string.Create(CultureInfo.InvariantCulture, $"0x{n:X}");
        return new PropertyRow(label, text, true, Decimal: text, Hex: hex, Json: text);
    }

    // {top, left, bottom, right} and {v, h} as coordinates; a rectangle's size after it.
    private static PropertyRow? Coordinates(string label, JsonElement value)
    {
        var keys = value.EnumerateObject().Select(p => p.Name).ToList();
        if (keys.Any(k => value.GetProperty(k).ValueKind != JsonValueKind.Number))
        {
            return null;
        }

        int Get(string key) => value.GetProperty(key).GetInt32();
        if (keys.Count == 4 && keys.All(k => k is "top" or "left" or "bottom" or "right"))
        {
            var (top, left, bottom, right) = (Get("top"), Get("left"), Get("bottom"), Get("right"));
            return new PropertyRow(label, string.Create(CultureInfo.InvariantCulture, $"{top}, {left}, {bottom}, {right}"), true,
                Note: string.Create(CultureInfo.InvariantCulture, $"{right - left} × {bottom - top} pixels"), Json: value.GetRawText());
        }

        if (keys.Count == 2 && keys.Contains("v") && keys.Contains("h"))
        {
            return new PropertyRow(label, string.Create(CultureInfo.InvariantCulture, $"{Get("v")}, {Get("h")}"), true, Json: value.GetRawText());
        }

        return null;
    }

    // "fontFamilyId" → "Font family ID".
    private static string Label(string key)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in key)
        {
            if ((char.IsUpper(c) || c == '_' || c == ' ') && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }

            if (c is not '_' and not ' ')
            {
                word.Append(char.ToLowerInvariant(c));
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        var text = string.Join(" ", words.Select(w => Words.GetValueOrDefault(w, w)));
        return text.Length == 0 ? key : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // "Runs" → "Run", "Entries" → "Entry".
    private static string Singular(string label) =>
        label.EndsWith("ies", StringComparison.Ordinal) ? label[..^3] + "y"
        : label.EndsWith('s') && !label.EndsWith("ss", StringComparison.Ordinal) ? label[..^1]
        : label;
}
