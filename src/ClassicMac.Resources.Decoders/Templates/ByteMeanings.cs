using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Templates
{
    /// <summary>What a byte of a resource is: a description, the field it belongs to, and the field's value.</summary>
    /// <param name="Text">What the byte is ("Character 1 of string 1, “Untitled”", "Length of string 2", "ID of item 3").</param>
    /// <param name="Start">The field's first byte (the field the byte is part of: a string's text, a word).</param>
    /// <param name="Length">The field's bytes.</param>
    /// <param name="Value">The byte's or field's value as text ("U", "8", "$FF"), or null for padding and ends.</param>
    public sealed record ByteMeaning(string Text, int Start, int Length, string? Value);

    /// <summary>
    /// What each byte of a resource means (the hex inspector's "In this resource"): for <c>'STR '</c> and
    /// <c>'STR#'</c> from their layout (strings.md), for other types from a <c>TMPL</c> read as ResEdit's template
    /// editor reads it (templates.md); other types have none.
    /// </summary>
    public static class ByteMeanings
    {
        /// <summary>
        /// The meaning of the byte at <paramref name="offset"/> in a resource of <paramref name="type"/> with
        /// <paramref name="data"/>; null outside the data, for types with no field map, and when the template cannot be
        /// used. <c>'STR '</c> and <c>'STR#'</c> are read as strings whatever the template.
        /// </summary>
        public static ByteMeaning? MeaningAt(FourCC type, ReadOnlySpan<byte> data, int offset, ResourceTemplate? template)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return null;
            }
            return type.ToString() switch
            {
                "STR " => InString(data, offset),
                "STR#" => InStringList(data, offset),
                _ => template is { Problems.Count: 0 } ? InTemplate(template, data, offset) : null,
            };
        }

        /// <summary>
        /// The meaning of the byte at <paramref name="offset"/> of resource <paramref name="id"/>: as the other overload,
        /// and for <c>'CODE'</c> its layout by ID (docs/formats/code/code-segments.md §1.2–§1.5): <c>'CODE'</c> 0's A5
        /// world and jump-table entries, another segment's near or far header, its code and far relocation lists.
        /// </summary>
        public static ByteMeaning? MeaningAt(FourCC type, short id, ReadOnlySpan<byte> data, int offset, ResourceTemplate? template)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return null;
            }

            return type.ToString() == "CODE" ? (id == 0 ? InJumpTable(data, offset) : InSegment(data, offset)) : MeaningAt(type, data, offset, template);
        }

        private static string Hex(int word) => "$" + word.ToString("X4", CultureInfo.InvariantCulture);

        private static int Word(ReadOnlySpan<byte> data, int at) => at + 2 <= data.Length ? data[at] << 8 | data[at + 1] : -1;

        private static long Long(ReadOnlySpan<byte> data, int at) =>
            at + 4 <= data.Length ? (long)(uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]) : -1;

        // 'CODE' 0 (§1.2–§1.3): the four sizes, then 8-byte entries from +$10; entry 1 the far marker in a far table, whose
        // later entries are segment, _LoadSeg and a 4-byte offset; near entries offset, MOVE.W #segment, segment, _LoadSeg.
        private static ByteMeaning? InJumpTable(ReadOnlySpan<byte> data, int offset)
        {
            if (offset < 16)
            {
                var at = offset & ~3;
                var label = (at / 4) switch
                {
                    0 => "Bytes above A5",
                    1 => "Bytes below A5 (the globals)",
                    2 => "Jump table size",
                    _ => "Jump table offset from A5",
                };
                return Long(data, at) is >= 0 and var value ? new ByteMeaning(label, at, 4, Number(value)) : null;
            }

            var entry = (offset - 16) / 8;
            var start = 16 + 8 * entry;
            if (start + 8 > data.Length)
            {
                return null;
            }

            var far = data.Length >= 32 && Word(data, 24) == 0 && Word(data, 26) == 0xFFFF && Long(data, 28) == 0;
            var name = $"Entry {entry}";
            if (far && entry == 1)
            {
                return new ByteMeaning($"{name}: the far table's marker", start, 8, null);
            }

            var within = offset - start;
            if (far && entry > 1)
            {
                return within switch
                {
                    < 2 => new ByteMeaning($"{name}: segment", start, 2, Number(Word(data, start))),
                    < 4 => new ByteMeaning($"{name}: _LoadSeg", start + 2, 2, Hex(Word(data, start + 2))),
                    _ => new ByteMeaning($"{name}: offset in segment {Word(data, start)}", start + 4, 4, Number(Long(data, start + 4))),
                };
            }

            var segment = Word(data, start + 4);
            return (within / 2) switch
            {
                0 => new ByteMeaning($"{name}: offset in segment {segment}'s code", start, 2, Number(Word(data, start))),
                1 => new ByteMeaning($"{name}: MOVE.W #segment,-(SP)", start + 2, 2, Hex(Word(data, start + 2))),
                2 => new ByteMeaning($"{name}: segment", start + 4, 2, Number(segment)),
                _ => new ByteMeaning($"{name}: _LoadSeg", start + 6, 2, Hex(Word(data, start + 6))),
            };
        }

        // A segment (§1.4–§1.5): the near header (first entry's offset, entry count) or the far one (marker $FFFF, the
        // near and far entries, the relocation lists' offsets and last relocations), then the code, then any far
        // relocation lists to the end.
        private static ByteMeaning? InSegment(ReadOnlySpan<byte> data, int offset)
        {
            static string Entry(long jtOffset) => $"(entry {jtOffset / 8})";
            if (Word(data, 0) != 0xFFFF)
            {
                if (offset < 4)
                {
                    if (offset < 2)
                    {
                        return new ByteMeaning($"First jump-table entry, as an offset {Entry(Word(data, 0))}", 0, 2, Number(Word(data, 0)));
                    }

                    return Word(data, 2) is >= 0 and var count ? new ByteMeaning("Number of jump-table entries", 2, 2, Number(count)) : null;
                }

                return new ByteMeaning($"Code, at +${offset - 4:X4}", 4, data.Length - 4, null);
            }

            if (offset < 0x28)
            {
                if (offset < 4)
                {
                    return offset < 2 ? new ByteMeaning("Far header marker", 0, 2, "$FFFF") : new ByteMeaning("Reserved", 2, 2, Number(Word(data, 2)));
                }

                var at = offset & ~3;
                var value = Long(data, at);
                if (value < 0)
                {
                    return null;
                }

                var label = at switch
                {
                    0x04 => $"First near jump-table entry, as an offset {Entry(value)}",
                    0x08 => "Number of near entries",
                    0x0C => $"First far jump-table entry, as an offset {Entry(value)}",
                    0x10 => "Number of far entries",
                    0x14 => "A5 relocation list's offset",
                    0x18 => "A5 at the last relocation",
                    0x1C => "PC relocation list's offset",
                    0x20 => "Address at the last relocation",
                    _ => "Reserved",
                };
                return new ByteMeaning(label, at, 4, Number(value));
            }

            // The relocation lists, each from its offset to the next list or the end; the code before the first.
            var length = data.Length;
            var lists = new[] { (Offset: Long(data, 0x14), Name: "A5 relocation list"), (Offset: Long(data, 0x1C), Name: "PC relocation list") }
                .Where(l => l.Offset >= 0x28 && l.Offset < length).OrderBy(l => l.Offset).ToList();
            var codeEnd = lists.Count > 0 ? (int)lists[0].Offset : data.Length;
            if (offset < codeEnd)
            {
                return new ByteMeaning($"Code, at +${offset - 0x28:X4}", 0x28, codeEnd - 0x28, null);
            }

            for (var i = 0; i < lists.Count; i++)
            {
                var from = (int)lists[i].Offset;
                var to = i + 1 < lists.Count ? (int)lists[i + 1].Offset : data.Length;
                if (offset >= from && offset < to)
                {
                    return new ByteMeaning(lists[i].Name, from, to - from, null);
                }
            }

            return null;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Character(byte b) => MacRoman.Decode([b]);

        // A text's characters: "Character k of <name>, “text”", the whole text as the field.
        private static ByteMeaning InText(ReadOnlySpan<byte> data, int start, int length, int offset, string name) =>
            new($"Character {offset - start + 1} of {name}, “{MacRoman.Decode(data.Slice(start, length))}”", start, length, Character(data[offset]));

        // 'STR ': a Pascal string (strings.md §1).
        private static ByteMeaning? InString(ReadOnlySpan<byte> data, int offset)
        {
            int length = Math.Min(data[0], data.Length - 1);
            if (offset == 0)
            {
                return new ByteMeaning("Length of the string", 0, 1, Number(data[0]));
            }
            return offset <= length
                ? InText(data, 1, length, offset, "the string")
                : new ByteMeaning("After the string", 1 + length, data.Length - 1 - length, null);
        }

        // 'STR#': a count word, then that many Pascal strings (strings.md §2).
        private static ByteMeaning? InStringList(ReadOnlySpan<byte> data, int offset)
        {
            int count = data.Length >= 2 ? data[0] << 8 | data[1] : data[0] << 8;
            if (offset < 2)
            {
                return new ByteMeaning("Number of strings", 0, 2, Number(count));
            }
            int at = 2;
            for (int i = 1; i <= count && at < data.Length; i++)
            {
                int length = Math.Min(data[at], data.Length - at - 1);
                if (offset == at)
                {
                    return new ByteMeaning($"Length of string {i}", at, 1, Number(data[at]));
                }
                if (offset <= at + length)
                {
                    return InText(data, at + 1, length, offset, $"string {i}");
                }
                at += 1 + data[at];
            }
            return new ByteMeaning("After the strings", at, data.Length - at, null);
        }

        // A type a template describes: the field the byte is in, named by its label and the list items it is in.
        private static ByteMeaning? InTemplate(ResourceTemplate template, ReadOnlySpan<byte> data, int offset)
        {
            var spans = template.Map(data);
            var here = spans.Where(s => offset >= s.Offset && offset - s.Offset < s.Length).ToList();
            if (here.Count == 0)
            {
                int end = spans.Count == 0 ? 0 : spans.Max(s => s.Offset + s.Length);
                return offset >= end ? new ByteMeaning("After the template's fields", end, data.Length - end, null) : null;
            }
            if (here.Count > 1 || here[0].Node.Type == "BBIT")
            {
                // The byte's bit fields, most significant first (templates.md §5): which named ones are set.
                var bits = data[offset];
                var named = here.Select((s, i) => (s.Node.Label, On: i < 8 && (bits & (0x80 >> i)) != 0)).Where(b => b.Label.Length > 0).ToList();
                static string List(IEnumerable<string> labels) => labels.Any() ? string.Join(", ", labels) : "none";
                var text = $"Bits on: {List(named.Where(b => b.On).Select(b => b.Label))}; off: {List(named.Where(b => !b.On).Select(b => b.Label))}";
                return new ByteMeaning(text, here[0].Offset, 1, "$" + Convert.ToHexString(data.Slice(offset, 1)));
            }
            return InField(here[0], data, offset);
        }

        private static string NameOf(TemplateSpan span) =>
            span.Items.Count == 0 ? span.Node.Label : $"{span.Node.Label} of item {string.Join(".", span.Items)}";

        private static ByteMeaning InField(TemplateSpan span, ReadOnlySpan<byte> data, int offset)
        {
            var name = NameOf(span);
            int start = span.Offset;
            int end = Math.Min(span.Offset + span.Length, data.Length);
            var type = span.Node.Type;
            if (span.IsListEnd)
            {
                return new ByteMeaning($"End of {span.Node.Label}", start, 1, null);
            }
            switch (type)
            {
                case "FBYT" or "FWRD" or "FLNG":
                    return new ByteMeaning(span.Node.Label.Length > 0 ? span.Node.Label : "Filler", start, span.Length, null);
                case "AWRD" or "ALNG":
                    return new ByteMeaning("Alignment", start, span.Length, null);
                case "PSTR" or "ESTR" or "OSTR":
                    return InCountedText(data, offset, start, end, 1, data[start], name);
                case "WSTR":
                    return InCountedText(data, offset, start, end, 2, end - start >= 2 ? data[start] << 8 | data[start + 1] : 0, name);
                case "LSTR":
                    return InCountedText(data, offset, start, end, 4,
                        end - start >= 4 ? (int)Math.Min(new BigEndianReader(data.Slice(start, 4).ToArray()).ReadUInt32(), int.MaxValue) : 0, name);
                case "CSTR" or "ECST" or "OCST":
                    {
                        int nul = data[start..end].IndexOf((byte)0);
                        int length = nul < 0 ? end - start : nul;
                        return offset < start + length
                            ? InText(data, start, length, offset, name)
                            : new ByteMeaning($"End of {name}", start + length, end - start - length, null);
                    }
                case "HEXD":
                    return InBytes(data, offset, start, end, name);
            }
            if (IsSized(type, out int size))
            {
                switch (type[0])
                {
                    case 'H':
                        return InBytes(data, offset, start, end, name);
                    case 'P':
                        return InCountedText(data, offset, start, end, 1, Math.Min(data[start], size), name);
                    default:   // Cnnn: a C string in a fixed field
                        {
                            int nul = data[start..end].IndexOf((byte)0);
                            int length = Math.Min(nul < 0 ? end - start : nul, Math.Max(0, size - 1));
                            return offset < start + length
                                ? InText(data, start, length, offset, name)
                                : new ByteMeaning($"Padding of {name}", start + length, end - start - length, null);
                        }
                }
            }
            return new ByteMeaning(name, start, span.Length, span.Text);
        }

        // A string with a count of `prefix` bytes in front: the count, the characters, then padding.
        private static ByteMeaning InCountedText(ReadOnlySpan<byte> data, int offset, int start, int end, int prefix, int count, string name)
        {
            if (offset < start + prefix)
            {
                return new ByteMeaning($"Length of {name}", start, prefix, Number(count));
            }
            int length = Math.Min(count, end - start - prefix);
            return offset < start + prefix + length
                ? InText(data, start + prefix, length, offset, name)
                : new ByteMeaning($"Padding of {name}", start + prefix + length, end - start - prefix - length, null);
        }

        private static ByteMeaning InBytes(ReadOnlySpan<byte> data, int offset, int start, int end, string name) =>
            new($"Byte {offset - start + 1} of {name}", start, end - start, "$" + Convert.ToHexString(data.Slice(offset, 1)));

        // Hnnn, Cnnn and Pnnn: a field of nnn (hex) bytes.
        private static bool IsSized(string type, out int size)
        {
            size = 0;
            return type[0] is 'H' or 'C' or 'P'
                && int.TryParse(type.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size);
        }
    }
}
