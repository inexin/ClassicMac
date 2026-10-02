using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>A piece of a chapter laid out with its pictures reflowed: a line of text or a row of pictures.</summary>
    /// <param name="Paragraph">The paragraph (from 0, counted by CR) it belongs to.</param>
    public abstract record DocumentBlock(int Paragraph);

    /// <summary>One line of text: the characters from <paramref name="Start"/> up to <paramref name="End"/>, without its CR.</summary>
    /// <param name="Start">The first character.</param>
    /// <param name="End">The character after the last.</param>
    /// <param name="Paragraph">The paragraph.</param>
    public sealed record DocumentLine(int Start, int End, int Paragraph) : DocumentBlock(Paragraph);

    /// <summary>Pictures side by side, each placed by its alignment across the column.</summary>
    /// <param name="Pictures">The pictures, in anchor order.</param>
    /// <param name="Paragraph">The paragraph of their anchors.</param>
    public sealed record PictureRow(IReadOnlyList<DocumentPicture> Pictures, int Paragraph) : DocumentBlock(Paragraph);

    /// <summary>
    /// A chapter's pictures reflowed into its text [ClassicMac], as the HTML output and the viewer show it (the Mac
    /// draws them over the text instead; see <c>docs/formats/output/html.md</c> §3.2): anchors on one line with only
    /// spaces between them make a row; the text on the line before the first anchor stays above it, without its
    /// trailing spaces (or not at all when blank); the rest of the anchors' line when blank, and the blank lines after
    /// it, are dropped, and text after the anchors on their line continues without its leading spaces.
    /// </summary>
    public static class DocumentFlow
    {
        /// <summary>The chapter's lines and picture rows, in order.</summary>
        public static IReadOnlyList<DocumentBlock> Blocks(DocumentChapter chapter)
        {
            ArgumentNullException.ThrowIfNull(chapter);
            var text = chapter.Text.Text;
            var pictures = chapter.Pictures.Where(p => p.Anchor >= 0 && p.Anchor < text.Length).OrderBy(p => p.Anchor).ToList();
            var blocks = new List<DocumentBlock>();
            var position = 0;
            for (var i = 0; i < pictures.Count;)
            {
                var row = new List<DocumentPicture> { pictures[i++] };
                while (i < pictures.Count && Blank(text, row[^1].Anchor + 1, pictures[i].Anchor))
                {
                    row.Add(pictures[i++]);
                }

                Lines(blocks, text, position, row[0].Anchor, beforeAnchor: true);
                blocks.Add(new PictureRow(row, ParagraphOf(text, row[0].Anchor)));

                position = row[^1].Anchor + 1;
                var next = i < pictures.Count ? pictures[i].Anchor : text.Length;
                for (var first = true; position < next; first = false)
                {
                    var lineEnd = text.IndexOf('\r', position, next - position);
                    var stop = lineEnd < 0 ? next : lineEnd;
                    if (!Blank(text, position, stop))
                    {
                        while (first && IsSpace(text[position]))
                        {
                            position++;
                        }

                        break;
                    }
                    position = lineEnd < 0 ? next : lineEnd + 1;
                }
            }
            Lines(blocks, text, position, text.Length, beforeAnchor: false);
            return blocks;
        }

        /// <summary>The paragraph (from 0) that character <paramref name="offset"/> is in.</summary>
        public static int ParagraphOf(string text, int offset)
        {
            ArgumentNullException.ThrowIfNull(text);
            return text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\r');
        }

        private static bool IsSpace(char c) => c is ' ' or '\t' or ' ';

        // True when the characters from start to end are spaces, on one line.
        private static bool Blank(string text, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (!IsSpace(text[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // The text from start to end as lines (up to each CR). Before an anchor, the part of the anchor's line before it
        // has no line break of its own: its trailing spaces are dropped, and the part too when it is blank.
        private static void Lines(List<DocumentBlock> blocks, string text, int start, int end, bool beforeAnchor)
        {
            var number = ParagraphOf(text, start);
            for (var line = start; line < end; number++)
            {
                var lineEnd = text.IndexOf('\r', line, end - line);
                if (lineEnd < 0)
                {
                    var contentEnd = end;
                    while (beforeAnchor && contentEnd > line && IsSpace(text[contentEnd - 1]))
                    {
                        contentEnd--;
                    }

                    if (contentEnd > line)
                    {
                        blocks.Add(new DocumentLine(line, contentEnd, number));
                    }

                    break;
                }
                blocks.Add(new DocumentLine(line, lineEnd, number));
                line = lineEnd + 1;
            }
        }
    }
}
