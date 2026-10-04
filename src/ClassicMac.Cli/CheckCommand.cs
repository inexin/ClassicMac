using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Files;
using ClassicMac.Resources;

namespace ClassicMac.Cli;

// `check` (docs/cli.md §2.7): the input read through, containers and every file's resource fork, its diagnostics
// printed as results; a plain HFS volume also gets the checks the writer makes before an edit (HfsWriter.Check).
internal sealed class CheckCommand(TextWriter output, TextWriter error)
{
    public int Run(FileInfo input, ContainerReadOptions containerOptions, ReadOptions readOptions, bool strict, bool quiet, bool json, bool deep = false)
    {
        var found = new List<(string Source, Diagnostic Diagnostic)>();
        Input opened;
        try
        {
            var diagnostics = new List<Diagnostic>();
            // Without --deep the input's own structures only: containers stored in it are not opened (cli.md §2.7).
            opened = Input.Open(input, containerOptions, diagnostics, deep ? int.MaxValue : 1);
            found.AddRange(diagnostics.Select(d => (input.Name, d)));
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
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
        FirstAidReport? firstAid = null;
        var volume = opened.Host.Layout == HostLayout.Plain && opened.Root.Volume?.Format is "HFS" or "HFS Plus";
        if (volume)
        {
            // The writer's checks are HFS's; an HFS Plus volume gets First Aid's.
            fault = opened.Root.Volume?.Format == "HFS" ? HfsWriter.Check(opened.Root.File.DataFork) : null;
            firstAid = HfsFirstAid.Verify(opened.Root.File.DataFork);
        }
        else if (InputEditSession.Open(input.FullName, containerOptions, readOptions) is { Kind: InputEditKind.HfsVolume, Partition: null } session)
        {
            // A disk image the writer edits (Disk Copy 4.2, NDIF): its disk gets the writer's checks and First Aid.
            volume = true;
            fault = HfsWriter.Check(ForkData.FromBytes(session.Volume));
            firstAid = HfsFirstAid.Verify(ForkData.FromBytes(session.Volume));
        }

        // A partitioned disk: each HFS partition gets the writer's checks and First Aid (one that wraps HFS Plus or is no
        // HFS is left out).
        var partitions = new List<(MacPartition Partition, string? Fault, FirstAidReport FirstAid)>();
        if (opened.Host.Layout == HostLayout.Plain)
        {
            foreach (var partition in PartitionMapReader.Partitions(opened.Root.File.DataFork).Where(p => p.Type == "Apple_HFS"))
            {
                try
                {
                    var slice = opened.Root.File.DataFork.Slice(partition.Offset, partition.Length);
                    var report = HfsFirstAid.Verify(slice);
                    partitions.Add((partition, report.HfsPlus ? null : HfsWriter.Check(slice), report));
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        fault ??= partitions.Select(p => p.Fault).FirstOrDefault(f => f is not null);

        int notOpened = opened.Leaves.Count(leaf => leaf.Node.UnreadFormat is not null);
        // A volume or partition the writer refuses, or that First Aid does not find OK, is one error too, counted with the
        // reader's.
        int errors = found.Count(f => f.Diagnostic.Severity == DiagnosticSeverity.Error)
                     + (volume && partitions.Count == 0 && (fault is not null || Fails(firstAid)) ? 1 : 0)
                     + partitions.Count(p => p.Fault is not null || Fails(p.FirstAid));
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
                if (volume && firstAid?.HfsPlus != true)
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

                FirstAidOutput.Json(w, partitions.Count == 0 ? firstAid : null);

                if (partitions.Count > 0)
                {
                    w.WriteStartArray("partitions");
                    foreach (var (partition, partitionFault, partitionFirstAid) in partitions)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("number", partition.Number);
                        w.WriteString("name", partition.Name);
                        if (!partitionFirstAid.HfsPlus)
                        {
                            w.WriteBoolean("passes", partitionFault is null);
                            w.WriteString("fault", partitionFault);
                        }

                        FirstAidOutput.Json(w, partitionFirstAid);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                w.WriteNumber("notOpened", notOpened);
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
            if (partitions.Count == 0 && firstAid is not null)
            {
                FirstAidOutput.Text(output, "first aid: ", firstAid);
            }

            // The writer's checks are HFS's; an HFS Plus volume has First Aid's lines only.
            if (volume && firstAid?.HfsPlus != true)
            {
                output.WriteLine($"volume: {fault ?? "passes the writer's checks"}");
            }

            foreach (var (partition, partitionFault, partitionFirstAid) in partitions)
            {
                FirstAidOutput.Text(output, $"partition {partition.Number} \"{partition.Name}\" first aid: ", partitionFirstAid);
                if (!partitionFirstAid.HfsPlus)
                {
                    output.WriteLine($"partition {partition.Number} \"{partition.Name}\": {partitionFault ?? "passes the writer's checks"}");
                }
            }

            if (notOpened > 0)
            {
                output.WriteLine($"{notOpened} {(notOpened == 1 ? "container" : "containers")} in it not opened (--deep checks inside them)");
            }

            output.WriteLine($"{errors} {(errors == 1 ? "error" : "errors")}, {warnings} {(warnings == 1 ? "warning" : "warnings")}");
        }

        return errors > 0 || fault is not null || (strict && warnings > 0) ? ExitCodes.Damaged : ExitCodes.Success;
    }

    // A volume First Aid does not find OK: one that needs repair, cannot be repaired, or is not an HFS disk.
    private static bool Fails(FirstAidReport? report) =>
        report is { Verdict: not (FirstAidVerdict.AppearsOk or FirstAidVerdict.NotChecked) };
}
