using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicMac.Core;

namespace ClassicMac.Resources.Cli
{
    // `list`: the resources of a raw resource fork. Container detection arrives with the container readers.
    internal sealed class ListCommand(TextWriter output, TextWriter error)
    {
        public int Run(FileInfo input, CommandLine.ListFormat format, ReadOptions options, bool strict, bool quiet)
        {
            ResourceFork fork;
            try
            {
                using var stream = input.OpenRead();
                fork = ResourceFork.Read(stream, options);
            }
            catch (InvalidDataException e)
            {
                error.WriteLine($"{input.Name}: not a readable resource fork: {e.Message}");
                return ExitCodes.Unreadable;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            foreach (var d in fork.Diagnostics)
            {
                if (quiet && d.Severity != DiagnosticSeverity.Error) continue;
                var at = d.Offset is { } offset ? $" at {offset}" : "";
                error.WriteLine($"{input.Name}: {d.Severity.ToString().ToLowerInvariant()}{at}: {d.Message} [{d.Code}]");
            }

            if (format == CommandLine.ListFormat.Json) WriteJson(input, fork);
            else WriteText(fork);

            var failed = fork.Diagnostics.Any(d =>
                d.Severity == DiagnosticSeverity.Error || (strict && d.Severity == DiagnosticSeverity.Warning));
            return failed ? ExitCodes.Damaged : ExitCodes.Success;
        }

        private void WriteText(ResourceFork fork)
        {
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

        private void WriteJson(FileInfo input, ResourceFork fork)
        {
            var list = new ListOutput(
                input.Name,
                fork.Resources.Select(r => new ListResource(
                    r.Type.ToString(), r.Id, r.Name?.ToString(), r.Length, Attributes(r.Attributes))).ToList());
            output.WriteLine(JsonSerializer.Serialize(list, CliJsonContext.Default.ListOutput));
        }

        private static string Quote(FourCC type) => $"'{type}'";

        private static string Attributes(ResourceAttributes attributes) =>
            attributes == ResourceAttributes.None ? "" : attributes.ToString().Replace(", ", ",");
    }

    internal sealed record ListOutput(string File, List<ListResource> Resources);

    internal sealed record ListResource(string Type, short Id, string? Name, int Size, string Attributes);

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ListOutput))]
    internal sealed partial class CliJsonContext : JsonSerializerContext
    {
    }
}
