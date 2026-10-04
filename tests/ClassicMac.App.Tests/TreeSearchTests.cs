using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The tree's filter (Ctrl+F) and type-ahead (design/boards/browse-tree.md, S6): matches among the loaded nodes, their
// letters highlighted; grouped "No name" files are skipped; the filter also reads containers not yet read.
public sealed class TreeSearchTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-search").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Volume.img: Games (Read Me, Realmz), Manual, Art (two files named with spaces: grouped) and Wrap.bin (MacBinary
    // holding "Inner Realm", not read when the disk opens).
    private async Task<(MainViewModel Model, InputNode Input)> Open()
    {
        var inner = new MacFile
        {
            Name = MacString.FromMacRoman("Inner Realm"),
            ResourceFork = ForkData.FromBytes(PreviewTests.Fork(("ICN#", 128, null, new byte[256]))),
        };
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var games = disk.Folder(HfsBuilder.Root, "Games");
        disk.File(games, "Realmz", [1], [], type: "APPL", creator: "RLMZ");
        disk.File(games, "Read Me", "hello"u8.ToArray(), []);
        disk.File(HfsBuilder.Root, "Manual", [2], []);
        var art = disk.Folder(HfsBuilder.Root, "Art");
        disk.File(art, " ", [3], []);
        disk.File(art, "  ", [4], []);
        disk.File(HfsBuilder.Root, "Wrap.bin", Files.Containers.MacBinaryWriter.ToArray(inner), []);
        var path = Path.Combine(folder, "Volume.img");
        File.WriteAllBytes(path, disk.Build("Volume"));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        return (model, input);
    }

    private static NodeViewModel Node(NodeViewModel parent, string name) => parent.Children.Single(c => c.Name == name);

    private static IEnumerable<NodeViewModel> All(NodeViewModel node) => node.Children.SelectMany(c => All(c).Prepend(c));

    [Fact]
    public async Task The_filter_shows_matches_with_their_folders_open()
    {
        var (model, input) = await Open();
        var games = Node(input, "Games");
        model.TreeSearch.FilterText = "READ";
        await model.TreeSearch.FilterTask;
        Assert.False(input.IsFilteredOut);
        Assert.False(games.IsFilteredOut);
        Assert.True(games.IsExpanded);
        Assert.Null(games.Match);
        var readMe = Node(games, "Read Me");
        Assert.False(readMe.IsFilteredOut);
        Assert.Equal((0, 4), readMe.Match);
        Assert.Equal(("", "Read", " Me"), (readMe.NameBefore, readMe.NameMatch, readMe.NameAfter));
        Assert.True(Node(games, "Realmz").IsFilteredOut);
        Assert.True(Node(input, "Manual").IsFilteredOut);
        Assert.True(Node(input, "Art").IsFilteredOut);

        model.TreeSearch.ClearFilterCommand.Execute(null);
        Assert.Equal("", model.TreeSearch.FilterText);
        Assert.All(All(input).Prepend(input), n => Assert.False(n.IsFilteredOut));
        Assert.Null(readMe.Match);
        Assert.Equal(("Read Me", "", ""), (readMe.NameBefore, readMe.NameMatch, readMe.NameAfter));
    }

    [Fact]
    public async Task A_matching_folder_keeps_what_it_holds()
    {
        var (model, input) = await Open();
        model.TreeSearch.FilterText = "game";
        await model.TreeSearch.FilterTask;
        var games = Node(input, "Games");
        Assert.Equal((0, 4), games.Match);
        Assert.All(games.Children, c => Assert.False(c.IsFilteredOut));
        Assert.All(games.Children, c => Assert.Null(c.Match));
    }

    [Fact]
    public async Task The_filter_reads_containers_not_yet_read()
    {
        var (model, input) = await Open();
        var wrap = input.Children.OfType<ContainerFileNode>().Single();
        Assert.True(wrap.IsUnread);
        model.TreeSearch.FilterText = "inner";
        await model.TreeSearch.FilterTask;
        Assert.False(wrap.IsUnread);
        var found = All(wrap).Single(n => n.Name == "Inner Realm");
        Assert.False(found.IsFilteredOut);
        Assert.Equal((0, 5), found.Match);
        Assert.False(wrap.IsFilteredOut);
        Assert.True(Node(input, "Manual").IsFilteredOut);
    }

    [Fact]
    public async Task Grouped_files_are_skipped_but_the_group_matches_no_name()
    {
        var (model, input) = await Open();
        var art = Node(input, "Art");
        var group = Assert.IsType<NoNameGroupNode>(Assert.Single(art.Children));
        model.TreeSearch.FilterText = "sp";
        await model.TreeSearch.FilterTask;
        Assert.True(art.IsFilteredOut);
        Assert.All(group.Children, c => Assert.True(c.IsFilteredOut));

        model.TreeSearch.FilterText = "no name";
        await model.TreeSearch.FilterTask;
        Assert.False(group.IsFilteredOut);
        Assert.Equal((0, 7), group.Match);
        Assert.All(group.Children, c => Assert.False(c.IsFilteredOut));   // shown inside the matching group

        model.TreeSearch.FilterText = "";
        await model.TreeSearch.FilterTask;
        model.TreeSearch.TypeAhead("sp");
        Assert.Equal(0, model.TreeSearch.MatchCount);
    }

    [Fact]
    public async Task Typing_jumps_between_loaded_matches()
    {
        var (model, input) = await Open();
        model.Selected = input;
        var games = Node(input, "Games");
        var readMe = Node(games, "Read Me");
        var realmz = Node(games, "Realmz");
        Assert.False(model.TreeSearch.IsTypeAheadOpen);

        model.TreeSearch.TypeAhead("rE");                                               // (one letter would select Wrap.bin, whose preview reads it)
        model.TreeSearch.TypeAhead("a");
        Assert.Equal("rEa", model.TreeSearch.TypeAheadText);
        Assert.True(model.TreeSearch.IsTypeAheadOpen);
        Assert.Equal((1, 2, "1 of 2 loaded matches"), (model.TreeSearch.MatchNumber, model.TreeSearch.MatchCount, model.TreeSearch.TypeAheadSummary));
        Assert.Same(readMe, model.Selected);
        Assert.True(games.IsExpanded);
        Assert.True(readMe.IsCurrentMatch);
        Assert.False(realmz.IsCurrentMatch);
        Assert.Equal((0, 3), realmz.Match);
        Assert.True(Node(input, "Manual").IsDimmed);
        Assert.True(games.IsDimmed);
        Assert.False(readMe.IsDimmed);

        model.TreeSearch.NextMatchCommand.Execute(null);                                // F3
        Assert.Same(realmz, model.Selected);
        Assert.Equal("2 of 2 loaded matches", model.TreeSearch.TypeAheadSummary);
        Assert.True(realmz.IsCurrentMatch);
        Assert.False(readMe.IsCurrentMatch);
        model.TreeSearch.NextMatchCommand.Execute(null);                                // wraps
        Assert.Same(readMe, model.Selected);
        model.TreeSearch.PreviousMatchCommand.Execute(null);                            // Shift+F3, wraps back
        Assert.Same(realmz, model.Selected);

        model.TreeSearch.TypeAhead("l");                                                // "real": the selection still matches
        Assert.Equal("1 of 1 loaded match", model.TreeSearch.TypeAheadSummary);
        Assert.Same(realmz, model.Selected);
        model.TreeSearch.TypeAheadBackspace();                                          // "rea": from the selection on
        Assert.Equal("2 of 2 loaded matches", model.TreeSearch.TypeAheadSummary);
        Assert.Same(realmz, model.Selected);

        model.TreeSearch.TypeAhead("x");
        Assert.Equal((0, "No loaded matches"), (model.TreeSearch.MatchCount, model.TreeSearch.TypeAheadSummary));
        Assert.Same(realmz, model.Selected);
        model.TreeSearch.NextMatchCommand.Execute(null);                                // nothing to go to
        Assert.Same(realmz, model.Selected);

        model.TreeSearch.ClearTypeAheadCommand.Execute(null);                           // Esc
        Assert.False(model.TreeSearch.IsTypeAheadOpen);
        Assert.Equal("", model.TreeSearch.TypeAheadText);
        Assert.All(All(input), n => Assert.False(n.IsDimmed || n.IsCurrentMatch || n.Match is not null));
    }

    [Fact]
    public async Task Typing_skips_loading_rows_filtered_rows_and_unread_containers()
    {
        var (model, input) = await Open();
        model.TreeSearch.TypeAhead("inner");                                            // inside Wrap.bin, not read: not a loaded match
        Assert.Equal(0, model.TreeSearch.MatchCount);
        model.TreeSearch.TypeAheadBackspace();
        model.TreeSearch.TypeAheadBackspace();
        model.TreeSearch.TypeAheadBackspace();
        model.TreeSearch.TypeAheadBackspace();
        model.TreeSearch.TypeAheadBackspace();
        Assert.False(model.TreeSearch.IsTypeAheadOpen);                                 // all typed away: closed
        model.TreeSearch.TypeAheadBackspace();                                          // nothing to remove
        Assert.False(model.TreeSearch.IsTypeAheadOpen);

        model.TreeSearch.TypeAhead("loading");
        Assert.Equal(0, model.TreeSearch.MatchCount);
        model.TreeSearch.ClearTypeAheadCommand.Execute(null);

        model.TreeSearch.FilterText = "manual";
        await model.TreeSearch.FilterTask;
        model.TreeSearch.TypeAhead("rea");                                              // Games is filtered out
        Assert.Equal(0, model.TreeSearch.MatchCount);
        model.TreeSearch.ClearTypeAheadCommand.Execute(null);
        Assert.Equal((0, 6), Node(input, "Manual").Match);                  // the filter's highlight comes back
    }

    [Fact]
    public async Task The_filter_applies_to_trees_laid_out_again()
    {
        var (model, input) = await Open();
        model.TreeSearch.FilterText = "sp";
        await model.TreeSearch.FilterTask;
        model.TreeDisplay.GroupNoName = false;                               // the files show as "(no name)" rows
        model.TreeSearch.FilterText = "(no";
        await model.TreeSearch.FilterTask;
        var art = Node(input, "Art");
        Assert.All(art.Children, c => Assert.Equal((0, 3), c.Match));
        model.TreeDisplay.GroupNoName = true;                                // grouped again: skipped
        Assert.True(art.IsFilteredOut);
    }
}
