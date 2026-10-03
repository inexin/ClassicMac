using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// Alias files in the viewer (docs/formats/resources/aliases.md §5): italic names with a badge, the original's path in
// the header with Show Original (Ctrl+R), the original's preview under a strip, a card when it is not found, and the
// Alias card in Details.
public sealed class AliasViewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-aliasview").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static void Pump(Task task) => Headless.Pump(task);

    private static NodeViewModel Node(NodeViewModel at, params string[] path)
    {
        foreach (var name in path)
        {
            at = at.Children.First(c => c.Title == name);
        }

        return at;
    }

    private (MainViewModel Model, InputNode Input) Open(MainViewModel? model = null, string? path = null)
    {
        model ??= new MainViewModel();
        var open = model.OpenAsync(path ?? AliasFixtures.Disk(folder));
        Pump(open);
        return (model, open.Result!);
    }

    [Fact]
    public void Alias_files_are_italic_in_the_tree() => Headless.OnUiThread(() =>
    {
        var (_, input) = Open();
        Assert.True(Node(input, "Moved alias").IsAliasFile);
        Assert.True(Node(input, "Moved alias").IsItalic);
        Assert.False(Node(input, "Docs", "Note").IsItalic);
        Assert.False(Node(input, "Docs", "Note").IsAliasFile);
    });

    [Fact]
    public void An_alias_shows_its_original_and_Show_Original_selects_it() => Headless.OnUiThread(() =>
    {
        var (model, input) = Open();
        var note = Node(input, "Docs", "Note");
        model.Selected = Node(input, "Moved alias");
        Pump(model.PreviewTask);
        Assert.Same(note, model.SelectedAlias?.Target);
        Assert.Equal("Alias to Note · SimpleText text document", model.Header!.Kind);
        Assert.Equal("Aliases: Docs: Note", model.Header.Original);
        Assert.Equal("Text", model.Preview.Kind.ToString());                // the original's preview
        Assert.Equal("Hello", model.Preview.Text);
        Assert.Equal("Alias of Aliases: Docs: Note", model.AliasStrip);
        Assert.False(model.AliasNotFound);
        var card = model.Details.Groups.Single(g => g.Title == "Alias").Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("Aliases: Old: Note", card["Original"]);                 // where the alias recorded it
        Assert.Equal("Yes, by its file ID", card["Found"]);
        Assert.Equal("Aliases: Docs: Note", card["Now at"]);
        Assert.Equal("Aliases", card["Volume"]);
        Assert.Equal(((FileNode)note).File.CatalogId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), card["File ID"]);
        Assert.True(model.ShowOriginalCommand.CanExecute(null));
        model.ShowOriginalCommand.Execute(null);
        Assert.Same(note, model.Selected);
        Assert.Null(model.SelectedAlias);
        Assert.Null(model.AliasStrip);
    });

    [Fact]
    public void A_folder_alias_and_an_alias_of_an_alias_lead_to_their_originals() => Headless.OnUiThread(() =>
    {
        var (model, input) = Open();
        model.Selected = Node(input, "Stuff alias");
        Assert.Same(Node(input, "Stuff"), model.SelectedAlias?.Target);
        Assert.Equal("Alias to Stuff · folder", model.Header!.Kind);
        model.Selected = Node(input, "Chain alias");
        Assert.Same(Node(input, "Note alias"), model.SelectedAlias?.Target);  // the Finder's Show Original goes one step
    });

    [Fact]
    public void An_alias_whose_original_is_gone_says_so() => Headless.OnUiThread(() =>
    {
        var (model, input) = Open();
        model.Selected = Node(input, "Gone alias");
        Pump(model.PreviewTask);
        Assert.True(model.AliasNotFound);
        Assert.Null(model.AliasStrip);
        Assert.Equal("Alias to Gone · original not found", model.Header!.Kind);
        Assert.Equal("Aliases: Old: Gone", model.Header.Original);
        Assert.Equal("Aliases", model.SelectedAlias!.Resolution.Alias.VolumeName.ToMacRoman());
        Assert.False(model.ShowOriginalCommand.CanExecute(null));
        var card = model.Details.Groups.Single(g => g.Title == "Alias").Rows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("No", card["Found"]);
        Assert.False(card.ContainsKey("Now at"));
    });

    [Fact]
    public void An_alias_to_another_open_volume_resolves_there() => Headless.OnUiThread(() =>
    {
        var other = new HfsBuilder();
        var target = other.File(HfsBuilder.Root, "Far", [1], []);
        var otherPath = Path.Combine(folder, "other.img");
        File.WriteAllBytes(otherPath, other.Build("Other"));
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Far alias", [], AliasBuilder.Fork(AliasBuilder.Alias("Other", 2, "Far", target, path: "Other:Far")), info: AliasFixtures.AliasInfo);
        var path = Path.Combine(folder, "here.img");
        File.WriteAllBytes(path, disk.Build("Here"));
        var (model, here) = Open(path: path);
        var (_, there) = Open(model, otherPath);
        model.Selected = Node(here, "Far alias");
        Assert.Same(Node(there, "Far"), model.SelectedAlias?.Target);
    });

    [Fact]
    public void The_header_shows_the_path_and_Show_Original_and_the_preview_its_strip_or_card() => Headless.OnUiThread(() =>
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        var (_, input) = Open(model);
        model.Selected = Node(input, "Moved alias");
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Aliases: Docs: Note", shown);
        Assert.Contains("Alias of Aliases: Docs: Note", shown);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "ShowOriginalButton" && b.IsEffectivelyVisible);
        var baselines = new List<string>();
        Baselines.Check(window, "alias-preview", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        model.Selected = Node(input, "Gone alias");
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        shown = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Original not found", shown);
        Assert.Contains("Aliases: Old: Gone", shown);
        Baselines.Check(window, "alias-not-found", baselines, Baselines.Variant.Light);
        model.SelectedTab = 0;                                                 // Details: the Alias card
        Dispatcher.UIThread.RunJobs();
        Baselines.Check(window, "alias-details", baselines, Baselines.Variant.Light);
        Baselines.Verify(baselines);
        window.Close();
    });
}
