namespace ClassicMac.Files.Tests;

// MacPathTree.AliasesTo (docs/formats/resources/aliases.md §5): the alias files on a volume whose original is an entry
// or inside it, which would no longer find it if it were deleted.
public sealed class AliasesToTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-aliasesto").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string[] Of(MacPathTree tree, string path) =>
        [.. tree.AliasesTo(tree.Resolve(path)!).Select(a => $"{a.Alias} > {a.Original}").Order()];

    [Fact]
    public void Aliases_to_an_item_or_into_a_folder_are_found()
    {
        using var tree = MacPathTree.Open(AliasFixtures.Disk(folder));

        Assert.Equal(["Moved alias > Docs:Note", "Note alias > Docs:Note"], Of(tree, "Docs:Note"));
        Assert.Equal(["Moved alias > Docs:Note", "Note alias > Docs:Note"], Of(tree, "Docs"));   // inside the folder
        Assert.Equal(["Stuff alias > Stuff"], Of(tree, "Stuff"));
        Assert.Equal(["Chain alias > Note alias"], Of(tree, "Note alias"));                          // an alias to an alias
        Assert.Empty(Of(tree, "Stuff:Thing"));
        Assert.Empty(Of(tree, "Old"));
    }
}
