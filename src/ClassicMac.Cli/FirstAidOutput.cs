using System.IO;
using System.Text.Json;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli;

// First Aid's report as `check` and `repair` print it (docs/cli.md §2.7, §3.2).
internal static class FirstAidOutput
{
    // Disk First Aid's lines: each problem, then the verdict.
    public static void Text(TextWriter output, string prefix, FirstAidReport report, string? summary = null)
    {
        foreach (var problem in report.Problems)
        {
            output.WriteLine(prefix + problem);
        }

        output.WriteLine(prefix + (summary ?? report.Summary));
    }

    public static void Json(Utf8JsonWriter w, FirstAidReport? report, string name = "firstAid", string? summary = null)
    {
        if (report is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("verdict", JsonNamingPolicy.CamelCase.ConvertName(report.Verdict.ToString()));
        w.WriteString("summary", summary ?? report.Summary);
        w.WriteStartArray("problems");
        foreach (var problem in report.Problems)
        {
            w.WriteStartObject();
            w.WriteNumber("number", problem.Number);
            w.WriteString("message", problem.Message);
            w.WriteNumber("arg2", problem.Arg2);
            w.WriteNumber("arg3", problem.Arg3);
            w.WriteString("stage", problem.Stage);
            w.WriteBoolean("repairable", problem.Repairable);
            w.WriteString("code", problem.Code);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
