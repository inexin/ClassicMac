using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// The 'alis' decoder (finder.alias, docs/formats/resources/aliases.md §5); the full output is the golden alis-0.json.
public class AliasDecoderTests
{
    private static (JsonElement? Json, List<Diagnostic> Diagnostics) Decode(byte[] data)
    {
        var fork = new ResourceFork();
        var resource = new Resource(FourCC.FromString("alis"), 0, data);
        fork.Add(resource);
        var decoder = ResourceDecoders.Create().First(d => d.CanDecode(resource.Type));
        Assert.Equal("finder.alias", decoder.Name);
        var diagnostics = new List<Diagnostic>();
        var files = decoder.Decode(new DecodeInput(resource, data, fork, diagnostics: diagnostics));
        return (JsonDocument.Parse(files.Single().Content).RootElement, diagnostics);
    }

    [Fact]
    public void A_folder_alias_has_no_type_and_names_its_kind()
    {
        var (json, diagnostics) = Decode(AliasBuilder.Alias("Disk", 2, "Stuff", 31, kind: 1, type: "\0\0\0\0", creator: "\0\0\0\0"));
        Assert.Empty(diagnostics);
        Assert.Equal("folder", json!.Value.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, json.Value.GetProperty("type").ValueKind);
        Assert.Equal("Disk: Stuff", json.Value.GetProperty("targetPath").GetString());
        Assert.Equal("BD", json.Value.GetProperty("volume").GetProperty("signature").GetString());
    }

    [Fact]
    public void Tagged_data_that_ends_early_or_a_size_that_differs_is_reported()
    {
        var (_, open) = Decode(AliasBuilder.Alias("Disk", 2, "Note", 30, terminate: false));
        Assert.Contains(open, d => d.Code == "alias.short");
        var padded = AliasBuilder.Alias("Disk", 2, "Note", 30);
        var (_, longer) = Decode([.. padded, 0, 0]);
        Assert.Contains(longer, d => d.Code == "alias.size");
    }

    [Fact]
    public void A_record_too_short_or_of_another_version_is_not_decoded() =>
        Assert.Throws<InvalidDataException>(() => Decode(new byte[100]));
}
