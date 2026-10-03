using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>
    /// Reads Microsoft Word 4 and 5 for the Macintosh documents (<c>'WDBN'</c>) into a <see cref="StyledDocument"/>: the
    /// text, its character formatting and its paragraphs' alignment, indents and spacing, as
    /// <c>docs/formats/documents/word-mac.md</c> lays them out. No specification of the format was published; its layout is
    /// fitted to Word's documents (§1). A fast-saved document is reported, not read.
    /// </summary>
    public static class MacWordDocuments
    {
        private const int HeaderSize = 0x100, Page = 512;

        // Zone numbers (word-mac.md §1.2).
        private const int StyleZone = 0, CharacterBins = 9, ParagraphBins = 10, PieceTable = 18, FontNames = 21;

        /// <summary>The Word version a document's signature names (4 or 5), or 0 when it is no Word 4 or 5 document.</summary>
        internal static int Version(ReadOnlySpan<byte> data) =>
            data.Length < 4 || data[0] != 0xFE || data[1] != 0x37 ? 0 : (data[2], data[3]) switch
            {
                (0, 0x1C) => 4,
                (0, 0x23) => 5,
                _ => 0,
            };

        /// <summary>Whether the data starts with a Word for the Macintosh signature, of any version (word-mac.md §1.1).</summary>
        internal static bool IsMacWord(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0xFE && data[1] is 0x32 or 0x34 or 0x37;

        /// <summary>
        /// The document in <paramref name="data"/> (a <c>'WDBN'</c> file's data fork), titled <paramref name="title"/>; null when
        /// it is no Word 4 or 5 document, or one this reader cannot read (reported).
        /// </summary>
        public static StyledDocument? Read(ReadOnlyMemory<byte> data, string title, DecodeOptions? options = null,
            ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(title);
            _ = options;
            diagnostics ??= new List<Diagnostic>();
            var span = data.Span;
            if (span.Length < HeaderSize || !IsMacWord(span))
            {
                return null;
            }

            var version = Version(span);
            if (version == 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.unsupported-version",
                    $"A Word for the Macintosh document of a version this reader does not read (signature ${span[0]:X2}{span[1]:X2}, " +
                    $"version ${span[2]:X2}{span[3]:X2}); only Word 4 and 5 are read."));
                return null;
            }

            return new Reader(new BigEndianReader(data), version, title, diagnostics).Read();
        }

        /// <summary>A Word 4 or 5 document's structures as read, for checks against real documents; null when it is none.</summary>
        internal static MacWordFacts? Inspect(ReadOnlyMemory<byte> data)
        {
            var version = Version(data.Span);
            return data.Length < HeaderSize || version == 0 ? null : new Reader(new BigEndianReader(data), version, "", []).Facts();
        }

        /// <summary>What <see cref="Inspect"/> finds.</summary>
        internal sealed record MacWordFacts(int Version, bool FastSaved, int FastSaves, int TextLength, IReadOnlyDictionary<short, string> Fonts,
            int Styles, int CharacterRuns, int ParagraphRuns, int BoldRuns);

        // The state of a character: Word's CHP as far as ClassicMac shows it.
        private readonly record struct Chp(byte Flags, short Font, int HalfPoints, byte Underline)
        {
            public bool Hidden => (Flags & 0x01) != 0;

            public bool AllCaps => (Flags & 0x02) != 0;

            // QuickDraw's style bits: bold 1, italic 2, underline 4, outline 8, shadow $10.
            public byte Face => (byte)(((Flags & 0x80) != 0 ? 0x01 : 0) | ((Flags & 0x40) != 0 ? 0x02 : 0) | (Underline != 0 ? 0x04 : 0)
                | ((Flags & 0x10) != 0 ? 0x08 : 0) | ((Flags & 0x08) != 0 ? 0x10 : 0));
        }

        // A paragraph's properties, in twips.
        private sealed record Pap(Justification Justification, int Left, int Right, int FirstLine, int Before, int After, bool InTable,
            bool RowEnd);

        private sealed record Style(byte[]? Chp, byte[]? Sprms);

        private sealed class Reader(BigEndianReader file, int version, string title, ICollection<Diagnostic> diagnostics)
        {
            // New York 12, Word's default when a style has no character properties [Reference: libmwaw].
            private static readonly Chp DefaultChp = new(0, 2, 24, 0);

            private static readonly Pap DefaultPap = new(Justification.Left, 0, 0, 0, 0, 0, false, false);

            private readonly HashSet<string> reported = [];
            private Dictionary<short, string> fonts = [];
            private List<Style> styles = [];

            // What the structures hold, read even from a fast-saved document (for the corpus checks).
            public MacWordFacts Facts()
            {
                var flags = file.ReadByteAt(0x0A);
                var fast = Zone(PieceTable).Length > 0;
                var characters = Bins(CharacterBins, words: false);
                var paragraphs = Bins(ParagraphBins, words: true);
                return new MacWordFacts(version, fast, flags >> 4, (int)file.ReadUInt32At(0x24), ReadFonts(Zone(FontNames)),
                    ReadStyles(Zone(StyleZone)).Count, characters.Count, paragraphs.Count,
                    characters.Count(r => r.Block.Length > 0 && (r.Block[0] & 0x80) != 0));
            }

            public StyledDocument? Read()
            {
                var flags = file.ReadByteAt(0x0A);
                var pieces = Zone(PieceTable);
                // A fast save leaves a piece table (zone 18); the flag byte's $04 is set in Word 5.1a's full saves too
                // [Verified: Word 5.1a documents], so it says nothing about fast saving.
                if (pieces.Length > 0)
                {
                    var saves = flags >> 4;
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.fast-saved",
                        $"\"{title}\" is a Word {version} document saved with Fast Save ({saves} fast saves): its text is in " +
                        "pieces this reader does not put together. Save it again in Word with Fast Save off to read it."));
                    return null;
                }

                var fcMin = file.ReadUInt32At(0x14);
                var fcMac = file.ReadUInt32At(0x18);
                var ccpText = file.ReadUInt32At(0x24);
                var length = file.Source.Length;
                if (fcMin < HeaderSize || fcMac < fcMin || fcMac > length)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-header",
                        $"\"{title}\": the text's place (${fcMin:X}–${fcMac:X}) is not in the file's {length} bytes."));
                    return null;
                }

                var textLength = (int)Math.Min(ccpText == 0 ? fcMac - fcMin : ccpText, fcMac - fcMin);
                fonts = ReadFonts(Zone(FontNames));
                styles = ReadStyles(Zone(StyleZone));
                var characters = Bins(CharacterBins, words: false);
                var paragraphs = Bins(ParagraphBins, words: true);
                return Build(file.Source.Slice((int)fcMin, textLength).Span, (int)fcMin, characters, paragraphs);
            }

            // A zone's bytes (word-mac.md §1.2), or empty when it lies outside the file (reported).
            private ReadOnlyMemory<byte> Zone(int index)
            {
                var at = index < 20 ? 0x40 + 6 * index : 0xBC + 6 * (index - 20);
                long fc = file.ReadUInt32At(at);
                var size = file.ReadUInt16At(at + 4);
                if (size == 0)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }

                if (fc + size > file.Source.Length)
                {
                    Report(DiagnosticSeverity.Warning, "word.bad-zone",
                        $"\"{title}\": table {index} (${fc:X}, {size} bytes) runs past the end of the file; left out.");
                    return ReadOnlyMemory<byte>.Empty;
                }

                return file.Source.Slice((int)fc, size);
            }

            private void Report(DiagnosticSeverity severity, string code, string message)
            {
                if (reported.Add(code))
                {
                    diagnostics.Add(new Diagnostic(severity, code, message));
                }
            }

            // The font names: a count, then per font a reserved word, its family ID and its name [Fitted].
            private static Dictionary<short, string> ReadFonts(ReadOnlyMemory<byte> zone)
            {
                var found = new Dictionary<short, string>();
                if (zone.Length < 2)
                {
                    return found;
                }

                var reader = new BigEndianReader(zone);
                var count = reader.ReadUInt16();
                for (var i = 0; i < count && reader.Position + 5 <= zone.Length; i++)
                {
                    reader.ReadUInt16();
                    var id = reader.ReadInt16();
                    var size = reader.ReadByte();
                    if (reader.Position + size > zone.Length)
                    {
                        break;
                    }

                    found.TryAdd(id, MacRoman.Decode(reader.ReadBytes(size)));
                }

                return found;
            }

            // The style sheet: a word, then the names, the character blocks and the paragraph blocks, each part a length
            // word (counting itself) and an entry per style ($FF for none) [Fitted].
            private List<Style> ReadStyles(ReadOnlyMemory<byte> zone)
            {
                var list = new List<Style>();
                try
                {
                    var reader = new BigEndianReader(zone);
                    if (zone.Length < 2)
                    {
                        return list;
                    }

                    reader.ReadUInt16();
                    var names = reader.ReadUInt16();
                    reader.ReadBytes(Math.Max(0, names - 2));
                    var chps = Blocks(reader);
                    var paps = Blocks(reader);
                    for (var i = 0; i < Math.Max(chps.Count, paps.Count); i++)
                    {
                        var chp = i < chps.Count ? chps[i] : null;
                        var pap = i < paps.Count ? paps[i] : null;
                        // A paragraph block is the style's number, six bytes of line data, then its sprms.
                        list.Add(new Style(chp, pap is { Length: >= 7 } ? pap[7..] : null));
                    }
                }
                catch (System.IO.EndOfStreamException)
                {
                    Report(DiagnosticSeverity.Warning, "word.bad-styles", $"\"{title}\": the style sheet ends early; its styles are left out.");
                    list.Clear();
                }

                return list;
            }

            private static List<byte[]?> Blocks(BigEndianReader reader)
            {
                var size = reader.ReadUInt16();
                var part = reader.ReadSubReader(Math.Max(0, size - 2));
                var blocks = new List<byte[]?>();
                while (part.Position < part.Source.Length)
                {
                    var count = part.ReadByte();
                    blocks.Add(count == 0xFF ? null : part.ReadBytes(count).ToArray());
                }

                return blocks;
            }

            // The runs a bin table's FKP pages give: FC from, FC to, and the property block (empty for none).
            private List<(long From, long To, byte[] Block)> Bins(int zone, bool words)
            {
                var runs = new List<(long, long, byte[])>();
                var table = Zone(zone);
                if (table.Length < 4)
                {
                    return runs;
                }

                var reader = new BigEndianReader(table);
                var count = (table.Length - 4) / 6;
                for (var i = 0; i < count; i++)
                {
                    var pn = reader.ReadUInt16At(4 * (count + 1) + 2 * i);
                    if ((long)(pn + 1) * Page > file.Source.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-zone", $"\"{title}\": a formatting page ({pn}) is past the end of the file; left out.");
                        continue;
                    }

                    var page = new BigEndianReader(file.Source.Slice(pn * Page, Page));
                    var crun = page.ReadByteAt(Page - 1);
                    if (5 * crun + 4 > Page - 1)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-zone", $"\"{title}\": formatting page {pn} says {crun} runs, more than a page holds; left out.");
                        continue;
                    }

                    for (var r = 0; r < crun; r++)
                    {
                        var at = page.ReadByteAt(4 * (crun + 1) + r) * 2;
                        var block = Array.Empty<byte>();
                        if (at > 0)
                        {
                            var size = words ? page.ReadByteAt(at) * 2 : page.ReadByteAt(at);
                            block = page.Source.Slice(at + 1, Math.Min(size, Page - 1 - at)).ToArray();
                        }

                        runs.Add((page.ReadUInt32At(4 * r), page.ReadUInt32At(4 * (r + 1)), block));
                    }
                }

                return runs;
            }

            // Applies a character block (a style's, or a CHPX) to a CHP: the first byte toggles the flags, the second says
            // which of the fields after it apply [Fitted; Reference: libmwaw].
            private static Chp Apply(Chp chp, ReadOnlySpan<byte> block, Chp style)
            {
                if (block.Length == 0)
                {
                    return chp;
                }

                var what = block.Length > 1 ? block[1] : (byte)0;
                if ((what & 0x40) != 0)
                {
                    chp = style;
                }

                chp = chp with { Flags = (byte)(chp.Flags ^ block[0]) };
                if ((what & 0x50) != 0 && block.Length >= 4)
                {
                    chp = chp with { Font = (short)((block[2] << 8) | block[3]) };
                }

                if ((what & 0x48) != 0 && block.Length >= 5 && block[4] > 0)
                {
                    chp = chp with { HalfPoints = block[4] };
                }

                if ((what & 0x04) != 0 && block.Length >= 8)
                {
                    chp = chp with { Underline = (byte)((block[7] >> 1) & 0x07) };
                }

                return chp;
            }

            // Applies paragraph sprms (word-mac.md §1.6); stops at an unknown one (reported).
            private Pap Apply(Pap pap, ReadOnlySpan<byte> sprms)
            {
                for (var i = 0; i < sprms.Length;)
                {
                    var sprm = sprms[i++];
                    if (sprm == 0)
                    {
                        break;
                    }

                    var size = sprm switch
                    {
                        0x02 or 0x05 or 0x07 or 0x08 or 0x09 or 0x0A or 0x0B or 0x18 or 0x19 => 1,
                        0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x16 or >= 0x1E and <= 0x22 or 0x94 or 0x99 => 2,
                        0x0F or 0x17 when i < sprms.Length => 1 + sprms[i],
                        0x98 when i + 1 < sprms.Length => 2 + ((sprms[i] << 8) | sprms[i + 1]),
                        _ => -1,
                    };
                    if (size < 0 || i + size > sprms.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.unknown-sprm",
                            $"\"{title}\": a paragraph property this reader does not know (sprm ${sprm:X2}); the paragraph's later properties are left out.");
                        break;
                    }

                    var arg = sprms.Slice(i, size);
                    var word = size >= 2 ? (short)((arg[0] << 8) | arg[1]) : (short)0;
                    pap = sprm switch
                    {
                        0x05 => pap with
                        {
                            Justification = arg[0] switch
                            {
                                1 => Justification.Center,
                                2 => Justification.Right,
                                3 => Justification.Full,
                                _ => Justification.Left,
                            },
                        },
                        0x10 => pap with { Right = word },
                        0x11 => pap with { Left = word },
                        0x13 => pap with { FirstLine = word },
                        0x15 => pap with { Before = word },
                        0x16 => pap with { After = word },
                        0x18 => pap with { InTable = arg[0] != 0 },
                        0x19 => pap with { RowEnd = arg[0] != 0 },
                        _ => pap,
                    };
                    i += size;
                }

                return pap;
            }

            private Style? StyleOf(int number) => number < styles.Count ? styles[number] : null;

            private Chp StyleChp(int number) => StyleOf(number)?.Chp is { } block ? Apply(DefaultChp, block, DefaultChp) : DefaultChp;

            private Pap StylePap(int number) => StyleOf(number)?.Sprms is { } sprms ? Apply(DefaultPap, sprms) : DefaultPap;

            private StyledDocument Build(ReadOnlySpan<byte> text, int fcMin, List<(long From, long To, byte[] Block)> characters,
                List<(long From, long To, byte[] Block)> paragraphs)
            {
                var output = new StringBuilder(text.Length);
                var runs = new List<TextRun>();
                var formats = new List<ParagraphFormat>();
                var (c, p) = (0, 0);
                var paragraphStart = true;
                Pap? pap = null;
                var style = 0;
                for (var i = 0; i < text.Length; i++)
                {
                    long fc = fcMin + i;
                    while (p < paragraphs.Count && paragraphs[p].To <= fc)
                    {
                        p++;
                        pap = null;
                    }

                    if (pap is null)
                    {
                        var block = p < paragraphs.Count && paragraphs[p].From <= fc ? paragraphs[p].Block : [];
                        style = block.Length > 0 ? block[0] : 0;
                        pap = Apply(StylePap(style), block.Length > 7 ? block.AsSpan(7) : []);
                    }

                    while (c < characters.Count && characters[c].To <= fc)
                    {
                        c++;
                    }

                    var chpx = c < characters.Count && characters[c].From <= fc ? characters[c].Block : [];
                    var styleChp = StyleChp(style);
                    var chp = Apply(styleChp, chpx, styleChp);
                    if (paragraphStart)
                    {
                        // One format per paragraph, so its first and last lines can be told (twips to points).
                        formats.Add(new ParagraphFormat(output.Length, pap.Justification, pap.Left / 20.0, pap.Right / 20.0,
                            pap.FirstLine / 20.0, pap.Before / 20.0, pap.After / 20.0));
                        paragraphStart = false;
                    }

                    var b = text[i];
                    char? shown = b switch
                    {
                        0x0D or 0x0B or 0x0C => '\r',
                        0x09 => '\t',
                        0x07 => pap.RowEnd ? '\r' : '\t',
                        0x1E => '‑',
                        0x1F => '­',
                        < 0x20 => null,
                        _ => chp.AllCaps ? char.ToUpperInvariant(MacRoman.ToChar(b)) : MacRoman.ToChar(b),
                    };
                    if (b is 0x0D or 0x0C or 0x07)
                    {
                        paragraphStart = true;
                    }

                    // Hidden text is left out, as Word shows and prints it by default [ClassicMac]; its paragraph marks stay.
                    if (chp.Hidden && b is not (0x0D or 0x0C or 0x07))
                    {
                        continue;
                    }

                    if (shown is null)
                    {
                        Report(DiagnosticSeverity.Info, "word.not-shown",
                            $"\"{title}\": pictures, footnote references and other special characters are left out.");
                        continue;
                    }

                    var size = chp.HalfPoints / 2;
                    var name = fonts.TryGetValue(chp.Font, out var known) ? known : StyleRuns.FontName(chp.Font);
                    var run = new TextRun(output.Length, 1, chp.Font, name, size > 0 ? size : 12, chp.Face, 0, 0, 0);
                    if (runs.Count > 0 && runs[^1] with { Start = run.Start, Length = 1 } == run && runs[^1].Start + runs[^1].Length == run.Start)
                    {
                        runs[^1] = runs[^1] with { Length = runs[^1].Length + 1 };
                    }
                    else
                    {
                        runs.Add(run);
                    }

                    output.Append(shown.Value);
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
