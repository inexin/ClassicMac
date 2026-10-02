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
        model.FilterText = "READ";
        await model.FilterTask;
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

        model.ClearFilterCommand.Execute(null);
        Assert.Equal("", model.FilterText);
        Assert.All(All(input).Prepend(input), n => Assert.False(n.IsFilteredOut));
        Assert.Null(readMe.Match);
        Assert.Equal(("Read Me", "", ""), (readMe.NameBefore, readMe.NameMatch, readMe.NameAfter));
    }

    [Fact]
    public async Task A_matching_folder_keeps_what_it_holds()
    {
        var (model, input) = await Open();
        model.FilterText = "game";
        await model.FilterTask;
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
        model.FilterText = "inner";
        await model.FilterTask;
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
        model.FilterText = "␣";
        await model.FilterTask;
        Assert.True(art.IsFilteredOut);
        Assert.All(group.Children, c => Assert.True(c.IsFilteredOut));

        model.FilterText = "no name";
        await model.FilterTask;
        Assert.False(group.IsFilteredOut);
        Assert.Equal((0, 7), group.Match);
        Assert.All(group.Children, c => Assert.False(c.IsFilteredOut));   // shown inside the matching group

        model.FilterText = "";
        await model.FilterTask;
        model.TypeAhead("␣");
        Assert.Equal(0, model.MatchCount);
    }

    [Fact]
    public async Task Typing_jumps_between_loaded_matches()
    {
        var (model, input) = await Open();
        model.Selected = input;
        var games = Node(input, "Games");
        var readMe = Node(games, "Read Me");
        var realmz = Node(games, "Realmz");
        Assert.False(model.IsTypeAheadOpen);

        model.TypeAhead("rE");                                               // (one letter would select Wrap.bin, whose preview reads it)
        model.TypeAhead("a");
        Assert.Equal("rEa", model.TypeAheadText);
        Assert.True(model.IsTypeAheadOpen);
        Assert.Equal((1, 2, "1 of 2 loaded matches"), (model.MatchNumber, model.MatchCount, model.TypeAheadSummary));
        Assert.Same(readMe, model.Selected);
        Assert.True(games.IsExpanded);
        Assert.True(readMe.IsCurrentMatch);
        Assert.False(realmz.IsCurrentMatch);
        Assert.Equal((0, 3), realmz.Match);
        Assert.True(Node(input, "Manual").IsDimmed);
        Assert.True(games.IsDimmed);
        Assert.False(readMe.IsDimmed);

        model.NextMatchCommand.Execute(null);                                // F3
        Assert.Same(realmz, model.Selected);
        Assert.Equal("2 of 2 loaded matches", model.TypeAheadSummary);
        Assert.True(realmz.IsCurrentMatch);
        Assert.False(readMe.IsCurrentMatch);
        model.NextMatchCommand.Execute(null);                                // wraps
        Assert.Same(readMe, model.Selected);
        model.PreviousMatchCommand.Execute(null);                            // Shift+F3, wraps back
        Assert.Same(realmz, model.Selected);

        model.TypeAhead("l");                                                // "real": the selection still matches
        Assert.Equal("1 of 1 loaded match", model.TypeAheadSummary);
        Assert.Same(realmz, model.Selected);
        model.TypeAheadBackspace();                                          // "rea": from the selection on
        Assert.Equal("2 of 2 loaded matches", model.TypeAheadSummary);
        Assert.Same(realmz, model.Selected);

        model.TypeAhead("x");
        Assert.Equal((0, "No loaded matches"), (model.MatchCount, model.TypeAheadSummary));
        Assert.Same(realmz, model.Selected);
        model.NextMatchCommand.Execute(null);                                // nothing to go to
        Assert.Same(realmz, model.Selected);

        model.ClearTypeAheadCommand.Execute(null);                           // Esc
        Assert.False(model.IsTypeAheadOpen);
        Assert.Equal("", model.TypeAheadText);
        Assert.All(All(input), n => Assert.False(n.IsDimmed || n.IsCurrentMatch || n.Match is not null));
    }

    [Fact]
    public async Task Typing_skips_loading_rows_filtered_rows_and_unread_containers()
    {
        var (model, input) = await Open();
        model.TypeAhead("inner");                                            // inside Wrap.bin, not read: not a loaded match
        Assert.Equal(0, model.MatchCount);
        model.TypeAheadBackspace();
        model.TypeAheadBackspace();
        model.TypeAheadBackspace();
        model.TypeAheadBackspace();
        model.TypeAheadBackspace();
        Assert.False(model.IsTypeAheadOpen);                                 // all typed away: closed
        model.TypeAheadBackspace();                                          // nothing to remove
        Assert.False(model.IsTypeAheadOpen);

        model.TypeAhead("loading");
        Assert.Equal(0, model.MatchCount);
        model.ClearTypeAheadCommand.Execute(null);

        model.FilterText = "manual";
        await model.FilterTask;
        model.TypeAhead("rea");                                              // Games is filtered out
        Assert.Equal(0, model.MatchCount);
        model.ClearTypeAheadCommand.Execute(null);
        Assert.Equal((0, 6), Node(input, "Manual").Match);                  // the filter's highlight comes back
    }

    [Fact]
    public async Task The_filter_applies_to_trees_laid_out_again()
    {
        var (model, input) = await Open();
        model.FilterText = "␣";
        await model.FilterTask;
        model.TreeDisplay.GroupNoName = false;                               // the files show as "(no name)" rows
        model.FilterText = "(no";
        await model.FilterTask;
        var art = Node(input, "Art");
        Assert.All(art.Children, c => Assert.Equal((0, 3), c.Match));
        model.TreeDisplay.GroupNoName = true;                                // grouped again: skipped
        Assert.True(art.IsFilteredOut);
    }
}
