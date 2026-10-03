using ClassicMac.Core;
using ClassicMac.Resources;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

// Mac paths (docs/cli.md §1): a host file, then Mac names joined by ':' (or '/'), going into disk images, archives and
// other containers as folders, and on into a file's resource fork.
public sealed class MacPathTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-macpath").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] Fork(params (string Type, short Id, string? Name, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, name, data) in resources)
        {
            var resource = new Resource(FourCC.FromString(type), id, data);
            if (name is not null)
            {
                resource.Name = MacString.FromMacRoman(name);
            }

            fork.Add(resource);
        }

        return fork.ToArray();
    }

    // disk.img, an HFS volume "Disk": System Folder:Finder (resources 'STR ' 128 "Hi" and 'TEXT' 128), System Folder:Read
    // Me, Inner.img (an HFS volume "Inner" with Deep:Note), Archive.bin (MacBinary of "Packed"), and "A/B".
    private string Disk()
    {
        var inner = new HfsBuilder();
        var deep = inner.Folder(HfsBuilder.Root, "Deep");
        inner.File(deep, "Note", "deep"u8.ToArray(), []);
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var system = disk.Folder(HfsBuilder.Root, "System Folder");
        disk.File(system, "Finder", "finder"u8.ToArray(), Fork(("STR ", 128, "Greeting", [2, (byte)'H', (byte)'i']), ("TEXT", 128, null, "text"u8.ToArray())),
            type: "FNDR", creator: "MACS");
        disk.File(system, "Read Me", "Hello\rWorld"u8.ToArray(), []);
        disk.File(HfsBuilder.Root, "Inner.img", inner.Build("Inner"), [], type: "rohd", creator: "ddsk");
        disk.File(HfsBuilder.Root, "Archive.bin", MacBinary(2, "Packed", "packed"u8.ToArray(), []), [], type: "BINA", creator: "SITx");
        disk.File(HfsBuilder.Root, "A/B", "slash"u8.ToArray(), []);
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    [Fact]
    public void The_host_file_is_the_first_part_of_the_path_that_is_a_file()
    {
        var disk = Disk();
        Assert.Equal((disk, "System Folder:Finder"), MacPaths.SplitHost(disk + ":System Folder:Finder"));
        Assert.Equal((disk, "System Folder/Finder"), MacPaths.SplitHost(disk + "/System Folder/Finder"));
        Assert.Equal((disk, ""), MacPaths.SplitHost(disk));
        Assert.Null(MacPaths.SplitHost(Path.Combine(folder, "gone.img") + ":x"));
        Assert.Null(MacPaths.SplitHost(folder + ":x"));                                     // a folder is not a host file
    }

    [Theory]
    [InlineData("System Folder:Finder", new[] { "System Folder", "Finder" })]
    [InlineData("System Folder/Finder", new[] { "System Folder", "Finder" })]
    [InlineData(":System Folder::Finder/", new[] { "System Folder", "Finder" })]           // empty names are skipped
    [InlineData(@"A\/B", new[] { "A/B" })]
    [InlineData(@"back\\slash", new[] { @"back\slash" })]
    [InlineData(@"a\x", new[] { @"a\x" })]                                                   // a backslash before anything else is itself
    [InlineData("Finder:#rsrc:'snd ':128", new[] { "Finder", "#rsrc", "'snd '", "128" })]
    [InlineData("Finder:#rsrc:'a:b/'", new[] { "Finder", "#rsrc", "'a:b/'" })]              // separators inside a quoted type
    [InlineData("", new string[0])]
    public void Paths_split_into_names(string path, string[] names) => Assert.Equal(names, MacPaths.Split(path));

    [Theory]
    [InlineData("Finder", "Finder")]
    [InlineData("A/B", @"A\/B")]
    [InlineData("a:b", @"a\:b")]
    [InlineData(@"a\b", @"a\\b")]
    public void Names_are_escaped_for_paths(string name, string escaped) => Assert.Equal(escaped, MacPaths.Escape(name));

    [Theory]
    [InlineData("Finder", "finder", true)]
    [InlineData("é", "É", true)]                                                              // HFS: case-insensitive
    [InlineData("e", "é", false)]                                                             // but not diacritic-insensitive
    [InlineData("Read Me", "ReadMe", false)]
    public void Names_compare_as_HFS_compares_them(string a, string b, bool equal) => Assert.Equal(equal, MacPaths.NamesEqual(a, b));

    [Fact]
    public void Paths_resolve_to_folders_files_containers_and_resources()
    {
        var disk = Disk();
        using var tree = MacPathTree.Open(disk);
        var root = tree.Root;
        Assert.Equal((MacPathKind.Container, "disk.img", disk, "HFS volume"), (root.Kind, root.Name, root.Path, root.Format));
        Assert.Null(tree.Parent(root));

        var system = tree.Resolve("System Folder")!;
        Assert.Equal((MacPathKind.Folder, "System Folder", disk + ":System Folder"), (system.Kind, system.Name, system.Path));
        var finder = tree.Resolve("system folder/FINDER")!;                                  // either separator, any case
        Assert.Equal((MacPathKind.File, "Finder", disk + ":System Folder:Finder"), (finder.Kind, finder.Name, finder.Path));
        Assert.Equal("FNDR", finder.File!.FinderInfo.Type.ToString());
        Assert.Same(system, tree.Parent(finder));

        var slash = tree.Resolve(@"A\/B")!;
        Assert.Equal(("A/B", disk + @":A\/B"), (slash.Name, slash.Path));

        // Into a disk image inside the disk, read when it is entered.
        var note = tree.Resolve("Inner.img:Deep:Note")!;
        Assert.Equal((MacPathKind.File, disk + ":Inner.img:Deep:Note"), (note.Kind, note.Path));
        Assert.Equal("deep"u8.ToArray(), note.File!.DataFork.ToArray());
        var inner = tree.Parent(tree.Parent(note)!)!;
        Assert.Equal((MacPathKind.Container, "Inner.img", "HFS volume"), (inner.Kind, inner.Name, inner.Format));
        Assert.Same(root, tree.Parent(inner));

        // Into a MacBinary file: the file it holds.
        var packed = tree.Resolve("Archive.bin:Packed")!;
        Assert.Equal((MacPathKind.File, "packed"), (packed.Kind, System.Text.Encoding.ASCII.GetString(packed.File!.DataFork.ToArray())));
        Assert.StartsWith("MacBinary", tree.Parent(packed)!.Format);

        // The resource fork, a type, a resource.
        var fork = tree.Resolve("System Folder:Finder:#rsrc")!;
        Assert.Equal((MacPathKind.ResourceFork, "#rsrc", disk + ":System Folder:Finder:#rsrc"), (fork.Kind, fork.Name, fork.Path));
        Assert.Equal(["'STR '", "'TEXT'"], tree.Children(fork).Select(c => c.Name));
        var type = tree.Resolve("System Folder:Finder:#rsrc:'STR '")!;
        Assert.Equal((MacPathKind.ResourceType, "STR "), (type.Kind, type.ResourceType!.Value.ToString()));
        var greeting = tree.Resolve("System Folder:Finder:#rsrc:'STR ':128")!;
        Assert.Equal((MacPathKind.Resource, "128", disk + ":System Folder:Finder:#rsrc:'STR ':128"), (greeting.Kind, greeting.Name, greeting.Path));
        Assert.Equal((short)128, greeting.Resource!.Id);
        Assert.Equal("Greeting", greeting.Resource.Name!.Value.ToMacRoman());
        Assert.Same(type, tree.Parent(greeting));
        Assert.Equal(128, tree.Resolve("System Folder:Finder:#rsrc:TEXT:128")!.Resource!.Id); // a type without spaces needs no quotes
        Assert.Equal(["128"], tree.Children(tree.Resolve("System Folder:Finder:#rsrc:TEXT")!).Select(c => c.Name));
    }

    [Fact]
    public void Children_are_what_the_tree_would_show()
    {
        using var tree = MacPathTree.Open(Disk());
        Assert.Equal(["A/B", "Archive.bin", "Inner.img", "System Folder"], tree.Children(tree.Root).Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Finder", "Read Me"], tree.Children(tree.Resolve("System Folder")!).Select(c => c.Name));
        Assert.Equal(["Deep"], tree.Children(tree.Resolve("Inner.img")!).Select(c => c.Name));
        // A file's children: its resource fork when it has one.
        Assert.Equal(["#rsrc"], tree.Children(tree.Resolve("System Folder:Finder")!).Select(c => c.Name));
        Assert.Empty(tree.Children(tree.Resolve("System Folder:Read Me")!));
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("System Folder:Nope")]
    [InlineData("System Folder:Read Me:Below")]                                               // a plain file holds nothing
    [InlineData("System Folder:Read Me:#rsrc")]                                               // no resource fork
    [InlineData("System Folder:Finder:#rsrc:'snd '")]
    [InlineData("System Folder:Finder:#rsrc:'STR ':129")]
    [InlineData("System Folder:Finder:#rsrc:'STR ':x")]
    public void A_path_to_nothing_resolves_to_null(string path)
    {
        using var tree = MacPathTree.Open(Disk());
        Assert.Null(tree.Resolve(path));
    }

    // A wrapper holding one container (a MacBinary file of a disk image) is passed through: its contents are listed in
    // its place, and the name of the disk it holds may be given or left out.
    [Fact]
    public void A_wrapper_of_one_disk_shows_the_disk_s_contents()
    {
        var volume = new HfsBuilder();
        volume.File(HfsBuilder.Root, "File", "x"u8.ToArray(), []);
        var path = Path.Combine(folder, "disk.bin");
        File.WriteAllBytes(path, MacBinary(2, "Disk Image", volume.Build("Vol"), []));
        using var tree = MacPathTree.Open(path);
        Assert.Equal(["File"], tree.Children(tree.Root).Select(c => c.Name));
        Assert.Equal(path + ":File", tree.Resolve("File")!.Path);
        Assert.Equal(path + ":File", tree.Resolve("Disk Image:File")!.Path);
        Assert.Same(tree.Root, tree.Parent(tree.Resolve("File")!));
    }

    // A whole path: the host file opened, the rest resolved.
    [Fact]
    public void A_full_path_opens_its_host_and_resolves_the_rest()
    {
        var disk = Disk();
        using var tree = MacPathTree.OpenPath(disk + "/System Folder/Read Me", out var entry)!;
        Assert.Equal(disk + ":System Folder:Read Me", entry!.Path);
        Assert.Null(MacPathTree.OpenPath(Path.Combine(folder, "gone"), out _));
        using var missing = MacPathTree.OpenPath(disk + ":Nope", out var none);
        Assert.NotNull(missing);
        Assert.Null(none);
    }

    // A file that is not a container: the host is a file, with its resource fork from its companions.
    [Fact]
    public void A_plain_host_file_is_a_file()
    {
        var path = Path.Combine(folder, "notes.txt");
        File.WriteAllText(path, "plain");
        using var tree = MacPathTree.Open(path);
        Assert.Equal((MacPathKind.File, "notes.txt"), (tree.Root.Kind, tree.Root.Name));
        Assert.Empty(tree.Children(tree.Root));
    }
}
