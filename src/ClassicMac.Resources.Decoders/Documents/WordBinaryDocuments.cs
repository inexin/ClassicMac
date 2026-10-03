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
            if (nFib < Word97)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.unsupported-version",
                    $"\"{title}\" is a Word 6 or 95 document (nFib ${nFib:X4}); only Word 97 and later are read."));
                return null;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[0x0A..]);
            if ((flags & 0x0100) != 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.encrypted",
                    $"\"{title}\" is a password-protected Word document ({((flags & 0x8000) != 0 ? "obfuscated" : "encrypted")}); it is not read."));
                return null;
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

        private sealed class Reader(ReadOnlyMemory<byte> document, ReadOnlyMemory<byte> table, string title, ICollection<Diagnostic> diagnostics)
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

            // A FibRgFcLcb97 pair as a slice of the table stream, or empty when absent or out of range (reported).
            private ReadOnlyMemory<byte> Table(int index)
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(Fib[152..]);
                if (index >= count || Fib.Length < 154 + 8 * (index + 1))
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                long fc = BinaryPrimitives.ReadUInt32LittleEndian(Fib[(154 + 8 * index)..]);
                long lcb = BinaryPrimitives.ReadUInt32LittleEndian(Fib[(158 + 8 * index)..]);
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
                var ccpText = Fib.Length >= 0x50 ? BinaryPrimitives.ReadInt32LittleEndian(Fib[0x4C..]) : 0;
                var pieces = Pieces(Table(Clx));
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

                fonts = ReadFonts(Table(SttbfFfn));
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

                        var c = piece.Compressed ? Compressed(document.Span[(int)fc]) : (char)BinaryPrimitives.ReadUInt16LittleEndian(document.Span[(int)fc..]);
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
                    var compressed = (fcCompressed & 0x40000000) != 0;
                    var fc = (long)(fcCompressed & 0x3FFFFFFF);
                    pieces.Add(new Piece(cp, end, compressed ? fc / 2 : fc, compressed, BinaryPrimitives.ReadUInt16LittleEndian(pcd[6..])));
                }

                return pieces;
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
                if (cbStshi < 18 || 2 + cbStshi > span.Length)
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

                    styles.Add(ReadStd(span.Slice(at, cbStd), stdfSize));
                    at += cbStd;
                }
            }

            private static Style? ReadStd(ReadOnlySpan<byte> std, int stdfSize)
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

                var cch = BinaryPrimitives.ReadUInt16LittleEndian(std[at..]);
                at += 2 + 2 * cch + 2;
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

                var count = (span.Length - 4) / 8;
                for (var i = 0; i < count; i++)
                {
                    var pn = BinaryPrimitives.ReadUInt32LittleEndian(span[(4 * (count + 1) + 4 * i)..]) & 0x3FFFFF;
                    if ((pn + 1L) * Page > document.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-zone", $"formatting page {pn} is past the end of the WordDocument stream; left out.");
                        continue;
                    }

                    var page = document.Span.Slice((int)pn * Page, Page);
                    var crun = page[Page - 1];
                    var entry = paragraphs ? 13 : 1;
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
                                var (start, size) = cb != 0 ? (at + 1, 2 * cb - 1) : (at + 2, 2 * page[at + 1]);
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

            private IEnumerable<(ushort Sprm, byte[] Operand)> Sprms(byte[] grpprl)
            {
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
