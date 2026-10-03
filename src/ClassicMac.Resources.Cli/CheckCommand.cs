using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
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
                    found.AddRange(diagnostics.Select(d => (Input.Source(input, node.File), d)));
                }
            }

            string? fault = null;
            var volume = opened.Host.Layout == HostLayout.Plain && opened.Root.Children.Count > 0 &&
                         opened.Root.Children.All(c => c.Format == HfsReader.Instance.FormatName);
            if (volume)
            {
                fault = HfsWriter.Check(opened.Root.File.DataFork);
            }

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

                output.WriteLine($"{errors} {(errors == 1 ? "error" : "errors")}, {warnings} {(warnings == 1 ? "warning" : "warnings")}");
            }

            return errors > 0 || fault is not null || (strict && warnings > 0) ? ExitCodes.Damaged : ExitCodes.Success;
        }
    }
}
