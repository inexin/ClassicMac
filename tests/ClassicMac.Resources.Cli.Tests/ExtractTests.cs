using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Export;
using ClassicMac.Tests;

namespace ClassicMac.Resources.Cli.Tests;

public class ExtractTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-extract-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    private static byte[] Fork(params (string Type, short Id, string? Name, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, name, data) in resources)
        {
            var r = new Resource(FourCC.FromString(type), id, data);
            if (name is not null) r.Name = MacString.FromMacRoman(name);
            fork.Add(r);
        }
        return fork.ToArray();
    }

    private static ExportManifest Manifest(string directory) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "manifest.json")), ExportManifestJson.Default.ExportManifest)!;

    [Fact]
    public void A_raw_fork_file_extracts_into_the_output_folder()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Fork(("STR ", 128, "Hello", [5, 72, 101, 108, 108, 111]), ("ICN#", 128, null, new byte[256])));
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("extract", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("2 resources from 1 files", output);
        Assert.Equal("Hello", File.ReadAllText(Path.Combine(target, "STR%20", "128 Hello.txt"))); // decoded
        Assert.True(File.Exists(Path.Combine(target, "ICN#", "128.png"))); // decoded to PNG
        Assert.Equal(["raw resource fork"], Manifest(target).Source.Formats);

        // Again into the same folder: refused unless overwriting.
        Assert.Equal(ExitCodes.IoError, Run("extract", input, "-o", target).Code);
        Assert.Equal(ExitCodes.Success, Run("extract", input, "-o", target, "--overwrite").Code);

        // --raw writes the data itself.
        var rawTarget = Path.Combine(folder, "raw");
        Assert.Equal(ExitCodes.Success, Run("extract", input, "-o", rawTarget, "--raw").Code);
        Assert.Equal([5, 72, 101, 108, 108, 111], File.ReadAllBytes(Path.Combine(rawTarget, "STR%20", "128 Hello.bin")));
        Assert.All(Manifest(rawTarget).Resources, r => Assert.Equal("raw", r.Decoder));
    }

    [Fact]
    public void Disks_extract_a_folder_per_file_with_resources()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Realmz", [1, 2, 3], Fork(("CODE", 1, "Main", [0x4E, 0x75]), ("vers", 1, null, [1, 0])), type: "APPL", creator: "RLMZ");
        var data = disk.Folder(HfsBuilder.Root, "Data");
        disk.File(data, "Scenario", [], Fork(("TEXT", 128, "Intro", "Once upon a time"u8.ToArray())));
        disk.File(HfsBuilder.Root, "Read Me", "no resources"u8.ToArray(), []);
        var input = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(input, disk.Build("Disk"));
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("extract", input, "-o", target, "--keep-raw");

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("3 resources from 2 files", output);
        var realmz = Manifest(Path.Combine(target, "Realmz"));
        Assert.Equal(("Realmz", "APPL", "RLMZ"), (realmz.Source.Name, realmz.Source.Type, realmz.Source.Creator));
        Assert.Equal([0x4E, 0x75], File.ReadAllBytes(Path.Combine(target, "Realmz", "CODE", "1 Main.bin")));
        Assert.True(File.Exists(Path.Combine(target, "Realmz", "raw", "CODE", "1.bin")));
        Assert.Equal("Once upon a time", File.ReadAllText(Path.Combine(target, "Data", "Scenario", "TEXT", "128 Intro.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "Read Me")));
    }

    [Fact]
    public void Types_can_be_chosen()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Fork(("STR ", 128, null, [0]), ("ICN#", 128, null, new byte[256])));
        var target = Path.Combine(folder, "out");

        Assert.Equal(ExitCodes.Success, Run("extract", input, "-o", target, "-t", "STR ").Code);

        Assert.Equal(["STR "], Manifest(target).Resources.Select(r => r.Type));
        Assert.False(Directory.Exists(Path.Combine(target, "ICN#")));
    }

    // Every disk image in the corpus extracts, and every manifest's hashes match the files written.
    [Fact]
    public void Corpus_images_extract_with_matching_manifests()
    {
        if (!CorpusFolders.Any)
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of disk images to run this.");

        string[] extensions = [".img", ".dsk", ".iso", ".hfv", ".image", ".smi"];
        var images = CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.')
                && !CorpusFolders.IsDamageTest(f))
            .ToList();
        int extracted = 0, resources = 0;
        foreach (var image in images)
        {
            var target = Path.Combine(folder, $"corpus{extracted++}");
            var (code, _, error) = Run("extract", image, "-o", target, "-q");
            if (error.Contains("being used by another process", StringComparison.Ordinal)) continue;
            Assert.True(code is ExitCodes.Success or ExitCodes.Damaged or ExitCodes.Unreadable, $"{image}: {error}");
            if (!Directory.Exists(target)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(target, "manifest.json", SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(manifestPath)!;
                foreach (var r in Manifest(directory).Resources)
                {
                    var bytes = File.ReadAllBytes(Path.Combine(directory, r.Path));
                    Assert.Equal(r.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
                    resources++;
                }
            }
        }
        TestContext.Current.SendDiagnosticMessage($"{extracted} images, {resources} resources checked.");
    }

    [Fact]
    public void Files_that_are_neither_containers_nor_forks_are_unreadable()
    {
        var input = Path.Combine(folder, "notes.txt");
        File.WriteAllText(input, "just text, no resources here at all");
        Assert.Equal(ExitCodes.Unreadable, Run("extract", input, "-o", Path.Combine(folder, "out")).Code);

        // An application's own data file (like Realmz's "Data Caste"): big enough, but its first 16 bytes point
        // outside it, so it is not taken for a damaged resource fork.
        var data = Path.Combine(folder, "Data Caste");
        File.WriteAllBytes(data, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 5)).ToArray());
        var (code, _, error) = Run("list", data);
        Assert.Equal(ExitCodes.Unreadable, code);
        Assert.Contains("do not describe a resource fork", error);
        Assert.Equal(ExitCodes.Unreadable, Run("extract", data, "-o", Path.Combine(folder, "out2")).Code);
    }
}
