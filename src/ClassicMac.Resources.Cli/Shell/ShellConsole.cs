using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Resources.Cli.Shell
{
    /// <summary>A line after tab completion: the new text, where the cursor goes, and the names to show when several fit.</summary>
    internal sealed record Completion(string Line, int Cursor, IReadOnlyList<string> Candidates);

    /// <summary>The shell's input and output, so tests drive it without a terminal.</summary>
    internal interface IShellConsole
    {
        /// <summary>Whether a person is typing: prompts are shown and leaving with unsaved changes asks what to do.</summary>
        bool Interactive { get; }

        /// <summary>Results.</summary>
        TextWriter Out { get; }

        /// <summary>Problems and warnings.</summary>
        TextWriter Error { get; }

        /// <summary>The next line, the prompt shown first (tab completing with <paramref name="complete"/>); null at the end.</summary>
        string? ReadLine(string prompt, Func<string, int, Completion>? complete);
    }

    /// <summary>Lines from a reader (a script file, or standard input that is not a terminal): no prompts, no questions.</summary>
    internal sealed class ScriptConsole(TextReader input, TextWriter output, TextWriter error) : IShellConsole
    {
        public bool Interactive => false;

        public TextWriter Out => output;

        public TextWriter Error => error;

        public string? ReadLine(string prompt, Func<string, int, Completion>? complete) => input.ReadLine();
    }

    /// <summary>A terminal's keys and screen, as the line editor uses them.</summary>
    internal interface IShellTerminal
    {
        /// <summary>The next key typed.</summary>
        ConsoleKeyInfo ReadKey();

        /// <summary>Shows the line being edited after the prompt, with the cursor at <paramref name="cursor"/>.</summary>
        void Show(string prompt, string line, int cursor);

        /// <summary>Ends the line being edited (the next <see cref="Show"/> starts a new one).</summary>
        void EndLine();

        /// <summary>Writes a line of text.</summary>
        void WriteLine(string text);
    }

    /// <summary>A person at a terminal: lines edited with history and tab completion.</summary>
    internal sealed class InteractiveConsole(IShellTerminal terminal, TextWriter output, TextWriter error) : IShellConsole
    {
        private readonly LineEditor editor = new(terminal);

        public bool Interactive => true;

        public TextWriter Out => output;

        public TextWriter Error => error;

        public string? ReadLine(string prompt, Func<string, int, Completion>? complete) => editor.ReadLine(prompt, complete);
    }
}
