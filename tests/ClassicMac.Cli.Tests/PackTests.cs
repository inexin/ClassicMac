using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files;
using ClassicMac.Resources;

namespace ClassicMac.Cli.Tests;

// `pack`: extract → pack gives back every unchanged resource byte for byte, from raw/ or the base fork; changes to raw
// files are packed, changes to decoded files are refused; containers carry the manifest's name and Finder info.
public class PackTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-pack-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    // A fork with a named, locked string (decoded), an icon list (decoded), a CODE segment (decoded, its .bin the data)
    // with attributes, a name with a backslash, and fork attributes.
    private static ResourceFork Original()
    {
        var fork = new ResourceFork { Attributes = (ResourceForkAttributes)0x0080 };
        fork.Add(new Resource(FourCC.FromString("STR "), 128, new byte[] { 5, 72, 101, 108, 108, 111 })
        {
            Name = MacString.FromMacRoman("Hello \\ world"),
            Attributes = ResourceAttributes.Locked,
        });
        fork.Add(new Resource(FourCC.FromString("ICN#"), 128, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()));
        fork.Add(new Resource(FourCC.FromString("CODE"), 1, new byte[] { 0, 0, 0, 0, 0x4E, 0x75 }) { Attributes = ResourceAttributes.Preload | ResourceAttributes.Purgeable });
        return fork;
    }

    private string Extract(bool keepRaw)
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Original().ToArray());
        var target = Path.Combine(folder, keepRaw ? "raw-export" : "export");
        string[] args = keepRaw ? ["extract", input, "-o", target, "--keep-raw"] : ["extract", input, "-o", target];
        Assert.Equal(ExitCodes.Success, Run(args).Code);
        return target;
    }

    private static void AssertSameResources(ResourceFork expected, ResourceFork actual)
    {
        Assert.Equal(expected.Attributes, actual.Attributes);
        Assert.Equal(expected.Resources.Select(r => (r.Type, r.Id, r.Name?.ToString(), r.Attributes, Convert.ToHexString(r.GetData().Span))),
            actual.Resources.Select(r => (r.Type, r.Id, r.Name?.ToString(), r.Attributes, Convert.ToHexString(r.GetData().Span))));
    }

    [Fact]
    public void An_export_with_raw_copies_packs_back_byte_for_byte()
    {
        var export = Extract(keepRaw: true);
        var packed = Path.Combine(folder, "packed.rsrc");

        var (code, output, error) = Run("pack", export, "-o", packed);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("3 resources", output);
        AssertSameResources(Original(), ResourceFork.Read(File.ReadAllBytes(packed)));
        Assert.Equal(ExitCodes.IoError, Run("pack", export, "-o", packed).Code); // exists
    }

    [Fact]
    public void Without_raw_copies_the_base_fork_gives_the_stored_data()
    {
        var export = Extract(keepRaw: false);
        var packed = Path.Combine(folder, "packed.rsrc");

        var (code, _, error) = Run("pack", export, "-o", packed);
        Assert.Equal(ExitCodes.Damaged, code);
        Assert.Contains("[pack.no-stored-data]", error); // the decoded STR and ICN#; CODE is its own data
        Assert.False(File.Exists(packed));

        Assert.Equal(ExitCodes.Success, Run("pack", export, "-o", packed, "--base", Path.Combine(folder, "App.rsrc")).Code);
        AssertSameResources(Original(), ResourceFork.Read(File.ReadAllBytes(packed)));
    }

    [Fact]
    public void Raw_files_can_change_decoded_ones_cannot_and_deletes_need_allowing()
    {
        var export = Extract(keepRaw: true);
        File.WriteAllBytes(Path.Combine(export, "CODE", "1.bin"), [0x4E, 0x71, 0x4E, 0x75]);
        var packed = Path.Combine(folder, "packed.rsrc");

        Assert.Equal(ExitCodes.Success, Run("pack", export, "-o", packed).Code);
        Assert.Equal([0x4E, 0x71, 0x4E, 0x75], ResourceFork.Read(File.ReadAllBytes(packed)).Find(FourCC.FromString("CODE"), 1)!.GetData().ToArray());

        File.WriteAllText(Path.Combine(export, "STR%20", "128 Hello %5C world.txt"), "Goodbye");
        var (code, _, error) = Run("pack", export, "-o", packed, "--overwrite");
        Assert.Equal(ExitCodes.Damaged, code);
        Assert.Contains("[pack.no-encoder]", error);

        File.Delete(Path.Combine(export, "STR%20", "128 Hello %5C world.txt"));
        Assert.Contains("[pack.missing-file]", Run("pack", export, "-o", packed, "--overwrite").Error);
        Assert.Equal(ExitCodes.Success, Run("pack", export, "-o", packed, "--overwrite", "--allow-deletes").Code);
        Assert.Equal(2, ResourceFork.Read(File.ReadAllBytes(packed)).Resources.Count);
    }

    // Decoded code's main file is its data (.bin): an application packs back from its export alone, and an edited .bin
    // is packed as it is.
    [Fact]
    public void Decoded_code_packs_back_from_its_bin_files()
    {
        var app = new ResourceFork();
        app.Add(new Resource(FourCC.FromString("CODE"), 0, ClassicMac.Resources.Decoders.Tests.CodeFixtures.Application0) { Attributes = ResourceAttributes.Preload });
        app.Add(new Resource(FourCC.FromString("CODE"), 1, ClassicMac.Resources.Decoders.Tests.CodeFixtures.Code1) { Name = MacString.FromMacRoman("Main") });
        app.Add(new Resource(FourCC.FromString("DRVR"), 12, ClassicMac.Resources.Decoders.Tests.CodeFixtures.Driver));
        app.Add(new Resource(FourCC.FromString("cfrg"), 0, ClassicMac.Resources.Decoders.Tests.CodeFixtures.Cfrg(("App", Code.Ppc.CfrgWhere.DataFork, 0, 0, 0, 0))));
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, app.ToArray());
        var export = Path.Combine(folder, "export");
        var (extracted, _, extractError) = Run("extract", input, "-o", export);
        Assert.True(extracted == ExitCodes.Success, extractError);
        Assert.True(File.Exists(Path.Combine(export, "CODE", "1 Main.s")));
        var packed = Path.Combine(folder, "packed.rsrc");

        var (code, _, error) = Run("pack", export, "-o", packed);

        Assert.True(code == ExitCodes.Success, error);
        AssertSameResources(app, ResourceFork.Read(File.ReadAllBytes(packed)));

        File.WriteAllBytes(Path.Combine(export, "CODE", "1 Main.bin"), [0, 0, 0, 1, 0x4E, 0x75]);
        Assert.Equal(ExitCodes.Success, Run("pack", export, "-o", packed, "--overwrite").Code);
        Assert.Equal([0, 0, 0, 1, 0x4E, 0x75], ResourceFork.Read(File.ReadAllBytes(packed)).Find(FourCC.FromString("CODE"), 1)!.GetData().ToArray());
    }

    [Fact]
    public void Containers_carry_the_name_and_Finder_info()
    {
        var input = Path.Combine(folder, "App.bin");
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("My App"),
            FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("APPL"), Creator = FourCC.FromString("MYAP"), Flags = (FinderFlags)0x2000 },
            ResourceFork = ForkData.FromBytes(Original().ToArray()),
        };
        File.WriteAllBytes(input, MacBinaryWriter.ToArray(file));
        var export = Path.Combine(folder, "export");
        Assert.Equal(ExitCodes.Success, Run("extract", input, "-o", export, "--keep-raw").Code);
        File.WriteAllText(Path.Combine(folder, "data.txt"), "data");

        foreach (var (container, extension) in new[] { ("macbinary", ".bin"), ("binhex", ".hqx"), ("applesingle", ".as") })
        {
            var packed = Path.Combine(folder, "packed" + extension);
            Assert.Equal(ExitCodes.Success, Run("pack", export, "-o", packed, "--container", container, "--data", Path.Combine(folder, "data.txt")).Code);
            var read = Input.Open(new FileInfo(packed), ContainerReadOptions.Default, []).Root.Children.Single().File;
            Assert.Equal(("My App", "APPL", "MYAP", (FinderFlags)0x2000), (read.Name.ToMacRoman(), read.FinderInfo.Type.ToString(), read.FinderInfo.Creator.ToString(), read.FinderInfo.Flags));
            Assert.Equal("data"u8.ToArray(), read.DataFork.ToArray());
            AssertSameResources(Original(), ResourceFork.Read(read.ResourceFork.ToArray()));
        }
    }
}
