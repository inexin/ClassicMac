using System;
using System.IO;
using System.Text;

namespace ClassicMac.Resources.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var binary = Console.OpenStandardOutput();
        return new CommandLine(Redirected(Console.IsOutputRedirected, binary, Console.Out),
                               Redirected(Console.IsErrorRedirected, Console.OpenStandardError(), Console.Error),
                               binary).Run(args);
    }

    // Redirected output is UTF-8 without a byte order mark, whatever the console's code page (docs/cli.md §2). A
    // console keeps .NET's writer, which writes Unicode to it; setting Console.OutputEncoding would change the
    // console's code page after the tool exits.
    private static TextWriter Redirected(bool redirected, Stream stream, TextWriter console) =>
        redirected ? new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true } : console;
}
