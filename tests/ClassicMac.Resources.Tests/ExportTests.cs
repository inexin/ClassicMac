using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Compression;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Tests;

public class ExportTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-export-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static readonly ExportSource Source = new(
        MacString.FromMacRoman("Realmz"), ["host file", "MacBinary II"], FourCC.FromString("APPL"), FourCC.FromString("RLMZ"), 0x2100);

    private static Resource Res(string type, short id, byte[] data, string? name = null, ResourceAttributes attributes = default)
    {
        var resource = new Resource(FourCC.FromString(type), id, data) { Attributes = attributes };
        if (name is not null) resource.Name = new MacString(MacRoman.Encode(name));
        return resource;
    }

    // A dcmp 0 resource (version 8 header) holding "01AABB 0002 11223344 4B 4C FF" (see DecompressionTests).
    private static Resource Compressed()
    {
        byte[] body = Convert.FromHexString("01AABB000211223344" + "4B4CFF");
        var data = new byte[CompressedResourceHeader.Length + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, CompressedResourceHeader.Signature);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), CompressedResourceHeader.Length);
        data[6] = 8;
        data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 10);
        data[12] = 0xFF;
        data[13] = 16;
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(14), 0);
        body.CopyTo(data, CompressedResourceHeader.Length);
        return new Resource(FourCC.FromString("CODE"), 1, data) { Attributes = ResourceAttributes.Compressed };
    }

    private static ResourceFork Fork(params Resource[] resources)
    {
        var fork = new ResourceFork();
        foreach (var r in resources) fork.Add(r);
        return fork;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void Resources_go_to_type_folders_with_a_manifest()
    {
        var fork = Fork(
            Res("PICT", 128, [1, 2, 3], "Title Screen"),
            Res("snd ", 200, [4, 5], "Door"),
            Res("STR#", -16455, [6], null, ResourceAttributes.Purgeable),
            Res("TEXT", 129, [7], "a/b: c?"));

        var result = ResourceExporter.Export(fork, folder, Source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, "PICT", "128 Title Screen.bin")));
        Assert.True(File.Exists(Path.Combine(folder, "snd%20", "200 Door.bin")));
        Assert.True(File.Exists(Path.Combine(folder, "STR#", "-16455.bin")));
        Assert.True(File.Exists(Path.Combine(folder, "TEXT", "129 a%2Fb%3A c%3F.bin")));

        var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(folder, "manifest.json")), ExportManifestJson.Default.ExportManifest)!;
        Assert.Equal(ExportManifest.CurrentVersion, manifest.FormatVersion);
        Assert.Equal(ExportManifest.SchemaUrl, manifest.Schema);
        Assert.Equal(("Realmz", "APPL", "RLMZ", 0x2100), (manifest.Source.Name, manifest.Source.Type, manifest.Source.Creator, manifest.Source.Flags));
        Assert.Equal(["host file", "MacBinary II"], manifest.Source.Formats);
        var str = manifest.Resources.Single(r => r.Type == "STR#");
        Assert.Equal((-16455, (string?)null, (int)ResourceAttributes.Purgeable, "STR#/-16455.bin"), (str.Id, str.Name, str.Attributes, str.Path));
        var snd = manifest.Resources.Single(r => r.Id == 200);
        Assert.Equal(("snd ", "736E6420", "Door", 2, 2, (short?)null, "raw"), (snd.Type, snd.TypeBytes, snd.Name, snd.Size, snd.StoredSize, snd.Dcmp, snd.Decoder));
        foreach (var r in manifest.Resources)
            Assert.Equal(r.Sha256, Sha(File.ReadAllBytes(Path.Combine(folder, r.Path))));
        Assert.Contains("\"$schema\"", File.ReadAllText(Path.Combine(folder, "manifest.json")));
    }

    [Fact]
    public void Compressed_resources_are_written_decompressed_and_kept_raw_on_request()
    {
        var resource = Compressed();
        var expected = ResourceDecompression.Default.GetData(resource).ToArray();

        var result = ResourceExporter.Export(Fork(resource), folder, Source, ExportOptions.Default with { KeepRaw = true });

        var entry = Assert.Single(result.Manifest.Resources);
        Assert.Equal((short?)0, entry.Dcmp);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, "CODE", "1.bin")));
        Assert.Equal((expected.Length, resource.Length), (entry.Size, entry.StoredSize));
        Assert.Equal("raw/CODE/1.bin", entry.RawPath);
        Assert.Equal(resource.GetData().ToArray(), File.ReadAllBytes(Path.Combine(folder, "raw", "CODE", "1.bin")));
        Assert.Equal(entry.StoredSha256, Sha(resource.GetData().ToArray()));
    }

    [Fact]
    public void Types_differing_only_in_case_and_names_that_collide_stay_apart()
    {
        var fork = Fork(
            Res("PICT", 128, [1], "Same"),
            Res("pict", 128, [2], "Same"),
            Res("TEXT", 1, [3], "x?"),
            Res("TEXT", 2, [4], "x?"));

        var result = ResourceExporter.Export(fork, folder, Source);

        Assert.Equal([1], File.ReadAllBytes(Path.Combine(folder, "PICT~50494354", "128 Same.bin")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(folder, "pict~70696374", "128 Same.bin")));
        Assert.Equal(4, result.Manifest.Resources.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Paths_fit_the_limit_and_types_can_be_chosen()
    {
        var fork = Fork(Res("TEXT", 1, [1], new string('n', 200)), Res("PICT", 2, [2]));

        var result = ResourceExporter.Export(fork, folder, Source,
            ExportOptions.Default with { MaxPathLength = 40, Types = new HashSet<FourCC> { FourCC.FromString("TEXT") } });

        var entry = Assert.Single(result.Manifest.Resources);
        Assert.True(entry.Path.Length <= 40, entry.Path);
        Assert.StartsWith("TEXT/1 nnn", entry.Path);
    }

    // Decodes TEXT into two files; throws for id 2, gives nothing for id 3.
    private sealed class StubDecoder : IResourceDecoder
    {
        public string Name => "stub";

        public int Version => 7;

        public bool CanDecode(FourCC type) => type == FourCC.FromString("TEXT");

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input) => input.Resource.Id switch
        {
            2 => throw new InvalidDataException("broken"),
            3 => [],
            5 => throw new EndOfStreamException("short"),
            _ => [new(".txt", "main"u8.ToArray(), "macintosh"), new(".json", "{}"u8.ToArray()), new(".txt", "second"u8.ToArray())],
        };
    }

    [Fact]
    public void Decoders_write_their_files_and_fall_back_to_raw()
    {
        var fork = Fork(Res("TEXT", 1, [1], "Good"), Res("TEXT", 2, [2]), Res("TEXT", 3, [3]), Res("PICT", 4, [4]), Res("TEXT", 5, [5]));

        var result = ResourceExporter.Export(fork, folder, Source,
            ExportOptions.Default with { Decoders = [new StubDecoder()], KeepRaw = true });

        var good = result.Manifest.Resources.Single(r => r.Id == 1);
        Assert.Equal(("stub", 7, "TEXT/1 Good.txt", "macintosh"), (good.Decoder, good.DecoderVersion, good.Path, good.Encoding));
        Assert.Equal(["TEXT/1 Good.json", "TEXT/1 Good ~2.txt"], good.OtherFiles!.Select(f => f.Path));
        Assert.Equal("second"u8.ToArray(), File.ReadAllBytes(Path.Combine(folder, "TEXT", "1 Good ~2.txt")));
        Assert.Equal(good.OtherFiles![0].Sha256, Sha("{}"u8.ToArray()));
        Assert.Equal("raw/TEXT/1.bin", good.RawPath);

        var failed = result.Manifest.Resources.Single(r => r.Id == 2);
        Assert.Equal(("raw", "TEXT/2.bin"), (failed.Decoder, failed.Path));
        Assert.Contains(result.Diagnostics, d => d.Code == "export.decoder-failed");
        Assert.Equal("raw", result.Manifest.Resources.Single(r => r.Id == 3).Decoder);
        Assert.Contains(result.Diagnostics, d => d.Code == "export.not-decoded");
        Assert.Equal("raw", result.Manifest.Resources.Single(r => r.Id == 4).Decoder); // no decoder for PICT
        Assert.Equal("raw", result.Manifest.Resources.Single(r => r.Id == 5).Decoder); // ran past its data
    }

    [Fact]
    public void A_folder_with_files_is_not_written_into_unless_overwriting()
    {
        File.WriteAllText(Path.Combine(folder, "other.txt"), "x");
        Assert.Throws<IOException>(() => ResourceExporter.Export(Fork(Res("TEXT", 1, [1])), folder, Source));
        ResourceExporter.Export(Fork(Res("TEXT", 1, [1])), folder, Source, ExportOptions.Default with { Overwrite = true });
    }
}
