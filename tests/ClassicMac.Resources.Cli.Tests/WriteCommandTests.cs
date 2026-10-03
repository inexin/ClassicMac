using System.Buffers.Binary;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;

namespace ClassicMac.Resources.Cli.Tests;

// The write commands on Mac paths (docs/cli.md §3): put, mkdir, rm, rename, set, res-add, res-rm, each with --dry-run,
// --json, and -o (a new file) or --in-place; the input never changes otherwise.
public sealed class WriteCommandTests : IDisposable
{
    private static readonly FourCC Str = FourCC.FromString("STR ");

    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-write-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    // A volume: Docs (holding Letter, with 'STR ' 128) and Read Me, with free space for new files.
    private string Disk()
    {
        var builder = new HfsBuilder();
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        builder.File(docs, "Letter", "data"u8.ToArray(), fork.ToArray());
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, WithFreeSpace(builder.Build("Disk")));
        return path;
    }

    private string Out => Path.Combine(folder, "out.img");

    private static IReadOnlyList<MacFile> Files(string image) => HfsReader.Instance.Read(ForkData.FromFile(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(string image) => HfsReader.Instance.ReadFolders(ForkData.FromFile(image), new ContainerContext());

    [Fact]
    public void Mkdir_writes_a_new_image_and_leaves_the_input()
    {
        var disk = Disk();
        var before = File.ReadAllBytes(disk);

        var (code, output, error) = Run("mkdir", disk + ":Docs:Old", "-o", Out);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains(Folders(Out), f => f.MacPath == "Docs:Old");
        Assert.Equal(before, File.ReadAllBytes(disk));
        Assert.Contains("mkdir Docs:Old", output);
        Assert.Contains("Wrote " + Out, output);
    }

    [Fact]
    public void Put_adds_a_host_file_into_a_folder_or_as_a_named_file()
    {
        var disk = Disk();
        var note = Path.Combine(folder, "note.txt");
        File.WriteAllText(note, "Hi");

        Assert.Equal(ExitCodes.Success, Run("put", note, disk + ":Docs", "--type", "TEXT", "--creator", "ttxt", "-o", Out).Code);
        var added = Files(Out).Single(f => f.MacPath == "Docs:note.txt");
        Assert.Equal(("Hi", FourCC.FromString("TEXT"), FourCC.FromString("ttxt")),
            (System.Text.Encoding.ASCII.GetString(added.DataFork.ToArray()), added.FinderInfo.Type, added.FinderInfo.Creator));

        var named = Path.Combine(folder, "named.img");
        Assert.Equal(ExitCodes.Success, Run("put", note, disk + ":Notes", "-o", named).Code);
        Assert.Contains(Files(named), f => f.MacPath == "Notes");
    }

    [Fact]
    public void Rm_deletes_a_file_and_a_folder_only_with_recursive()
    {
        var disk = Disk();

        var refused = Run("rm", disk + ":Docs", "-o", Out);
        Assert.Equal(ExitCodes.Usage, refused.Code);
        Assert.Contains("nonempty", refused.Error);
        Assert.False(File.Exists(Out));

        Assert.Equal(ExitCodes.Success, Run("rm", disk + ":Docs", "--recursive", "-o", Out).Code);
        Assert.Equal(["Read Me"], Files(Out).Select(f => f.MacPath));
    }

    [Fact]
    public void Rename_and_set_change_an_item()
    {
        var disk = Disk();

        Assert.Equal(ExitCodes.Success, Run("rename", disk + ":read me", "About", "-o", Out).Code);     // names as HFS compares them
        var set = Path.Combine(folder, "set.img");
        var (code, _, error) = Run("set", Out + ":About", "--type", "ttro", "--flags", "Invisible,HasBundle", "-o", set);

        Assert.True(code == ExitCodes.Success, error);
        var about = Files(set).Single(f => f.MacPath == "About");
        Assert.Equal((FourCC.FromString("ttro"), FourCC.FromString("ttxt"), FinderFlags.IsInvisible | FinderFlags.HasBundle),
            (about.FinderInfo.Type, about.FinderInfo.Creator, about.FinderInfo.Flags));
        Assert.Equal(ExitCodes.Success, Run("set", disk + ":Docs", "--flags", "0x4000", "-o", set).Code);
        Assert.Equal(FinderFlags.IsInvisible, Folders(set).Single(f => f.MacPath == "Docs").FinderInfo.Flags);
        Assert.Equal(ExitCodes.Usage, Run("set", disk + ":Docs", "--type", "TEXT", "-o", set).Code);       // a folder has no type
    }

    [Fact]
    public void Res_add_and_res_rm_change_a_file_s_resources()
    {
        var disk = Disk();
        var data = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(data, "two"u8.ToArray());

        Assert.Equal(ExitCodes.Success, Run("res-add", disk + ":Docs:Letter:#rsrc:'STR ':129", data, "--name", "Second", "-o", Out).Code);
        var fork = ResourceFork.Read(Files(Out).Single(f => f.MacPath == "Docs:Letter").ResourceFork.ToArray());
        Assert.Equal(("two", "Second"), (System.Text.Encoding.ASCII.GetString(fork.Find(Str, 129)!.GetData().Span), fork.Find(Str, 129)!.Name?.ToMacRoman()));

        Assert.Equal(ExitCodes.Usage, Run("res-add", disk + ":Docs:Letter:#rsrc:'STR ':128", data, "-o", Out).Code);   // exists
        Assert.Equal(ExitCodes.Success, Run("res-add", disk + ":Docs:Letter:#rsrc:'STR ':128", data, "--replace", "-o", Out).Code);

        var removed = Path.Combine(folder, "removed.img");
        Assert.Equal(ExitCodes.Success, Run("res-rm", disk + ":Docs:Letter:#rsrc:'STR ':128", "-o", removed).Code);
        Assert.Null(ResourceFork.Read(Files(removed).Single(f => f.MacPath == "Docs:Letter").ResourceFork.ToArray()).Find(Str, 128));
    }

    [Fact]
    public void Dry_run_prints_the_plan_and_writes_nothing()
    {
        var disk = Disk();
        var before = File.ReadAllBytes(disk);

        var (code, output, _) = Run("rm", disk + ":Read Me", "--dry-run");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("delete Read Me", output);
        Assert.Contains("Dry run: nothing written.", output);
        Assert.Equal(before, File.ReadAllBytes(disk));
    }

    [Fact]
    public void Json_output_lists_the_changes()
    {
        var disk = Disk();

        var (code, output, _) = Run("mkdir", disk + ":New", "-o", Out, "--json");

        Assert.Equal(ExitCodes.Success, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(Path.GetFullPath(disk), json.RootElement.GetProperty("input").GetString());
        Assert.Equal(Path.GetFullPath(Out), json.RootElement.GetProperty("written")[0].GetString());
        Assert.False(json.RootElement.GetProperty("dryRun").GetBoolean());
        var change = Assert.Single(json.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal(("mkdir", "New"), (change.GetProperty("action").GetString(), change.GetProperty("path").GetString()));
    }

    [Fact]
    public void A_write_needs_an_output_or_in_place_and_in_place_keeps_the_original()
    {
        var disk = Disk();
        var before = File.ReadAllBytes(disk);

        var missing = Run("mkdir", disk + ":New");
        Assert.Equal(ExitCodes.Usage, missing.Code);
        Assert.Contains("-o", missing.Error);
        Assert.Equal(ExitCodes.Usage, Run("mkdir", disk + ":New", "-o", disk).Code);              // never over the input

        Assert.Equal(ExitCodes.Success, Run("mkdir", disk + ":New", "--in-place").Code);
        Assert.Contains(Folders(disk), f => f.MacPath == "New");
        Assert.Equal(before, File.ReadAllBytes(disk + ".orig"));
    }

    [Fact]
    public void Paths_that_name_nothing_exit_not_found()
    {
        var disk = Disk();

        Assert.Equal(ExitCodes.NotFound, Run("rm", disk + ":Missing", "-o", Out).Code);
        Assert.Equal(ExitCodes.NotFound, Run("mkdir", disk + ":Missing:New", "-o", Out).Code);
        Assert.Equal(ExitCodes.NotFound, Run("rm", Path.Combine(folder, "nothing.img") + ":X", "-o", Out).Code);
        Assert.Equal(ExitCodes.NotFound, Run("res-rm", disk + ":Missing:#rsrc:'STR ':128", "-o", Out).Code);
        Assert.Equal(ExitCodes.Usage, Run("res-rm", disk + ":Docs:Letter:#rsrc:'STR '", "-o", Out).Code);   // no ID: a usage error
    }

    [Fact]
    public void A_single_Mac_file_s_resources_are_changed_in_its_own_form()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        var input = Path.Combine(folder, "One.bin");
        File.WriteAllBytes(input, MacBinaryWriter.ToArray(new MacFile { Name = MacString.FromMacRoman("One"), ResourceFork = ForkData.FromBytes(fork.ToArray()) }));
        var data = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(data, "x"u8.ToArray());
        var output = Path.Combine(folder, "Two.bin");

        var (code, _, error) = Run("res-add", input + ":One:#rsrc:'STR ':130", data, "-o", output);

        Assert.True(code == ExitCodes.Success, error);
        Assert.NotNull(ResourceFork.Read(ClassicMac.Files.Editing.HostImport.Read(output).ResourceFork.ToArray()).Find(Str, 130));
        Assert.Equal(ExitCodes.Usage, Run("mkdir", input + ":New", "-o", output).Code);            // no folders in one file
    }

    private static byte[] WithFreeSpace(byte[] image)
    {
        const int allocationBlocks = 1600;
        int oldBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12));
        int oldFree = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22));
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), allocationBlocks);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22), checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }
}
