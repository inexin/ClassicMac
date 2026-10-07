using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents;

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

    // Word 6 and 95's FIB identifier [Verified: Word 6.0 for the Macintosh documents; Reference: Apache POI].
    private const ushort Word6Ident = 0xA5DC;
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
        if (span.Length < 0x20 || BinaryPrimitives.ReadUInt16LittleEndian(span) is not (WordIdent or Word6Ident))
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
            return new Reader(document, document, document, title, diagnostics, word6: true, mac).Read();
        }

        var tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        if (file.Find(tableName) is not { } table)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "word.bad-fib", $"\"{title}\": the FIB names the {tableName} stream, which the file does not have."));
            return null;
        }

        // Pictures are in the Data stream ([MS-DOC] PICFAndOfficeArtData).
        var pictures = file.Find("Data") is { } dataEntry ? file.ReadStream(dataEntry) : ReadOnlyMemory<byte>.Empty;
        return new Reader(document, file.ReadStream(table), pictures, title, diagnostics).Read();
    }

    /// <summary>One piece of the text ([MS-DOC] §2.9.177): characters from <paramref name="Cp"/> up to <paramref name="CpEnd"/>,
    /// stored from <paramref name="Fc"/> one byte (compressed) or two each.</summary>
    internal readonly record struct Piece(int Cp, int CpEnd, long Fc, bool Compressed, ushort Prm);

    // Character properties as far as ClassicMac shows them.
    internal readonly record struct Chp(bool Bold, bool Italic, bool Outline, bool Shadow, bool Caps, bool Hidden, bool Underline, int Font, int HalfPoints,
        bool SmallCaps = false, int Ico = 0, bool Special = false, int PicLocation = -1);

    // The character the HTML output and the viewer anchor a picture at (DocumentChapter's option space).
    private const char Anchor = (char)0xA0;

    // The most a deflated PICT blip may inflate to (64 MB).
    private const long MaxPicture = 64L << 20;

    // The Ico colours ([MS-DOC] §2.9.119): 0 automatic, 1 black, 2 blue, 3 cyan, 4 green, 5 magenta, 6 red, 7 yellow,
    // 8 white, 9–16 the dark ones and grey [Doc; Verified: 6 is red in Word 6.0 for the Macintosh].
    private static readonly (byte, byte, byte)[] Icos =
    [
        (0, 0, 0), (0, 0, 0), (0, 0, 0xFF), (0, 0xFF, 0xFF), (0, 0xFF, 0), (0xFF, 0, 0xFF), (0xFF, 0, 0), (0xFF, 0xFF, 0), (0xFF, 0xFF, 0xFF),
        (0, 0, 0x80), (0, 0x80, 0x80), (0, 0x80, 0), (0x80, 0, 0x80), (0x80, 0, 0), (0x80, 0x80, 0), (0x80, 0x80, 0x80), (0xC0, 0xC0, 0xC0),
    ];

    // Paragraph properties, in twips.
    internal sealed record Pap(Justification Justification, int Left, int Right, int FirstLine, int Before, int After, bool InTable, bool RowEnd,
        short[]? CellEdges = null);

    private sealed record Style(int Base, int Type, byte[] Papx, byte[] Chpx);

    private sealed class Reader(ReadOnlyMemory<byte> document, ReadOnlyMemory<byte> table, ReadOnlyMemory<byte> data, string title, ICollection<Diagnostic> diagnostics,
        bool word6 = false, bool mac = false)
    {
        private readonly HashSet<string> reported = [];
        private readonly Dictionary<int, (Chp Chp, Pap Pap)> styleCache = [];
        private List<string> fonts = [];
        private readonly List<byte[]> prcs = [];                // the Clx's Prcs, which a Prm1 names
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
            if (!word6 && index >= BinaryPrimitives.ReadUInt16LittleEndian(Fib[152..]))
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            return TableAt(at, index);
        }

        // The table structure whose FC and length are at +at in the FIB.
        private ReadOnlyMemory<byte> TableAt(int at, int index)
        {
            if (Fib.Length < at + 8)
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
            // The character counts: the main text, footnotes, headers, macros, annotations, endnotes, text boxes, header text
            // boxes, each part's text after the one before ([MS-DOC] FibRgLw97; Word 6's from +$34).
            var ccpAt = word6 ? 0x34 : 0x4C;
            int Ccp(int k) => Fib.Length >= ccpAt + 4 * k + 4 ? Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(Fib[(ccpAt + 4 * k)..])) : 0;
            var ccpText = Ccp(0);
            var allText = Enumerable.Range(0, 8).Sum(Ccp) + 1;
            var pieces = Pieces(Table(Clx));
            if (word6 && pieces.Count == 0 && Fib.Length >= 0x20)
            {
                // A Word 6 document that was not fast saved has no piece table: its text is one run of bytes from fcMin
                // [Reference: Apache POI].
                pieces.Add(new Piece(0, allText, BinaryPrimitives.ReadUInt32LittleEndian(Fib[0x18..]), true, 0));
            }
            if (pieces.Count == 0)
            {
                Report(DiagnosticSeverity.Error, "word.bad-pieces", "the document has no piece table, so its text cannot be found.");
                return null;
            }

            fonts = word6 ? ReadSixFonts(Table(SttbfFfn), mac) : ReadFonts(Table(SttbfFfn));
            ReadStyles(Table(Stshf));
            var characters = Bins(Table(PlcfBteChpx), paragraphs: false);
            var paragraphs = Bins(Table(PlcfBtePapx), paragraphs: true);

            // The main text: CPs 0 up to ccpText ([MS-DOC] §2.4.1), with each character's FC; then each note's text.
            var text = new List<(char Char, long Fc, ushort Prm)>();
            void Add(int from, int to)
            {
            foreach (var piece in pieces)
            {
                for (var cp = Math.Max(piece.Cp, from); cp < piece.CpEnd && cp < to; cp++)
                {
                    var fc = piece.Fc + (cp - piece.Cp) * (piece.Compressed ? 1 : 2);
                    if (fc + (piece.Compressed ? 1 : 2) > document.Length)
                    {
                        Report(DiagnosticSeverity.Warning, "word.bad-pieces", "a piece of the text lies past the end of the WordDocument stream; cut there.");
                        break;
                    }

                    var c = !piece.Compressed ? (char)BinaryPrimitives.ReadUInt16LittleEndian(document.Span[(int)fc..])
                        : mac ? MacRoman.ToChar(document.Span[(int)fc]) : Compressed(document.Span[(int)fc]);
                    text.Add((c, fc, piece.Prm));
                }
            }
            }

            Add(0, ccpText);
            var mainLength = text.Count;
            var footnotes = Notes(Table(2), Table(3), ccpText, Ccp(1), "footnote");
            var endnotes = Notes(word6 ? TableAt(0x1D2, 46) : Table(46), word6 ? TableAt(0x1DA, 47) : Table(47),
                ccpText + Ccp(1) + Ccp(2) + Ccp(3) + Ccp(4), Ccp(5), "endnote");
            var notes = new List<(int Reference, int Start, int End, int Number)>();
            foreach (var (reference, from, to, number) in footnotes.Concat(endnotes))
            {
                var start = text.Count;
                Add(from, to);
                notes.Add((reference, start, text.Count, number));
            }

            return Build(text, characters, paragraphs, mainLength, notes);
        }

        // A part's notes ([MS-DOC] PlcffndRef and PlcffndTxt, PlcfendRef and PlcfendTxt; Word 6's endnotes' at FIB +$1D2 and
        // +$1DA [Fitted: Word 6.0 for the Macintosh documents]): the references' CPs, n + 1, then a 2-byte FRD each; each
        // note's first CP in the part's text, n + 2. Each is (its reference, its text's first CP and the one after its
        // last, its number among the part's notes).
        private List<(int Reference, int From, int To, int Number)> Notes(ReadOnlyMemory<byte> references, ReadOnlyMemory<byte> texts,
            int partStart, int partLength, string kind)
        {
            var notes = new List<(int, int, int, int)>();
            var count = references.Length >= 10 ? (references.Length - 4) / 6 : 0;
            for (var k = 0; k < count; k++)
            {
                var reference = BinaryPrimitives.ReadInt32LittleEndian(references.Span[(4 * k)..]);
                if (4 * (k + 2) > texts.Length)
                {
                    Report(DiagnosticSeverity.Warning, "word.bad-notes", $"{kind} {k + 1}'s text cannot be found; it is left out.");
                    continue;
                }

                var from = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(texts.Span[(4 * k)..]), 0, partLength);
                var to = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(texts.Span[(4 * (k + 1))..]), from, partLength);
                notes.Add((reference, partStart + from, partStart + to, k + 1));
            }

            return notes;
        }

        // A picture ([MS-DOC] PICFAndOfficeArtData) at sprmCPicLocation in the Data stream (Word 6's in the WordDocument
        // stream): lcb, cbHeader, the mfpf's mm at +6; the goal size in twips at +28 and +30, scaled by mx and my in
        // thousandths at +32 and +34. An mm of $64 (or $66, after a name) is an Office Art shape, its picture a blip; another
        // is a Windows metafile, which Word 6.0 for the Macintosh follows with the PICT itself. Null when it cannot be read.
        private (ReadOnlyMemory<byte> Data, PictureFormat Format, int Width, int Height)? Picture(int location)
        {
            if (location < 0 || location > data.Length - 0x24)
            {
                return null;
            }

            var header = data.Span[location..];
            long lcb = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int cbHeader = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            if (cbHeader < 0x24 || lcb < cbHeader || lcb > data.Length - location)
            {
                return null;
            }

            int mm = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            var picf = data.Slice(location, cbHeader);
            int Size(int goal, int scale) => (int)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(picf.Span[goal..])
                * (BinaryPrimitives.ReadUInt16LittleEndian(picf.Span[scale..]) is var factor and > 0 ? factor : 1000) / 20000.0);
            var body = data.Slice(location + cbHeader, (int)lcb - cbHeader);
            if (mm == 0x66 && body.Length > 0)
            {
                body = body[Math.Min(body.Length, 1 + body.Span[0])..];
            }

            var found = mm is 0x64 or 0x66 ? Blip(body) : AfterMetafile(body);
            return found is { } picture ? (picture.Data, picture.Format, Math.Max(0, Size(28, 32)), Math.Max(0, Size(30, 34))) : null;
        }

        // The first blip in Office Art records ([MS-ODRAW] OfficeArtRecordHeader: version and instance, type, length),
        // looking into containers (version $F) and FBSEs (a 36-byte header, a name, then the blip).
        private static (ReadOnlyMemory<byte> Data, PictureFormat Format)? Blip(ReadOnlyMemory<byte> records)
        {
            for (var at = 0; at + 8 <= records.Length;)
            {
                var span = records.Span[at..];
                int versionAndInstance = BinaryPrimitives.ReadUInt16LittleEndian(span), type = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                long length = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                if (length > records.Length - at - 8)
                {
                    return null;
                }

                var body = records.Slice(at + 8, (int)length);
                var found = (versionAndInstance & 0xF) == 0xF ? Blip(body)
                    : type == 0xF007 && length >= 36 && 36 + body.Span[33] <= length ? Blip(body[(36 + body.Span[33])..])
                    : type is >= 0xF018 and <= 0xF117 ? BlipData(type, versionAndInstance >> 4, body)
                    : null;
                if (found is not null)
                {
                    return found;
                }

                at += 8 + (int)length;
            }

            return null;
        }

        // A blip's picture ([MS-ODRAW] OfficeArtBlipPICT, OfficeArtBlipPNG, OfficeArtBlipJPEG): after one UID, or two for the
        // instances that say so; a PICT after a 34-byte metafile header (cbSize, rcBounds, ptSize, cbSave, compression: 0
        // deflated, $FE stored), a PNG or JPEG after a tag byte. Other kinds (EMF, WMF, DIB, TIFF) are not read.
        private static (ReadOnlyMemory<byte> Data, PictureFormat Format)? BlipData(int type, int instance, ReadOnlyMemory<byte> body)
        {
            switch (type)
            {
                case 0xF01C:
                    {
                        var uids = instance == 0x543 ? 32 : 16;
                        if (body.Length < uids + 34)
                        {
                            return null;
                        }

                        var header = body.Span[uids..];
                        long size = BinaryPrimitives.ReadUInt32LittleEndian(header), saved = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
                        var stored = body[(uids + 34)..];
                        stored = stored[..(int)Math.Min(saved, stored.Length)];
                        if (header[32] == 0xFE)
                        {
                            return (stored, PictureFormat.Pict);
                        }

                        if (header[32] != 0 || size > MaxPicture)
                        {
                            return null;
                        }

                        try
                        {
                            using var inflater = new ZLibStream(new MemoryStream(stored.ToArray()), CompressionMode.Decompress);
                            var pict = new byte[size];
                            inflater.ReadAtLeast(pict, pict.Length, throwOnEndOfStream: false);
                            return (pict, PictureFormat.Pict);
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    }

                case 0xF01E:
                    {
                        var start = (instance == 0x6E1 ? 32 : 16) + 1;
                        return body.Length > start ? (body[start..], PictureFormat.Png) : null;
                    }

                case 0xF01D or 0xF02A:
                    {
                        var start = (instance is 0x46B or 0x6E3 ? 32 : 16) + 1;
                        return body.Length > start ? (body[start..], PictureFormat.Jpeg) : null;
                    }

                default:
                    return null;
            }
        }

        // The PICT after a Windows metafile [Fitted: Word 6.0 for the Macintosh documents]: the metafile's 18-byte header
        // (type 1 or 2, header size 9; its size field undercounts), then records (a size in words, a function) up to the
        // one of function 0 that ends it.
        private static (ReadOnlyMemory<byte> Data, PictureFormat Format)? AfterMetafile(ReadOnlyMemory<byte> body)
        {
            var span = body.Span;
            if (span.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(span) is not (1 or 2) || BinaryPrimitives.ReadUInt16LittleEndian(span[2..]) != 9)
            {
                return null;
            }

            for (long at = 18; at <= span.Length - 6;)
            {
                long words = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)at..]);
                var function = BinaryPrimitives.ReadUInt16LittleEndian(span[(int)(at + 4)..]);
                if (words < 3 || words > (span.Length - at) / 2)
                {
                    return null;
                }

                at += 2 * words;
                if (function == 0)
                {
                    return at <= span.Length - 10 ? (body[(int)at..], PictureFormat.Pict) : null;
                }
            }

            return null;
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
                var size = BinaryPrimitives.ReadInt16LittleEndian(span[(at + 1)..]);
                if (size < 0)
                {
                    return pieces;                                       // a damaged Prc: no piece table found
                }

                prcs.Add(span.Slice(at + 3, Math.Min(size, span.Length - at - 3)).ToArray());
                at += 3 + size;
            }

            if (at + 5 > span.Length || span[at] != 0x02)
            {
                return pieces;
            }

            var lcb = BinaryPrimitives.ReadInt32LittleEndian(span[(at + 1)..]);
            var plc = span.Slice(at + 5, Math.Clamp(lcb, 0, span.Length - at - 5));
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
            68 => (-1, 0x6A03),
            12 or 15 or 23 or 64 or 74 or 77 or 79 or 81 or 82 or 103 or 105 or 106 or 108 or >= 111 and <= 116 or 179 or 181 or 191 or 207 => (-1, 0),
            117 => (1, 0x0855),
            65 or 66 or 67 or 71 or 75 or 100 or 102 or 104 or 118 or 119 => (1, 0),
            90 => (1, 0x083A),
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
            98 => (1, 0x2A42),
            99 => (2, 0x4A43),
            131 or 132 or 138 or 139 or 142 or 143 or 146 or 147 or 150 or 151 or 152 or 153 or 158 or 159 or 162 or 185 or 186 => (1, 0),
            136 or 137 => (3, 0),
            140 or 141 or 144 or 145 or 148 or 149 or 154 or 155 or 156 or 157 or 160 or 161 or >= 164 and <= 171 or 182 or 183 or 184 or 189 or 195 or 197 or 198 => (2, 0),
            133 => (-1, 0),
            163 => (0, 0),
            187 => (12, 0),
            188 => (-2, 0),
            190 => (-2, 0xD608),
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

        // A piece's Prm ([MS-DOC] Prm, Prm0, Prm1): a fast save's property changes to its text. A Prm1 (bit 0 set) names
        // the Clx's Prc by index (bits 1–15); a Prm0 is one sprm, by isprm (bits 1–7), with its operand byte (bits 8–15),
        // by [MS-DOC] Prm0's table in Word 97. That table's isprms are Word 6's sprm numbers (5 sprmPJc, $55 sprmCFBold
        // …), so a Word 6 Prm0's isprm is taken as its sprm [Fitted: the tables agree; no Word 6 Prm sample]. A Prm0 of
        // 0 has no effect; only the sprms this reader applies are mapped.
        private IEnumerable<(ushort Sprm, byte[] Operand)> PrmSprms(ushort prm)
        {
            if ((prm & 1) != 0)
            {
                var index = prm >> 1;
                if (index < prcs.Count)
                {
                    return Sprms(prcs[index]);
                }

                Report(DiagnosticSeverity.Warning, "word.piece-properties",
                    "a fast save's property changes name a list the document does not have, so they are left out.");
                return [];
            }

            var (isprm, val) = ((byte)((prm >> 1) & 0x7F), (byte)(prm >> 8));
            if (prm == 0)
            {
                return [];
            }

            if (word6)
            {
                return SixSprm(isprm) switch
                {
                    (0, var same and not 0) => [(same, [])],
                    (1, var same and not 0) => [(same, [val])],
                    _ => [],
                };
            }

            ushort? sprm = isprm switch
            {
                0x05 => 0x2403,              // sprmPJc
                0x18 => 0x2416,              // sprmPFInTable
                0x19 => 0x2417,              // sprmPFTtp
                0x53 => 0xFFFF,              // sprmCPlain (as Word 6's, the style's properties)
                0x55 => 0x0835,              // sprmCFBold
                0x56 => 0x0836,              // sprmCFItalic
                0x58 => 0x0838,              // sprmCFOutline
                0x59 => 0x0839,              // sprmCFShadow
                0x5A => 0x083A,              // sprmCFSmallCaps
                0x5B => 0x083B,              // sprmCFCaps
                0x5C => 0x083C,              // sprmCFVanish
                0x5E => 0x2A3E,              // sprmCKul
                0x62 => 0x2A42,              // sprmCIco
                _ => null,
            };
            return sprm is { } code ? [(code, [val])] : [];
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
        private Chp ApplyChp(Chp chp, byte[] grpprl, Chp style) => ApplyChp(chp, Sprms(grpprl), style);

        private static Chp ApplyChp(Chp chp, IEnumerable<(ushort Sprm, byte[] Operand)> sprms, Chp style)
        {
            foreach (var (sprm, operand) in sprms)
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
                    0x083A => chp with { SmallCaps = Toggle(operand[0], style.SmallCaps) },
                    0x2A42 => chp with { Ico = operand[0] },
                    0x2A3E => chp with { Underline = operand[0] != 0 },
                    0x4A43 => chp with { HalfPoints = BinaryPrimitives.ReadUInt16LittleEndian(operand) },
                    0x4A4F => chp with { Font = BinaryPrimitives.ReadUInt16LittleEndian(operand) },
                    0x0855 => chp with { Special = operand[0] != 0 },
                    // sprmCPicLocation: 4 bytes (Word 6's after a size byte).
                    0x6A03 when operand.Length >= 4 => chp with { PicLocation = BinaryPrimitives.ReadInt32LittleEndian(operand.AsSpan(operand.Length - 4)) },
                    _ => chp,
                };
            }

            return chp;
        }

        // Paragraph sprms ([MS-DOC] §2.6.2).
        private Pap ApplyPap(Pap pap, byte[] grpprl) => ApplyPap(pap, Sprms(grpprl));

        private static Pap ApplyPap(Pap pap, IEnumerable<(ushort Sprm, byte[] Operand)> sprms)
        {
            foreach (var (sprm, operand) in sprms)
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
                    // sprmTDefTable ([MS-DOC] §2.6.5): a size word, the cell count, then the row's left edge and each
                    // cell's right edge (twips) [Verified: Word 6.0 for the Macintosh, its sprm 190].
                    0xD608 when operand.Length >= 3 => pap with { CellEdges = Edges(operand) },
                    _ => pap,
                };
            }

            return pap;
        }

        private static short[] Edges(byte[] operand)
        {
            var count = operand[2];
            var edges = new List<short>();
            for (var k = 0; k <= count && 4 + 2 * k <= operand.Length; k++)
            {
                edges.Add(BinaryPrimitives.ReadInt16LittleEndian(operand.AsSpan(3 + 2 * k)));
            }

            return [.. edges];
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

        // The main text's characters are text[..mainLength]; each note's follow, from Start to End (indexes in text), its
        // mark ($02) at its reference (a main-text CP) and at its start.
        private StyledDocument Build(List<(char Char, long Fc, ushort Prm)> text, List<(long From, long To, byte[] Block)> characters,
            List<(long From, long To, byte[] Block)> paragraphs, int mainLength, List<(int Reference, int Start, int End, int Number)> notes)
        {
            var references = new Dictionary<int, int>();
            var noteStarts = new Dictionary<int, int>();
            var noteEnds = new Dictionary<int, int>();
            for (var k = 0; k < notes.Count; k++)
            {
                references.TryAdd(notes[k].Reference, k);
                noteStarts.TryAdd(notes[k].Start, k);
                noteEnds.TryAdd(notes[k].End, k);
            }

            var notePlaces = new (int Reference, int Start, int End)[notes.Count];
            var pictures = new List<DocumentPicture>();
            characters.Sort((a, b) => a.From.CompareTo(b.From));
            paragraphs.Sort((a, b) => a.From.CompareTo(b.From));
            var output = new StringBuilder(text.Count);
            var runs = new List<TextRun>();
            var formats = new List<ParagraphFormat>();
            var tables = new List<DocumentTable>();
            int? tableStart = null;
            short[]? tableEdges = null;
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
                pap = ApplyPap(pap, PrmSprms(text[end].Prm));                       // the changes of the piece holding its mark
                // A table: the rows from its first cell to the paragraph after its last row.
                if (pap.InTable && tableStart is null)
                {
                    tableStart = output.Length;
                }
                else if (!pap.InTable && tableStart is { } open)
                {
                    tables.Add(new DocumentTable(open, output.Length, tableEdges?.Select(e => e / 20.0).ToArray() ?? []));
                    (tableStart, tableEdges) = (null, null);
                }

                if (pap.RowEnd && pap.CellEdges is { } edges)
                {
                    tableEdges ??= edges;
                }

                formats.Add(new ParagraphFormat(output.Length, pap.Justification, pap.Left / 20.0, pap.Right / 20.0, pap.FirstLine / 20.0,
                    pap.Before / 20.0, pap.After / 20.0));
                for (var i = start; i <= end; i++)
                {
                    if (noteStarts.TryGetValue(i, out var starting))
                    {
                        notePlaces[starting].Start = output.Length;
                    }

                    if (noteEnds.TryGetValue(i, out var ended))
                    {
                        notePlaces[ended].End = output.Length;
                    }

                    var (c, fc, prm) = text[i];
                    var chpRun = Find(characters, fc);
                    var chp = chpRun >= 0 ? ApplyChp(styleChp, characters[chpRun].Block, styleChp) : styleChp;
                    chp = ApplyChp(chp, PrmSprms(prm), styleChp);

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

                    // A note's mark ($02 with sprmCFSpec): its number at its reference and at the start of its text.
                    var note = c == (char)0x02 && chp.Special
                        ? references.TryGetValue(i, out var referenced) ? referenced : noteStarts.TryGetValue(i, out var own) ? own : -1
                        : -1;
                    (ReadOnlyMemory<byte> Data, PictureFormat Format, int Width, int Height)? picture = null;
                    if (c == (char)0x01 && chp.Special && !chp.Hidden)
                    {
                        picture = Picture(chp.PicLocation);
                        if (picture is null)
                        {
                            Report(DiagnosticSeverity.Warning, "word.bad-picture", "a picture's data cannot be found; it is left out.");
                        }
                    }

                    var shown = c switch
                    {
                        '\r' or '\f' or (char)0x0E => "\r",
                        '\v' => ((char)0x2028).ToString(),                     // a line break within the paragraph [Verified: Word 6.0]
                        '\t' => "\t",
                        '\a' => pap.RowEnd ? "\r" : "\t",
                        (char)0x1E => "‑",
                        (char)0x1F => "­",
                        (char)0x02 when note >= 0 => notes[note].Number.ToString(CultureInfo.InvariantCulture),
                        (char)0x01 when picture is not null => Anchor.ToString(),
                        < ' ' => null,
                        _ => (chp.Caps ? char.ToUpperInvariant(c) : c).ToString(),
                    };
                    if (note >= 0 && i < mainLength)
                    {
                        notePlaces[note].Reference = output.Length;
                    }

                    if (picture is { } found && shown is not null)
                    {
                        pictures.Add(new DocumentPicture(output.Length, (short)(pictures.Count + 1), found.Data, found.Width, found.Height,
                            pap.Justification switch { Justification.Center => PictureAlignment.Center, Justification.Right => PictureAlignment.Right, _ => PictureAlignment.Left },
                            NoScale: true, PictureAction.None) { Format = found.Format });
                    }
                    if (chp.Hidden && c is not ('\r' or '\a' or '\f'))
                    {
                        continue;
                    }

                    if (shown is null && c == (char)0x01 && chp.Special)
                    {
                        continue;                                            // a picture that cannot be read (reported)
                    }

                    if (shown is null)
                    {
                        Report(DiagnosticSeverity.Info, "word.not-shown", "special characters other than pictures and note marks are left out.");
                        continue;
                    }

                    var name = chp.Font < fonts.Count ? fonts[chp.Font] : "Times New Roman";
                    var face = (byte)((chp.Bold ? 0x01 : 0) | (chp.Italic ? 0x02 : 0) | (chp.Underline ? 0x04 : 0) | (chp.Outline ? 0x08 : 0)
                        | (chp.Shadow ? 0x10 : 0));
                    var size = chp.HalfPoints / 2;
                    var (red, green, blue) = Icos[chp.Ico is >= 0 and < 17 ? chp.Ico : 0];
                    var textRun = new TextRun(output.Length, shown.Length, (short)chp.Font, name, size > 0 ? size : 10, face, red, green, blue) { SmallCaps = chp.SmallCaps };
                    if (runs.Count > 0 && runs[^1] with { Start = textRun.Start, Length = textRun.Length } == textRun && runs[^1].Start + runs[^1].Length == textRun.Start)
                    {
                        runs[^1] = runs[^1] with { Length = runs[^1].Length + textRun.Length };
                    }
                    else
                    {
                        runs.Add(textRun);
                    }

                    output.Append(shown);
                }

                start = end + 1;
            }

            if (tableStart is { } last)
            {
                tables.Add(new DocumentTable(last, output.Length, tableEdges?.Select(e => e / 20.0).ToArray() ?? []));
            }

            foreach (var (_, k) in noteEnds.Where(e => e.Key >= text.Count))
            {
                notePlaces[k].End = output.Length;
            }

            var chapter = new DocumentChapter(1, title, new StyledText(output.ToString(), runs, true), Justification.Left, null, pictures, 0)
            {
                Paragraphs = formats,
                Tables = tables,
                Notes = [.. notePlaces.Select((n, k) => new DocumentNote(notes[k].Number, notes[k].Number.ToString(CultureInfo.InvariantCulture), n.Reference, n.Start, n.End))],
            };
            return new StyledDocument(DocumentKind.Word, title, [chapter], []);
        }
    }
}
