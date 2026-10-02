using System.Text;
using System.Text.Json;
using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Code;
using static ClassicMac.Resources.Decoders.Tests.CodeFixtures;

namespace ClassicMac.Resources.Decoders.Tests;

// A whole file's code (the disasm command): one listing per segment, code resource and fragment, and code.json.
public class CodeExportTests
{
    private static (IReadOnlyList<CodeFile> Files, List<Diagnostic> Diagnostics) Run(ResourceFork fork, byte[] dataFork, CodeCpu cpu = CodeCpu.Both)
    {
        var diagnostics = new List<Diagnostic>();
        return (CodeExport.Disassemble(fork, () => dataFork, cpu, null, diagnostics), diagnostics);
    }

    private static string Text(IReadOnlyList<CodeFile> files, string name) => Encoding.UTF8.GetString(files.Single(f => f.Name == name).Content.Span);

    private static JsonElement Model(IReadOnlyList<CodeFile> files) =>
        JsonDocument.Parse(files.Single(f => f.Name == "code.json").Content).RootElement.Clone();

    [Fact]
    public void A_fat_application_gives_every_listing_and_the_model()
    {
        var (fork, data) = FatApplication();

        var (files, diagnostics) = Run(fork, data);

        Assert.Empty(diagnostics);
        Assert.Equal(["CODE-1 Main.s", "DRVR-12 .D.s", "ncod-5.s", "fragment-0 App.s", "code.json"], files.Select(f => f.Name));
        Assert.Equal(CodeListing.ForSegment(CodeApplication.Read(fork, new List<Diagnostic>()), 1).Text, Text(files, "CODE-1 Main.s"));
        Assert.StartsWith("; 'DRVR' 12 \".D\": 68k code resource\n", Text(files, "DRVR-12 .D.s"), StringComparison.Ordinal);
        Assert.StartsWith("; \"App\": PowerPC fragment ('pwpc'), 3 sections\n", Text(files, "fragment-0 App.s"), StringComparison.Ordinal);
        Assert.Contains("InterfaceLib::InitGraf", Text(files, "fragment-0 App.s"), StringComparison.Ordinal);

        var model = Model(files);
        var application = model.GetProperty("application");
        Assert.Equal(("unknown", 1), (application.GetProperty("model").GetString(), application.GetProperty("entry").GetProperty("segment").GetInt32()));
        var segment = Assert.Single(model.GetProperty("segments").EnumerateArray());
        Assert.Equal((1, "Main", "CODE-1 Main.s"), (segment.GetProperty("id").GetInt32(), segment.GetProperty("name").GetString(), segment.GetProperty("file").GetString()));
        Assert.Equal(["entry", "JT1"], segment.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Equal([("DRVR", 12, "68k", "DRVR-12 .D.s"), ("ncod", 5, "pef", "ncod-5.s")], model.GetProperty("codeResources").EnumerateArray()
            .Select(r => (r.GetProperty("type").GetString(), r.GetProperty("id").GetInt32(), r.GetProperty("format").GetString(), r.GetProperty("file").GetString())));
        var fragment = Assert.Single(model.GetProperty("fragments").EnumerateArray());
        Assert.Equal((0, "App", "dataFork", 0x10, "fragment-0 App.s", "pwpc"),
            (fragment.GetProperty("index").GetInt32(), fragment.GetProperty("name").GetString(), fragment.GetProperty("where").GetString(),
                fragment.GetProperty("offset").GetInt32(), fragment.GetProperty("file").GetString(), fragment.GetProperty("architecture").GetString()));
        Assert.Equal(["main", ".InitGraf", "Helper"], fragment.GetProperty("functions").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
    }

    [Fact]
    public void The_cpu_picks_the_68k_or_the_PowerPC_code()
    {
        var (fork, data) = FatApplication();

        Assert.Equal(["CODE-1 Main.s", "DRVR-12 .D.s", "code.json"], Run(fork, data, CodeCpu.M68k).Files.Select(f => f.Name));
        var ppc = Run(fork, data, CodeCpu.PowerPC).Files;
        Assert.Equal(["ncod-5.s", "fragment-0 App.s", "code.json"], ppc.Select(f => f.Name));
        Assert.Equal(JsonValueKind.Null, Model(ppc).GetProperty("application").ValueKind);
        Assert.Equal(0, Model(ppc).GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public void The_data_fork_is_read_only_for_a_data_fork_fragment()
    {
        var read = false;
        var files = CodeExport.Disassemble(Application(), () => { read = true; return Array.Empty<byte>(); }, CodeCpu.Both, null, new List<Diagnostic>());

        Assert.False(read);
        Assert.Equal(["CODE-1 Main.s", "code.json"], files.Select(f => f.Name));
    }

    [Fact]
    public void A_fragment_past_the_data_fork_is_reported()
    {
        var (fork, data) = FatApplication();

        var (files, diagnostics) = Run(fork, data[..0x20]);

        Assert.DoesNotContain(files, f => f.Name.StartsWith("fragment", StringComparison.Ordinal));
        var d = Assert.Single(diagnostics);
        Assert.Equal((DiagnosticSeverity.Warning, "code.fragment-range"), (d.Severity, d.Code));
        Assert.Equal(JsonValueKind.Null, Model(files).GetProperty("fragments")[0].GetProperty("file").ValueKind);
    }

    [Fact]
    public void A_fragment_of_length_0_runs_to_the_end_of_the_data_fork()
    {
        var fork = Fork(Res("cfrg", 0, Cfrg(("Lib", CfrgWhere.DataFork, 0, 0, 0, 0))));

        var (files, diagnostics) = Run(fork, Fragment());

        Assert.Empty(diagnostics);
        Assert.Equal(["fragment-0 Lib.s", "code.json"], files.Select(f => f.Name));
    }

    [Fact]
    public void Every_cfrg_resource_names_fragments()
    {
        // The System file keeps fragments in 'cfrg' resources other than 0: cfrg 1's member is listed as fragment-1.0.
        var fork = Fork(Res("cfrg", 0, Cfrg(("A", CfrgWhere.DataFork, 0, 0, 0, 0))), Res("cfrg", 1, Cfrg(("B", CfrgWhere.DataFork, 0, 0, 0, 0))));

        var (files, diagnostics) = Run(fork, Fragment());

        Assert.Empty(diagnostics);
        Assert.Equal(["fragment-0 A.s", "fragment-1.0 B.s", "code.json"], files.Select(f => f.Name));
        Assert.Equal([0, 1], Model(files).GetProperty("fragments").EnumerateArray().Select(f => f.GetProperty("cfrg").GetInt32()));
    }

    [Fact]
    public void A_data_fork_that_is_not_PEF_is_reported()
    {
        var fork = Fork(Res("cfrg", 0, Cfrg(("Lib", CfrgWhere.DataFork, 0, 0, 0, 0))));

        var (files, diagnostics) = Run(fork, new byte[64]);

        Assert.Equal("code.fragment-unreadable", Assert.Single(diagnostics).Code);
        Assert.Equal(["code.json"], files.Select(f => f.Name));
    }

    [Fact]
    public void A_fragment_in_a_resource_is_listed_once()
    {
        // Member 0 names 'abcd' 128 (a type no decoder lists); member 1 names 'ncod' 5, listed as a code resource already.
        var fork = Fork(
            Res("ncod", 5, Fragment()),
            Res("abcd", 128, Fragment()),
            Res("cfrg", 0, Cfrg(("A", CfrgWhere.Resource, 0, 0, 0x61626364, 128), ("B", CfrgWhere.Resource, 0, 0, 0x6E636F64, 5))));

        var (files, diagnostics) = Run(fork, []);

        Assert.Empty(diagnostics);
        Assert.Equal(["ncod-5.s", "abcd-128.s", "code.json"], files.Select(f => f.Name));
        Assert.Equal(["abcd-128.s", "ncod-5.s"], Model(files).GetProperty("fragments").EnumerateArray().Select(f => f.GetProperty("file").GetString()));
    }

    [Fact]
    public void A_missing_fragment_resource_and_other_locations_are_reported()
    {
        var fork = Fork(Res("cfrg", 0, Cfrg(("A", CfrgWhere.Resource, 0, 0, 0x61626364, 1), ("B", CfrgWhere.NamedFragment, 0, 0, 0, 0))));

        var (_, diagnostics) = Run(fork, []);

        Assert.Equal([("code.fragment-missing", DiagnosticSeverity.Warning), ("code.fragment-elsewhere", DiagnosticSeverity.Info)],
            diagnostics.Select(d => (d.Code, d.Severity)));
    }

    [Fact]
    public void A_cfrg_that_does_not_read_is_reported()
    {
        var (files, diagnostics) = Run(Fork(Res("cfrg", 0, [0, 1])), []);

        Assert.Equal("code.cfrg-unreadable", Assert.Single(diagnostics).Code);
        Assert.Empty(files);
    }

    [Fact]
    public void A_file_without_code_gives_nothing()
    {
        var (files, diagnostics) = Run(Fork(Res("STR ", 128, [0])), []);

        Assert.Empty(files);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Segments_without_CODE_0_are_listed_alone()
    {
        var (files, _) = Run(Fork(Res("CODE", 1, Code1)), []);

        Assert.Equal(["CODE-1.s", "code.json"], files.Select(f => f.Name));
        Assert.StartsWith("; 'CODE' 1: 68k segment, near header\n", Text(files, "CODE-1.s"), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Model(files).GetProperty("application").ValueKind);
    }

    [Fact]
    public void An_application_whose_CODE_0_does_not_read_is_reported_and_its_segments_listed_alone()
    {
        var (files, diagnostics) = Run(Fork(Res("CODE", 0, [0, 0]), Res("CODE", 1, Code1)), []);

        Assert.Equal(["CODE-1.s", "code.json"], files.Select(f => f.Name));
        Assert.Equal("code.jump-table-unreadable", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Names_are_made_safe_and_kept_apart()
    {
        var (files, _) = Run(Fork(Res("PTCH", 1, [0x4E, 0x75], "a/b"), Res("ptch", 1, [0x4E, 0x75], "A/B")), []);

        Assert.Equal(["PTCH-1 a%2Fb.s", "ptch-1 A%2FB ~2.s", "code.json"], files.Select(f => f.Name));
    }

    [Fact]
    public void A_damaged_segment_is_listed_with_its_diagnostics()
    {
        // CODE 1 too short for its header: the listing reports it.
        var (files, diagnostics) = Run(Fork(Res("CODE", 1, [0x4E, 0x75])), []);

        Assert.Equal(["CODE-1.s", "code.json"], files.Select(f => f.Name));
        Assert.Contains(diagnostics, d => d.Code == "m68k.segment-header");
    }
}
