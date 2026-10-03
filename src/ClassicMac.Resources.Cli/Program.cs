using System;

namespace ClassicMac.Resources.Cli
{
    internal static class Program
    {
        private static int Main(string[] args) => new CommandLine(Console.Out, Console.Error, Console.OpenStandardOutput()).Run(args);
    }
}
