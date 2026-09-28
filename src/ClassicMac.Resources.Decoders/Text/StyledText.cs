using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Resources.Decoders.Text
{
    /// <summary>One stretch of styled text: its characters and TextEdit's style for them.</summary>
    /// <param name="Start">The first character.</param>
    /// <param name="Length">How many characters.</param>
    /// <param name="FontId">The font family ID.</param>
    /// <param name="FontName">Its name, for the standard IDs of <i>Inside Macintosh: Text</i>; "Font n" otherwise.</param>
    /// <param name="Size">Point size (12 when the style says 0, the default).</param>
    /// <param name="Face">QuickDraw style bits: bold 1, italic 2, underline 4, outline 8, shadow $10, condense $20, extend $40.</param>
    /// <param name="Red">Red, 0–255.</param>
    /// <param name="Green">Green, 0–255.</param>
    /// <param name="Blue">Blue, 0–255.</param>
    public sealed record TextRun(int Start, int Length, short FontId, string FontName, int Size, byte Face, byte Red, byte Green, byte Blue)
    {
        /// <summary>Bold.</summary>
        public bool Bold => (Face & 0x01) != 0;

        /// <summary>Italic.</summary>
        public bool Italic => (Face & 0x02) != 0;

        /// <summary>Underlined.</summary>
        public bool Underline => (Face & 0x04) != 0;

        /// <summary>Outlined.</summary>
        public bool Outline => (Face & 0x08) != 0;

        /// <summary>Shadowed.</summary>
        public bool Shadow => (Face & 0x10) != 0;

        /// <summary>Condensed.</summary>
        public bool Condense => (Face & 0x20) != 0;

        /// <summary>Extended.</summary>
        public bool Extend => (Face & 0x40) != 0;
    }

    /// <summary>
    /// Text with TextEdit styles (<c>'TEXT'</c> with its <c>'styl'</c>, as SimpleText and TextEdit keep them): the text in
    /// Unicode and runs covering all of it, in order. Line breaks stay as stored (CR).
    /// </summary>
    /// <param name="Text">The text.</param>
    /// <param name="Runs">The styled runs, covering the text from start to end.</param>
    /// <param name="Complete">False when the style data ended before its last run.</param>
    public sealed record StyledText(string Text, IReadOnlyList<TextRun> Runs, bool Complete)
    {
        /// <summary>
        /// Reads <paramref name="text"/> in the options' encoding and styles it with <paramref name="styl"/> (a
        /// <c>StScrpRec</c>); with no style data, one plain run in Geneva 12.
        /// </summary>
        public static StyledText Read(ReadOnlySpan<byte> text, ReadOnlySpan<byte> styl, DecodeOptions? options = null)
        {
            var decoded = MacText.Decode(text, options ?? DecodeOptions.Default);
            var complete = true;
            var styles = styl.IsEmpty ? [] : StyleRuns.Read(styl, out complete);
            if (styles.Count == 0) styles = [new StyleRun(0, 0, 0, 3, 0, 12, 0, 0, 0)];

            // As TEUseStyleScrap applies a style scrap (disassembly; SimpleText, the Help Manager and DOCMaker all use
            // it): in stored order from the start of the text, each run up to where the next run's start says (a run's
            // own start is never read), in either direction when they are out of order, a later run overwriting an
            // earlier one. Starts compare unsigned (a negative one is past the end); once a run reaches the end of the
            // text, the rest are ignored.
            var styleOf = new int[decoded.Length];
            var length = (uint)decoded.Length;
            uint position = 0;
            for (var i = 0; i < styles.Count && position < length; i++)
            {
                var end = i + 1 < styles.Count ? Math.Min((uint)styles[i + 1].Start, length) : length;
                var low = (int)Math.Min(position, end);
                var high = (int)Math.Max(position, end);
                styleOf.AsSpan(low, high - low).Fill(i);
                position = end;
            }
            var runs = new List<TextRun>();
            for (var start = 0; start < decoded.Length;)
            {
                var end = start + 1;
                while (end < decoded.Length && styleOf[end] == styleOf[start]) end++;
                var s = styles[styleOf[start]];
                runs.Add(new TextRun(start, end - start, s.Font, StyleRuns.FontName(s.Font), s.Size > 0 ? s.Size : 12, s.Face,
                    (byte)(s.Red >> 8), (byte)(s.Green >> 8), (byte)(s.Blue >> 8)));
                start = end;
            }
            return new StyledText(decoded, runs, complete);
        }
    }
}
