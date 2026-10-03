using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Tests;

// The read side of the file commands (docs/cli.md §2): ls, stat, cat's bytes, find and get on Mac paths.
public sealed class MacCommandsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-maccommands").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private MacPathTree Open() => MacPathTree.Open(MacPathFixtures.Disk(folder));

    private static MacPathEntry At(MacPathTree tree, string path) => tree.Resolve(path) ?? throw new Xunit.Sdk.XunitException($"no {path}");

    [Fact]
    public void Ls_lists_what_an_entry_holds_with_its_facts()
    {
        using var tree = Open();
        var root = MacCommands.List(tree, tree.Root);
        Assert.Equal(["A/B", "Archive.bin", "Empty", "Inner.img", "System Folder"], root.Select(e => e.Name).Order(StringComparer.Ordinal));
        var system = MacCommands.List(tree, At(tree, "System Folder"));
        var finder = system.Single(e => e.Name == "Finder");
        Assert.Equal(("file", "FNDR", "MACS", 6L, finder.ResourceSize), (finder.Kind, finder.Type, finder.Creator, finder.DataSize, finder.ResourceSize));
        Assert.True(finder.ResourceSize > 0);
        Assert.Equal((ushort)0x6000, finder.Flags);
        Assert.Equal(["hasBundle", "invisible"], finder.FlagNames);
        Assert.Equal(tree.Root.Path + ":System Folder:Finder", finder.Path);

        var archive = root.Single(e => e.Name == "Archive.bin");
        Assert.Equal(("container", "MacBinary II"), (archive.Kind, archive.Format));
        var packed = MacCommands.List(tree, At(tree, "Archive.bin")).Single();
        Assert.Equal((new DateTime(1999, 1, 24, 5, 20, 0), new DateTime(1999, 1, 24, 5, 21, 0)), (packed.Created, packed.Modified));
        Assert.Equal("folder", root.Single(e => e.Name == "Empty").Kind);
        Assert.Empty(MacCommands.List(tree, At(tree, "Empty")));                         // an empty folder, from the catalog

        // A file lists as itself; a fork its types; a type its resources.
        Assert.Equal(["Read Me"], MacCommands.List(tree, At(tree, "System Folder:Read Me")).Select(e => e.Name));
        var types = MacCommands.List(tree, At(tree, "System Folder:Finder:#rsrc"));
        Assert.Equal([("resource-type", "'STR '", 1), ("resource-type", "'TEXT'", 1)], types.Select(t => (t.Kind, t.Name, t.Count!.Value)));
        var greeting = MacCommands.List(tree, At(tree, "System Folder:Finder:#rsrc:'STR '")).Single();
        Assert.Equal(("resource", "STR ", (short)128, "Greeting", 3L), (greeting.Kind, greeting.ResourceType, greeting.ResourceId!.Value, greeting.ResourceName, greeting.DataSize!.Value));
    }

    [Fact]
    public void Stat_says_how_the_entry_was_read()
    {
        using var tree = Open();
        var note = MacCommands.Stat(tree, At(tree, "Inner.img:Deep:Note"));
        Assert.Equal(("file", "TEXT", "ttxt", 9L, 0L), (note.Kind, note.Type, note.Creator, note.DataSize, note.ResourceSize));
        Assert.Equal([("disk.img", "host file"), ("disk.img", "HFS volume"), ("Inner.img", "HFS volume")], note.Chain.Select(c => (c.Name, c.Format)));
        Assert.Equal(tree.Root.Path + ":Inner.img:Deep:Note", note.Path);

        var fork = MacCommands.Stat(tree, At(tree, "System Folder:Finder:#rsrc"));
        Assert.Equal(("resource-fork", "ResourceFork", 2), (fork.Kind, fork.ResourceForkSource, fork.Count));
        var folderInfo = MacCommands.Stat(tree, At(tree, "Empty"));
        Assert.Equal(("folder", 0), (folderInfo.Kind, folderInfo.Count));
    }

    [Theory]
    [InlineData(MacFork.Data, "System Folder:Finder", "finder")]
    [InlineData(MacFork.Data, "Inner.img:Deep:Note", "deep note")]
    [InlineData(MacFork.Data, "System Folder:Finder:#rsrc:TEXT:128", "text")]                // a resource's data
    [InlineData(MacFork.Data, "Archive.bin:Packed", "packed")]
    public void Bytes_are_a_fork_or_a_resource_s_data(MacFork fork, string path, string text)
    {
        using var tree = Open();
        Assert.Equal(text, System.Text.Encoding.ASCII.GetString(MacCommands.ReadBytes(tree, At(tree, path), fork)));
    }

    [Fact]
    public void The_resource_fork_s_bytes_and_text_in_Mac_OS_Roman()
    {
        using var tree = Open();
        var fork = MacCommands.ReadBytes(tree, At(tree, "System Folder:Finder"), MacFork.Resource);
        Assert.Equal(At(tree, "System Folder:Finder").File!.ResourceFork.ToArray(), fork);
        Assert.Equal("Hello\nWorld é", MacCommands.Text(MacCommands.ReadBytes(tree, At(tree, "System Folder:Read Me"), MacFork.Data)));
        Assert.Throws<InvalidOperationException>(() => MacCommands.ReadBytes(tree, At(tree, "System Folder"), MacFork.Data));
        Assert.Equal("00000000  66 69 6E 64 65 72                                 finder\n", MacCommands.Hex("finder"u8.ToArray()));
    }

    [Theory]
    [InlineData("*", null, null, null, null, null, 9, "A/B|Archive.bin|Deep|Empty|Finder|Inner.img|Note|Packed|Read Me|System Folder")]
    [InlineData("*e*", null, null, null, null, null, 9, "Archive.bin|Deep|Empty|Finder|Inner.img|Note|Packed|Read Me|System Folder")]
    [InlineData(null, "TEXT", null, null, null, null, 9, "A/B|Note|Read Me")]
    [InlineData(null, null, "MACS", null, null, null, 9, "Finder")]
    [InlineData(null, null, null, MacPathKind.Container, null, null, 9, "Archive.bin|Inner.img")]
    [InlineData(null, null, null, MacPathKind.Folder, null, null, 9, "Deep|Empty|System Folder")]
    [InlineData(null, null, null, null, "STR ", null, 9, "Finder|Packed")]
    [InlineData(null, null, null, null, null, "World", 9, "Read Me")]
    [InlineData("*", null, null, MacPathKind.File, null, null, 0, "A/B|Finder|Read Me")]    // depth 0: containers not entered
    public void Find_searches_through_containers(string? name, string? type, string? creator, MacPathKind? kind, string? resourceType, string? contains, int depth, string found)
    {
        using var tree = Open();
        var query = new MacFindQuery
        {
            Name = name,
            Type = type is null ? null : FourCC.FromString(type),
            Creator = creator is null ? null : FourCC.FromString(creator),
            Kind = kind,
            ResourceType = resourceType is null ? null : FourCC.FromString(resourceType),
            Contains = contains is null ? null : System.Text.Encoding.ASCII.GetBytes(contains),
            MaxDepth = depth,
        };
        var matches = MacCommands.Find(tree, tree.Root, query).Select(e => e.Name).Order(StringComparer.Ordinal);
        Assert.Equal(found, string.Join("|", matches));
    }

    [Fact]
    public void Find_stops_at_its_limit_and_from_where_it_starts()
    {
        using var tree = Open();
        Assert.Equal(3, MacCommands.Find(tree, tree.Root, new MacFindQuery { Name = "*" }).Take(3).Count());
        Assert.Equal(["Note"], MacCommands.Find(tree, At(tree, "Inner.img"), new MacFindQuery { Kind = MacPathKind.File }).Select(e => e.Name));
        Assert.Equal(tree.Root.Path + ":Inner.img:Deep:Note", MacCommands.Find(tree, tree.Root, new MacFindQuery { Name = "note" }).Single().Path);
    }

    [Theory]
    [InlineData("a*b", "A/B", true)]
    [InlineData("?older", "Folder", true)]
    [InlineData("read me", "Read Me", true)]
    [InlineData("*.img", "Inner.img", true)]
    [InlineData("*.img", "Inner.imgx", false)]
    [InlineData("[x]", "[x]", true)]
    public void Name_patterns_are_globs_ignoring_case(string pattern, string name, bool match) =>
        Assert.Equal(match, MacCommands.Matches(pattern, name));

    [Fact]
    public void Get_writes_a_file_with_both_forks_or_a_resource()
    {
        using var tree = Open();
        var finder = At(tree, "System Folder:Finder");
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;

        var written = MacCommands.Get(tree, finder, output, MacGetFormat.AppleDouble, overwrite: false);
        Assert.Equal([Path.Combine(output, "Finder"), Path.Combine(output, "._Finder")], written);
        Assert.Equal("finder", File.ReadAllText(written[0]));

        written = MacCommands.Get(tree, finder, output, MacGetFormat.MacBinary, overwrite: false);
        Assert.Equal([Path.Combine(output, "Finder.bin")], written);
        var back = MacBinaryReader.III.Read(ForkData.FromBytes(File.ReadAllBytes(written[0])), new ContainerContext()).Single();
        Assert.Equal(("FNDR", finder.File!.ResourceFork.Length), (back.FinderInfo.Type.ToString(), back.ResourceFork.Length));

        var raw = Directory.CreateDirectory(Path.Combine(folder, "raw")).FullName;
        written = MacCommands.Get(tree, finder, raw, MacGetFormat.Raw, overwrite: false);
        Assert.Equal([Path.Combine(raw, "Finder"), Path.Combine(raw, "Finder.rsrc")], written);

        written = MacCommands.Get(tree, At(tree, "System Folder:Finder:#rsrc:'STR ':128"), raw, MacGetFormat.Raw, overwrite: false);
        Assert.Equal([Path.Combine(raw, "STR_128.bin")], written);
        Assert.Equal([2, (byte)'H', (byte)'i'], File.ReadAllBytes(written[0]));

        // A folder: its files and folders below it.
        var tree2 = Directory.CreateDirectory(Path.Combine(folder, "tree")).FullName;
        written = MacCommands.Get(tree, At(tree, "Inner.img"), tree2, MacGetFormat.AppleDouble, overwrite: false, enter: true);
        Assert.Contains(Path.Combine(tree2, "Inner.img", "Deep", "Note"), written);

        Assert.Throws<IOException>(() => MacCommands.Get(tree, finder, output, MacGetFormat.MacBinary, overwrite: false));
        Assert.Single(MacCommands.Get(tree, finder, output, MacGetFormat.MacBinary, overwrite: true));
    }
}
