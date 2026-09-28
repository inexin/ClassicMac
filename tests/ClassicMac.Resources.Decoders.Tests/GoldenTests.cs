using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// Golden outputs: every fixture decoded as extract decodes it, compared with Golden/. Text outputs (.txt, .json, .bdf,
// .rtf) are kept as files, images and sounds as SHA-256 and length in Golden/golden.json, along with each fixture's
// decoder and diagnostic codes. With CLASSICMAC_UPDATE_GOLDEN=1 the tests rewrite Golden/ instead; review the change
// in git.
public class GoldenTests
{
    private static readonly string[] TextExtensions = [".txt", ".json", ".rtf", ".bdf"];

    private static bool Updating => Environment.GetEnvironmentVariable("CLASSICMAC_UPDATE_GOLDEN") == "1";

    private static string GoldenFolder([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Golden");

    [Fact]
    public void Decoders_give_their_golden_outputs()
    {
        var fork = GoldenFixtures.Fork();
        var entries = new List<object>();
        var texts = new Dictionary<string, byte[]>();
        foreach (var fixture in GoldenFixtures.All())
        {
            var decoder = ResourceDecoders.Create(fixture.Options).FirstOrDefault(d => d.CanDecode(fixture.Resource.Type));
            if (decoder is null) continue;
            var diagnostics = new List<Diagnostic>();
            var files = decoder.Decode(new DecodeInput(fixture.Resource, fixture.Resource.GetData(), fork, diagnostics: diagnostics));
            var outputs = new List<object>();
            foreach (var file in files)
            {
                var bytes = file.Content.ToArray();
                if (TextExtensions.Any(e => file.Extension.EndsWith(e, StringComparison.Ordinal)))
                {
                    var name = fixture.Key + file.Extension;
                    texts[name] = bytes;
                    outputs.Add(new { extension = file.Extension, file = name });
                }
                else
                {
                    outputs.Add(new { extension = file.Extension, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), length = bytes.Length });
                }
            }
            entries.Add(new
            {
                key = fixture.Key,
                decoder = $"{decoder.Name} {decoder.Version}",
                diagnostics = diagnostics.Select(d => $"{d.Severity} {d.Code}").ToList(),
                outputs,
            });
        }
        var golden = JsonSerializer.Serialize(new { fixtures = entries }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        Compare("golden.json", Encoding.UTF8.GetBytes(golden), texts);
    }

    // The manifest of the whole fixture fork, exported with the built-in decoders: pins manifest format 1.1.
    [Fact]
    public void The_export_manifest_matches_its_golden()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-golden-").FullName;
        try
        {
            var source = new ExportSource(MacString.FromMacRoman("Fixtures"), ["resource fork"], FourCC.FromString("rsrc"), FourCC.FromString("RSED"), 0);
            ResourceExporter.Export(GoldenFixtures.Fork(), folder, source, ExportOptions.Default with { Decoders = ResourceDecoders.Create() });
            var manifest = File.ReadAllBytes(Path.Combine(folder, "manifest.json"));
            Compare("manifest.json", manifest, []);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Every type a built-in decoder handles has a fixture, so a new decoder or type cannot skip the goldens.
    [Fact]
    public void Every_decoded_type_has_a_fixture()
    {
        var fixtures = GoldenFixtures.All().Select(f => f.Resource.Type).ToHashSet();
        var handled = ResourceDecoders.Create().OfType<IBuiltInDecoder>().SelectMany(d => d.Types).ToList();
        Assert.Equal(ResourceDecoders.Create().Count, ResourceDecoders.Create().OfType<IBuiltInDecoder>().Count());
        Assert.Empty(handled.Where(t => !fixtures.Contains(t)).Select(t => t.ToString()));
    }

    private static void Compare(string mainName, byte[] main, Dictionary<string, byte[]> texts)
    {
        var folder = GoldenFolder();
        if (Updating)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, mainName), main);
            foreach (var (name, bytes) in texts) File.WriteAllBytes(Path.Combine(folder, name), bytes);
            return;
        }
        var problems = new List<string>();
        foreach (var (name, bytes) in texts.Append(new(mainName, main)))
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) problems.Add($"{name}: no golden file");
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) problems.Add($"{name}: differs from its golden");
        }
        Assert.True(problems.Count == 0,
            string.Join("\n", problems) + "\nIf the change is intended, run with CLASSICMAC_UPDATE_GOLDEN=1 and review Golden/ in git.");
    }
}
