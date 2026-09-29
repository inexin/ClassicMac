using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Graphics.Fonts
{
    /// <summary>A table of an outline font: its tag, checksum, and where it is.</summary>
    public readonly record struct OutlineTable(FourCC Tag, uint Checksum, uint Offset, uint Length);

    /// <summary>
    /// An outline font (<c>'sfnt'</c>): the TrueType font file's bytes as they are, read for its table directory and its
    /// names. Writing the data out unchanged gives a <c>.ttf</c> file.
    /// </summary>
    public sealed class OutlineFont
    {
        /// <summary>The sfnt version: $00010000 or <c>'true'</c> for TrueType outlines, <c>'typ1'</c> for PostScript Type 1.</summary>
        public FourCC Version { get; private init; }

        /// <summary>The tables, as the directory lists them.</summary>
        public IReadOnlyList<OutlineTable> Tables { get; private init; } = [];

        /// <summary>The family name (name ID 1), or null.</summary>
        public string? FamilyName { get; private init; }

        /// <summary>The subfamily name (name ID 2), or null.</summary>
        public string? SubfamilyName { get; private init; }

        /// <summary>The full name (name ID 4), or null.</summary>
        public string? FullName { get; private init; }

        /// <summary>Whether it holds TrueType outlines (version $00010000 or <c>'true'</c>) and a <c>glyf</c> table.</summary>
        public bool IsTrueType => (Version.Value == 0x00010000 || Version == FourCC.FromString("true")) && Tables.Any(t => t.Tag == FourCC.FromString("glyf"));

        /// <summary>
        /// Reads the offset table and table directory, and the <c>name</c> table's names (Macintosh Roman, else Unicode).
        /// Throws <see cref="InvalidDataException"/> under 12 bytes.
        /// </summary>
        public static OutlineFont Read(ReadOnlySpan<byte> data, ICollection<Diagnostic>? diagnostics = null)
        {
            if (data.Length < 12) throw new InvalidDataException($"An sfnt needs a 12-byte offset table; this is {data.Length} bytes.");
            var count = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            var tables = new List<OutlineTable>();
            var shortData = false;
            for (var i = 0; i < count; i++)
            {
                var at = 12 + 16 * i;
                if (at + 16 > data.Length)
                {
                    shortData = true;
                    break;
                }
                var table = new OutlineTable(new FourCC(data.Slice(at, 4)), BinaryPrimitives.ReadUInt32BigEndian(data[(at + 4)..]),
                    BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]), BinaryPrimitives.ReadUInt32BigEndian(data[(at + 12)..]));
                if (table.Offset + (long)table.Length > data.Length) shortData = true;
                tables.Add(table);
            }
            string? family = null, subfamily = null, full = null;
            if (tables.Find(t => t.Tag == FourCC.FromString("name")) is { Length: >= 6 } names && names.Offset + (long)names.Length <= data.Length)
            {
                var table = data.Slice((int)names.Offset, (int)names.Length);
                family = Name(table, 1);
                subfamily = Name(table, 2);
                full = Name(table, 4);
            }
            if (shortData)
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "font.short", "The sfnt's table directory points past its data."));
            return new OutlineFont
            {
                Version = new FourCC(data[..4]),
                Tables = tables,
                FamilyName = family,
                SubfamilyName = subfamily,
                FullName = full,
            };
        }

        // A name record's string: the Macintosh platform's (1, Roman) first, else a Unicode platform's (0, or 3 with
        // encoding 1), UTF-16 big-endian.
        private static string? Name(ReadOnlySpan<byte> table, int nameId)
        {
            var count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            var strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
            string? unicode = null;
            for (var i = 0; i < count; i++)
            {
                var at = 6 + 12 * i;
                if (at + 12 > table.Length) break;
                var r = table[at..];
                int platform = BinaryPrimitives.ReadUInt16BigEndian(r), encoding = BinaryPrimitives.ReadUInt16BigEndian(r[2..]),
                    id = BinaryPrimitives.ReadUInt16BigEndian(r[6..]), length = BinaryPrimitives.ReadUInt16BigEndian(r[8..]),
                    offset = BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
                if (id != nameId || strings + offset + length > table.Length) continue;
                var bytes = table.Slice(strings + offset, length);
                if (platform == 1 && encoding == 0) return MacRoman.Decode(bytes);
                if (unicode is null && (platform == 0 || (platform == 3 && encoding == 1))) unicode = Encoding.BigEndianUnicode.GetString(bytes);
            }
            return unicode;
        }
    }

    /// <summary>A font colour table (<c>'fctb'</c>, of the same ID as its <c>'NFNT'</c>): a colour table indexed by pixel value.</summary>
    public static class FontColors
    {
        /// <summary>The (pixel value, red, green, blue) entries, 16 bits per component; empty under 8 bytes.</summary>
        public static IReadOnlyList<(int Value, ushort Red, ushort Green, ushort Blue)> Read(ReadOnlySpan<byte> data)
        {
            var entries = new List<(int, ushort, ushort, ushort)>();
            if (data.Length < 8) return entries;
            var count = BinaryPrimitives.ReadInt16BigEndian(data[6..]) + 1;
            for (var i = 0; i < count && 16 + i * 8 <= data.Length; i++)
            {
                var e = data[(8 + i * 8)..];
                entries.Add((BinaryPrimitives.ReadInt16BigEndian(e), BinaryPrimitives.ReadUInt16BigEndian(e[2..]),
                    BinaryPrimitives.ReadUInt16BigEndian(e[4..]), BinaryPrimitives.ReadUInt16BigEndian(e[6..])));
            }
            return entries;
        }
    }
}
