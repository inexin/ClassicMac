using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Templates
{
    /// <summary>One line of a <c>TMPL</c>: a label and a four-character field type.</summary>
    public sealed record TemplateField(string Label, string Type);

    /// <summary>
    /// A template field in its list structure: a list begin (<c>LSTB</c>, <c>LSTC</c>, <c>LSTZ</c>) holds the fields
    /// up to its <c>LSTE</c> as <see cref="Children"/>.
    /// </summary>
    public sealed class TemplateNode
    {
        internal TemplateNode(TemplateField field, IReadOnlyList<TemplateNode> children) => (Field, Children) = (field, children);

        /// <summary>The template line.</summary>
        public TemplateField Field { get; }

        /// <summary>A list's fields; empty for other fields.</summary>
        public IReadOnlyList<TemplateNode> Children { get; }

        /// <summary>The field's label.</summary>
        public string Label => Field.Label;

        /// <summary>The field type (<c>DWRD</c>, <c>PSTR</c>, <c>LSTC</c>…).</summary>
        public string Type => Field.Type;

        /// <summary>A list begin, whose fields are <see cref="Children"/>.</summary>
        public bool IsList => Type is "LSTB" or "LSTC" or "LSTZ";

        /// <summary>A count (<c>OCNT</c>, <c>ZCNT</c>): written from the item count of the list after it.</summary>
        public bool IsCount => Type is "OCNT" or "ZCNT";

        /// <summary>A field that holds no value: fillers (<c>FBYT</c>, <c>FWRD</c>, <c>FLNG</c>) and alignment
        /// (<c>AWRD</c>, <c>ALNG</c>), written as zeros.</summary>
        public bool IsHidden => Type is "FBYT" or "FWRD" or "FLNG" or "AWRD" or "ALNG";

        /// <summary>A one-bit (<c>BBIT</c>) or boolean (<c>BOOL</c>) field: its text is "1" or "0".</summary>
        public bool IsFlag => Type is "BBIT" or "BOOL";

        /// <inheritdoc/>
        public override string ToString() => $"{Label} ({Type})";
    }

    /// <summary>A field's value: text for a scalar, items for a list.</summary>
    public abstract record TemplateValue(TemplateNode Node);

    /// <summary>
    /// A scalar field's value as text, the way it is shown and typed: decimal numbers, <c>$</c>-prefixed hex, a
    /// <c>RECT</c> as "top, left, bottom, right", strings and characters as text, hex dumps as hex digits, flags as
    /// "1" or "0".
    /// </summary>
    public sealed record TemplateScalar(TemplateNode Node, string Text) : TemplateValue(Node);

    /// <summary>A list's items, each the values of the list's fields.</summary>
    public sealed record TemplateList(TemplateNode Node, IReadOnlyList<IReadOnlyList<TemplateValue>> Items) : TemplateValue(Node);

    /// <summary>What <see cref="ResourceTemplate.Read"/> found.</summary>
    /// <param name="Values">The values of the template's fields (hidden fields left out).</param>
    /// <param name="MissingBytes">Bytes the template needed past the end of the data, read as zeros.</param>
    /// <param name="Extra">Data past what the template consumed.</param>
    public sealed record TemplateReadResult(IReadOnlyList<TemplateValue> Values, int MissingBytes, ReadOnlyMemory<byte> Extra);

    /// <summary>
    /// A <c>TMPL</c> resource, and reading and writing resource data through it as ResEdit 2.1.3's template editor
    /// does: the resource's name is the type it describes; its fields are <c>{Str255 label; OSType type}</c> pairs
    /// to the end of the resource. ResEdit 2.1.3 knows 31 field types and <c>Hnnn</c>, <c>Cnnn</c>, <c>Pnnn</c>
    /// (<c>nnn</c> three uppercase hex digits); a template with any other, or with a malformed list, is refused.
    /// </summary>
    public sealed class ResourceTemplate
    {
        private static readonly HashSet<string> Known =
        [
            "DBYT", "DWRD", "DLNG", "HBYT", "HWRD", "HLNG", "CHAR", "TNAM", "BOOL", "BBIT", "RECT", "PSTR", "ESTR", "OSTR",
            "WSTR", "LSTR", "CSTR", "ECST", "OCST", "HEXD", "FBYT", "FWRD", "FLNG", "AWRD", "ALNG", "OCNT", "ZCNT",
            "LSTC", "LSTB", "LSTZ", "LSTE",
        ];

        private ResourceTemplate(IReadOnlyList<TemplateField> fields)
        {
            Fields = fields;
            Problems = Check(fields);
            Nodes = Nest(fields);
        }

        /// <summary>The template's lines, in order.</summary>
        public IReadOnlyList<TemplateField> Fields { get; }

        /// <summary>The fields in their list structure.</summary>
        public IReadOnlyList<TemplateNode> Nodes { get; }

        /// <summary>Why ResEdit 2.1.3 would refuse the template; empty when it is usable.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Reads a <c>TMPL</c> resource's data.</summary>
        /// <exception cref="InvalidDataException">The data ends inside a field.</exception>
        public static ResourceTemplate Parse(ReadOnlySpan<byte> data)
        {
            var fields = new List<TemplateField>();
            for (int at = 0; at < data.Length;)
            {
                int length = data[at];
                if (at + 1 + length + 4 > data.Length) throw new InvalidDataException($"The template ends inside the field at offset {at}.");
                fields.Add(new TemplateField(MacRoman.Decode(data.Slice(at + 1, length)), MacRoman.Decode(data.Slice(at + 1 + length, 4))));
                at += 5 + length;
            }
            return new ResourceTemplate(fields);
        }

        /// <summary>The <c>TMPL</c> in <paramref name="fork"/> for resources of <paramref name="type"/>: the one named
        /// with the type's four characters, as ResEdit finds it (Get1NamedResource); null when there is none.</summary>
        public static Resource? Find(ResourceFork fork, FourCC type)
        {
            ArgumentNullException.ThrowIfNull(fork);
            var name = type.ToString();
            return fork.OfType(FourCC.FromString("TMPL")).FirstOrDefault(r => r.Name is { } n && n.ToMacRoman() == name);
        }

        // ---- checking (ResEdit's template check: STR# 150 #4-#11) ----

        private static bool IsSized(string type) =>
            type.Length == 4 && type[0] is 'H' or 'C' or 'P' && type.Skip(1).All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

        private static int SizeOf(string type) => int.Parse(type.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        private static List<string> Check(IReadOnlyList<TemplateField> fields)
        {
            var problems = new List<string>();
            int depth = 0, lstbDepth = 0, bits = 0;
            var previous = "";
            for (int i = 0; i < fields.Count; i++)
            {
                var type = fields[i].Type;
                if (type == "HEXD" && i < fields.Count - 1) problems.Add($"HEXD (“{fields[i].Label}”) is not the last field.");
                if (type == "BBIT") bits++;
                else
                {
                    if (bits % 8 != 0) problems.Add($"A run of {bits} BBIT fields before “{fields[i].Label}” is not a multiple of 8.");
                    bits = 0;
                }
                if (type != "LSTC" && previous is "OCNT" or "ZCNT") problems.Add($"The count before “{fields[i].Label}” is not followed by LSTC.");
                if (type is "LSTB" or "LSTC" or "LSTZ")
                {
                    depth++;
                    if (type == "LSTC" && previous is not ("OCNT" or "ZCNT")) problems.Add($"LSTC (“{fields[i].Label}”) does not follow an OCNT or ZCNT.");
                    if (type == "LSTB")
                    {
                        if (lstbDepth > 0) problems.Add($"LSTB (“{fields[i].Label}”) follows another LSTB.");
                        else lstbDepth = depth;
                    }
                }
                else if (type == "LSTE")
                {
                    depth--;
                    // ResEdit clears its LSTB mark only when depth returns to the LSTB's own depth (a list nested in it
                    // closing), so a second LSTB after one without a nested list is refused too.
                    if (depth == lstbDepth) lstbDepth = 0;
                }
                if (!Known.Contains(type) && !IsSized(type)) problems.Add($"“{fields[i].Label}” has the unknown field type '{type}'.");
                previous = type;
            }
            if (depth > 0) problems.Add("A list has no LSTE.");
            else if (depth < 0) problems.Add("An LSTE has no list begin.");
            if (bits % 8 != 0) problems.Add($"A run of {bits} BBIT fields at the end is not a multiple of 8.");
            return problems;
        }

        private static List<TemplateNode> Nest(IReadOnlyList<TemplateField> fields)
        {
            var root = new List<TemplateNode>();
            var stack = new Stack<(TemplateField Field, List<TemplateNode> Children)>();
            var current = root;
            foreach (var field in fields)
            {
                if (field.Type is "LSTB" or "LSTC" or "LSTZ")
                {
                    stack.Push((field, current));
                    current = [];
                }
                else if (field.Type == "LSTE")
                {
                    if (stack.Count == 0) continue;
                    var (list, parent) = stack.Pop();
                    parent.Add(new TemplateNode(list, current));
                    current = parent;
                }
                else
                {
                    current.Add(new TemplateNode(field, []));
                }
            }
            while (stack.Count > 0)
            {
                var (list, parent) = stack.Pop();
                parent.Add(new TemplateNode(list, current));
                current = parent;
            }
            return root;
        }

        // ---- reading ----

        /// <summary>
        /// Reads resource data through the template. Where the data ends before the template does, the missing bytes
        /// read as zeros (ResEdit offers to append them); data after the last field is returned as
        /// <see cref="TemplateReadResult.Extra"/> (ResEdit offers to cut it off).
        /// </summary>
        /// <exception cref="InvalidOperationException">The template has <see cref="Problems"/>.</exception>
        public TemplateReadResult Read(ReadOnlySpan<byte> data)
        {
            if (Problems.Count > 0) throw new InvalidOperationException(Problems[0]);
            var reader = new Reader(data.ToArray());
            var values = reader.Items(Nodes);
            if (reader.Bit != 7) reader.Position++;
            int end = Math.Min(reader.Position, data.Length);
            return new TemplateReadResult(values, reader.Missing, data[end..].ToArray());
        }

        private sealed class Reader(byte[] data)
        {
            public int Position;
            public int Bit = 7;
            public int Missing;

            private int End => data.Length;

            // `count` bytes at the position; bytes past the end read as zeros.
            private byte[] Take(int count)
            {
                var bytes = new byte[count];
                int available = Math.Clamp(End - Position, 0, count);
                data.AsSpan(Math.Min(Position, End), available).CopyTo(bytes);
                Missing = Math.Max(Missing, Position + count - End);
                Position += count;
                return bytes;
            }

            public List<TemplateValue> Items(IReadOnlyList<TemplateNode> nodes)
            {
                var values = new List<TemplateValue>();
                int count = 0;
                foreach (var node in nodes)
                {
                    if (node.IsList)
                    {
                        var items = new List<IReadOnlyList<TemplateValue>>();
                        switch (node.Type)
                        {
                            case "LSTC":
                                for (int i = 0; i < count; i++) items.Add(Items(node.Children));
                                break;
                            case "LSTB":
                                while (Position < End) items.Add(Items(node.Children));
                                break;
                            default:   // LSTZ: to a 0 byte at the start of an item, which is consumed
                                while (Position < End)
                                {
                                    if (data[Position] == 0) { Position++; break; }
                                    items.Add(Items(node.Children));
                                }
                                break;
                        }
                        values.Add(new TemplateList(node, items));
                        continue;
                    }
                    var text = Field(node.Type);
                    if (node.IsCount)
                    {
                        int value = int.Parse(text, CultureInfo.InvariantCulture);
                        count = node.Type == "ZCNT" ? (value + 1) & 0xFFFF : value;
                    }
                    if (!node.IsHidden) values.Add(new TemplateScalar(node, text));
                }
                return values;
            }

            private string Field(string type)
            {
                switch (type)
                {
                    case "DBYT": return ((sbyte)Take(1)[0]).ToString(CultureInfo.InvariantCulture);
                    case "DWRD": return new BigEndianReader(Take(2)).ReadInt16().ToString(CultureInfo.InvariantCulture);
                    case "DLNG": return new BigEndianReader(Take(4)).ReadInt32().ToString(CultureInfo.InvariantCulture);
                    case "HBYT": return "$" + Convert.ToHexString(Take(1));
                    case "HWRD": return "$" + Convert.ToHexString(Take(2));
                    case "HLNG": return "$" + Convert.ToHexString(Take(4));
                    case "OCNT" or "ZCNT": return new BigEndianReader(Take(2)).ReadUInt16().ToString(CultureInfo.InvariantCulture);
                    case "CHAR": { var b = Take(1); return b[0] == 0 ? "" : MacRoman.Decode(b); }
                    case "TNAM": return MacRoman.Decode(Take(4));
                    case "BOOL": return Take(2)[0] != 0 ? "1" : "0";   // only the first byte counts
                    case "RECT":
                    {
                        var rect = new BigEndianReader(Take(8)).ReadMacRect();
                        return string.Join(", ", rect.Top, rect.Left, rect.Bottom, rect.Right);
                    }
                    case "BBIT":
                    {
                        int bit = Position < End ? (data[Position] >> Bit) & 1 : 0;
                        if (Position >= End) Missing = Math.Max(Missing, Position + 1 - End);
                        if (--Bit < 0) { Bit = 7; Position++; }
                        return bit.ToString(CultureInfo.InvariantCulture);
                    }
                    case "PSTR" or "ESTR" or "OSTR":
                    {
                        int n = Take(1)[0];
                        int pad = type == "ESTR" && n % 2 == 0 || type == "OSTR" && n % 2 == 1 ? 1 : 0;
                        return Text(n, pad);
                    }
                    case "WSTR": return Text(new BigEndianReader(Take(2)).ReadUInt16(), 0);
                    case "LSTR": return Text((int)Math.Min(new BigEndianReader(Take(4)).ReadUInt32(), int.MaxValue), 0);
                    case "CSTR" or "ECST" or "OCST":
                    {
                        int start = Math.Min(Position, End);
                        int nul = Array.IndexOf(data, (byte)0, start);
                        int n = (nul < 0 ? End : nul) - start;
                        int extra = type == "ECST" && n % 2 == 0 || type == "OCST" && n % 2 == 1 ? 2 : 1;
                        var text = MacRoman.Decode(data.AsSpan(start, n));
                        Position = Math.Min(start + n + extra, End);
                        return text;
                    }
                    case "HEXD":
                    {
                        var text = Convert.ToHexString(data.AsSpan(Math.Min(Position, End)));
                        Position = End;
                        return text;
                    }
                    case "FBYT": Take(1); return "";
                    case "FWRD": Take(2); return "";
                    case "FLNG": Take(4); return "";
                    case "AWRD": Position += Position & 1; return "";
                    case "ALNG": Position += -Position & 3; return "";
                }
                int size = SizeOf(type);
                switch (type[0])
                {
                    case 'H': return Convert.ToHexString(Take(size));
                    case 'C':
                    {
                        var b = Take(size);
                        int nul = Array.IndexOf(b, (byte)0);
                        return MacRoman.Decode(b.AsSpan(0, nul < 0 || nul > size - 1 ? Math.Max(0, size - 1) : nul));
                    }
                    default:
                    {
                        var b = Take(size + 1);
                        return MacRoman.Decode(b.AsSpan(1, Math.Min(b[0], size)));
                    }
                }
            }

            // n characters of text, then `pad` bytes; the position stops at the end of the data.
            private string Text(int n, int pad)
            {
                int start = Math.Min(Position, End);
                int length = Math.Min(n, End - start);
                var text = MacRoman.Decode(data.AsSpan(start, length));
                Position = Math.Min(start + n + pad, End);
                return text;
            }
        }

        // ---- writing ----

        /// <summary>A new list item: the list's fields with empty or zero values, nested lists empty.</summary>
        public static IReadOnlyList<TemplateValue> NewItem(TemplateNode list)
        {
            ArgumentNullException.ThrowIfNull(list);
            return Defaults(list.Children);
        }

        /// <summary>The template's fields with empty or zero values.</summary>
        public IReadOnlyList<TemplateValue> NewValues() => Defaults(Nodes);

        private static List<TemplateValue> Defaults(IReadOnlyList<TemplateNode> nodes)
        {
            var values = new List<TemplateValue>();
            foreach (var node in nodes)
            {
                if (node.IsList) values.Add(new TemplateList(node, []));
                else if (!node.IsHidden) values.Add(new TemplateScalar(node, DefaultText(node.Type)));
            }
            return values;
        }

        private static string DefaultText(string type) => type switch
        {
            "DBYT" or "DWRD" or "DLNG" or "BOOL" or "BBIT" or "OCNT" => "0",
            "ZCNT" => "65535",
            "HBYT" => "$00",
            "HWRD" => "$0000",
            "HLNG" => "$00000000",
            "RECT" => "0, 0, 0, 0",
            "TNAM" => "    ",
            _ => "",
        };

        /// <summary>
        /// The data for <paramref name="values"/> (in the shape <see cref="Read"/> gives), then <paramref name="extra"/>.
        /// Counts are written from the item count of the list after them; fillers and alignment as zeros.
        /// </summary>
        /// <exception cref="ArgumentException">A value is not one its field can hold.</exception>
        public byte[] Write(IReadOnlyList<TemplateValue> values, ReadOnlySpan<byte> extra = default)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (Problems.Count > 0) throw new InvalidOperationException(Problems[0]);
            var writer = new Writer();
            writer.Items(Nodes, values);
            writer.Flush();
            writer.Output.Write(extra);
            return writer.Output.ToArray();
        }

        private sealed class Writer
        {
            public readonly MemoryStream Output = new();
            private int bits, bitCount;

            public void Flush()
            {
                if (bitCount == 0) return;
                Output.WriteByte((byte)(bits << (8 - bitCount)));
                bits = bitCount = 0;
            }

            public void Items(IReadOnlyList<TemplateNode> nodes, IReadOnlyList<TemplateValue> values)
            {
                int v = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node.Type != "BBIT") Flush();
                    if (node.IsHidden)
                    {
                        int pad = node.Type switch
                        {
                            "FBYT" => 1, "FWRD" => 2, "FLNG" => 4,
                            "AWRD" => (int)(Output.Length & 1),
                            _ => (int)(-Output.Length & 3),
                        };
                        Output.Write(new byte[pad]);
                        continue;
                    }
                    if (v >= values.Count || !ReferenceEquals(values[v].Node, node))
                        throw new ArgumentException($"No value for “{node.Label}”.", nameof(values));
                    var value = values[v++];
                    if (node.IsList)
                    {
                        var list = (TemplateList)value;
                        foreach (var item in list.Items)
                        {
                            Items(node.Children, item);
                            Flush();
                        }
                        if (node.Type == "LSTZ") Output.WriteByte(0);
                        continue;
                    }
                    var text = ((TemplateScalar)value).Text;
                    if (node.IsCount)
                    {
                        // The count of the LSTC that follows (ResEdit keeps it in step as items are added and removed).
                        int count = i + 1 < nodes.Count && nodes[i + 1].IsList && v < values.Count && values[v] is TemplateList next ? next.Items.Count : 0;
                        if (count > 0xFFFF) throw new ArgumentException($"“{node.Label}”: too many items.");
                        U16(node.Type == "ZCNT" ? (count - 1) & 0xFFFF : count);
                        continue;
                    }
                    Field(node, text);
                }
            }

            private void U16(int value) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)value); Output.Write(s); }

            private void Number(TemplateNode node, string text, int bytes)
            {
                long value = ParseNumber(node, text);
                long min = -(1L << (bytes * 8 - 1)), max = (1L << (bytes * 8)) - 1;
                if (value < min || value > max) throw new ArgumentException($"“{node.Label}”: {text.Trim()} does not fit in {bytes} byte{(bytes == 1 ? "" : "s")}.");
                for (int b = bytes - 1; b >= 0; b--) Output.WriteByte((byte)(value >> (8 * b)));
            }

            private static long ParseNumber(TemplateNode node, string text)
            {
                var t = text.Trim();
                bool ok = t.StartsWith('$')
                    ? long.TryParse(t.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value)
                    : long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
                if (!ok) throw new ArgumentException($"“{node.Label}”: “{text}” is not a number (decimal, or hex after $).");
                return value;
            }

            private static byte[] Encode(TemplateNode node, string text, int max)
            {
                if (!MacRoman.TryEncode(text, out var bytes)) throw new ArgumentException($"“{node.Label}”: the text is not all Mac OS Roman.");
                if (bytes.Length > max) throw new ArgumentException($"“{node.Label}”: the text is longer than {max} characters.");
                return bytes;
            }

            private static byte[] Hex(TemplateNode node, string text)
            {
                var digits = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (digits.StartsWith('$')) digits = digits[1..];
                try
                {
                    if (digits.Length % 2 != 0) throw new FormatException();
                    return Convert.FromHexString(digits);
                }
                catch (FormatException)
                {
                    throw new ArgumentException($"“{node.Label}”: “{text}” is not whole hex bytes.");
                }
            }

            private void Field(TemplateNode node, string text)
            {
                var type = node.Type;
                switch (type)
                {
                    case "DBYT" or "HBYT": Number(node, text, 1); return;
                    case "DWRD" or "HWRD": Number(node, text, 2); return;
                    case "DLNG" or "HLNG": Number(node, text, 4); return;
                    case "CHAR":
                    {
                        var b = Encode(node, text, 1);
                        Output.WriteByte(b.Length == 0 ? (byte)0 : b[0]);
                        return;
                    }
                    case "TNAM":
                    {
                        var b = Encode(node, text, 4);
                        Output.Write(b);
                        for (int i = b.Length; i < 4; i++) Output.WriteByte((byte)' ');
                        return;
                    }
                    case "BOOL": Output.Write(Flag(node, text) ? new byte[] { 1, 0 } : new byte[2]); return;
                    case "BBIT":
                        bits = (bits << 1) | (Flag(node, text) ? 1 : 0);
                        if (++bitCount == 8) Flush();
                        return;
                    case "RECT":
                    {
                        var parts = text.Split(',');
                        if (parts.Length != 4) throw new ArgumentException($"“{node.Label}”: a rectangle is four numbers: top, left, bottom, right.");
                        foreach (var part in parts) Number(node, part, 2);
                        return;
                    }
                    case "PSTR" or "ESTR" or "OSTR":
                    {
                        var b = Encode(node, text, 255);
                        Output.WriteByte((byte)b.Length);
                        Output.Write(b);
                        if (type == "ESTR" && b.Length % 2 == 0 || type == "OSTR" && b.Length % 2 == 1) Output.WriteByte(0);
                        return;
                    }
                    case "WSTR":
                    {
                        var b = Encode(node, text, 0xFFFF);
                        U16(b.Length);
                        Output.Write(b);
                        return;
                    }
                    case "LSTR":
                    {
                        var b = Encode(node, text, int.MaxValue);
                        Span<byte> s = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)b.Length);
                        Output.Write(s);
                        Output.Write(b);
                        return;
                    }
                    case "CSTR" or "ECST" or "OCST":
                    {
                        var b = Encode(node, text, int.MaxValue);
                        if (Array.IndexOf(b, (byte)0) >= 0) throw new ArgumentException($"“{node.Label}”: a C string cannot hold a NUL.");
                        Output.Write(b);
                        Output.WriteByte(0);
                        if (type == "ECST" && b.Length % 2 == 0 || type == "OCST" && b.Length % 2 == 1) Output.WriteByte(0);
                        return;
                    }
                    case "HEXD": Output.Write(Hex(node, text)); return;
                }
                int size = SizeOf(type);
                switch (type[0])
                {
                    case 'H':
                    {
                        var b = Hex(node, text);
                        if (b.Length > size) throw new ArgumentException($"“{node.Label}”: more than {size} bytes.");
                        Output.Write(b);
                        Output.Write(new byte[size - b.Length]);
                        return;
                    }
                    case 'C':
                    {
                        var b = Encode(node, text, Math.Max(0, size - 1));
                        Output.Write(b);
                        Output.Write(new byte[size - b.Length]);
                        return;
                    }
                    default:
                    {
                        var b = Encode(node, text, Math.Min(size, 255));
                        Output.WriteByte((byte)b.Length);
                        Output.Write(b);
                        Output.Write(new byte[size - b.Length]);
                        return;
                    }
                }
            }

            private static bool Flag(TemplateNode node, string text) => text.Trim() switch
            {
                "1" or "true" or "True" => true,
                "0" or "false" or "False" or "" => false,
                _ => throw new ArgumentException($"“{node.Label}”: “{text}” is not 1 or 0."),
            };
        }
    }
}
