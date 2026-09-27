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

                if (opened.IsPlain && opened.Root.File.ResourceFork.Length == 0)
                {
                    // Not a container: the file itself should be a resource fork.
                    ResourceFork raw;
                    try
                    {
                        raw = ReadFork(opened.Root.File.DataFork, options);
                    }
                    catch (InvalidDataException e)
                    {
                        error.WriteLine($"{input.Name}: not a Mac container or a resource fork: {e.Message}");
                        return ExitCodes.Unreadable;
                    }
                    reporter.Write(input.Name, raw.Diagnostics);
                    entries.Add(new ListEntry(opened.Root.File, ["raw resource fork"], raw));
                }
                else
                {
                    foreach (var (node, chain) in opened.Leaves)
                    {
                        ResourceFork? fork = null;
                        if (node.File.ResourceFork.Length == 0 && TryDataForkAsFork(node.File.DataFork, options) is { } inData)
                        {
                            // Some applications keep resource-fork data in a data file (Realmz's .rsf files).
                            reporter.Write(Source(input, node.File), inData.Diagnostics);
                            entries.Add(new ListEntry(node.File, [.. chain, "data fork as resource fork"], inData));
                            continue;
                        }
                        if (node.File.ResourceFork.Length > 0)
                        {
                            try
                            {
                                fork = ReadFork(node.File.ResourceFork, options);
                                reporter.Write(Source(input, node.File), fork.Diagnostics);
                            }
                            catch (InvalidDataException e)
                            {
                                reporter.Write(Source(input, node.File),
                                    [new Diagnostic(DiagnosticSeverity.Error, "fork.unreadable", e.Message)]);
                            }
                        }
                        entries.Add(new ListEntry(node.File, chain, fork));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            if (format == CommandLine.ListFormat.Json) WriteJson(input, entries);
            else WriteText(entries);
            return reporter.ExitCode;
        }

        private static ResourceFork ReadFork(ForkData fork, ReadOptions options)
        {
            using var stream = fork.Open();
            return ResourceFork.Read(stream, options);
        }

        // Where a diagnostic came from: the input, and the Mac file inside it when that has another name.
        private static string Source(FileInfo input, MacFile file)
        {
            var name = file.MacPath;
            return name == input.Name ? input.Name : $"{input.Name} > {name}";
        }

        // A data fork that reads as a resource fork with at least one resource and no errors, or null.
        private static ResourceFork? TryDataForkAsFork(ForkData data, ReadOptions options)
        {
            if (data.Length is < 256 or > ResourceFork.MaxForkLength) return null;
            // A cheap look at the header first, so a volume's thousands of data files are not all read in full: the
            // data and map areas must lie inside the fork, the map after the reserved area.
            var header = data.ReadPrefix(16);
            long dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            long mapOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            long dataLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            long mapLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            if (dataOffset < 16 || mapOffset < 16 || mapLength < 30 || dataOffset + dataLength > data.Length
                || mapOffset + mapLength > data.Length) return null;
            try
            {
                var fork = ReadFork(data, options);
                return fork.Resources.Count > 0 && !fork.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
                    ? fork
                    : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private void WriteText(List<ListEntry> entries)
        {
            foreach (var entry in entries)
            {
                // A lone raw fork prints its table only, as before containers existed.
                if (entries.Count > 1 || entry.Chain[0] != "raw resource fork")
                    output.WriteLine($"\"{entry.File.MacPath}\" ({string.Join(" > ", entry.Chain)})");
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
                e.File.MacPath,
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

        private sealed record ListEntry(MacFile File, IReadOnlyList<string> Chain, ResourceFork? Fork);
    }

    internal sealed record ListOutput(string Input, List<ListFile> Files);

    internal sealed record ListFile(
        string Name, string Path, List<string> Formats, string Type, string Creator, List<ListResource>? Resources);

    internal sealed record ListResource(string Type, short Id, string? Name, int Size, string Attributes);

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ListOutput))]
    internal sealed partial class CliJsonContext : JsonSerializerContext
    {
    }
}
