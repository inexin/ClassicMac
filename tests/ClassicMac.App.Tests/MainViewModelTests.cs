using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-app-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

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

    // An HFS disk: "Read Me" at the top, and in Games:Realmz a file with STR# and two ICN#.
    private string Disk()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var games = disk.Folder(HfsBuilder.Root, "Games");
        var realmz = disk.Folder(games, "Realmz");
        disk.File(realmz, "Realmz", [1, 2, 3], Fork(("STR#", 128, "Races", [0, 0]), ("ICN#", 128, null, new byte[256]), ("ICN#", 129, null, new byte[256])),
            type: "APPL", creator: "RLMZ");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static T Child<T>(NodeViewModel node, string title) where T : NodeViewModel =>
        Assert.IsType<T>(node.Children.Single(c => c.Title == title));

    [Fact]
    public async Task Disks_open_as_a_tree_down_to_resources()
    {
        var model = new MainViewModel();

        var input = await model.OpenAsync(Disk());

        Assert.NotNull(input);
        Assert.Same(input, Assert.Single(model.Roots));
        Assert.Contains("1 diagnostic", model.Status + "1 diagnostic"); // status is set
        Child<FileNode>(input, "Read Me");
        var realmz = Child<FileNode>(Child<FolderNode>(Child<FolderNode>(input, "Games"), "Realmz"), "Realmz");
        Assert.Equal(NodeKind.Loading, Assert.Single(realmz.Children).Kind); // resources not read yet

        realmz.IsExpanded = true;
        await realmz.EnsureLoadedAsync();

        Assert.Equal(["'ICN#' (2)", "'STR#' (1)"], realmz.Children.Select(c => c.Title));
        Assert.Equal(["128", "129"], realmz.Children[0].Children.Select(c => c.Title));
        Assert.Equal("128 “Races”", realmz.Children[1].Children[0].Title);
        Assert.Empty(Child<FileNode>(input, "Read Me").Children); // no resources, no expander
    }

    // An HFS disk holding containers: "Big" (70 KB, first, so the BinHex text is past where a BinHex reader looks),
    // "Wrap.bin" (MacBinary of "Inner", with STR# 128 in its resource fork) and "Docs:Bad.hqx" (BinHex whose data
    // CRC is wrong).
    private string ContainerDisk()
    {
        var inner = new ClassicMac.Files.MacFile
        {
            Name = MacString.FromMacRoman("Inner"),
            DataFork = ClassicMac.Files.ForkData.FromBytes("inner"u8.ToArray()),
            ResourceFork = ClassicMac.Files.ForkData.FromBytes(Fork(("STR#", 128, null, [0, 0]))),
        };
        var text = ClassicMac.Files.Containers.BinHexWriter.ToText(new ClassicMac.Files.MacFile
        {
            Name = MacString.FromMacRoman("Note"), DataFork = ClassicMac.Files.ForkData.FromBytes(new byte[300]),
        }).ToCharArray();
        var data = Array.LastIndexOf(text, ':') - 20;
        text[data] = text[data] == 'A' ? 'B' : 'A';
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Big", new byte[70_000], []);
        disk.File(HfsBuilder.Root, "Wrap.bin", ClassicMac.Files.Containers.MacBinaryWriter.ToArray(inner), []);
        disk.File(disk.Folder(HfsBuilder.Root, "Docs"), "Bad.hqx", System.Text.Encoding.ASCII.GetBytes(text), []);
        var path = Path.Combine(folder, "containers.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    [Fact]
    public async Task Containers_on_a_disk_are_read_when_expanded()
    {
        var model = new MainViewModel();

        var input = (await model.OpenAsync(ContainerDisk()))!;

        var wrap = Child<ContainerFileNode>(input, "Wrap.bin (MacBinary III)");
        Assert.Equal(NodeKind.Loading, Assert.Single(wrap.Children).Kind);
        Assert.Empty(model.Diagnostics);

        wrap.IsExpanded = true;
        await wrap.EnsureLoadedAsync();

        var file = Child<FileNode>(wrap, "Inner");
        await file.EnsureLoadedAsync();
        Assert.Equal(["'STR#' (1)"], file.Children.Select(c => c.Title));
        Assert.Equal("MacBinary III", file.Node.Format);
    }

    [Fact]
    public async Task A_container_reports_its_problems_at_its_node_when_read()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(ContainerDisk()))!;
        var bad = Child<ContainerFileNode>(Child<FolderNode>(input, "Docs"), "Bad.hqx (BinHex 4.0)");

        await bad.EnsureLoadedAsync();

        Assert.NotEmpty(model.Diagnostics);
        Assert.All(model.Diagnostics, d => Assert.Same(bad, d.Node));
        Assert.All(model.Diagnostics, d => Assert.Equal(bad.Source, d.Source));
    }

    [Fact]
    public async Task Problems_inside_nested_files_name_the_file()
    {
        var model = new MainViewModel();
        var path = Path.Combine(folder, "outer.bin");
        var bad = File.ReadAllBytes(ContainerDisk()); // the disk, MacBinary-wrapped: its files are one level further in
        File.WriteAllBytes(path, ClassicMac.Files.Containers.MacBinaryWriter.ToArray(new ClassicMac.Files.MacFile
        {
            Name = MacString.FromMacRoman("Disk"), DataFork = ClassicMac.Files.ForkData.FromBytes(bad),
        }));
        var input = (await model.OpenAsync(path))!;
        var disk = Child<ContainerFileNode>(input, "Disk (HFS volume)");
        var hqx = Child<ContainerFileNode>(Child<FolderNode>(disk, "Docs"), "Bad.hqx (BinHex 4.0)");

        await hqx.EnsureLoadedAsync();

        Assert.NotEmpty(model.Diagnostics);
        Assert.All(model.Diagnostics, d => Assert.Equal(hqx.Source, d.Source));
    }

    [Fact]
    public async Task Details_follow_the_selection()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var realmz = Child<FileNode>(Child<FolderNode>(Child<FolderNode>(input, "Games"), "Realmz"), "Realmz");
        await realmz.EnsureLoadedAsync();

        model.Selected = realmz;
        var rows = model.Details.Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("Realmz", model.Details.Heading);
        Assert.Equal(("Games:Realmz:Realmz", "'APPL' / 'RLMZ'", "HFS volume"), (rows["Mac path"], rows["Type / creator"], rows["Found in"]));
        Assert.StartsWith("3 in 2 types, from the resource fork", rows["Resources"]);

        model.Selected = realmz.Children[1].Children[0];
        rows = model.Details.Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal(("'STR#'", "128", "Races", "2 bytes"), (rows["Type"], rows["ID"], rows["Name"], rows["Stored size"]));

        model.Selected = input;
        Assert.Equal("host file", model.Details.Rows.Single(r => r.Label == "Read as").Value);
    }

    [Fact]
    public async Task Raw_fork_files_show_their_types()
    {
        var path = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(path, Fork(("STR ", 128, null, [2, (byte)'h', (byte)'i'])));
        var model = new MainViewModel();

        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();

        Assert.Equal(["'STR ' (1)"], input.Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Diagnostics_carry_their_source_and_lead_to_their_node()
    {
        // A fork whose map lists a resource with its data outside the data area.
        var fork = Fork(("STR ", 128, null, [2, (byte)'h', (byte)'i']));
        fork.AsSpan(8, 4).Clear(); // data length 0: the resource's data lies outside
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Broken", [], fork);
        var path = Path.Combine(folder, "broken.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var model = new MainViewModel();

        var input = (await model.OpenAsync(path))!;
        var broken = Child<FileNode>(input, "Broken");
        await broken.EnsureLoadedAsync();

        var entry = model.Diagnostics.First(d => d.Node == broken);
        Assert.Equal("broken.img › Broken", entry.Source);
        model.Filter = DiagnosticFilter.Errors;
        Assert.All(model.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Diagnostic.Severity));

        model.Filter = DiagnosticFilter.All;
        model.SelectedDiagnostic = entry;
        Assert.Same(broken, model.Selected);
    }

    [Fact]
    public async Task Unreadable_paths_are_reported_and_inputs_close()
    {
        var model = new MainViewModel();
        Assert.Null(await model.OpenAsync(Path.Combine(folder, "missing.img")));
        Assert.Contains(model.Diagnostics, d => d.Code == "input.unreadable");

        var input = (await model.OpenAsync(Disk()))!;
        model.Selected = input;
        model.CloseCommand.Execute(null);
        Assert.Empty(model.Roots);
    }
}
