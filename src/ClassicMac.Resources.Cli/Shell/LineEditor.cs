using System;
using System.Collections.Generic;

namespace ClassicMac.Resources.Cli.Shell
{
    /// <summary>
    /// Line editing at a terminal (docs/cli.md §5.3): typing and deleting anywhere in the line, Left/Right, Home/End,
    /// Escape to clear, Up/Down through the lines typed before in the session, Tab to complete; Enter ends the line,
    /// Ctrl+D (or Ctrl+Z) on an empty line ends the input.
    /// </summary>
    internal sealed class LineEditor(IShellTerminal terminal)
    {
        private readonly List<string> history = [];

        /// <summary>The lines typed so far, oldest first (commands only, not answers to questions).</summary>
        public IReadOnlyList<string> History => history;

        /// <summary>Reads a line; null when the input ends.</summary>
        public string? ReadLine(string prompt, Func<string, int, Completion>? complete)
        {
            var line = "";
            var cursor = 0;
            var recalled = history.Count;
            var draft = "";
            terminal.Show(prompt, line, cursor);
            while (true)
            {
                var key = terminal.ReadKey();
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        terminal.EndLine();
                        if (complete is not null && line.Trim().Length > 0 && (history.Count == 0 || history[^1] != line))
                        {
                            history.Add(line);
                        }

                        return line;
                    case ConsoleKey.D or ConsoleKey.Z when control:
                        if (line.Length == 0)
                        {
                            terminal.EndLine();
                            return null;
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            line = line.Remove(--cursor, 1);
                        }

                        break;
                    case ConsoleKey.Delete:
                        if (cursor < line.Length)
                        {
                            line = line.Remove(cursor, 1);
                        }

                        break;
                    case ConsoleKey.LeftArrow:
                        cursor = Math.Max(0, cursor - 1);
                        break;
                    case ConsoleKey.RightArrow:
                        cursor = Math.Min(line.Length, cursor + 1);
                        break;
                    case ConsoleKey.Home:
                        cursor = 0;
                        break;
                    case ConsoleKey.End:
                        cursor = line.Length;
                        break;
                    case ConsoleKey.Escape:
                        (line, cursor) = ("", 0);
                        break;
                    case ConsoleKey.UpArrow when complete is not null:
                        if (recalled > 0)
                        {
                            if (recalled == history.Count)
                            {
                                draft = line;
                            }

                            line = history[--recalled];
                            cursor = line.Length;
                        }

                        break;
                    case ConsoleKey.DownArrow when complete is not null:
                        if (recalled < history.Count)
                        {
                            recalled++;
                            line = recalled == history.Count ? draft : history[recalled];
                            cursor = line.Length;
                        }

                        break;
                    case ConsoleKey.Tab when complete is not null:
                        var completion = complete(line, cursor);
                        if (completion.Candidates.Count > 1 && completion.Line == line)
                        {
                            terminal.EndLine();
                            terminal.WriteLine(string.Join("  ", completion.Candidates));
                        }

                        (line, cursor) = (completion.Line, completion.Cursor);
                        break;
                    default:
                        if (key.KeyChar >= ' ' && !control)
                        {
                            line = line.Insert(cursor++, key.KeyChar.ToString());
                        }

                        break;
                }

                terminal.Show(prompt, line, cursor);
            }
        }
    }
}
