using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>
    /// Reads Word 97 binary documents (Word 98 for the Macintosh saved them, type <c>'W8BN'</c>) into a
    /// <see cref="StyledDocument"/>: the main text through the piece table, its character formatting and its paragraphs'
    /// alignment, indents and spacing, by Microsoft's [MS-DOC] as <c>docs/formats/documents/word-binary.md</c> says. The
    /// document is a compound file ([MS-CFB], <see cref="CompoundFile"/>). Encrypted documents are reported, not read.
    /// All values are little-endian.
    /// </summary>
    public static class WordBinaryDocuments
    {
        private const ushort WordIdent = 0xA5EC;
        private const int Page = 512;

        /// <summary>The first nFib of Word 97's FIB ([MS-DOC] §2.5.2); earlier ones are Word 6 and 95's.</summary>
        internal const ushort Word97 = 0x00C1;

        /// <summary>Word 6's nFib; Word 95's is $0068 (docs/formats/documents/word-binary.md §4).</summary>
        internal const ushort Word6 = 0x0065;

        // The FibRgFcLcb97 pairs ClassicMac reads ([MS-DOC] §2.5.6), by index.
        private const int Stshf = 1, PlcfBteChpx = 12, PlcfBtePapx = 13, SttbfFfn = 15, Clx = 33;

        // Windows-1252's characters at $80–$9F, as FcCompressed maps an 8-bit character ([MS-DOC] §2.9.73); 0 where it has none.
        private static readonly char[] Windows1252 =
        [
            '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
            '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
        ];

        /// <summary>
        /// The document in <paramref name="data"/> (a <c>'W8BN'</c> file's data fork), titled <paramref name="title"/>; null
        /// when it is no Word 97 document, or one this reader cannot read (reported).
        /// </summary>
        public static StyledDocument? Read(ReadOnlyMemory<byte> data, string title, DecodeOptions? options = null,
            ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(title);
            _ = options;
            diagnostics ??= new List<Diagnostic>();
            if (!CompoundFile.IsCompoundFile(data.Span))
            {
                return null;
            }

            CompoundFile file;
            try
            {
                file = CompoundFile.Read(data, diagnostics);
            }
            catch (InvalidDataException e)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-container", $"\"{title}\": {e.Message}"));
                return null;
            }

            if (file.Find("WordDocument") is not { } entry)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-container",
                    $"\"{title}\" is a compound file with no WordDocument stream: not a Word document."));
                return null;
            }

            var document = file.ReadStream(entry);
            var span = document.Span;
            if (span.Length < 0x20 || BinaryPrimitives.ReadUInt16LittleEndian(span) != WordIdent)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-fib", $"\"{title}\": the WordDocument stream does not start with a Word FIB."));
                return null;
            }

            var nFib = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
            if (nFib < Word6)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.unsupported-version",
                    $"\"{title}\": a FIB older than Word 6's (nFib ${nFib:X4}); only Word 6 and later are read."));
                return null;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[0x0A..]);
            if ((flags & 0x0100) != 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.encrypted",
                    $"\"{title}\" is a password-protected Word document ({((flags & 0x8000) != 0 ? "obfuscated" : "encrypted")}); it is not read."));
                return null;
            }

            // Word 6 and 95 keep everything in the WordDocument stream, in Mac OS Roman when the FIB's chse says so [Reference: Apache POI; wv].
            if (nFib < Word97)
            {
                var mac = span.Length >= 0x16 && BinaryPrimitives.ReadUInt16LittleEndian(span[0x14..]) == 256;
                return new Reader(document, document, title, diagnostics, word6: true, mac).Read();
            }

            var tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
            if (file.Find(tableName) is not { } table)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-fib", $"\"{title}\": the FIB names the {tableName} stream, which the file does not have."));
                return null;
            }

            return new Reader(document, file.ReadStream(table), title, diagnostics).Read();
        }

        /// <summary>One piece of the text ([MS-DOC] §2.9.177): characters from <paramref name="Cp"/> up to <paramref name="CpEnd"/>,
        /// stored from <paramref name="Fc"/> one byte (compressed) or two each.</summary>
        internal readonly record struct Piece(int Cp, int CpEnd, long Fc, bool Compressed, ushort Prm);

        // Character properties as far as ClassicMac shows them.
        internal readonly record struct Chp(bool Bold, bool Italic, bool Outline, bool Shadow, bool Caps, bool Hidden, bool Underline, int Font, int HalfPoints);

        // Paragraph properties, in twips.
        internal sealed record Pap(Justification Justification, int Left, int Right, int FirstLine, int Before, int After, bool InTable, bool RowEnd);

        private sealed record Style(int Base, int Type, byte[] Papx, byte[] Chpx);

        private sealed class Reader(ReadOnlyMemory<byte> document, ReadOnlyMemory<byte> table, string title, ICollection<Diagnostic> diagnostics,
            bool word6 = false, bool mac = false)
        {
            private readonly HashSet<string> reported = [];
            private readonly Dictionary<int, (Chp Chp, Pap Pap)> styleCache = [];
            private List<string> fonts = [];
            private List<Style?> styles = [];
            private int defaultFont;

            private ReadOnlySpan<byte> Fib => document.Span;

            private void Report(DiagnosticSeverity severity, string code, string message)
            {
                if (reported.Add(code))
                {
                    diagnostics.Add(new Diagnostic(severity, code, $"\"{title}\": {message}"));
                }
            }

            // A FibRgFcLcb97 pair as a slice of the table stream, or empty when absent or out of range (reported). Word 6's FIB
            // has the same pairs in the same order from +$58, with no count [Reference: Apache POI].
            private ReadOnlyMemory<byte> Table(int index)
            {
                var at = word6 ? 0x58 + 8 * index : 154 + 8 * index;
                if (!word6 && index >= BinaryPrimitives.ReadUInt16LittleEndian(Fib[152..]) || Fib.Length < at + 8)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                long fc = BinaryPrimitives.ReadUInt32LittleEndian(Fib[at..]);
                long lcb = BinaryPrimitives.ReadUInt32LittleEndian(Fib[(at + 4)..]);
                if (lcb == 0)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                if (fc + lcb > table.Length)
                {
                    Report(DiagnosticSeverity.Warning, "word.bad-zone", $"table structure {index} (${fc:X}, {lcb} bytes) runs past the end of the table stream; left out.");
                    return ReadOnlyMemory<byte>.Empty;
                }

                return table.Slice((int)fc, (int)lcb);
            }

            public StyledDocument? Read()
            {
                var ccpAt = word6 ? 0x34 : 0x4C;
                var ccpText = Fib.Length >= ccpAt + 4 ? BinaryPrimitives.ReadInt32LittleEndian(Fib[ccpAt..]) : 0;
                var pieces = Pieces(Table(Clx));
                if (word6 && pieces.Count == 0 && Fib.Length >= 0x20)
                {
                    // A Word 6 document that was not fast saved has no piece table: its text is one run of bytes from fcMin
                    // [Reference: Apache POI].
                    pieces.Add(new Piece(0, ccpText, BinaryPrimitives.ReadUInt32LittleEndian(Fib[0x18..]), true, 0));
                }
                if (pieces.Count == 0)
                {
                    Report(DiagnosticSeverity.Error, "word.bad-pieces", "the document has no piece table, so its text cannot be found.");
                    return null;
                }

                if (pieces.Any(p => p.Prm != 0))
                {
                    Report(DiagnosticSeverity.Warning, "word.piece-properties",
                        "some text was formatted by a fast save's property changes, which this reader does not apply.");
                }

                fonts = word6 ? ReadSixFonts(Table(SttbfFfn), mac) : ReadFonts(Table(SttbfFfn));
                ReadStyles(Table(Stshf));
                var characters = Bins(Table(PlcfBteChpx), paragraphs: false);
                var paragraphs = Bins(Table(PlcfBtePapx), paragraphs: true);

                // The main text: CPs 0 up to ccpText ([MS-DOC] §2.4.1), with each character's FC.
                var text = new List<(char Char, long Fc)>();
                foreach (var piece in pieces)
                {
                    for (var cp = Math.Max(piece.Cp, 0); cp < piece.CpEnd && cp < ccpText; cp++)
                    {
                        var fc = piece.Fc + (cp - piece.Cp) * (piece.Compressed ? 1 : 2);
                        if (fc + (piece.Compressed ? 1 : 2) > document.Length)
                        {
                            Report(DiagnosticSeverity.Warning, "word.bad-pieces", "a piece of the text lies past the end of the WordDocument stream; cut there.");
                            break;
                        }

                        var c = !piece.Compressed ? (char)BinaryPrimitives.ReadUInt16LittleEndian(document.Span[(int)fc..])
                            : mac ? MacRoman.ToChar(document.Span[(int)fc]) : Compressed(document.Span[(int)fc]);
                        text.Add((c, fc));
                    }
                }

                return Build(text, characters, paragraphs);
            }

            private static char Compressed(byte b) => b is >= 0x80 and <= 0x9F && Windows1252[b - 0x80] != '\0' ? Windows1252[b - 0x80] : (char)b;

            // The Clx ([MS-DOC] §2.9.38): Prcs (0x01, a size and grpprl) to skip, then the Pcdt (0x02, a size, the PlcPcd).
            private List<Piece> Pieces(ReadOnlyMemory<byte> clx)
            {
                var span = clx.Span;
                var pieces = new List<Piece>();
                var at = 0;
                while (at < span.Length && span[at] == 0x01 && at + 3 <= span.Length)
                {
                    at += 3 + BinaryPrimitives.ReadInt16LittleEndian(span[(at + 1)..]);
                }

                if (at + 5 > span.Length || span[at] != 0x02)
                {
                    return pieces;
                }

                var lcb = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 1)..]);
                var plc = span.Slice(at + 5, Math.Min(lcb, span.Length - at - 5));
                var count = (plc.Length - 4) / 12;
                for (var i = 0; i < count; i++)
                {
                    var cp = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]);
                    var end = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * (i + 1))..]);
                    var pcd = plc[(4 * (count + 1) + 8 * i)..];
                    var fcCompressed = BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..]);
                    if (word6)
                    {
                        // Word 6's pieces are 8-bit text at a plain FC [Reference: Apache POI].
                        pieces.Add(new Piece(cp, end, fcCompressed, true, BinaryPrimitives.ReadUInt16LittleEndian(pcd[6..])));
                        continue;
                    }

                    var compressed = (fcCompressed & 0x40000000) != 0;
                    var fc = (long)(fcCompressed & 0x3FFFFFFF);
                    pieces.Add(new Piece(cp, end, compressed ? fc / 2 : fc, compressed, BinaryPrimitives.ReadUInt16LittleEndian(pcd[6..])));
                }

                return pieces;
            }

            // Word 6's font table: its size in bytes, then per font a size byte (less one), ffid, a weight word, a charset, the
            // alternate name's index, and the name, 8-bit and null-terminated [Reference: Apache POI].
            private static List<string> ReadSixFonts(ReadOnlyMemory<byte> table, bool mac)
            {
                var span = table.Span;
                var found = new List<string>();
                if (span.Length < 2)
                {
                    return found;
                }

                var end = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(span), span.Length);
                for (var at = 2; at + 6 <= end;)
                {
                    var size = span[at] + 1;
                    var name = span.Slice(at + 6, Math.Max(0, Math.Min(size - 6, end - at - 6)));
                    var zero = name.IndexOf((byte)0);
                    name = zero >= 0 ? name[..zero] : name;
                    found.Add(mac ? MacRoman.Decode(name) : string.Concat(name.ToArray().Select(Compressed)));
                    at += size;
                }

                return found;
            }

            // SttbfFfn ([MS-DOC] §2.9.286): a count, a zero extra size, then per font a size byte and an FFN ([MS-DOC] §2.9.82)
            // whose name, null-terminated UTF-16, starts 39 bytes in.
            private static List<string> ReadFonts(ReadOnlyMemory<byte> sttbf)
            {
                var span = sttbf.Span;
                var found = new List<string>();
                if (span.Length < 4)
                {
                    return found;
                }

                var count = BinaryPrimitives.ReadUInt16LittleEndian(span);
                var at = 4;
                for (var i = 0; i < count && at < span.Length; i++)
                {
                    var size = span[at] + 1;
                    var ffn = span.Slice(at + 1, Math.Min(size - 1, span.Length - at - 1));
                    var name = new StringBuilder();
                    for (var c = 39; c + 1 < ffn.Length; c += 2)
                    {
                        var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(ffn[c..]);
                        if (ch == '\0')
                        {
                            break;
                        }

                        name.Append(ch);
                    }

                    found.Add(name.ToString());
                    at += size;
                }

                return found;
            }

            // The STSH ([MS-DOC] §2.9.271): the STSHI, then per style an STD (§2.9.258) of a StdfBase (§2.9.260), its name, and
            // its property exceptions: a paragraph style's UpxPapx and UpxChpx, a character style's UpxChpx.
            private void ReadStyles(ReadOnlyMemory<byte> stsh)
            {
                var span = stsh.Span;
                styles = [];
                if (span.Length < 2)
                {
                    return;
                }

                var cbStshi = BinaryPrimitives.ReadUInt16LittleEndian(span);
                if (cbStshi < (word6 ? 14 : 18) || 2 + cbStshi > span.Length)
                {
                    Report(DiagnosticSeverity.Warning, "word.bad-styles", "the style sheet's header is damaged; styles are left out.");
                    return;
                }

                var stshi = span.Slice(2, cbStshi);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(stshi);
                var stdfSize = BinaryPrimitives.ReadUInt16LittleEndian(stshi[2..]);
                defaultFont = BinaryPrimitives.ReadUInt16LittleEndian(stshi[12..]);
                var at = 2 + cbStshi;
                for (var i = 0; i < count && at + 2 <= span.Length; i++)
                {
                    var cbStd = BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
                    at += 2;
                    if (cbStd == 0 || at + cbStd > span.Length)
                    {
                        styles.Add(null);
                        at += Math.Min(cbStd, span.Length - at);
                        continue;
                    }

                    styles.Add(ReadStd(span.Slice(at, cbStd), stdfSize, word6));
                    at += cbStd;
                }
            }

            private static Style? ReadStd(ReadOnlySpan<byte> std, int stdfSize, bool word6)
            {
                if (std.Length < 10)
                {
                    return null;
                }

                var stkBase = BinaryPrimitives.ReadUInt16LittleEndian(std[2..]);
                var (type, istdBase) = (stkBase & 0x0F, stkBase >> 4);
                var at = stdfSize;
                if (at + 2 > std.Length)
                {
                    return new Style(istdBase, type, [], []);
                }

                if (word6)
                {
                    // Word 6's name: a length byte, the 8-bit characters, a null; the UPXs start at an even offset
                    // (assumed: docs/formats/documents/word-binary.md §4.1).
                    at += 1 + std[at] + 1;
                    at += at & 1;
                }
                else
                {
                    var cch = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
                    at += 2 + 2 * cch + 2;
                }
                byte[] papxBytes = [], chpxBytes = [];
                if (type == 1)
                {
                    papxBytes = Upx(std, ref at, papx: true);
                    chpxBytes = Upx(std, ref at, papx: false);
                }
                else if (type == 2)
                {
                    chpxBytes = Upx(std, ref at, papx: false);
                }

                return new Style(istdBase, type, papxBytes, chpxBytes);
            }

            // An LPUpx: a size, the UPX (a UpxPapx's istd then grpprl, or a UpxChpx's grpprl), a pad byte to an even size.
            private static byte[] Upx(ReadOnlySpan<byte> std, ref int position, bool papx)
            {
                if (position + 2 > std.Length)
                {
                    return [];
                }

                var size = BinaryPrimitives.ReadUInt16LittleEndian(std[position..]);
                var start = position + 2 + (papx ? 2 : 0);
                var end = Math.Min(position + 2 + size, std.Length);
                position += 2 + size + (size & 1);
                return start < end ? std[start..end].ToArray() : [];
            }

            // A bin table's runs ([MS-DOC] §2.8.5, §2.8.6): FC from, FC to, and the run's property block: a CHPX's grpprl
            // (§2.9.32), or a PAPX's istd and grpprl (§2.9.175).
            private List<(long From, long To, byte[] Block)> Bins(ReadOnlyMemory<byte> plc, bool paragraphs)
            {
                var runs = new List<(long, long, byte[])>();
                var span = plc.Span;
                if (span.Length < 4)
                {
                    return runs;
                }

                // Word 6's page numbers are 2 bytes [Reference: Apache POI].
                var pnSize = word6 ? 2 : 4;
                var count = (span.Length - 4) / (4 + pnSize);
                for (var i = 0; i < count; i++)
                {
                    var pnAt = 4 * (count + 1) + pnSize * i;
                    var pn = word6 ? BinaryPrimitives.ReadUInt16LittleEndian(span[pnAt..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[pnAt..]) & 0x3FFFFF;
                    if ((pn + 1L) * Page > document.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-zone", $"formatting page {pn} is past the end of the WordDocument stream; left out.");
                        continue;
                    }

                    var page = document.Span.Slice((int)pn * Page, Page);
                    var crun = page[Page - 1];
                    // Word 6's BX is the offset byte and 6 bytes of line data (assumed: word-binary.md §4.1).
                    var entry = paragraphs ? (word6 ? 7 : 13) : 1;
                    if (4 * (crun + 1) + entry * crun > Page - 1)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-zone", $"formatting page {pn} says {crun} runs, more than a page holds; left out.");
                        continue;
                    }

                    for (var r = 0; r < crun; r++)
                    {
                        var at = page[4 * (crun + 1) + entry * r] * 2;
                        var block = Array.Empty<byte>();
                        if (at > 0 && at < Page - 1)
                        {
                            if (paragraphs)
                            {
                                // PapxInFkp: cb, or 0 and a second count; the GrpPrlAndIstd is 2 × cb − 1 bytes, or 2 × the second.
                                var cb = page[at];
                                // Word 6's PAPX: a count of words, then that many words (assumed: word-binary.md §4.1).
                                var (start, size) = word6 ? (at + 1, 2 * cb) : cb != 0 ? (at + 1, 2 * cb - 1) : (at + 2, 2 * page[at + 1]);
                                block = page.Slice(start, Math.Max(0, Math.Min(size, Page - 1 - start))).ToArray();
                            }
                            else
                            {
                                block = page.Slice(at + 1, Math.Min(page[at], Page - 1 - at - 1)).ToArray();
                            }
                        }

                        runs.Add((BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * r)..]), BinaryPrimitives.ReadUInt32LittleEndian(page[(4 * (r + 1))..]), block));
                    }
                }

                return runs;
            }

            // A style's properties: its base style's, then its own exceptions ([MS-DOC] §2.4.6.3, §2.4.6.4).
            private (Chp Chp, Pap Pap) StyleProperties(int istd, int depth = 0)
            {
                if (styleCache.TryGetValue(istd, out var cached))
                {
                    return cached;
                }

                var properties = (Chp: new Chp(false, false, false, false, false, false, false, defaultFont, 20),
                    Pap: new Pap(Justification.Left, 0, 0, 0, 0, 0, false, false));
                if (istd < styles.Count && styles[istd] is { } style && depth < 16)
                {
                    if (style.Base != 0x0FFF && style.Base != istd)
                    {
                        properties = StyleProperties(style.Base, depth + 1);
                    }

                    properties = (ApplyChp(properties.Chp, style.Chpx, properties.Chp), ApplyPap(properties.Pap, style.Papx));
                }

                styleCache[istd] = properties;
                return properties;
            }

            // A Sprm's operand size ([MS-DOC] §2.2.5.1), or −1 when it cannot be read.
            private static int OperandSize(ushort sprm, ReadOnlySpan<byte> rest) => (sprm >> 13) switch
            {
                0 or 1 => 1,
                2 or 4 or 5 => 2,
                3 => 4,
                7 => 3,
                _ when sprm == 0xD608 && rest.Length >= 2 => 2 + BinaryPrimitives.ReadUInt16LittleEndian(rest) - 1,
                _ when sprm == 0xC615 && rest.Length >= 1 && rest[0] == 255 => -1,
                _ when rest.Length >= 1 => 1 + rest[0],
                _ => -1,
            };

            // Word 6's one-byte sprms and their operand sizes (0 none, −1 a size byte and that many, −2 a size word, −3
            // unknown), and the Word 97 sprm each sets the same property as [Reference: LibreOffice].
            private static (int Size, ushort Same) SixSprm(byte sprm) => sprm switch
            {
                2 => (2, 0),
                4 or 6 or 7 or 8 or 9 or 10 or 11 or 13 or 14 or 37 or 44 or 50 or 51 => (1, 0),
                5 => (1, 0x2403),
                16 => (2, 0x840E),
                17 => (2, 0x840F),
                18 or 26 or 27 or 28 or >= 30 and <= 36 or >= 38 and <= 43 or 45 or 46 or 47 or 48 or 49 => (2, 0),
                19 => (2, 0x8411),
                20 => (4, 0),
                21 => (2, 0xA413),
                22 => (2, 0xA414),
                24 => (1, 0x2416),
                25 => (1, 0x2417),
                29 => (1, 0),
                12 or 15 or 23 or 64 or 68 or 74 or 77 or 79 or 81 or 82 or 103 or 105 or 106 or 108 or >= 111 and <= 116 or 179 or 181 or 191 or 207 => (-1, 0),
                65 or 66 or 67 or 71 or 75 or 90 or 100 or 102 or 104 or 117 or 118 or 119 => (1, 0),
                69 or 72 or 80 or 96 or 97 or 101 or 107 or 109 or 110 or >= 121 and <= 124 => (2, 0),
                70 => (4, 0),
                73 or 95 => (3, 0),
                83 => (0, 0xFFFF),
                85 => (1, 0x0835),
                86 => (1, 0x0836),
                87 => (1, 0),
                88 => (1, 0x0838),
                89 => (1, 0x0839),
                91 => (1, 0x083B),
                92 => (1, 0x083C),
                93 => (2, 0x4A4F),
                94 => (1, 0x2A3E),
                98 => (1, 0),
                99 => (2, 0x4A43),
                131 or 132 or 138 or 139 or 142 or 143 or 146 or 147 or 150 or 151 or 152 or 153 or 158 or 159 or 162 or 185 or 186 => (1, 0),
                136 or 137 => (3, 0),
                140 or 141 or 144 or 145 or 148 or 149 or 154 or 155 or 156 or 157 or 160 or 161 or >= 164 and <= 171 or 182 or 183 or 184 or 189 or 195 or 197 or 198 => (2, 0),
                133 => (-1, 0),
                163 => (0, 0),
                187 => (12, 0),
                188 or 190 => (-2, 0),
                192 or 194 or 196 or 200 => (4, 0),
                193 or 199 => (5, 0),
                _ => (-3, 0),
            };

            private IEnumerable<(ushort Sprm, byte[] Operand)> Sprms(byte[] grpprl)
            {
                if (word6)
                {
                    foreach (var sprm in SixSprms(grpprl))
                    {
                        yield return sprm;
                    }

                    yield break;
                }

                for (var i = 0; i + 2 <= grpprl.Length;)
                {
                    var sprm = BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(i));
                    var size = OperandSize(sprm, grpprl.AsSpan(i + 2));
                    if (size < 0 || i + 2 + size > grpprl.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-sprm", $"a property list is damaged (sprm ${sprm:X4}); its later properties are left out.");
                        yield break;
                    }

                    yield return (sprm, grpprl.AsSpan(i + 2, size).ToArray());
                    i += 2 + size;
                }
            }

            private IEnumerable<(ushort Sprm, byte[] Operand)> SixSprms(byte[] grpprl)
            {
                for (var i = 0; i < grpprl.Length;)
                {
                    var code = grpprl[i];
                    var (size, same) = SixSprm(code);
                    if (code == 0)
                    {
                        yield break;
                    }

                    if (size == -1 && i + 1 < grpprl.Length)
                    {
                        size = 1 + grpprl[i + 1];
                    }
                    else if (size == -2 && i + 2 < grpprl.Length)
                    {
                        size = 2 + BinaryPrimitives.ReadUInt16LittleEndian(grpprl.AsSpan(i + 1)) - 1;
                    }

                    if (size < 0 || i + 1 + size > grpprl.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-sprm", $"a property this reader does not know (Word 6 sprm {code}); the rest of its list is left out.");
                        yield break;
                    }

                    if (same != 0)
                    {
                        yield return (same, grpprl.AsSpan(i + 1, size).ToArray());
                    }

                    i += 1 + size;
                }
            }

            // A ToggleOperand ([MS-DOC] §2.9.327): 0 off, 1 on, $80 the style's value, $81 its opposite.
            private static bool Toggle(byte value, bool style) => value switch
            {
                0 => false,
                1 => true,
                0x80 => style,
                0x81 => !style,
                _ => style,
            };

            // Character sprms ([MS-DOC] §2.6.1).
            private Chp ApplyChp(Chp chp, byte[] grpprl, Chp style)
            {
                foreach (var (sprm, operand) in Sprms(grpprl))
                {
                    chp = sprm switch
                    {
                        0xFFFF => style,
                        0x0835 => chp with { Bold = Toggle(operand[0], style.Bold) },
                        0x0836 => chp with { Italic = Toggle(operand[0], style.Italic) },
                        0x0838 => chp with { Outline = Toggle(operand[0], style.Outline) },
                        0x0839 => chp with { Shadow = Toggle(operand[0], style.Shadow) },
                        0x083B => chp with { Caps = Toggle(operand[0], style.Caps) },
                        0x083C => chp with { Hidden = Toggle(operand[0], style.Hidden) },
                        0x2A3E => chp with { Underline = operand[0] != 0 },
                        0x4A43 => chp with { HalfPoints = BinaryPrimitives.ReadUInt16LittleEndian(operand) },
                        0x4A4F => chp with { Font = BinaryPrimitives.ReadUInt16LittleEndian(operand) },
                        _ => chp,
                    };
                }

                return chp;
            }

            // Paragraph sprms ([MS-DOC] §2.6.2).
            private Pap ApplyPap(Pap pap, byte[] grpprl)
            {
                foreach (var (sprm, operand) in Sprms(grpprl))
                {
                    short Word() => BinaryPrimitives.ReadInt16LittleEndian(operand);
                    pap = sprm switch
                    {
                        0x2403 or 0x2461 => pap with
                        {
                            Justification = operand[0] switch
                            {
                                1 => Justification.Center,
                                2 => Justification.Right,
                                3 or 4 => Justification.Full,
                                _ => Justification.Left,
                            },
                        },
                        0x840E or 0x845D => pap with { Right = Word() },
                        0x840F or 0x845E => pap with { Left = Word() },
                        0x8411 or 0x8460 => pap with { FirstLine = Word() },
                        0xA413 => pap with { Before = (ushort)Word() },
                        0xA414 => pap with { After = (ushort)Word() },
                        0x2416 => pap with { InTable = operand[0] != 0 },
                        0x2417 => pap with { RowEnd = operand[0] != 0 },
                        _ => pap,
                    };
                }

                return pap;
            }

            private static int Find(List<(long From, long To, byte[] Block)> runs, long fc)
            {
                var (low, high) = (0, runs.Count - 1);
                while (low <= high)
                {
                    var middle = (low + high) / 2;
                    if (runs[middle].To <= fc)
                    {
                        low = middle + 1;
                    }
                    else if (runs[middle].From > fc)
                    {
                        high = middle - 1;
                    }
                    else
                    {
                        return middle;
                    }
                }

                return -1;
            }

            private StyledDocument Build(List<(char Char, long Fc)> text, List<(long From, long To, byte[] Block)> characters,
                List<(long From, long To, byte[] Block)> paragraphs)
            {
                characters.Sort((a, b) => a.From.CompareTo(b.From));
                paragraphs.Sort((a, b) => a.From.CompareTo(b.From));
                var output = new StringBuilder(text.Count);
                var runs = new List<TextRun>();
                var formats = new List<ParagraphFormat>();
                var inField = new Stack<bool>();                 // per open field: whether its result has begun
                for (var start = 0; start < text.Count;)
                {
                    // A paragraph: up to and including its mark; its properties are the PAPX at the mark's FC ([MS-DOC] §2.4.2).
                    var end = start;
                    while (end < text.Count - 1 && text[end].Char is not ('\r' or '\a' or '\f'))
                    {
                        end++;
                    }

                    var run = Find(paragraphs, text[end].Fc);
                    var block = run >= 0 ? paragraphs[run].Block : [];
                    var istd = block.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(block) : 0;
                    var (styleChp, stylePap) = StyleProperties(istd);
                    var pap = block.Length > 2 ? ApplyPap(stylePap, block[2..]) : stylePap;
                    formats.Add(new ParagraphFormat(output.Length, pap.Justification, pap.Left / 20.0, pap.Right / 20.0, pap.FirstLine / 20.0,
                        pap.Before / 20.0, pap.After / 20.0));
                    for (var i = start; i <= end; i++)
                    {
                        var (c, fc) = text[i];
                        var chpRun = Find(characters, fc);
                        var chp = chpRun >= 0 ? ApplyChp(styleChp, characters[chpRun].Block, styleChp) : styleChp;

                        // Fields ([MS-DOC]): $13 begins, $14 separates the instructions from the result, $15 ends; the
                        // instructions are left out, the result shown.
                        if (c == '\u0013')
                        {
                            inField.Push(false);
                            continue;
                        }

                        if (c == '\u0014' && inField.Count > 0)
                        {
                            inField.Pop();
                            inField.Push(true);
                            continue;
                        }

                        if (c == '\u0015' && inField.Count > 0)
                        {
                            inField.Pop();
                            continue;
                        }

                        if (inField.Count > 0 && !inField.Peek())
                        {
                            continue;
                        }

                        char? shown = c switch
                        {
                            '\r' or '\v' or '\f' or '\u000E' => '\r',
                            '\t' => '\t',
                            '\a' => pap.RowEnd ? '\r' : '\t',
                            '\u001E' => '‑',
                            '\u001F' => '­',
                            < ' ' => null,
                            _ => chp.Caps ? char.ToUpperInvariant(c) : c,
                        };
                        if (chp.Hidden && c is not ('\r' or '\a' or '\f'))
                        {
                            continue;
                        }

                        if (shown is null)
                        {
                            Report(DiagnosticSeverity.Info, "word.not-shown", "pictures, footnote references and other special characters are left out.");
                            continue;
                        }

                        var name = chp.Font < fonts.Count ? fonts[chp.Font] : "Times New Roman";
                        var face = (byte)((chp.Bold ? 0x01 : 0) | (chp.Italic ? 0x02 : 0) | (chp.Underline ? 0x04 : 0) | (chp.Outline ? 0x08 : 0)
                            | (chp.Shadow ? 0x10 : 0));
                        var size = chp.HalfPoints / 2;
                        var textRun = new TextRun(output.Length, 1, (short)chp.Font, name, size > 0 ? size : 10, face, 0, 0, 0);
                        if (runs.Count > 0 && runs[^1] with { Start = textRun.Start, Length = 1 } == textRun && runs[^1].Start + runs[^1].Length == textRun.Start)
                        {
                            runs[^1] = runs[^1] with { Length = runs[^1].Length + 1 };
                        }
                        else
                        {
                            runs.Add(textRun);
                        }

                        output.Append(shown.Value);
                    }

                    start = end + 1;
                }

                var chapter = new DocumentChapter(1, title, new StyledText(output.ToString(), runs, true), Justification.Left, null, [], 0)
                {
                    Paragraphs = formats,
                };
                return new StyledDocument(DocumentKind.Word, title, [chapter], []);
            }
        }
    }
}
