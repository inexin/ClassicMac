using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// Kinds as the Finder names them (finder.md §2.3, §5), from a volume's applications and System, then the built-in
// table: in the inspector's kind line, the Details tab and the tree's tooltip.
public sealed class FileKindsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-kinds").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A 'kind' resource (finder.md §1.4).
    private static byte[] Kind(string signature, short region, params (string Type, string Kind)[] entries)
    {
        var bytes = new List<byte>([.. MacRoman.Encode(signature), (byte)(region >> 8), (byte)region, 0, 0, 0, (byte)entries.Length]);
        foreach (var (type, kind) in entries)
        {
            bytes.AddRange(MacRoman.Encode(type));
            bytes.Add((byte)MacRoman.Encode(kind).Length);
            bytes.AddRange(MacRoman.Encode(kind));
            if (bytes.Count % 2 != 0)
            {
                bytes.Add(0);
            }
        }

        return [.. bytes];
    }

    private static byte[] Fork(params (string Type, short Id, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, data) in resources)
        {
            fork.Add(new Resource(FourCC.FromString(type), id, data));
        }

        return fork.ToArray();
    }

    // Applications: SimpleText (a 'kind' naming TEXT, and its name) and Teach (a bundle, no 'kind'); the System Folder's
    // System Resources with the standard kinds; documents for each, one whose application is not here, and one unknown.
    private async Task<(MainViewModel Model, InputNode Input)> Open()
    {
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var apps = HfsBuilder.Root;                                          // one catalog node: few folders
        disk.File(apps, "SimpleText", [], Fork(("kind", 128, Kind("ttxt", 0, ("apnm", "SimpleText"), ("TEXT", "SimpleText text document")))),
            type: "APPL", creator: "ttxt");
        disk.File(apps, "Teach", [], Fork(("BNDL", 128, [.. "TCH "u8, 0, 0, 0, 0, .. "FREF"u8, 0, 0, 0, 0, 0, 128]), ("FREF", 128, [.. "TEXT"u8, 0, 0, 0])),
            type: "APPL", creator: "TCH ");
        var system = HfsBuilder.Root;
        disk.File(system, "System Resources", [], Fork(("kind", -16550, Kind("istd", 0, ("PICT", "PICT document")))),
            type: "zsyr", creator: "MACS");
        var docs = disk.Folder(HfsBuilder.Root, "Docs");
        disk.File(docs, "Read Me", [1], [], type: "TEXT", creator: "ttxt");
        disk.File(docs, "Help", [1], [], type: "TEXT", creator: "hbwr");
        disk.File(docs, "Lesson", [1], [], type: "TEXT", creator: "TCH ");
        disk.File(docs, "Photo", [1], [], type: "PICT", creator: "WXYZ");
        disk.File(docs, "Mystery", [1], [], type: "ZZZZ", creator: "WXYZ");
        var path = Path.Combine(folder, "kinds.img");
        File.WriteAllBytes(path, disk.Build("Kinds"));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        return (model, input);
    }

    private static NodeViewModel Node(InputNode input, params string[] path)
    {
        NodeViewModel at = input;
        foreach (var name in path)
        {
            at = at.Children.First(c => c.Title == name);
        }

        return at;
    }

    [Theory]
    [InlineData("Read Me", "SimpleText text document", "from SimpleText’s 'kind' 128")]
    [InlineData("Lesson", "Teach document", "from Teach, by its file name")]
    [InlineData("Help", "Apple Help page", "built-in")]
    [InlineData("Photo", "PICT document", "from the System’s 'kind' -16550")]
    [InlineData("Mystery", "document", "built-in")]
    public async Task Documents_are_named_from_the_volume_then_the_table(string name, string kind, string source)
    {
        var (model, input) = await Open();
        var file = (FileNode)Node(input, "Docs", name);
        var found = FileKinds.Of(file);
        Assert.Equal(kind, found.Text);
        Assert.Equal(source, FileKinds.Source(found));

        model.Selected = file;
        Assert.Equal($"{char.ToUpperInvariant(kind[0])}{kind[1..]} in Docs", model.Header!.Kind);
        var rows = model.Details.Groups.Single(g => g.Title == "File").Rows;
        Assert.Equal((kind, source), (rows.Single(r => r.Label == "Kind").Value, rows.Single(r => r.Label == "Kind from").Value));
        Assert.Equal($"{kind}\n{source}", file.KindTip!.ToString());
    }

    [Fact]
    public async Task Applications_folders_and_system_files_have_the_Finders_kinds()
    {
        var (model, input) = await Open();
        Assert.Equal("application program", FileKinds.Of((FileNode)Node(input, "SimpleText")).Text);
        Assert.Equal("system file", FileKinds.Of((FileNode)Node(input, "System Resources")).Text);
        model.Selected = Node(input, "Teach");
        Assert.Equal("Application program in kinds.img", model.Header!.Kind);
    }

    [Fact]
    public async Task One_resolver_serves_the_volume_and_the_tooltip_waits_until_shown()
    {
        var (_, input) = await Open();
        var readMe = (FileNode)Node(input, "Docs", "Read Me");
        var tip = readMe.KindTip;
        Assert.False(FileKinds.HasResolver(input));                       // nothing resolved until the tip is shown
        _ = tip!.ToString();
        Assert.True(FileKinds.HasResolver(input));
        Assert.Same(FileKinds.ResolverFor(readMe), FileKinds.ResolverFor((FileNode)Node(input, "Docs", "Help")));
    }
}
