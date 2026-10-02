using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.Views;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The Details tab in groups (design/boards/details.md, P4): File, Forks, Dates, Finder flags and How it was read for a
// file; cards of their own for inputs, resources, types and folders; Copy all as plain "Label: value" lines.
public sealed class DetailsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-details").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class FakeShell : IShell
    {
        public string? Copied { get; private set; }

        public void OpenUri(Uri uri)
        {
        }

        public Task ShowAboutAsync(AboutInfo about) => Task.CompletedTask;

        public void Minimize()
        {
        }

        public void ToggleZoom()
        {
        }

        public Task CopyTextAsync(string text)
        {
            Copied = text;
            return Task.CompletedTask;
        }
    }

    // An HFS disk: Games:Realmz (application with a bundle, inited, locked, label 2, at (14, 220)) with three
    // resources, one of them compressed; and Read Me (no flags, data fork only).
    private async Task<(MainViewModel Model, InputNode Input, FileNode Realmz, FileNode ReadMe)> Open()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR#"), 128, new byte[] { 0, 0 }));
        fork.Add(new Resource(FourCC.FromString("ICN#"), 128, new byte[256]));
        fork.Add(new Resource(FourCC.FromString("ICN#"), 129, new byte[256]));
        var disk = new HfsBuilder();
        var games = disk.Folder(HfsBuilder.Root, "Games");
        disk.File(games, "Realmz", [1, 2, 3, 4], fork.ToArray(), info: new FinderInfo
        {
            Type = FourCC.FromString("APPL"),
            Creator = FourCC.FromString("RLMZ"),
            Flags = FinderFlags.HasBundle | FinderFlags.HasBeenInited | (FinderFlags)(2 << 1),
            Location = new MacPoint(14, 220),
        }, locked: true);
        disk.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var path = Path.Combine(folder, "Disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var model = new MainViewModel { Shell = new FakeShell() };
        var input = (await model.OpenAsync(path))!;
        var realmz = (FileNode)input.Children.OfType<FolderNode>().Single().Children.Single();
        await realmz.EnsureLoadedAsync();
        var readMe = (FileNode)input.Children.Single(c => c.Title == "Read Me");
        return (model, input, realmz, readMe);
    }

    private static DetailGroup Group(DetailsViewModel details, string title) => details.Groups.Single(g => g.Title == title);

    private static string Value(DetailGroup group, string label) => group.Rows.Single(r => r.Label == label).Value;

    [Fact]
    public async Task A_file_reads_in_five_groups()
    {
        var (model, _, realmz, _) = await Open();
        model.Selected = realmz;
        var details = model.Details;
        Assert.Equal(["File", "Forks", "Dates", "Finder flags", "How it was read"], details.Groups.Select(g => g.Title));
        Assert.Equal([false, false, false, false, true], details.Groups.Select(g => g.Wide));
        Assert.Equal(["FILE", "FORKS", "DATES", "FINDER FLAGS", "HOW IT WAS READ"], details.Groups.Select(g => g.Caption));
        Assert.Equal(details.Groups.Where(g => !g.Wide), details.Cards);
        Assert.Equal(details.Groups.Where(g => g.Wide), details.WideCards);

        var file = Group(details, "File");
        Assert.Equal(["Name", "Kind", "Type / creator", "Mac path", "In"], file.Rows.Select(r => r.Label));
        Assert.Equal(("Realmz", "'APPL' / 'RLMZ'", "Games:Realmz", "Disk.img › Games"),
            (Value(file, "Name"), Value(file, "Type / creator"), Value(file, "Mac path"), Value(file, "In")));
        Assert.True(file.Rows.Single(r => r.Label == "Type / creator").Mono);
        Assert.True(file.Rows.Single(r => r.Label == "Mac path").Mono);
        Assert.Equal([false, false, false, false, true], file.Rows.Select(r => r.Link));             // "In" leads to the folder
        Assert.Equal((true, false), (Group(details, "Forks").Rows[0].HasBar, file.Rows[0].HasBar));
        Assert.Same(realmz.Parent, details.InNode);
    }

    [Fact]
    public async Task Forks_have_bars_relative_to_the_larger_and_the_resources_summed_up()
    {
        var (model, _, realmz, readMe) = await Open();
        model.Selected = realmz;
        var forks = Group(model.Details, "Forks");
        var data = forks.Rows.Single(r => r.Label == "Data fork");
        var resource = forks.Rows.Single(r => r.Label == "Resource fork");
        Assert.Equal("4 bytes", data.Value);
        Assert.True(data.Mono);
        Assert.Equal(1.0, resource.Bar);
        Assert.Equal(4.0 / realmz.File.ResourceFork.Length, data.Bar!.Value, 6);
        Assert.Equal("3 in 2 types, from the resource fork", Value(forks, "Resources"));
        Assert.Equal("'ICN#' 'STR#'", Value(forks, "Holds"));
        Assert.DoesNotContain(forks.Rows, r => r.Label == "Compressed");      // none compressed

        model.Selected = readMe;
        forks = Group(model.Details, "Forks");
        Assert.Equal(0.0, forks.Rows.Single(r => r.Label == "Resource fork").Bar);
        Assert.Equal(1.0, forks.Rows.Single(r => r.Label == "Data fork").Bar);
    }

    [Fact]
    public void Holds_lists_the_first_types_and_how_many_more()
    {
        var fork = new ResourceFork();
        foreach (var type in new[] { "AAAA", "BBBB", "CCCC", "DDDD", "EEEE", "FFFF", "GGGG" })
        {
            fork.Add(new Resource(FourCC.FromString(type), 128, new byte[] { 1 }));
        }

        Assert.Equal("'AAAA' 'BBBB' 'CCCC' 'DDDD' 'EEEE' +2 more", DetailsViewModel.Holds(fork));
    }

    [Fact]
    public void Compressed_resources_are_counted_with_their_decompressors()
    {
        var fork = new ResourceFork();
        byte[] Compressed(byte dcmp) => [0xA8, 0x9F, 0x65, 0x72, 0x00, 0x12, 0x08, 0x01, 0x00, 0x00, 0x01, 0x00, 0, 0, 0, dcmp, 0, 0];
        fork.Add(new Resource(FourCC.FromString("CODE"), 1, Compressed(2)) { Attributes = ResourceAttributes.Compressed });
        fork.Add(new Resource(FourCC.FromString("CODE"), 2, Compressed(2)) { Attributes = ResourceAttributes.Compressed });
        fork.Add(new Resource(FourCC.FromString("snd "), 1, Compressed(0)) { Attributes = ResourceAttributes.Compressed });
        fork.Add(new Resource(FourCC.FromString("STR "), 1, new byte[] { 0 }));
        Assert.Equal("3 resources ('dcmp' 0, 2)", DetailsViewModel.Compression(fork));
        Assert.Null(DetailsViewModel.Compression(new ResourceFork()));
    }

    [Fact]
    public async Task Dates_say_how_the_volume_keeps_time()
    {
        var (model, _, realmz, _) = await Open();
        model.Selected = realmz;
        var dates = Group(model.Details, "Dates");
        Assert.Equal(["Created", "Modified"], dates.Rows.Select(r => r.Label));
        Assert.All(dates.Rows, r => Assert.True(r.Mono));
        Assert.Equal("Mac local time, as stored. No time zone.", dates.Note);
        Assert.Equal("Stored in UTC, shown in your time zone.", DetailsViewModel.DateNote("HFS Plus volume"));
        Assert.Equal("Stored in UTC, shown in your time zone.", DetailsViewModel.DateNote("zip archive"));
        Assert.Equal("Stored in UTC, shown in your time zone.", DetailsViewModel.DateNote("tar archive"));
        Assert.Equal("Mac local time, as stored. No time zone.", DetailsViewModel.DateNote("MFS volume"));
        Assert.Null(DetailsViewModel.DateNote("StuffIt archive"));
    }

    [Fact]
    public async Task Finder_flags_are_chips_with_the_raw_word_the_label_and_the_location()
    {
        var (model, _, realmz, readMe) = await Open();
        model.Selected = realmz;
        var flags = Group(model.Details, "Finder flags");
        Assert.Equal(["Has bundle", "Inited", "Shared", "Invisible", "Locked", "Custom icon", "Stationery", "Alias"], flags.Flags.Select(f => f.Name));
        Assert.Equal([true, true, false, false, true, false, false, false], flags.Flags.Select(f => f.IsSet));
        Assert.Equal(("0x2104", "Hot (2)", "(14, 220)"), (Value(flags, "Raw"), Value(flags, "Label"), Value(flags, "Location")));
        Assert.True(flags.Rows.Single(r => r.Label == "Raw").Mono);

        // Read Me: what the volume has for it (the builder marks files inited), no label, not locked.
        model.Selected = readMe;
        flags = Group(model.Details, "Finder flags");
        var raw = readMe.File.FinderInfo.Flags;
        Assert.Equal($"0x{(ushort)raw:X4}", Value(flags, "Raw"));
        Assert.Equal((raw & FinderFlags.HasBeenInited) != 0, flags.Flags.Single(f => f.Name == "Inited").IsSet);
        Assert.False(flags.Flags.Single(f => f.Name == "Locked").IsSet);
        Assert.False(flags.Flags.Single(f => f.Name == "Has bundle").IsSet);
        Assert.Equal("None", Value(flags, "Label"));
    }

    [Fact]
    public async Task How_it_was_read_is_the_container_chain_and_the_problems()
    {
        var (model, input, realmz, _) = await Open();
        model.Selected = realmz;
        var read = Group(model.Details, "How it was read");
        Assert.Equal(["Disk.img", "HFS volume", "Realmz · both forks"], read.Chain.Select(s => s.Text));
        Assert.Equal([false, false, true], read.Chain.Select(s => s.IsLast));
        Assert.Equal("No problems found in this file", model.Details.ProblemsText);
        Assert.False(model.Details.HasProblems);

        model.DiagnosticsPanel.Add(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "test.odd", "Odd."), realmz.Source, realmz));
        model.DiagnosticsPanel.Add(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Info, "test.info", "Info."), realmz.Source, realmz));
        model.Selected = input;
        model.Selected = realmz;
        Assert.Equal("1 problem", model.Details.ProblemsText);
        Assert.True(model.Details.HasProblems);
        model.DiagnosticsPanel.IsExpanded = false;
        model.ShowProblemsCommand.Execute(null);
        Assert.True(model.DiagnosticsPanel.IsExpanded);
        Assert.Equal(realmz.Source, model.DiagnosticsPanel.Search);

        // A resource: its file's chain and the resource.
        model.Selected = realmz.Children.OfType<ResourceTypeNode>().First().Children[0];
        var chain = Group(model.Details, "How it was read").Chain.Select(s => s.Text).ToList();
        Assert.Equal(["Disk.img", "HFS volume", "Realmz · both forks", "'ICN#' 128"], chain);
    }

    [Fact]
    public async Task Inputs_resources_types_and_folders_have_cards_of_their_own()
    {
        var (model, input, realmz, _) = await Open();
        model.Selected = input;
        var details = model.Details;
        Assert.Equal("Input", details.Groups[0].Title);
        Assert.Equal(["Path", "Read as", "Holds"], details.Groups[0].Rows.Select(r => r.Label));
        Assert.Equal("host file", Value(details.Groups[0], "Read as"));
        Assert.Equal("HFS volume", Value(details.Groups[0], "Holds"));
        Assert.Equal(["Input", "How it was read"], details.Groups.Select(g => g.Title));   // a host file has no Mac dates: no Dates card

        var type = realmz.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "ICN#");
        model.Selected = type.Children[0];
        details = model.Details;
        var resource = Group(details, "Resource");
        Assert.Equal(["Type", "ID", "Name", "Attributes", "Size"], resource.Rows.Select(r => r.Label));
        Assert.Equal(("'ICN#'", "128", "256 bytes"), (Value(resource, "Type"), Value(resource, "ID"), Value(resource, "Size")));
        Assert.True(resource.Rows.Single(r => r.Label == "Type").Mono);

        model.Selected = type;
        Assert.Equal(["Resources", "IDs", "Total size"], Group(model.Details, "Type").Rows.Select(r => r.Label));

        model.Selected = realmz.Parent;
        Assert.Equal(["Kind", "Items"], Group(model.Details, "Folder").Rows.Select(r => r.Label));
    }

    [Fact]
    public async Task Copy_all_puts_every_row_on_the_clipboard_as_plain_lines()
    {
        var (model, _, realmz, _) = await Open();
        var shell = (FakeShell)model.Shell!;
        model.Selected = realmz;
        await model.CopyDetailsCommand.ExecuteAsync(null);
        var lines = shell.Copied!.Split('\n');
        Assert.Contains("Name: Realmz", lines);
        Assert.Contains("Type / creator: 'APPL' / 'RLMZ'", lines);
        Assert.Contains("Finder flags: Has bundle, Inited, Locked", lines);
        Assert.Contains("Read as: Disk.img → HFS volume → Realmz · both forks", lines);
        Assert.Contains("Problems: No problems found in this file", lines);
        Assert.Equal(model.Details.CopyText, shell.Copied);
        Assert.False(model.CopyDetailsCommand.CanExecute(null) && model.Details.Groups.Count == 0);

        model.Selected = null;
        Assert.False(model.CopyDetailsCommand.CanExecute(null));
    }

    [Fact]
    public void The_tab_draws_the_cards_the_chips_and_the_chain() => Headless.OnUiThread(() =>
    {
        var task = Open();
        Headless.Pump(task);
        var (model, _, realmz, _) = task.Result;
        var window = new MainWindow { DataContext = model, Width = 1200, Height = 820 };
        window.Show();
        model.Selected = realmz;
        Headless.Pump(model.PreviewTask);
        model.SelectedTab = 0;
        model.DiagnosticsPanel.IsExpanded = false;                       // room for the cards
        Dispatcher.UIThread.RunJobs();

        var cards = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "DetailCards");
        Assert.IsType<UniformGrid>(cards.ItemsPanelRoot);
        Assert.Equal(["FILE", "FORKS", "DATES", "FINDER FLAGS"], Captions(cards));
        var wide = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "DetailWideCards");
        Assert.Equal(["HOW IT WAS READ"], Captions(wide));

        // Chips: set ones filled, unset ones dashed; the chain's last step highlighted, arrows between the steps.
        var chips = cards.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("chip")).ToList();
        Assert.Equal(8, chips.Count);
        Assert.Equal(["Has bundle", "Inited", "Locked"], chips.Where(c => c.Classes.Contains("set")).Select(c => c.Children.OfType<TextBlock>().Single().Text));
        Assert.All(chips.Where(c => c.Classes.Contains("unset")), c => Assert.False(c.Children.OfType<Border>().Single().IsVisible));
        var steps = wide.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("step")).ToList();
        Assert.Equal(model.Details.Groups[^1].Chain.Count, steps.Count);
        Assert.Equal([.. Enumerable.Repeat(false, steps.Count - 1), true], steps.Select(s => s.Classes.Contains("last")));
        Assert.Equal(steps.Count - 1, wide.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "→" && t.IsVisible));
        Assert.Contains(wide.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("problems") && t.IsVisible && t.Text == "No problems found in this file");

        // Fork bars, mono values, the dates' note.
        Assert.Equal(2, cards.GetVisualDescendants().OfType<ProgressBar>().Count(b => b.IsVisible));
        Assert.Contains(cards.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "'APPL' / 'RLMZ'" && t.Classes.Contains("mono"));
        Assert.Contains(cards.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Mac local time, as stored. No time zone." && t.IsVisible);

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        Assert.True(buttons.Single(b => b.Name == "CopyDetails").IsEffectivelyEnabled);
        var baselines = new List<string>();
        Baselines.Check(window, "details", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        Baselines.Verify(baselines);

        var link = buttons.Single(b => b.Classes.Contains("in") && b.IsVisible);   // In: the folder
        Assert.Equal("Disk.img › Games", link.Content);
        link.Command!.Execute(null);
        Headless.Pump(model.DraftTask);
        Assert.Same(realmz.Parent, model.Selected);
        window.Close();
    });

    private static List<string?> Captions(ItemsControl cards) =>
        cards.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("detail-card-header"))
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>().First().Text).ToList();

    [Fact]
    public async Task In_goes_to_the_parent()
    {
        var (model, _, realmz, _) = await Open();
        model.Selected = realmz;
        await model.GoToInCommand.ExecuteAsync(null);
        Assert.Same(realmz.Parent, model.Selected);
    }
}
