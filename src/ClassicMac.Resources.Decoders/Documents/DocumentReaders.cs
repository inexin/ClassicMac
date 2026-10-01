using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>Reads DOCMaker and SimpleText documents into a <see cref="StyledDocument"/>.</summary>
    public static class StyledDocuments
    {
        private static readonly FourCC Wndo = FourCC.FromString("Wndo"), Text = FourCC.FromString("TEXT"), Styl = FourCC.FromString("styl"),
            PInf = FourCC.FromString("pInf"), Str = FourCC.FromString("STR "), StrList = FourCC.FromString("STR#"), Pict = FourCC.FromString("PICT"),
            Clut = FourCC.FromString("clut"), STwD = FourCC.FromString("sTwD"), Contents = FourCC.FromString("cnt#"),
            Ttro = FourCC.FromString("ttro");

        private const byte Anchor = 0xCA; // option-space
        private const int MaxPictures = 60, DefaultWindowWidth = 480, ScrollBar = 15;

        /// <summary>
        /// The document a file holds, or null when it holds none: a DOCMaker document (by its resources), else a SimpleText
        /// document (a <c>TEXT</c> or <c>ttro</c> file with a <c>styl</c> 128 or <c>PICT</c> 1000 and up).
        /// </summary>
        public static StyledDocument? Read(ReadOnlyMemory<byte> dataFork, ResourceFork? fork, FourCC type, string title,
            DecodeOptions? options = null, ReadOptions? readOptions = null, ICollection<Diagnostic>? diagnostics = null)
        {
            options ??= DecodeOptions.Default;
            diagnostics ??= new List<Diagnostic>();
            if (fork is not null && ReadDocMaker(fork, title, options, readOptions, diagnostics) is { } docMaker) return docMaker;
            return ReadSimpleText(dataFork, fork, type, title, options, readOptions, diagnostics);
        }

        /// <summary>
        /// A DOCMaker stand-alone document (DOCMaker 4.8.4's reader, from its disassembly): chapter k (from 1, one per
        /// <c>Wndo</c>) is <c>TEXT</c>, <c>styl</c> and <c>Wndo</c> 127+k, titled by <c>STR </c> 2000+k, with pictures
        /// <c>pInf</c> 100k+100+j (j from 1 to the first missing, at most 60), the j-th anchored at the j-th option-space.
        /// </summary>
        public static StyledDocument? ReadDocMaker(ResourceFork fork, string title, DecodeOptions? options = null,
            ReadOptions? readOptions = null, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(fork);
            options ??= DecodeOptions.Default;
            diagnostics ??= new List<Diagnostic>();
            var count = fork.OfType(Wndo).Count();
            if (count < 1 || fork.Find(Text, 128) is null) return null;
            byte[]? Data(FourCC type, int id) =>
                fork.Find(type, (short)id) is { } r ? ResourceDecompression.Default.GetData(r, fork, readOptions, diagnostics).ToArray() : null;
            void Report(DiagnosticSeverity severity, string code, string message) => diagnostics.Add(new Diagnostic(severity, code, message));

            // The window: sTwD 128 mode 2 gives its width in word 3, mode 1 in word 1; else DOCMaker's default.
            var windowWidth = DefaultWindowWidth;
            if (Data(STwD, 128) is { Length: >= 8 } window)
            {
                var reader = new BigEndianReader(window);
                var mode = reader.ReadInt16At(0);
                if (mode == 2) windowWidth = reader.ReadInt16At(6);
                else if (mode == 1) windowWidth = reader.ReadInt16At(2);
            }
            var backgrounds = Data(Clut, 128) is { } clut ? ColorTable(clut) : [];
            var word = StringListItem(Data(StrList, 128), 16, options) ?? "Chapter";

            var chapters = new List<DocumentChapter>();
            for (var k = 1; k <= count; k++)
            {
                var id = 127 + k;
                var chapterTitle = Data(Str, 2000 + k) is { } s ? Pascal(s, options) : $"{word} {k}";
                if (Data(Text, id) is not { } text || Data(Wndo, id) is not { Length: >= 20 } wndo)
                {
                    Report(DiagnosticSeverity.Error, "document.missing-part", $"Chapter {k} (\"{chapterTitle}\") has no TEXT or Wndo {id}; skipped.");
                    continue;
                }
                var wndoReader = new BigEndianReader(wndo);
                var left = wndoReader.ReadInt16At(2);
                var right = wndoReader.ReadInt16At(6);
                var justification = wndoReader.ReadInt16At(0x12) switch
                {
                    1 => Justification.Center,
                    -1 => Justification.Right,
                    _ => Justification.Left,
                };
                Rgb? background = backgrounds.Count == 0 ? null : backgrounds[k - 1 < backgrounds.Count ? k - 1 : 0];

                var anchors = Anchors(text);
                var pictures = new List<DocumentPicture>();
                for (var j = 1; j <= MaxPictures && Data(PInf, 100 * k + 100 + j) is { } info; j++)
                {
                    if (j > anchors.Count)
                    {
                        Report(DiagnosticSeverity.Warning, "document.unanchored-picture",
                            $"Chapter {k}'s picture {j} (pInf {100 * k + 100 + j}) has no option-space to anchor it; not shown.");
                        continue;
                    }
                    if (PictureInfo(info, anchors[j - 1], fork, readOptions, diagnostics, options, k, count) is { } picture) pictures.Add(picture);
                }
                chapters.Add(new DocumentChapter(k, chapterTitle, StyledText.Read(text, Data(Styl, id), options), justification, background,
                    pictures, windowWidth - ScrollBar - left - right));
            }
            return new StyledDocument(DocumentKind.DocMaker, title, chapters, ContentsEntries(Data(Contents, 128), options));
        }

        /// <summary>
        /// A SimpleText document (SimpleText 1.4, from its disassembly): the data fork's text styled by <c>styl</c> 128, and,
        /// when the file has any <c>PICT</c>, the k-th option-space (from 0) showing <c>PICT</c> 1000+k, centred.
        /// </summary>
        public static StyledDocument? ReadSimpleText(ReadOnlyMemory<byte> dataFork, ResourceFork? fork, FourCC type, string title,
            DecodeOptions? options = null, ReadOptions? readOptions = null, ICollection<Diagnostic>? diagnostics = null)
        {
            options ??= DecodeOptions.Default;
            diagnostics ??= new List<Diagnostic>();
            if (type != Text && type != Ttro) return null;
            var styl = fork?.Find(Styl, 128) is { } s ? ResourceDecompression.Default.GetData(s, fork, readOptions, diagnostics).ToArray() : null;
            var hasPictures = fork is not null && fork.OfType(Pict).Any();
            if (styl is null && !(hasPictures && fork!.OfType(Pict).Any(p => p.Id >= 1000))) return null;

            var text = dataFork.ToArray();
            var pictures = new List<DocumentPicture>();
            if (hasPictures)
            {
                var anchors = Anchors(text);
                for (var k = 0; k < anchors.Count; k++)
                {
                    // k advances even when the picture is missing, so a missing one leaves a gap.
                    if (fork!.Find(Pict, (short)(1000 + k)) is not { } resource) continue;
                    var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
                    var (width, height) = PictureSize(data.Span, scaleTo72Dpi: true);
                    pictures.Add(new DocumentPicture(anchors[k], (short)(1000 + k), data, width, height, PictureAlignment.Center, true, PictureAction.None));
                }
            }
            var chapter = new DocumentChapter(1, title, StyledText.Read(text, styl, options), Justification.Left, null, pictures, 0);
            return new StyledDocument(DocumentKind.SimpleText, title, [chapter], []);
        }

        // The offsets of the option-space characters (one byte each in the single-byte encodings).
        private static List<int> Anchors(ReadOnlySpan<byte> text)
        {
            var anchors = new List<int>();
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == Anchor) anchors.Add(i);
            }
            return anchors;
        }

        // pInf: PICT ID, alignment (1 centre, 2 left, 3 right), no-scale flag (1), action and its data, print flag.
        private static DocumentPicture? PictureInfo(byte[] info, int anchor, ResourceFork fork, ReadOptions? readOptions,
            ICollection<Diagnostic> diagnostics, DecodeOptions options, int chapter, int chapters)
        {
            if (info.Length < 8)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "document.bad-picture", $"Chapter {chapter} has a pInf of {info.Length} bytes; skipped."));
                return null;
            }
            var span = info.AsSpan();
            var reader = new BigEndianReader(span);
            var id = reader.ReadInt16();
            var alignment = reader.ReadInt16() switch
            {
                2 => PictureAlignment.Left,
                3 => PictureAlignment.Right,
                _ => PictureAlignment.Center,
            };
            var noScale = reader.ReadInt16() == 1;
            int code = reader.ReadInt16();
            var at = 8;
            var action = new PictureAction(Math.Abs(code), code >= 0);
            switch (Math.Abs(code))
            {
                case 1 when span.Length >= at + 4:
                    action = action with { Chapter = reader.ReadInt16At(at), Paragraph = reader.ReadInt16At(at + 2) };
                    if (action.Chapter < 1 || action.Chapter > chapters)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "document.bad-link",
                            $"Chapter {chapter}'s picture {id} links to chapter {action.Chapter}, which the document does not have; the reader ignores the click."));
                    }
                    break;
                case 5 or 7 or 8 or 16:
                    if (MacText.TryReadPascal(span, ref at, out var text)) action = action with { Text = MacText.Decode(text, options) };
                    break;
                case 13 when span.Length >= at + 12:
                    at += 12;
                    if (MacText.TryReadPascal(span, ref at, out var script)) action = action with { Text = MacText.Decode(script, options) };
                    break;
                case 0 or 2 or 3 or 4 or 6 or 9 or 10 or 11 or 12 or 14 or 15:
                    break;
                default:
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "document.unknown-action",
                        $"Chapter {chapter}'s picture {id} has action {code}, which DOCMaker 4.8 does not have."));
                    break;
            }
            ReadOnlyMemory<byte>? picture = null;
            int width = 0, height = 0;
            if (fork.Find(Pict, id) is { } resource)
            {
                picture = ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
                (width, height) = PictureSize(picture.Value.Span, scaleTo72Dpi: false);
            }
            else
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "document.missing-picture", $"Chapter {chapter}'s PICT {id} is missing."));
            }
            return new DocumentPicture(anchor, id, picture, width, height, alignment, noScale, action);
        }

        // A picture's frame size; SimpleText scales an extended version 2 picture's frame to 72 dpi by its resolution.
        internal static (int Width, int Height) PictureSize(ReadOnlySpan<byte> pict, bool scaleTo72Dpi)
        {
            if (pict.Length < 10) return (0, 0);
            var reader = new BigEndianReader(pict);
            var frame = reader.ReadMacRectAt(2);
            int width = frame.Right - frame.Left;
            int height = frame.Bottom - frame.Top;
            if (scaleTo72Dpi && pict.Length >= 0x1C && reader.ReadUInt16At(0x0A) == 0x0011 && reader.ReadInt16At(0x10) == -2)
            {
                var hRes = reader.ReadInt32At(0x14) / 65536.0;
                var vRes = reader.ReadInt32At(0x18) / 65536.0;
                if (hRes > 0) width = (int)(width * 72 / hRes);
                if (vRes > 0) height = (int)(height * 72 / vRes);
            }
            return (Math.Max(0, width), Math.Max(0, height));
        }

        // A colour table's entries: seed (4), flags (2), count − 1 (2), then value and RGB (8 each).
        private static List<Rgb> ColorTable(byte[] clut)
        {
            var colours = new List<Rgb>();
            if (clut.Length < 8) return colours;
            var count = new BigEndianReader(clut).ReadInt16At(6) + 1;
            for (var i = 0; i < count && 16 + i * 8 <= clut.Length; i++)
            {
                var e = clut.AsSpan(8 + i * 8);
                colours.Add(new Rgb(e[2], e[4], e[6]));
            }
            return colours;
        }

        // cnt#: a count, then {chapter, selection start, selection end, title} each.
        private static List<ContentsEntry> ContentsEntries(byte[]? data, DecodeOptions options)
        {
            var entries = new List<ContentsEntry>();
            if (data is not { Length: >= 2 }) return entries;
            var reader = new BigEndianReader(data);
            var count = reader.ReadInt16At(0);
            var at = 2;
            for (var i = 0; i < count && at + 6 < data.Length; i++)
            {
                var span = data.AsSpan();
                int chapter = reader.ReadInt16At(at), start = reader.ReadInt16At(at + 2), end = reader.ReadInt16At(at + 4);
                at += 6;
                if (!MacText.TryReadPascal(span, ref at, out var title)) break;
                entries.Add(new ContentsEntry(chapter, start, end, MacText.Decode(title, options)));
            }
            return entries;
        }

        private static string Pascal(byte[] data, DecodeOptions options)
        {
            var at = 0;
            MacText.TryReadPascal(data, ref at, out var text);
            return MacText.Decode(text, options);
        }

        private static string? StringListItem(byte[]? list, int index, DecodeOptions options)
        {
            if (list is not { Length: >= 2 } || index > new BigEndianReader(list).ReadUInt16At(0)) return null;
            var at = 2;
            for (var i = 1; i <= index; i++)
            {
                if (!MacText.TryReadPascal(list, ref at, out var item)) return null;
                if (i == index) return MacText.Decode(item, options);
            }
            return null;
        }
    }
}
