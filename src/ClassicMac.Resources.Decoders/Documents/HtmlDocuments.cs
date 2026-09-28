using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>One file of a converted document: its path in the output folder ('/'-separated) and its content.</summary>
    /// <param name="Path">The path, relative to the output folder.</param>
    /// <param name="Content">The bytes.</param>
    public sealed record DocumentFile(string Path, ReadOnlyMemory<byte> Content);

    /// <summary>
    /// Writes a <see cref="StyledDocument"/> as a folder of HTML: <c>index.html</c> (a DOCMaker document's contents, or a
    /// SimpleText document's text), <c>chapter-NN.html</c> per DOCMaker chapter, <c>style.css</c>, and the pictures in
    /// <c>images/</c>. UTF-8 with LF line ends; the same document always gives the same bytes.
    /// </summary>
    /// <remarks>
    /// The pictures are reflowed into the text [ClassicMac]: DOCMaker and SimpleText draw a picture over the text at the
    /// top of its anchor's line, and authors leave blank lines below it to make room. Here each picture is a block at its
    /// anchor instead; pictures anchored on one line sit side by side in one row, each placed by its alignment; text on
    /// the line before the first anchor stays above the row, and the whitespace after the row (the room left for it) is
    /// dropped, so the text continues right under the pictures.
    /// </remarks>
    public static class HtmlDocuments
    {
        /// <summary>The document's files: pages first, then <c>style.css</c>, then the pictures by ID.</summary>
        public static IReadOnlyList<DocumentFile> Write(StyledDocument document, DecodeOptions? options = null,
            ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            return new Writer(document, options ?? DecodeOptions.Default, diagnostics ?? new List<Diagnostic>()).Files();
        }

        // The file of chapter k, or index.html for a SimpleText document's only chapter.
        private static string PageName(StyledDocument document, int chapter) =>
            document.Kind == DocumentKind.SimpleText ? "index.html" : $"chapter-{chapter.ToString("D2", CultureInfo.InvariantCulture)}.html";

        private sealed class Writer(StyledDocument document, DecodeOptions options, ICollection<Diagnostic> diagnostics)
        {
            private readonly List<string> styles = []; // CSS declarations, one class each (s0, s1, …)
            private readonly SortedDictionary<short, string?> images = []; // PICT ID → its file, or null when it cannot be drawn
            private readonly List<DocumentFile> imageFiles = [];
            private readonly HashSet<(int Chapter, int Paragraph)> targets = []; // paragraphs that links go to
            private int nextTarget; // the first paragraph of the chapter whose link anchor is not written yet

            public IReadOnlyList<DocumentFile> Files()
            {
                CollectTargets();
                var pages = new List<DocumentFile>();
                if (document.Kind == DocumentKind.DocMaker) pages.Add(Page("index.html", Contents()));
                for (var i = 0; i < document.Chapters.Count; i++) pages.Add(Page(PageName(document, document.Chapters[i].Number), Chapter(i)));
                pages.Add(Page("style.css", Css()));
                pages.AddRange(imageFiles.OrderBy(f => f.Path, StringComparer.Ordinal));
                return pages;
            }

            private static DocumentFile Page(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

            // Picture links to a paragraph, and contents entries, need an anchor at that paragraph.
            private void CollectTargets()
            {
                foreach (var chapter in document.Chapters)
                {
                    foreach (var picture in chapter.Pictures)
                    {
                        if (picture.Action is { Code: 1, Paragraph: >= 0 } a) targets.Add((a.Chapter, a.Paragraph));
                    }
                }
                foreach (var entry in document.Contents)
                {
                    if (document.Chapters.FirstOrDefault(c => c.Number == entry.Chapter) is { } chapter)
                        targets.Add((entry.Chapter, ParagraphOf(chapter.Text.Text, entry.SelectionStart)));
                }
            }

            private static int ParagraphOf(string text, int offset) =>
                text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\r');

            private string Contents()
            {
                var html = Head(document.Title, null);
                html.Append("<main class=\"contents\">\n<h1>").Append(Escape(document.Title)).Append("</h1>\n<ul>\n");
                foreach (var chapter in document.Chapters)
                {
                    var page = PageName(document, chapter.Number);
                    html.Append("<li><a href=\"").Append(page).Append("\">").Append(Escape(chapter.Title)).Append("</a>");
                    var entries = document.Contents.Where(e => e.Chapter == chapter.Number).ToList();
                    if (entries.Count > 0)
                    {
                        html.Append("\n<ul>\n");
                        foreach (var entry in entries)
                        {
                            html.Append(CultureInfo.InvariantCulture,
                                $"<li><a href=\"{page}#p{ParagraphOf(chapter.Text.Text, entry.SelectionStart)}\">{Escape(entry.Title)}</a></li>\n");
                        }
                        html.Append("</ul>\n");
                    }
                    html.Append("</li>\n");
                }
                html.Append("</ul>\n</main>\n</body>\n</html>\n");
                return html.ToString();
            }

            private string Chapter(int index)
            {
                var chapter = document.Chapters[index];
                var docMaker = document.Kind == DocumentKind.DocMaker;
                var html = Head(docMaker ? $"{document.Title}: {chapter.Title}" : document.Title, chapter.Background);
                var navigation = docMaker ? Navigation(index) : "";
                html.Append(navigation);
                html.Append("<main class=\"column\" style=\"");
                if (chapter.ColumnWidth > 0) html.Append(CultureInfo.InvariantCulture, $"width:{chapter.ColumnWidth}px;");
                html.Append("text-align:").Append(chapter.Justification switch
                {
                    Justification.Center => "center",
                    Justification.Right => "right",
                    _ => "left",
                }).Append("\">\n");
                Body(html, chapter);
                html.Append("</main>\n").Append(navigation).Append("</body>\n</html>\n");
                return html.ToString();
            }

            // Previous, contents and next, at the top and bottom of each chapter [ClassicMac].
            private string Navigation(int index)
            {
                var links = new List<string>();
                links.Add(index > 0 ? $"<a href=\"{PageName(document, document.Chapters[index - 1].Number)}\" rel=\"prev\">‹ {Escape(document.Chapters[index - 1].Title)}</a>" : "<span></span>");
                links.Add("<a href=\"index.html\">Contents</a>");
                links.Add(index + 1 < document.Chapters.Count ? $"<a href=\"{PageName(document, document.Chapters[index + 1].Number)}\" rel=\"next\">{Escape(document.Chapters[index + 1].Title)} ›</a>" : "<span></span>");
                return "<nav>" + string.Join("", links) + "</nav>\n";
            }

            private StringBuilder Head(string title, Rgb? background)
            {
                var html = new StringBuilder("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
                html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
                html.Append("<meta name=\"generator\" content=\"ClassicMac\">\n");
                html.Append("<title>").Append(Escape(title)).Append("</title>\n<link rel=\"stylesheet\" href=\"style.css\">\n</head>\n");
                html.Append(background is { } b ? $"<body style=\"background:{Colour(b.Red, b.Green, b.Blue)}\">\n" : "<body>\n");
                return html;
            }

            // The chapter's text with its pictures reflowed (see the class remarks).
            private void Body(StringBuilder html, DocumentChapter chapter)
            {
                var text = chapter.Text.Text;
                var pictures = chapter.Pictures.Where(p => p.Anchor >= 0 && p.Anchor < text.Length).OrderBy(p => p.Anchor).ToList();
                nextTarget = 0;
                var position = 0;
                for (var i = 0; i < pictures.Count;)
                {
                    // A row: the anchors on one line with only whitespace between them.
                    var row = new List<DocumentPicture> { pictures[i++] };
                    while (i < pictures.Count && Blank(text, row[^1].Anchor + 1, pictures[i].Anchor)) row.Add(pictures[i++]);

                    Paragraphs(html, chapter, position, row[0].Anchor, beforeAnchor: true);
                    Targets(html, chapter, ParagraphOf(text, row[0].Anchor));
                    Row(html, chapter, row);

                    // Drop the rest of the anchors' line when it is blank, and the blank lines after it; text after the
                    // anchors on their line continues without its leading spaces.
                    position = row[^1].Anchor + 1;
                    var next = i < pictures.Count ? pictures[i].Anchor : text.Length;
                    for (var first = true; position < next; first = false)
                    {
                        var lineEnd = text.IndexOf('\r', position, next - position);
                        var stop = lineEnd < 0 ? next : lineEnd;
                        if (!Blank(text, position, stop))
                        {
                            while (first && IsSpace(text[position])) position++;
                            break;
                        }
                        position = lineEnd < 0 ? next : lineEnd + 1;
                    }
                }
                Paragraphs(html, chapter, position, text.Length, beforeAnchor: false);
                Targets(html, chapter, int.MaxValue);
            }

            private static bool IsSpace(char c) => c is ' ' or '\t' or '\u00A0';

            // True when the characters from start to end are spaces, on one line.
            private static bool Blank(string text, int start, int end)
            {
                for (var i = start; i < end; i++)
                {
                    if (!IsSpace(text[i])) return false;
                }
                return true;
            }

            // The text from start to end as paragraphs, one per line (CR). Before an anchor, the part of the anchor's line
            // before it has no line break of its own: its trailing spaces are dropped, and the part too when it is blank.
            private void Paragraphs(StringBuilder html, DocumentChapter chapter, int start, int end, bool beforeAnchor)
            {
                var text = chapter.Text.Text;
                var number = ParagraphOf(text, start);
                for (var line = start; line < end; number++)
                {
                    var lineEnd = text.IndexOf('\r', line, end - line);
                    if (lineEnd < 0)
                    {
                        var contentEnd = end;
                        while (beforeAnchor && contentEnd > line && IsSpace(text[contentEnd - 1])) contentEnd--;
                        if (contentEnd == line) break;
                        Targets(html, chapter, number);
                        Paragraph(html, chapter, line, contentEnd);
                        break;
                    }
                    Targets(html, chapter, number);
                    Paragraph(html, chapter, line, lineEnd);
                    line = lineEnd + 1;
                }
            }

            // The link anchors of the paragraphs up to this one, including those dropped with the whitespace.
            private void Targets(StringBuilder html, DocumentChapter chapter, int upTo)
            {
                var due = targets.Where(t => t.Chapter == chapter.Number && t.Paragraph >= nextTarget && t.Paragraph <= upTo).Select(t => t.Paragraph);
                foreach (var paragraph in due.Order()) html.Append(CultureInfo.InvariantCulture, $"<a id=\"p{paragraph}\"></a>\n");
                nextTarget = upTo == int.MaxValue ? upTo : Math.Max(nextTarget, upTo + 1);
            }

            // One line: a paragraph styled by its first run (its line height), its text in spans per run; an empty line
            // keeps its height.
            private void Paragraph(StringBuilder html, DocumentChapter chapter, int start, int end)
            {
                var runs = chapter.Text.Runs;
                var first = runs.FirstOrDefault(r => r.Start <= start && start < r.Start + r.Length) ?? runs.LastOrDefault();
                html.Append("<p").Append(first is null ? "" : $" class=\"{Style(first)}\"").Append('>');
                if (start == end)
                {
                    html.Append("<br>");
                }
                else
                {
                    foreach (var run in runs)
                    {
                        var from = Math.Max(start, run.Start);
                        var to = Math.Min(end, run.Start + run.Length);
                        if (from >= to) continue;
                        var style = Style(run);
                        var content = Escape(chapter.Text.Text[from..to]);
                        html.Append(run == first ? content : $"<span class=\"{style}\">{content}</span>");
                    }
                }
                html.Append("</p>\n");
            }

            // Pictures side by side: left, centre and right, each in its place across the column.
            private void Row(StringBuilder html, DocumentChapter chapter, List<DocumentPicture> row)
            {
                html.Append("<div class=\"row\">");
                foreach (var (alignment, name) in new[] { (PictureAlignment.Left, "l"), (PictureAlignment.Center, "c"), (PictureAlignment.Right, "r") })
                {
                    var placed = row.Where(p => p.Alignment == alignment).ToList();
                    if (placed.Count == 0) continue;
                    html.Append("<div class=\"").Append(name).Append("\">");
                    foreach (var picture in placed) html.Append(Picture(chapter, picture));
                    html.Append("</div>");
                }
                html.Append("</div>\n");
            }

            private string Picture(DocumentChapter chapter, DocumentPicture picture)
            {
                // DOCMaker scales a picture wider than the column down to it, unless the pInf says not to.
                int width = picture.Width, height = picture.Height;
                if (!picture.NoScale && chapter.ColumnWidth > 0 && width > chapter.ColumnWidth)
                {
                    height = (int)Math.Round((double)height * chapter.ColumnWidth / width);
                    width = chapter.ColumnWidth;
                }
                var size = string.Create(CultureInfo.InvariantCulture, $"width=\"{width}\" height=\"{height}\"");
                var file = Image(chapter, picture);
                var alt = $"PICT {picture.PictureId}";
                var element = file is null
                    ? $"<span class=\"missing\" style=\"width:{width}px;height:{height}px\" title=\"{alt} (not drawn)\"></span>"
                    : $"<img src=\"{file}\" {size} alt=\"{alt}\">";
                var (href, title) = Link(chapter, picture.Action);
                if (href is not null) return $"<a href=\"{Escape(href)}\"{(title is null ? "" : $" title=\"{Escape(title)}\"")}>{element}</a>";
                return title is null ? element : $"<span title=\"{Escape(title)}\">{element}</span>";
            }

            // Where a picture's action goes, and a note for actions a web page cannot do.
            private (string? Href, string? Title) Link(DocumentChapter chapter, PictureAction action)
            {
                var index = document.Chapters.ToList().FindIndex(c => c.Number == chapter.Number);
                string? Chapter(int number, string suffix = "") =>
                    document.Chapters.Any(c => c.Number == number) ? PageName(document, number) + suffix : null;
                return action.Code switch
                {
                    1 => (Chapter(action.Chapter, action.Paragraph >= 0 ? $"#p{action.Paragraph}" : ""), null),
                    10 => ("javascript:history.back()", null),
                    11 => ("index.html", null),
                    14 => (index + 1 < document.Chapters.Count ? PageName(document, document.Chapters[index + 1].Number) : null, null),
                    15 => (index > 0 ? PageName(document, document.Chapters[index - 1].Number) : null, null),
                    3 => ("javascript:print()", null),
                    8 => (null, action.Text),
                    5 => (null, $"Opens {action.Text}"),
                    7 => (null, $"Plays the movie {action.Text}"),
                    13 or 16 => (null, "Runs a script"),
                    _ => (null, null),
                };
            }

            // The picture's file, drawn once per PICT ID; null when it is missing or cannot be drawn.
            private string? Image(DocumentChapter chapter, DocumentPicture picture)
            {
                if (images.TryGetValue(picture.PictureId, out var known)) return known;
                string? file = null;
                if (picture.Picture is { } data)
                {
                    try
                    {
                        if ((long)picture.Width * picture.Height > options.MaxImagePixels) throw new InvalidDataException("Its frame is over the pixel limit.");
                        var bitmap = PictureDecoder.Draw(data.ToArray(), options);
                        var id = picture.PictureId < 0 ? $"m{-picture.PictureId}" : picture.PictureId.ToString(CultureInfo.InvariantCulture);
                        file = $"images/pict-{id}{options.ImageEncoder.Extension}";
                        imageFiles.Add(new DocumentFile(file, options.ImageEncoder.Encode(bitmap.Width, bitmap.Height, bitmap.Pixels)));
                    }
                    catch (Exception e) when (e is NotSupportedException or EndOfStreamException or ArgumentException
                        or OverflowException or InvalidDataException or IndexOutOfRangeException)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "document.undrawable-picture",
                            $"Chapter {chapter.Number}'s PICT {picture.PictureId} cannot be drawn: {e.Message}"));
                    }
                }
                images[picture.PictureId] = file;
                return file;
            }

            private string Style(TextRun run)
            {
                var css = new StringBuilder("font-family:").Append(FontFamily(run.FontName));
                css.Append(CultureInfo.InvariantCulture, $";font-size:{run.Size}px");
                if (run.LineHeight > 0) css.Append(CultureInfo.InvariantCulture, $";line-height:{run.LineHeight}px");
                if (run.Bold) css.Append(";font-weight:bold");
                if (run.Italic) css.Append(";font-style:italic");
                if (run.Underline) css.Append(";text-decoration:underline");
                if (run.Outline || run.Shadow) css.Append(";-webkit-text-stroke:1px currentColor;-webkit-text-fill-color:transparent");
                if (run.Shadow) css.Append(";text-shadow:1px 1px 0 currentColor");
                if (run.Condense) css.Append(";letter-spacing:-1px");
                if (run.Extend) css.Append(";letter-spacing:1px");
                if ((run.Red, run.Green, run.Blue) != (0, 0, 0)) css.Append(";color:").Append(Colour(run.Red, run.Green, run.Blue));
                var declaration = css.ToString();
                var index = styles.IndexOf(declaration);
                if (index < 0)
                {
                    index = styles.Count;
                    styles.Add(declaration);
                }
                return "s" + index.ToString(CultureInfo.InvariantCulture);
            }

            private string Css()
            {
                var css = new StringBuilder();
                css.Append("body{margin:0;padding:16px;background:#fff;color:#000}\n");
                css.Append("nav{display:flex;justify-content:space-between;gap:16px;max-width:640px;margin:0 auto 16px;font:13px Geneva,Verdana,sans-serif}\n");
                css.Append("nav:last-child{margin:16px auto 0}\n");
                css.Append(".contents{max-width:640px;margin:0 auto;font:14px Geneva,Verdana,sans-serif}\n");
                css.Append(".column{max-width:100%;margin:0 auto;white-space:pre-wrap;overflow-wrap:break-word}\n");
                css.Append(".column p{margin:0}\n");
                css.Append(".row{display:grid;grid-template-columns:1fr auto 1fr;align-items:start;white-space:normal}\n");
                css.Append(".row .l{grid-column:1;justify-self:start}\n.row .c{grid-column:2}\n.row .r{grid-column:3;justify-self:end}\n");
                css.Append(".row img,.row .missing{display:inline-block;vertical-align:top}\n");
                css.Append(".missing{box-sizing:border-box;border:1px dashed #888}\n");
                for (var i = 0; i < styles.Count; i++) css.Append(CultureInfo.InvariantCulture, $".s{i}{{{styles[i]}}}\n");
                return css.ToString();
            }
        }

        // A Mac font's CSS family: its name first, then what looks like it on other systems.
        internal static string FontFamily(string name) => name switch
        {
            "Chicago" or "Charcoal" => "Chicago,Charcoal,system-ui,sans-serif",
            "Geneva" => "Geneva,Verdana,sans-serif",
            "New York" => "\"New York\",\"Times New Roman\",serif",
            "Monaco" => "Monaco,Consolas,monospace",
            "Times" => "Times,\"Times New Roman\",serif",
            "Helvetica" => "Helvetica,Arial,sans-serif",
            "Helvetica Narrow" => "\"Helvetica Narrow\",\"Arial Narrow\",sans-serif",
            "Courier" => "Courier,\"Courier New\",monospace",
            "Palatino" => "Palatino,\"Palatino Linotype\",\"Book Antiqua\",serif",
            "Bookman" => "Bookman,\"Bookman Old Style\",serif",
            "Avant Garde" => "\"Avant Garde\",\"Century Gothic\",sans-serif",
            "New Century Schoolbook" => "\"New Century Schoolbook\",\"Century Schoolbook\",serif",
            "Zapf Chancery" => "\"Zapf Chancery\",\"Monotype Corsiva\",cursive",
            _ => $"\"{name.Replace("\"", "", StringComparison.Ordinal)}\",Geneva,Verdana,sans-serif",
        };

        private static string Colour(byte red, byte green, byte blue) => $"#{red:x2}{green:x2}{blue:x2}";

        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                escaped.Append(c switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    '\r' or '\n' => "\n",
                    < ' ' and not '\t' => "",
                    _ => c.ToString(),
                });
            }
            return escaped.ToString();
        }
    }
}
