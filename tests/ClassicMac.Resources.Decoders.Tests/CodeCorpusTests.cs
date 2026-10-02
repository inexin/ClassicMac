using ClassicMac.Code.Tests;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Code;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// Phase 11's exit for the integration: with CLASSICMAC_CODE_CORPUS set, every code resource of the corpus samples
// decodes (data, listing and model; 'CODE' 0 and 'cfrg' data and model) with no decoder failure and no error, and
// disasm lists each sample's code, the data-fork fragments included. Each sample is found by file name; its data fork
// is a file beside it. The counts are facts of the samples.
public class CodeCorpusTests
{
    public static TheoryData<string, long, string?, long, int, int> Samples => new()
    {
        // name, length, data fork file, its length, code resources decoded, disasm listings
        { "ResEdit.rsrc", 0, null, 0, 64, 63 },
        { "Realmz 7.1.2", 576955, null, 0, 2, 1 },
        { "QDHarness.APPL", 117059, null, 0, 9, 8 },
        { "DC612", 434333, "DC612.pef", 288984, 33, 32 }, // 31 segments and code resources, the data-fork fragment
        { "MacOS9_System.rsrc", 8021864, "MacOS9_System_datafork.bin", 6284692, 294, 444 }, // 282 code resources, 162 fragments
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Code_decodes_and_lists_with_no_error(string name, long length, string? dataForkName, long dataForkLength, int decoded, int listings)
    {
        var fork = ResourceFork.Read(CodeCorpus.Require(name, length == 0 ? null : length));
        var data = dataForkName is null ? [] : CodeCorpus.Require(dataForkName, dataForkLength);
        var decoders = ResourceDecoders.Create().Where(d => d.Name.StartsWith("code.", StringComparison.Ordinal)).ToList();
        var errors = new List<string>();
        int count = 0;
        foreach (var resource in fork.Resources)
        {
            if (decoders.FirstOrDefault(d => d.CanDecode(resource.Type)) is not { } decoder)
                continue;
            var diagnostics = new List<Diagnostic>();
            var files = decoder.Decode(new DecodeInput(resource, ResourceDecompression.Default.GetData(resource, fork, null, diagnostics), fork,
                diagnostics: diagnostics));
            int expected = resource.Type.ToString() == "cfrg" || (resource.Type.ToString() == "CODE" && resource.Id == 0) ? 2 : 3;
            if (files.Count != expected) errors.Add($"{resource}: {files.Count} files");
            errors.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{resource}: {d.Message} [{d.Code}]"));
            count++;
        }
        var disasm = new List<Diagnostic>();
        var listed = CodeExport.Disassemble(fork, () => data, CodeCpu.Both, null, disasm);
        errors.AddRange(disasm.Where(d => d.Severity == DiagnosticSeverity.Error || d.Code.StartsWith("code.", StringComparison.Ordinal))
            .Select(d => $"disasm: {d.Message} [{d.Code}]"));

        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(40)));
        Assert.Equal((decoded, listings), (count, listed.Count(f => f.Name.EndsWith(".s", StringComparison.Ordinal))));
    }
}
