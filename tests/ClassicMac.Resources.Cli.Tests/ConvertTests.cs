using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Export;
using static ClassicMac.Resources.Decoders.Tests.DocumentFixtures;

namespace ClassicMac.Resources.Cli.Tests;

// Documents: `convert` writes each one as an HTML folder; `extract` adds document/ and the manifest's document entry.
public class ConvertTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-convert-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    private static ExportManifest Manifest(string directory) =>
        JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "manifest.json")), ExportManifestJson.Default.ExportManifest)!;

    // A SimpleText document: its text in the data fork, a picture at the option-space.
    private static (byte[] Data, byte[] Fork) ReadMe()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("styl"), 128, Styl((0, 16, 12, 3, 0, 12, 0))));
        fork.Add(new Resource(FourCC.FromString("PICT"), 1000, Picture(10, 10)));
        return ([.. "Read me\r"u8, 0xCA, .. "\rThanks."u8], fork.ToArray());
    }

    [Fact]
    public void Convert_writes_every_document_on_a_disk_in_a_folder_each()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Manual", [], DocMaker().ToArray(), type: "APPL", creator: "Dk@P");
        var (data, fork) = ReadMe();
        var docs = disk.Folder(HfsBuilder.Root, "Docs");
        disk.File(docs, "Read Me", data, fork, type: "TEXT", creator: "ttxt");
        disk.File(HfsBuilder.Root, "Game", [], ExtractTestsFork(), type: "APPL", creator: "RLMZ");
        var input = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(input, disk.Build("Disk"));
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("convert", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error); // the link to chapter 9 is only an Info
        Assert.Contains("2 documents, to", output);
        Assert.Contains("Manual: Manual/index.html", output);
        Assert.True(File.Exists(Path.Combine(target, "Manual", "chapter-02.html")));
        Assert.True(File.Exists(Path.Combine(target, "Manual", "images", "pict-1003.png")));
        Assert.Contains("images/pict-1000.png", File.ReadAllText(Path.Combine(target, "Docs", "Read Me", "index.html")));
        Assert.False(Directory.Exists(Path.Combine(target, "Game")));

        // Again into the same folder: refused unless overwriting.
        Assert.Equal(ExitCodes.IoError, Run("convert", input, "-o", target).Code);
        Assert.Equal(ExitCodes.Success, Run("convert", input, "-o", target, "--overwrite").Code);
    }

    [Fact]
    public void A_Word_document_with_no_resource_fork_converts()
    {
        var word = new ClassicMac.Resources.Decoders.Tests.MacWordBuilder().Font(3, "Geneva").Style([0x00, 0x10, 0x00, 0x03], [])
            .Text("Dear reader\r").Pap(0, 12, 0, 0x05, 0x01).Build();
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Letter", word, [], type: "WDBN", creator: "MSWD");
        var input = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(input, disk.Build("Disk"));
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("convert", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("1 document, to", output);
        var html = File.ReadAllText(Path.Combine(target, "index.html"));
        Assert.Contains("Dear reader", html);
        Assert.Contains("text-align:center", html);
    }

    [Fact]
    public void One_document_converts_straight_into_the_output_folder()
    {
        var input = Path.Combine(folder, "Manual.rsrc");
        File.WriteAllBytes(input, DocMaker().ToArray());
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("convert", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("1 document, to", output);
        Assert.True(File.Exists(Path.Combine(target, "index.html")));

        var other = Path.Combine(folder, "Other.rsrc");
        File.WriteAllBytes(other, ExtractTestsFork());
        var none = Run("convert", other, "-o", Path.Combine(folder, "none"));
        Assert.Equal(ExitCodes.Success, none.Code);
        Assert.Contains("No documents in Other.rsrc.", none.Output);
    }

    [Fact]
    public void Extract_adds_the_document_beside_the_resources()
    {
        var input = Path.Combine(folder, "Manual.rsrc");
        File.WriteAllBytes(input, DocMaker().ToArray());
        var target = Path.Combine(folder, "out");

        var (code, _, error) = Run("extract", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        var manifest = Manifest(target);
        Assert.Equal("1.2", manifest.FormatVersion);
        var document = manifest.Document!;
        Assert.Equal(("document.html", 1, "document/index.html"), (document.Converter, document.ConverterVersion, document.Path));
        Assert.Contains(document.Files, f => f.Path == "document/images/pict-1001.png");
        Assert.All(document.Files, f => Assert.True(File.Exists(Path.Combine([target, .. f.Path.Split('/')])), f.Path));
        Assert.Contains(manifest.Diagnostics, d => d.Code == "document.bad-link");

        // --no-documents, --raw and a type filter leave it out.
        foreach (var options in new[] { new[] { "--no-documents" }, ["--raw"], ["-t", "TEXT"] })
        {
            var other = Path.Combine(folder, string.Concat(options));
            Assert.Equal(ExitCodes.Success, Run(["extract", input, "-o", other, .. options]).Code);
            Assert.Null(Manifest(other).Document);
            Assert.False(Directory.Exists(Path.Combine(other, "document")));
        }
    }

    private static byte[] ExtractTestsFork()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR "), 128, new byte[] { 2, 104, 105 }));
        return fork.ToArray();
    }
}
