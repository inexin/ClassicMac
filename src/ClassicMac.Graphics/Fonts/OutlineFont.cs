using System;
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
        public static OutlineFont Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic>? diagnostics = null)
        {
            var reader = new ClassicMac.Core.BigEndianReader(data);
            return Read(reader, diagnostics);
        }

        /// <summary>Reads an sfnt from the reader's current position and advances past its bytes.</summary>
        public static OutlineFont Read(ClassicMac.Core.BigEndianReader reader, ICollection<Diagnostic>? diagnostics = null)
        {
            int start = reader.Position;
            int length = reader.Remaining;
            if (length < 12)
            {
                throw new InvalidDataException($"An sfnt needs a 12-byte offset table; this is {length} bytes.");
            }

            var count = reader.ReadUInt16At(start + 4);
            var tables = new List<OutlineTable>();
            var shortData = false;
            for (var i = 0; i < count; i++)
            {
                var at = 12 + 16 * i;
                if (at + 16 > length)
                {
                    shortData = true;
                    break;
                }
                var table = new OutlineTable(new FourCC(reader.ReadUInt32At(start + at)), reader.ReadUInt32At(start + at + 4),
                    reader.ReadUInt32At(start + at + 8), reader.ReadUInt32At(start + at + 12));
                if (table.Offset + (long)table.Length > length)
                {
                    shortData = true;
                }

                tables.Add(table);
            }
            string? family = null, subfamily = null, full = null;
            if (tables.Find(t => t.Tag == FourCC.FromString("name")) is { Length: >= 6 } names && names.Offset + (long)names.Length <= length)
            {
                reader.Position = start + (int)names.Offset;
                var tableReader = reader.ReadSubReader((int)names.Length);
                family = Name(tableReader, 1);
                tableReader.Position = 0;
                subfamily = Name(tableReader, 2);
                tableReader.Position = 0;
                full = Name(tableReader, 4);
            }
            if (shortData)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "font.short", "The sfnt's table directory points past its data."));
            }

            var result = new OutlineFont
            {
                Version = new FourCC(reader.ReadUInt32At(start)),
                Tables = tables,
                FamilyName = family,
                SubfamilyName = subfamily,
                FullName = full,
            };
            reader.Position = start + length;
            return result;
        }

        // A name record's string: the Macintosh platform's (1, Roman) first, else a Unicode platform's (0, or 3 with
        // encoding 1), UTF-16 big-endian.
        private static string? Name(BigEndianReader table, int nameId)
        {
            var count = table.ReadUInt16At(2);
            var strings = table.ReadUInt16At(4);
            string? unicode = null;
            for (var i = 0; i < count; i++)
            {
                var at = 6 + 12 * i;
                if (at + 12 > table.Length)
                {
                    break;
                }

                int platform = table.ReadUInt16At(at), encoding = table.ReadUInt16At(at + 2),
                    id = table.ReadUInt16At(at + 6), length = table.ReadUInt16At(at + 8),
                    offset = table.ReadUInt16At(at + 10);
                if (id != nameId || strings + offset + length > table.Length)
                {
                    continue;
                }

                var bytes = table.ReadBytesAt(strings + offset, length);
                if (platform == 1 && encoding == 0)
                {
                    return MacRoman.Decode(bytes);
                }

                if (unicode is null && (platform == 0 || (platform == 3 && encoding == 1)))
                {
                    unicode = Encoding.BigEndianUnicode.GetString(bytes);
                }
            }
            return unicode;
        }
    }

    /// <summary>A colour entry in a font colour table (<c>'fctb'</c>).</summary>
    /// <param name="Value">The pixel value this colour represents.</param>
    /// <param name="Red">The red component, 16 bits per channel.</param>
    /// <param name="Green">The green component, 16 bits per channel.</param>
    /// <param name="Blue">The blue component, 16 bits per channel.</param>
    public readonly record struct FontColorEntry(int Value, ushort Red, ushort Green, ushort Blue);

    /// <summary>Reads a font colour table (<c>'fctb'</c>, of the same ID as its <c>'NFNT'</c>).</summary>
    public static class FontColorTable
    {
        /// <summary>The (pixel value, red, green, blue) entries, 16 bits per component; empty under 8 bytes.</summary>
        public static IReadOnlyList<FontColorEntry> Read(ReadOnlyMemory<byte> data)
        {
            var reader = new BigEndianReader(data);
            return Read(reader);
        }

        /// <summary>Reads entries from the reader's current position and advances it past the table.</summary>
        public static IReadOnlyList<FontColorEntry> Read(BigEndianReader reader)
        {
            var entries = new List<FontColorEntry>();
            if (reader.Remaining < 8)
            {
                return entries;
            }

            reader.Skip(6);
            var count = reader.ReadInt16() + 1;
            for (var i = 0; i < count && reader.Remaining >= 8; i++)
            {
                entries.Add(new FontColorEntry(reader.ReadInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16()));
            }
            return entries;
        }
    }
}
