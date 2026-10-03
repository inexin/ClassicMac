using System;
using System.Collections.Generic;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Documents
{
    /// <summary>
    /// A document of styled TextEdit text with pictures anchored in it: a DOCMaker stand-alone document (chapters) or a
    /// SimpleText document (one chapter).
    /// </summary>
    /// <param name="Kind">Which program's document it is.</param>
    /// <param name="Title">The document's title (the file's name).</param>
    /// <param name="Chapters">The chapters, in order.</param>
    /// <param name="Contents">The table of contents' entries below the chapter titles.</param>
    public sealed record StyledDocument(DocumentKind Kind, string Title, IReadOnlyList<DocumentChapter> Chapters,
        IReadOnlyList<ContentsEntry> Contents);

    /// <summary>The program a document is for.</summary>
    public enum DocumentKind
    {
        /// <summary>A DOCMaker stand-alone document.</summary>
        DocMaker,

        /// <summary>A SimpleText (or TeachText) document.</summary>
        SimpleText,

        /// <summary>A Microsoft Word document.</summary>
        Word,
    }

    /// <summary>One chapter: styled text and the pictures drawn over it.</summary>
    /// <param name="Number">The chapter number, from 1.</param>
    /// <param name="Title">The chapter's title.</param>
    /// <param name="Text">The text and its style runs. Option-space characters (U+00A0) anchor the pictures.</param>
    /// <param name="Justification">How every line of the chapter is aligned.</param>
    /// <param name="Background">The chapter's background colour, or null for white.</param>
    /// <param name="Pictures">The pictures, in anchor order.</param>
    /// <param name="ColumnWidth">The text column's width in pixels, or 0 when the document does not fix one.</param>
    public sealed record DocumentChapter(int Number, string Title, StyledText Text, Justification Justification, Rgb? Background,
        IReadOnlyList<DocumentPicture> Pictures, int ColumnWidth)
    {
        /// <summary>
        /// The paragraphs' own formats, by their first character, in order; empty when every line takes
        /// <see cref="Justification"/> (TextEdit documents).
        /// </summary>
        public IReadOnlyList<ParagraphFormat> Paragraphs { get; init; } = [];

        /// <summary>The format of the paragraph that character <paramref name="offset"/> is in, or null when there is none.</summary>
        public ParagraphFormat? ParagraphAt(int offset)
        {
            var (low, high) = (0, Paragraphs.Count - 1);
            ParagraphFormat? found = null;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (Paragraphs[middle].Start <= offset)
                {
                    found = Paragraphs[middle];
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }
    }

    /// <summary>A paragraph's format, from its first character to the next paragraph's; lengths in points.</summary>
    /// <param name="Start">The paragraph's first character.</param>
    /// <param name="Justification">How its lines are aligned.</param>
    /// <param name="LeftIndent">The indent from the left edge of the column.</param>
    /// <param name="RightIndent">The indent from the right edge.</param>
    /// <param name="FirstLineIndent">The first line's indent from <paramref name="LeftIndent"/> (negative for a hanging indent).</param>
    /// <param name="SpaceBefore">The space above the paragraph.</param>
    /// <param name="SpaceAfter">The space below it.</param>
    public sealed record ParagraphFormat(int Start, Justification Justification, double LeftIndent = 0, double RightIndent = 0,
        double FirstLineIndent = 0, double SpaceBefore = 0, double SpaceAfter = 0);

    /// <summary>A colour, 0–255 per component.</summary>
    public readonly record struct Rgb(byte Red, byte Green, byte Blue);

    /// <summary>TextEdit's line justification.</summary>
    public enum Justification
    {
        /// <summary>Left.</summary>
        Left,

        /// <summary>Centred.</summary>
        Center,

        /// <summary>Right.</summary>
        Right,

        /// <summary>Justified to both edges (a Word paragraph).</summary>
        Full,
    }

    /// <summary>
    /// A picture drawn over the text: its top at the top of its anchor's line, placed by its alignment within the text
    /// column, the text not wrapping around it.
    /// </summary>
    /// <param name="Anchor">The option-space character it is anchored at.</param>
    /// <param name="PictureId">Its <c>PICT</c> resource ID.</param>
    /// <param name="Picture">The <c>PICT</c> data, or null when the resource is missing.</param>
    /// <param name="Width">Its frame's width in pixels (0 when missing).</param>
    /// <param name="Height">Its frame's height in pixels (0 when missing).</param>
    /// <param name="Alignment">Where it sits in the column.</param>
    /// <param name="NoScale">When false, a picture wider than the column is scaled down to fit it.</param>
    /// <param name="Action">What clicking it does.</param>
    public sealed record DocumentPicture(int Anchor, short PictureId, ReadOnlyMemory<byte>? Picture, int Width, int Height,
        PictureAlignment Alignment, bool NoScale, PictureAction Action);

    /// <summary>Where a picture sits in the text column.</summary>
    public enum PictureAlignment
    {
        /// <summary>Centred.</summary>
        Center,

        /// <summary>At the left edge.</summary>
        Left,

        /// <summary>At the right edge.</summary>
        Right,
    }

    /// <summary>What clicking a DOCMaker picture does.</summary>
    /// <param name="Code">DOCMaker's action code: 0 none, 1 go to chapter, 2 About, 3 Print, 4 Quit, 5 launch a file, 7 play a
    /// movie, 8 show a note, 10 back, 11 contents, 12 Find, 13 send an Apple event, 14 next chapter, 15 previous chapter,
    /// 16 run a script; 6 and 9 need code the stand-alone reader leaves out.</param>
    /// <param name="Highlights">True when the picture inverts while it is clicked; false for a button with no feedback (a
    /// negative code in the file).</param>
    /// <param name="Chapter">Action 1's chapter.</param>
    /// <param name="Paragraph">Action 1's paragraph (from 0), or −1 for the chapter's start.</param>
    /// <param name="Text">The file path, movie, note or script text of actions 5, 7, 8, 13 and 16.</param>
    public sealed record PictureAction(int Code, bool Highlights, int Chapter = 0, int Paragraph = -1, string? Text = null)
    {
        /// <summary>No action.</summary>
        public static PictureAction None { get; } = new(0, true);
    }

    /// <summary>A table-of-contents entry below a chapter title: a selection in the chapter.</summary>
    /// <param name="Chapter">The chapter.</param>
    /// <param name="SelectionStart">The first character selected.</param>
    /// <param name="SelectionEnd">The character after the selection.</param>
    /// <param name="Title">The entry's text.</param>
    public sealed record ContentsEntry(int Chapter, int SelectionStart, int SelectionEnd, string Title);
}
