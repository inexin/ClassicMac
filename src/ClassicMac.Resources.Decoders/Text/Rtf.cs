using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassicMac.Resources.Decoders.Text;

// Styled text as RTF 1.x: a font table from the runs' font IDs, a colour table from their colours, and per run the
// font, size (half-points), colour and QuickDraw style (bold, italic, underline, outline, shadow; condense and
// extend as character spacing). Text is escaped, non-ASCII as \uN? (Unicode, with '?' for readers without it), and
// the Mac's CR becomes \par.
internal static class Rtf
{
    public static string Write(StyledText styled)
    {
        var runs = styled.Runs;
        var fonts = runs.Select(r => r.FontId).Distinct().ToList();
        var colours = runs.Select(r => (r.Red, r.Green, r.Blue)).Distinct().ToList();

        var rtf = new StringBuilder("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1\n{\\fonttbl");
        for (var i = 0; i < fonts.Count; i++)
        {
            rtf.Append(CultureInfo.InvariantCulture, $"{{\\f{i} {Escape(StyleRuns.FontName(fonts[i]))};}}");
        }

        rtf.Append("}\n{\\colortbl;");
        foreach (var (r, g, b) in colours)
        {
            rtf.Append(CultureInfo.InvariantCulture, $"\\red{r}\\green{g}\\blue{b};");
        }

        rtf.Append("}\n");

        foreach (var run in runs)
        {
            rtf.Append(CultureInfo.InvariantCulture, $"\\plain\\f{fonts.IndexOf(run.FontId)}\\fs{run.Size * 2}\\cf{colours.IndexOf((run.Red, run.Green, run.Blue)) + 1}");
            if (run.Bold)
            {
                rtf.Append("\\b");
            }

            if (run.Italic)
            {
                rtf.Append("\\i");
            }

            if (run.Underline)
            {
                rtf.Append("\\ul");
            }

            if (run.Outline)
            {
                rtf.Append("\\outl");
            }

            if (run.Shadow)
            {
                rtf.Append("\\shad");
            }

            if (run.Condense)
            {
                rtf.Append("\\expnd-2\\expndtw-10");
            }

            if (run.Extend)
            {
                rtf.Append("\\expnd2\\expndtw10");
            }

            rtf.Append(' ');
            rtf.Append(Escape(styled.Text.Substring(run.Start, run.Length)));
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
