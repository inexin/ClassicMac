using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassicMac.Resources.Decoders.Text
{
    // Styled text as RTF 1.x: a font table from the runs' font IDs, a colour table from their colours, and per run the
    // font, size (half-points), colour and QuickDraw style (bold, italic, underline, outline, shadow; condense and
    // extend as character spacing). Text is escaped, non-ASCII as \uN? (Unicode, with '?' for readers without it), and
    // the Mac's CR becomes \par.
    internal static class Rtf
    {
        public static string Write(string text, IReadOnlyList<StyleRun> runs)
        {
            var fonts = runs.Select(r => r.Font).Distinct().ToList();
            if (fonts.Count == 0) fonts.Add(0);
            var colours = runs.Select(r => (r.Red, r.Green, r.Blue)).Distinct().ToList();

            var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1\n{\\fonttbl");
            for (var i = 0; i < fonts.Count; i++) rtf.Append(CultureInfo.InvariantCulture, $"{{\\f{i} {Escape(StyleRuns.FontName(fonts[i]))};}}");
            rtf.Append("}\n{\\colortbl;");
            foreach (var (r, g, b) in colours) rtf.Append(CultureInfo.InvariantCulture, $"\\red{r >> 8}\\green{g >> 8}\\blue{b >> 8};");
            rtf.Append("}\n");

            var ordered = runs.Where(r => r.Start >= 0).OrderBy(r => r.Start).ToList();
            if (ordered.Count == 0 || ordered[0].Start > 0) ordered.Insert(0, new StyleRun(0, 0, 0, fonts[0], 0, 12, 0, 0, 0));
            for (var i = 0; i < ordered.Count; i++)
            {
                var run = ordered[i];
                var start = Math.Min(run.Start, text.Length);
                var end = i + 1 < ordered.Count ? Math.Min(ordered[i + 1].Start, text.Length) : text.Length;
                if (end <= start) continue;
                var size = run.Size > 0 ? run.Size : 12;
                rtf.Append(CultureInfo.InvariantCulture, $"\\plain\\f{fonts.IndexOf(run.Font)}\\fs{size * 2}");
                var colour = colours.IndexOf((run.Red, run.Green, run.Blue));
                if (colour >= 0) rtf.Append(CultureInfo.InvariantCulture, $"\\cf{colour + 1}");
                if ((run.Face & 0x01) != 0) rtf.Append("\\b");
                if ((run.Face & 0x02) != 0) rtf.Append("\\i");
                if ((run.Face & 0x04) != 0) rtf.Append("\\ul");
                if ((run.Face & 0x08) != 0) rtf.Append("\\outl");
                if ((run.Face & 0x10) != 0) rtf.Append("\\shad");
                if ((run.Face & 0x20) != 0) rtf.Append("\\expnd-2\\expndtw-10");
                if ((run.Face & 0x40) != 0) rtf.Append("\\expnd2\\expndtw10");
                rtf.Append(' ');
                rtf.Append(Escape(text[start..end]));
            }
            rtf.Append("}\n");
            return rtf.ToString();
        }

        private static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\' or '{' or '}':
                        escaped.Append('\\').Append(c);
                        break;
                    case '\r' or '\n':
                        escaped.Append("\\par\n");
                        break;
                    case '\t':
                        escaped.Append("\\tab ");
                        break;
                    case < ' ':
                        break; // other control characters have no RTF meaning
                    case > '\u007E':
                        escaped.Append(CultureInfo.InvariantCulture, $"\\u{(short)c}?");
                        break;
                    default:
                        escaped.Append(c);
                        break;
                }
            }
            return escaped.ToString();
        }
    }
}
