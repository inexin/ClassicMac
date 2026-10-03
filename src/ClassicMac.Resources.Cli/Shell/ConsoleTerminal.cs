using System;

namespace ClassicMac.Resources.Cli.Shell
{
    /// <summary>The process's console as the line editor's terminal: keys read without echo, the line redrawn in place.</summary>
    internal sealed class ConsoleTerminal : IShellTerminal
    {
        private bool fresh = true;
        private int startLeft;
        private int startTop;
        private int shown;
        private int endLeft;
        private int endTop;

        public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

        public void Show(string prompt, string line, int cursor)
        {
            if (fresh)
            {
                (startLeft, startTop) = (Console.CursorLeft, Console.CursorTop);
                fresh = false;
            }
            else
            {
                Console.SetCursorPosition(startLeft, startTop);
            }

            var text = prompt + line;
            Console.Write(text.PadRight(shown));
            shown = text.Length;

            // The console scrolls when the line runs past its last row: the start moves up as many rows.
            var width = Math.Max(1, Console.BufferWidth);
            var lastRow = startTop + ((startLeft + Math.Max(text.Length, shown)) / width);
            if (lastRow >= Console.BufferHeight)
            {
                startTop -= lastRow - Console.BufferHeight + 1;
            }

            var end = startLeft + text.Length;
            (endLeft, endTop) = (end % width, Math.Max(0, startTop + (end / width)));
            var at = startLeft + prompt.Length + cursor;
            Console.SetCursorPosition(at % width, Math.Max(0, startTop + (at / width)));
        }

        public void EndLine()
        {
            if (!fresh)
            {
                Console.SetCursorPosition(endLeft, Math.Min(endTop, Console.BufferHeight - 1));
                Console.WriteLine();
            }

            fresh = true;
            shown = 0;
        }

        public void WriteLine(string text) => Console.WriteLine(text);
    }
}
