using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Export;
using ClassicMac.Tests;

namespace ClassicMac.Resources.Cli.Tests;

// Phase 3's exit check: every input in the corpus (CLASSICMAC_CORPUS, one or more folders) exports with the built-in
// decoders and no decoder fails. Failures: a decoder error or exception, a resource of a decoded type left raw, a
// manifest hash that does not match its file. Damaged inputs (containers, volumes or forks the Mac would refuse, the
// harness's damage tests) and warnings are reported, not failed. Known, explained cases are listed in
// Corpus/allowlist.json. Corpus/baseline.json holds, per input (by name and SHA-256, wherever the corpus lives), its
// counts and one hash of all its outputs, so a change in any decoded output names the input; inputs not present are
// skipped. CLASSICMAC_UPDATE_BASELINE=1 rewrites the baseline (keeping entries for inputs not present).
public class CorpusExportTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-corpus-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string CorpusFile(string name, [CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Corpus", name);

    private sealed record Summary(string Name, string Sha256, int Forks, int Resources, int Decoded, int Raw, string Outputs);

    private sealed record Allowed(string Sha256, string Type, int Id, string Reason);

    [Fact]
    public void Corpus_exports_without_decoder_errors()
    {
        if (!CorpusFolders.Any) Assert.Skip("Set CLASSICMAC_CORPUS to one or more corpus folders (separated by ';') to run this.");

        var decoders = ResourceDecoders.Create();
        var allowlist = ReadAllowlist();
        var failures = new List<string>();
        var damaged = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var warnings = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var summaries = new Dictionary<string, Summary>(StringComparer.Ordinal);
        var target = 0;

        foreach (var path in Inputs())
        {
            var inputDiagnostics = new List<Diagnostic>();
            List<ForkToExtract> forks;
            ContainerNode root;
            IReadOnlyList<string> companions = [];
            try
            {
                var input = Input.Open(new FileInfo(path), ContainerReadOptions.Default, inputDiagnostics);
                root = input.Root;
                companions = input.Host.Companions;
                forks = Forks(input, inputDiagnostics);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Count(damaged, e is InvalidDataException ? "unreadable" : "io");
                continue;
            }
            foreach (var d in inputDiagnostics)
                Count(d.Severity == DiagnosticSeverity.Error ? damaged : warnings, d.Code);
            if (forks.Count == 0) continue;

            var sha = Sha256([path, .. companions.Order(StringComparer.Ordinal)]); // a fork may be in a companion file
            if (summaries.ContainsKey(sha)) continue;
            var output = Path.Combine(folder, (target++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var exported = new List<(string Source, Diagnostic Diagnostic)>();
            var result = Unpacker.Extract(root, forks, output, ExportOptions.Default with { Decoders = decoders }, exported);
            var name = Path.GetFileName(path);
            var damageTest = CorpusFolders.IsDamageTest(path);
            foreach (var failure in result.Failed) failures.Add($"{name}: {failure}");
            foreach (var (source, d) in exported)
            {
                if (d.Severity == DiagnosticSeverity.Error && damageTest) Count(damaged, d.Code);
                else if (d.Severity == DiagnosticSeverity.Error || d.Code == "export.decoder-failed") failures.Add($"{name} > {source}: {d.Message} [{d.Code}]");
                else Count(warnings, d.Code);
            }

            // The manifests: decoded or raw per resource, the hashes checked, all outputs hashed together.
            int resources = 0, decoded = 0, raw = 0;
            var outputs = new List<string>();
            foreach (var manifestPath in Directory.EnumerateFiles(output, "manifest.json", SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(manifestPath)!;
                var relative = Path.GetRelativePath(output, directory).Replace('\\', '/');
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                foreach (var r in manifest.RootElement.GetProperty("resources").EnumerateArray())
                {
                    resources++;
                    var type = FourCC.FromString(r.GetProperty("type").GetString()!);
                    var id = r.GetProperty("id").GetInt32();
                    var filePath = r.GetProperty("path").GetString()!;
                    var fileSha = r.GetProperty("sha256").GetString()!;
                    if (Sha256(Path.Combine(directory, filePath)) != fileSha) failures.Add($"{name} > {relative}/{filePath}: hash differs from its manifest");
                    outputs.Add($"{relative}/{filePath} {fileSha}");
                    if (r.GetProperty("decoder").GetString() != "raw")
                    {
                        decoded++;
                    }
                    else
                    {
                        raw++;
                        if (decoders.Any(d => d.CanDecode(type)) && !allowlist.Any(a => a.Sha256 == sha && a.Type == type.ToString() && a.Id == id))
                            failures.Add($"{name} > {relative}: '{type}' {id} was left raw although a decoder handles its type");
                    }
                }
            }
            outputs.Sort(StringComparer.Ordinal);
            var outputsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', outputs))));
            summaries[sha] = new Summary(name, sha, forks.Count, resources, decoded, raw, outputsHash);
            Directory.Delete(output, recursive: true);
        }

        var changes = CompareBaseline(summaries.Values);
        TestContext.Current.SendDiagnosticMessage(
            $"{summaries.Count} inputs, {summaries.Values.Sum(s => s.Resources)} resources ({summaries.Values.Sum(s => s.Decoded)} decoded, "
            + $"{summaries.Values.Sum(s => s.Raw)} raw). Damaged inputs: {Format(damaged)}. Warnings: {Format(warnings)}.");
        Assert.True(failures.Count == 0, $"{failures.Count} decoder failures:\n" + string.Join('\n', failures.Take(50)));
        Assert.True(changes.Count == 0, "Outputs changed from the baseline (if intended, run with CLASSICMAC_UPDATE_BASELINE=1):\n"
            + string.Join('\n', changes.Take(50)));
    }

    // Every file in the corpus folders except companions (.rsrc/.finf folders, ._ files); an empty one may have a fork beside it.
    private static IEnumerable<string> Inputs() =>
        CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.') && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    // The forks extract reads: a plain file that is no container is a raw fork, if it is one; otherwise each file's
    // resource fork (or a data fork holding one). Forks with no resources are left out.
    private static List<ForkToExtract> Forks(Input input, List<Diagnostic> diagnostics)
    {
        var forks = new List<ForkToExtract>();
        if (input.IsPlain && input.Root.File.ResourceFork.Length == 0)
        {
            if (!MacFileResources.LooksLikeFork(input.Root.File.DataFork)) return forks;
            var raw = MacFileResources.ReadRaw(input.Root.File.DataFork, ReadOptions.Default, diagnostics);
            if (raw.Fork is { Resources.Count: > 0 } fork) forks.Add(new ForkToExtract(input.Root, ["raw resource fork"], fork));
            return forks;
        }
        foreach (var (node, chain) in input.Leaves)
        {
            var found = MacFileResources.Read(node.File, ReadOptions.Default, diagnostics);
            if (found.Fork is { Resources.Count: > 0 } fork) forks.Add(new ForkToExtract(node, chain, fork));
        }
        return forks;
    }

    private static List<string> CompareBaseline(IEnumerable<Summary> current)
    {
        var path = CorpusFile("baseline.json");
        var baseline = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Summary>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
        var known = baseline.ToDictionary(s => s.Sha256, StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable("CLASSICMAC_UPDATE_BASELINE") == "1")
        {
            foreach (var s in current) known[s.Sha256] = s;
            var sorted = known.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Sha256, StringComparer.Ordinal).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(sorted, JsonOptions).ReplaceLineEndings("\n") + "\n");
            return [];
        }
        var changes = new List<string>();
        var added = 0;
        foreach (var s in current)
        {
            if (!known.TryGetValue(s.Sha256, out var before)) added++;
            else if (before with { Name = s.Name } != s) changes.Add($"{s.Name}: {Describe(before)} → {Describe(s)}");
        }
        if (added > 0) TestContext.Current.SendDiagnosticMessage($"{added} inputs are not in the baseline yet.");
        return changes;
    }

    private static string Describe(Summary s) =>
        $"{s.Forks} forks, {s.Resources} resources ({s.Decoded} decoded, {s.Raw} raw), outputs {s.Outputs[..12]}";

    private static List<Allowed> ReadAllowlist()
    {
        var path = CorpusFile("allowlist.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Allowed>>(File.ReadAllText(path), JsonOptions) ?? [] : [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // One hash over several files' contents, in order (an input and its companions).
    private static string Sha256(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths) hash.AppendData(File.ReadAllBytes(path));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Count(SortedDictionary<string, int> counts, string code) => counts[code] = counts.GetValueOrDefault(code) + 1;

    private static string Format(SortedDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"));
}
