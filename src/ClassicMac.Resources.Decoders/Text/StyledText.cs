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
            var ordered = styles.Where(r => r.Start >= 0).OrderBy(r => r.Start).ToList();
            if (ordered.Count == 0 || ordered[0].Start > 0) ordered.Insert(0, new StyleRun(0, 0, 0, 3, 0, 12, 0, 0, 0));
            var runs = new List<TextRun>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                var start = Math.Min(s.Start, decoded.Length);
                var end = i + 1 < ordered.Count ? Math.Min(ordered[i + 1].Start, decoded.Length) : decoded.Length;
                if (end <= start) continue;
                runs.Add(new TextRun(start, end - start, s.Font, StyleRuns.FontName(s.Font), s.Size > 0 ? s.Size : 12, s.Face,
                    (byte)(s.Red >> 8), (byte)(s.Green >> 8), (byte)(s.Blue >> 8)));
            }
            return new StyledText(decoded, runs, complete);
        }
    }
}
