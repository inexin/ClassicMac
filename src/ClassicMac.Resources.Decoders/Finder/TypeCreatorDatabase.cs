using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Finder
{
    /// <summary>
    /// A type and creator database (docs/formats/resources/finder.md §2.6): TCDB's records, which ClassicMac ships as its
    /// own TSV (<see cref="Shipped"/>), or a spreadsheet (xlsx) in TCDB's layout the user supplies (<see cref="Load"/>),
    /// which replaces the shipped one. It names documents that neither the volume nor ClassicMac's own table knows;
    /// <see cref="KnownKinds.Resolve"/> asks it last.
    /// </summary>
    public sealed class TypeCreatorDatabase
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly FourCC Appl = FourCC.FromString("APPL");
        private static readonly FourCC[] AnyCode = [FourCC.FromString("****"), FourCC.FromString("????")];
        private const string Unspecified = "Unspecified Creator";

        /// <summary>The embedded resource holding the shipped data (tools/TcdbData writes it).</summary>
        internal const string ShippedResource = "ClassicMac.Resources.Decoders.Finder.tcdb.tsv.gz";

        /// <summary>The shipped TSV's column header line.</summary>
        internal const string TsvHeader = "Type\tCreator\tFile name\tComments\tCategory\tExtension";

        private static readonly Lazy<TypeCreatorDatabase> shipped = new(LoadShipped);

        private readonly Dictionary<(FourCC Type, FourCC? Creator), Entry> kinds = [];
        private readonly Dictionary<FourCC, (string Name, int Rank)> applications = [];

        private sealed record Entry(string Text, string? Application, int Rank);

        private TypeCreatorDatabase(FinderKindSource source) => Source = source;

        /// <summary>
        /// TCDB 2003.10 (Type/Creator Database by Ilan Szekely), as ClassicMac ships it: read from the embedded resource the
        /// first time it is asked for, then kept.
        /// </summary>
        public static TypeCreatorDatabase Shipped => shipped.Value;

        /// <summary>Where its kinds say they come from: <see cref="FinderKindSource.Database"/> (shipped) or <see cref="FinderKindSource.UserDatabase"/>.</summary>
        public FinderKindSource Source { get; }

        /// <summary>How many kinds it holds: type and creator pairs, and types for any creator.</summary>
        public int Count => kinds.Count;

        /// <summary>How many records (rows) were read, used or not.</summary>
        public int RecordCount { get; private set; }

        /// <summary>The kind of a document of <paramref name="type"/> and <paramref name="creator"/>: the pair's, else the type's for any creator; null when unknown.</summary>
        public FinderKind? Find(FourCC type, FourCC creator) =>
            kinds.TryGetValue((type, creator), out var entry) || kinds.TryGetValue((type, null), out entry)
                ? new FinderKind(entry.Text, Source, entry.Application, null)
                : null;

        /// <summary>The name of the application with <paramref name="signature"/> (its <c>APPL</c> row, else its "any type" row), or null.</summary>
        public string? ApplicationName(FourCC signature) => applications.TryGetValue(signature, out var known) ? known.Name : null;

        /// <summary>Reads the spreadsheet's first sheet (<c>xl/worksheets/sheet1.xml</c> with <c>xl/sharedStrings.xml</c>).</summary>
        /// <exception cref="InvalidDataException">It is not an xlsx, or its first row names no Type and Creator columns.</exception>
        public static TypeCreatorDatabase Load(Stream xlsx)
        {
            ArgumentNullException.ThrowIfNull(xlsx);
            try
            {
                using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);
                var shared = zip.GetEntry("xl/sharedStrings.xml") is { } strings ? SharedStrings(strings) : [];
                var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException("The spreadsheet has no first sheet (xl/worksheets/sheet1.xml).");
                var database = new TypeCreatorDatabase(FinderKindSource.UserDatabase);
                database.Read(Rows(sheet, shared));
                return database;
            }
            catch (XmlException e)
            {
                throw new InvalidDataException($"The spreadsheet's XML cannot be read: {e.Message}", e);
            }
        }

        /// <summary>Whether this build carries the shipped data (a build without the generated file has none).</summary>
        public static bool HasShippedData => typeof(TypeCreatorDatabase).Assembly.GetManifestResourceInfo(ShippedResource) is not null;

        // The embedded data; an empty database when the build has none.
        private static TypeCreatorDatabase LoadShipped()
        {
            using var resource = typeof(TypeCreatorDatabase).Assembly.GetManifestResourceStream(ShippedResource);
            if (resource is null)
            {
                return new TypeCreatorDatabase(FinderKindSource.Database);
            }

            using var unzipped = new GZipStream(resource, CompressionMode.Decompress);
            using var reader = new StreamReader(unzipped, Encoding.UTF8);
            return ReadTsv(reader);
        }

        /// <summary>
        /// Reads ClassicMac's TSV of TCDB's records (finder.md §2.6): UTF-8, '#' comment lines, then <see cref="TsvHeader"/>,
        /// then one record a line. Codes are their four bytes, with <c>\xHH</c> for a byte that is not printable ASCII or is a
        /// backslash; a code of another length is kept in the file and skipped here.
        /// </summary>
        /// <exception cref="InvalidDataException">The header is missing, a row has fewer than six columns, or an escape is bad.</exception>
        internal static TypeCreatorDatabase ReadTsv(TextReader reader)
        {
            var database = new TypeCreatorDatabase(FinderKindSource.Database);
            var header = false;
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                number++;
                if (!header && line.StartsWith('#'))     // comments come before the header ("#BIN" is a type)
                {
                    continue;
                }

                if (!header)
                {
                    header = line == TsvHeader ? true : throw new InvalidDataException($"Line {number} is not the header \"{TsvHeader}\".");
                    continue;
                }

                var fields = line.Split('\t');
                if (fields.Length < 6)
                {
                    throw new InvalidDataException($"Line {number} has {fields.Length} columns, not 6.");
                }

                database.RecordCount++;
                if (Unescape(fields[0], number) is { Length: 4 } type && Unescape(fields[1], number) is { Length: 4 } creator)
                {
                    database.Add(new FourCC(type), new FourCC(creator), Blank(fields[2]), Blank(fields[3]), Blank(fields[4]));
                }
            }

            return header ? database : throw new InvalidDataException("The data has no header line.");
        }

        private static string? Blank(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        // A code's bytes from its text: printable ASCII as itself, \xHH for any other byte.
        private static byte[] Unescape(string text, int line)
        {
            var bytes = new List<byte>(4);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\')
                {
                    bytes.Add(text[i] is >= ' ' and <= '~' ? (byte)text[i] : throw new InvalidDataException($"Line {line} has a code with a character to escape."));
                    continue;
                }

                if (text.Length - i < 4 || text[i + 1] != 'x'
                    || !byte.TryParse(text.AsSpan(i + 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var value))
                {
                    throw new InvalidDataException($"Line {line} has a bad escape in a code.");
                }

                bytes.Add(value);
                i += 3;
            }
            return [.. bytes];
        }

        private void Read(IEnumerable<string?[]> rows)
        {
            int type = -1, creator = -1, name = -1, comments = -1, category = -1;
            var first = true;
            foreach (var row in rows)
            {
                if (first)
                {
                    first = false;
                    string? At(int i) => row[i]?.Trim();
                    for (var i = 0; i < row.Length; i++)
                    {
                        switch (At(i)?.ToUpperInvariant())
                        {
                            case "TYPE":
                                type = i;
                                break;
                            case "CREATOR":
                                creator = i;
                                break;
                            case "FILE NAME":
                                name = i;
                                break;
                            case "COMMENTS":
                                comments = i;
                                break;
                            case "CATEGORY":
                                category = i;
                                break;
                        }
                    }
                    if (type < 0 || creator < 0)
                    {
                        throw new InvalidDataException("The spreadsheet's first row names no Type and Creator columns.");
                    }
                    continue;
                }

                string? Cell(int i) => i >= 0 && i < row.Length ? row[i] : null;
                RecordCount++;
                if (Code(Cell(type)) is { } typeCode && Code(Cell(creator)) is { } creatorCode)   // not a number, four bytes
                {
                    Add(typeCode, creatorCode, Text(Cell(name)), Text(Cell(comments)), Text(Cell(category)));
                }
            }
        }

        // One record: an application's name (an APPL record, or one for any type), else a document's kind (finder.md §2.6).
        private void Add(FourCC typeCode, FourCC creator, string? name, string? comments, string? category)
        {
            FourCC? creatorCode = AnyCode.Contains(creator) ? null : creator;          // null: any creator
            var application = comments is null || comments.Equals(Unspecified, StringComparison.OrdinalIgnoreCase) ? null : comments;
            if (AnyCode.Contains(typeCode))
            {
                if (creatorCode is { } any && application is not null)
                {
                    Name(any, application, 1);
                }
                return;
            }

            if (typeCode == Appl)
            {
                if (creatorCode is { } signature && name is not null)
                {
                    Name(signature, name, 0);
                }
                return;
            }

            // The example file name "Application—kind" names it best; else the category; else "<application> document".
            (string Text, int Rank)? kind = name?.IndexOfAny(Separators) is int at and >= 0
                ? (Join(name[..at], name[(at + 1)..]), 0)
                : category is not null ? (category, 1)
                : application is not null ? ($"{application} document", 2)
                : null;
            if (kind is not { } found || found.Text.Length == 0)
            {
                return;
            }

            var key = (typeCode, creatorCode is { } c ? c : (FourCC?)null);
            if (!kinds.TryGetValue(key, out var known) || found.Rank < known.Rank)
            {
                kinds[key] = new Entry(found.Text, application, found.Rank);
            }
        }

        private void Name(FourCC signature, string name, int rank)
        {
            if (!applications.TryGetValue(signature, out var known) || rank < known.Rank)
            {
                applications[signature] = (name, rank);
            }
        }

        private static string Join(string application, string document) =>
            string.Join(' ', new[] { application.Trim(), document.Trim() }.Where(p => p.Length > 0));

        // The em dash (as it reads once decoded), a bullet, or the replacement character a converter left: [Fitted] to the file.
        private static readonly char[] Separators = ['—', '•', '�'];

        // A cell's text decoded back to Mac Roman, trimmed; null when blank.
        private static string? Text(string? cell)
        {
            if (cell is null)
            {
                return null;
            }

            var text = (Bytes(cell) is { } bytes ? MacRoman.Decode(bytes) : cell).Trim();
            return text.Length == 0 ? null : text;
        }

        // A four-character code: the cell's four bytes, exactly; null for anything else.
        private static FourCC? Code(string? cell) => cell is not null && Bytes(cell) is { Length: 4 } bytes ? new FourCC(bytes) : null;

        // The spreadsheet holds Mac Roman bytes read as Windows-1252 ([Fitted]: 'Ñ' for the em dash $D1, 'ð' for the Apple
        // logo $F0); the bytes back, or null when the text has a character Windows-1252 cannot have written.
        private static byte[]? Bytes(string text)
        {
            var bytes = new byte[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c <= 0xFF)
                {
                    bytes[i] = (byte)c;
                }
                else if (Array.IndexOf(Windows1252High, c) is var at and >= 0)
                {
                    bytes[i] = (byte)(0x80 + at);
                }
                else
                {
                    return null;
                }
            }
            return bytes;
        }

        // Windows-1252 $80–$9F; its five unassigned bytes are read as the control characters of the same value.
        private static readonly char[] Windows1252High =
        [
            '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
            '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
        ];

        // ---- The xlsx parts (ECMA-376 SpreadsheetML): shared strings and the sheet's rows ----

        private static List<string> SharedStrings(ZipArchiveEntry entry)
        {
            var strings = new List<string>();
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    strings.Add(Concatenated((XElement)XNode.ReadFrom(reader)));   // leaves the reader after it
                }
                else
                {
                    reader.Read();
                }
            }
            return strings;
        }

        // A string item's text: its <t>, or its runs' <t>s, leaving out phonetic runs (<rPh>).
        private static string Concatenated(XElement item) =>
            string.Concat(item.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value));

        private static IEnumerable<string?[]> Rows(ZipArchiveEntry entry, List<string> shared)
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                {
                    yield return Cells((XElement)XNode.ReadFrom(reader), shared);
                }
                else
                {
                    reader.Read();
                }
            }
        }

        // A row's cells by column: shared, inline and formula strings; numbers and other values are null (not text).
        private static string?[] Cells(XElement row, List<string> shared)
        {
            var cells = new List<string?>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = Column((string?)cell.Attribute("r")) ?? cells.Count;
                var value = (string?)cell.Element(Main + "v");
                var text = (string?)cell.Attribute("t") switch
                {
                    "s" when int.TryParse(value, out var index) && index >= 0 && index < shared.Count => shared[index],
                    "s" => throw new InvalidDataException($"A cell refers to shared string {value}, which is not there."),
                    "inlineStr" => cell.Element(Main + "is") is { } inline ? Concatenated(inline) : null,
                    "str" => value,
                    _ => null,
                };
                while (cells.Count <= column)
                {
                    cells.Add(null);
                }
                cells[column] = text;
            }
            return [.. cells];
        }

        // "C12" → 2; null without a reference.
        private static int? Column(string? reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return null;
            }

            var column = 0;
            foreach (var c in reference)
            {
                if (c is < 'A' or > 'Z')
                {
                    break;
                }
                column = column * 26 + (c - 'A' + 1);
                if (column > 16384)
                {
                    throw new InvalidDataException($"The cell reference {reference} is past the last column.");
                }
            }
            return column == 0 ? null : column - 1;
        }
    }
}
