using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    // `list`: the resources of every file inside the input, through any containers. A plain file that is no container
    // is read as a raw resource fork.
    internal sealed class ListCommand(TextWriter output, TextWriter error)
    {
        public int Run(
            FileInfo input, CommandLine.ListFormat format, ReadOptions options, ContainerReadOptions containerOptions,
            bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            var entries = new List<ListEntry>();
            try
            {
                var containerDiagnostics = new List<Diagnostic>();
                var opened = Input.Open(input, containerOptions, containerDiagnostics);
                reporter.Write(input.Name, containerDiagnostics);

                try
                {
                    entries.AddRange(opened.Forks(input, options, reporter).Select(f => new ListEntry(f.Node.File, opened.MacPath(f.Node), f.Chain, f.Fork)));
                }
                catch (InvalidDataException e)
                {
                    error.WriteLine($"{input.Name}: not a Mac container or a resource fork: {e.Message}");
                    return ExitCodes.Unreadable;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            if (format == CommandLine.ListFormat.Json)
            {
                WriteJson(input, entries);
            }
            else
            {
                WriteText(entries);
            }

            return reporter.ExitCode;
        }

        private void WriteText(List<ListEntry> entries)
        {
            foreach (var entry in entries)
            {
                // A lone raw fork prints its table only, as before containers existed.
                if (entries.Count > 1 || entry.Chain[0] != "raw resource fork")
                {
                    output.WriteLine($"\"{entry.Path}\" ({string.Join(" > ", entry.Chain)})");
                }

                if (entry.Fork is not { } fork)
                {
                    output.WriteLine("  no resource fork");
                    continue;
                }
                output.WriteLine($"{"Type",-8} {"ID",6} {"Size",9}  {"Attributes",-24} Name");
                foreach (var type in fork.Types)
                {
                    foreach (var r in fork.OfType(type).OrderBy(r => r.Id))
                    {
                        var name = r.Name is { } n ? $"\"{n}\"" : "";
                        output.WriteLine($"{Quote(r.Type),-8} {r.Id,6} {r.Length,9}  {Attributes(r.Attributes),-24} {name}".TrimEnd());
                    }
                }
                output.WriteLine($"{fork.Resources.Count} resources in {fork.Types.Count} types");
            }
        }

        private void WriteJson(FileInfo input, List<ListEntry> entries)
        {
            var list = new ListOutput(input.Name, entries.Select(e => new ListFile(
                e.File.Name.ToString(),
                e.Path,
                e.Chain.ToList(),
                e.File.FinderInfo.Type.ToString(),
                e.File.FinderInfo.Creator.ToString(),
                e.Fork?.Resources.Select(r => new ListResource(
                    r.Type.ToString(), r.Id, r.Name?.ToString(), r.Length, Attributes(r.Attributes))).ToList())).ToList());
            output.WriteLine(JsonSerializer.Serialize(list, CliJsonContext.Default.ListOutput));
        }

        private static string Quote(FourCC type) => $"'{type}'";

        private static string Attributes(ResourceAttributes attributes) =>
            attributes == ResourceAttributes.None ? "" : attributes.ToString().Replace(", ", ",");

        private sealed record ListEntry(MacFile File, string Path, IReadOnlyList<string> Chain, ResourceFork? Fork);
    }

    internal sealed record ListOutput(string Input, List<ListFile> Files);

    internal sealed record ListFile(
        string Name, string Path, List<string> Formats, string Type, string Creator, List<ListResource>? Resources);

    internal sealed record ListResource(string Type, short Id, string? Name, int Size, string Attributes);

    [JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ListOutput))]
    internal sealed partial class CliJsonContext : JsonSerializerContext
    {
    }
}
