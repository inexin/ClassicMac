using ClassicMac.Files.Commands;

namespace ClassicMac.Files.Tests;

// HFS Plus symbolic links in the path tree (docs/formats/file-systems/hfs-plus.md §2.8): an slnk/rhap file whose data
// fork is a POSIX path, followed relative to the link's folder or, when absolute, from its volume's root, through links
// along the way, ':' in a name standing for the '/' HFS Plus keeps, at most 32 links (BSD's MAXSYMLINKS).
public sealed class SymbolicLinkTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-symlinks").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private MacPathTree Open()
    {
        var builder = new HfsPlusBuilder { Hfsx = true, CaseSensitive = true };
        uint @private = builder.Folder(HfsPlusBuilder.Root, "private");
        uint etc = builder.Folder(@private, "etc");
        builder.File(etc, "hosts", "127.0.0.1"u8.ToArray(), []);
        builder.Symlink(HfsPlusBuilder.Root, "etc", "private/etc");
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Read Me", "one"u8.ToArray(), []);
        builder.File(docs, "read me", "two"u8.ToArray(), []);
        builder.Symlink(docs, "Up", "../private/etc/hosts");
        builder.Symlink(docs, "Abs", "/etc/hosts");                               // through the root's etc link
        builder.Symlink(docs, "Same", "./Read Me");
        builder.Symlink(docs, "Lower", "read me");
        builder.File(HfsPlusBuilder.Root, "a/b", "slash"u8.ToArray(), []);
        builder.Symlink(HfsPlusBuilder.Root, "Colon", "a:b");
        builder.Symlink(HfsPlusBuilder.Root, "Chain", "Docs/Up");
        builder.Symlink(HfsPlusBuilder.Root, "Loop", "Loop");
        builder.Symlink(HfsPlusBuilder.Root, "Gone", "private/nowhere");
        var path = Path.Combine(folder, "links.img");
        File.WriteAllBytes(path, builder.Build("Links"));
        return MacPathTree.Open(path);
    }

    private static string Data(MacPathEntry? entry) =>
        System.Text.Encoding.UTF8.GetString(Assert.IsType<MacPathEntry>(entry).File!.DataFork.ToArray());

    [Fact]
    public void A_link_is_followed_relative_to_its_folder_or_from_the_volume_s_root()
    {
        using var tree = Open();

        Assert.Equal("127.0.0.1", Data(tree.Follow(tree.Resolve("Docs:Up")!)));
        Assert.Equal("127.0.0.1", Data(tree.Follow(tree.Resolve("Docs:Abs")!)));
        Assert.Equal("127.0.0.1", Data(tree.Follow(tree.Resolve("Chain")!)));          // a link to a link
        var etc = tree.Follow(tree.Resolve("etc")!)!;
        Assert.Equal((MacPathKind.Folder, "etc"), (etc.Kind, etc.Name));
        Assert.EndsWith("private:etc", etc.Path, StringComparison.Ordinal);
    }

    // A path through a link: with follow, each name before the last that is a link or alias leads on from its target.
    [Fact]
    public void A_path_through_a_link_resolves_when_following()
    {
        using var tree = Open();

        Assert.Null(tree.Resolve("etc:hosts"));
        Assert.Equal("127.0.0.1", Data(tree.Resolve("etc:hosts", follow: true)));
        Assert.Null(tree.Resolve("Gone:hosts", follow: true));
    }

    [Fact]
    public void Names_are_matched_exactly_first_and_a_colon_is_the_stored_slash()
    {
        using var tree = Open();

        Assert.Equal("one", Data(tree.Follow(tree.Resolve("Docs:Same")!)));
        Assert.Equal("two", Data(tree.Follow(tree.Resolve("Docs:Lower")!)));
        Assert.Equal("slash", Data(tree.Follow(tree.Resolve("Colon")!)));
    }

    [Fact]
    public void A_loop_or_a_missing_target_is_not_found_and_other_entries_are_themselves()
    {
        using var tree = Open();

        Assert.Null(tree.Follow(tree.Resolve("Loop")!));
        Assert.Null(tree.Follow(tree.Resolve("Gone")!));
        var readMe = tree.Resolve("Docs:Read Me")!;
        Assert.Same(readMe, tree.Follow(readMe));
    }

    [Fact]
    public void Stat_shows_a_link_s_target_and_where_it_leads()
    {
        using var tree = Open();

        var up = MacCommands.Stat(tree, tree.Resolve("Docs:Up")!).SymbolicLink!;
        Assert.Equal(("../private/etc/hosts", true), (up.Target, up.Found));
        Assert.EndsWith("private:etc:hosts", up.ResolvedPath, StringComparison.Ordinal);
        var gone = MacCommands.Stat(tree, tree.Resolve("Gone")!).SymbolicLink!;
        Assert.Equal(("private/nowhere", false, null), (gone.Target, gone.Found, gone.ResolvedPath));
        Assert.Null(MacCommands.Stat(tree, tree.Resolve("Docs:Read Me")!).SymbolicLink);
    }
}
