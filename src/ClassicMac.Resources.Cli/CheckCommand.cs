using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Resources.Cli
{
    // `check` (docs/cli.md §2.7): the input read through, containers and every file's resource fork, its diagnostics
    // printed as results; a plain HFS volume also gets the checks the writer makes before an edit (HfsWriter.Check).
    internal sealed class CheckCommand(TextWriter output, TextWriter error)
    {
        public int Run(FileInfo input, ContainerReadOptions containerOptions, ReadOptions readOptions, bool strict, bool quiet, bool json)
        {
            var found = new List<(string Source, Diagnostic Diagnostic)>();
            Input opened;
            try
            {
                var diagnostics = new List<Diagnostic>();
                opened = Input.Open(input, containerOptions, diagnostics);
                found.AddRange(diagnostics.Select(d => (input.Name, d)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{input.Name}: {e.Message}");
                return ExitCodes.IoError;
            }

            // A plain host file no container reader recognised is just a file, not a resource fork to check.
            if (!opened.IsPlain)
            {
                foreach (var (node, _) in opened.Leaves)
                {
                    var diagnostics = new List<Diagnostic>();
                    MacFileResources.Read(node.File, readOptions, diagnostics);
                    found.AddRange(diagnostics.Select(d => (opened.Chain(input, node), d)));
                }
            }

            string? fault = null;
            var volume = opened.Host.Layout == HostLayout.Plain && opened.Root.Volume?.Format == "HFS";
            if (volume)
            {
                fault = HfsWriter.Check(opened.Root.File.DataFork);
            }
            else if (opened.Host.Layout == HostLayout.Plain && DiskCopy42Reader.Instance.CanRead(opened.Root.File.DataFork) &&
                     InputEditSession.Open(input.FullName, containerOptions, readOptions).Region is { DiskCopy42: true } disk)
            {
                // A Disk Copy 4.2 image of an HFS disk: the disk gets the writer's checks.
                volume = true;
                fault = HfsWriter.Check(opened.Root.File.DataFork.Slice(disk.Offset, disk.Length));
            }

            // A partitioned disk: each HFS partition gets the writer's checks (one that wraps HFS Plus or is no HFS is left out).
            var partitions = new List<(MacPartition Partition, string? Fault)>();
            if (opened.Host.Layout == HostLayout.Plain)
            {
                foreach (var partition in PartitionMapReader.Partitions(opened.Root.File.DataFork).Where(p => p.Type == "Apple_HFS"))
                {
                    try
                    {
                        partitions.Add((partition, HfsWriter.Check(opened.Root.File.DataFork.Slice(partition.Offset, partition.Length))));
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
            }

            fault ??= partitions.Select(p => p.Fault).FirstOrDefault(f => f is not null);

            int errors = found.Count(f => f.Diagnostic.Severity == DiagnosticSeverity.Error);
            int warnings = found.Count(f => f.Diagnostic.Severity == DiagnosticSeverity.Warning);
            if (json)
            {
                output.WriteLine(MacPathJson.Document(w =>
                {
                    w.WriteString("input", input.FullName);
                    w.WriteStartArray("diagnostics");
                    foreach (var (source, d) in found)
                    {
                        w.WriteStartObject();
                        w.WriteString("source", source);
                        w.WriteString("severity", d.Severity.ToString().ToLowerInvariant());
                        w.WriteString("code", d.Code);
                        w.WriteString("message", d.Message);
                        if (d.Location is { } location)
                        {
                            w.WriteString("location", location);
                        }

                        if (d.Offset is { } offset)
                        {
                            w.WriteNumber("offset", offset);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    if (volume)
                    {
                        w.WriteStartObject("volume");
                        w.WriteBoolean("passes", fault is null);
                        w.WriteString("fault", fault);
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteNull("volume");
                    }

                    if (partitions.Count > 0)
                    {
                        w.WriteStartArray("partitions");
                        foreach (var (partition, partitionFault) in partitions)
                        {
                            w.WriteStartObject();
                            w.WriteNumber("number", partition.Number);
                            w.WriteString("name", partition.Name);
                            w.WriteBoolean("passes", partitionFault is null);
                            w.WriteString("fault", partitionFault);
                            w.WriteEndObject();
                        }

                        w.WriteEndArray();
                    }

                    w.WriteNumber("errors", errors);
                    w.WriteNumber("warnings", warnings);
                }));
            }
            else
            {
                var reporter = new Reporter(output, strict: false, quiet);
                foreach (var (source, d) in found)
                {
                    reporter.Write(source, [d]);
                }
                if (volume)
                {
                    output.WriteLine($"volume: {fault ?? "passes the writer's checks"}");
                }

                foreach (var (partition, partitionFault) in partitions)
                {
                    output.WriteLine($"partition {partition.Number} \"{partition.Name}\": {partitionFault ?? "passes the writer's checks"}");
                }

                output.WriteLine($"{errors} {(errors == 1 ? "error" : "errors")}, {warnings} {(warnings == 1 ? "warning" : "warnings")}");
            }

            return errors > 0 || fault is not null || (strict && warnings > 0) ? ExitCodes.Damaged : ExitCodes.Success;
        }
    }
}
