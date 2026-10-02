using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The diagnostics panel (design/boards/diagnostics.md, D1–D3): counts, filters, search, sorting, grouping by file,
// collapsing and the latest problem.
public class DiagnosticsPanelTests
{
    private static DiagnosticEntry Entry(DiagnosticSeverity severity, string source, string code = "test.code", string message = "a message") =>
        new(new Diagnostic(severity, code, message), source, null);

    private static DiagnosticEntry Error(string source, string code = "e.code", string message = "broken") => Entry(DiagnosticSeverity.Error, source, code, message);

    private static DiagnosticEntry Warning(string source, string code = "w.code", string message = "odd") => Entry(DiagnosticSeverity.Warning, source, code, message);

    private static DiagnosticEntry Info(string source, string code = "i.code", string message = "fyi") => Entry(DiagnosticSeverity.Info, source, code, message);

    private static DiagnosticsPanel Panel(params DiagnosticEntry[] entries)
    {
        var panel = new DiagnosticsPanel();
        foreach (var e in entries)
        {
            panel.Add(e);
        }

        return panel;
    }

    [Fact]
    public void Counts_cover_every_diagnostic_whatever_the_filter()
    {
        var panel = Panel(Error("a"), Error("a"), Warning("b"), Info("c"), Info("c"), Info("c"));
        panel.Filter = DiagnosticFilter.Errors;
        Assert.Equal((2, 1, 3), (panel.ErrorCount, panel.WarningCount, panel.InfoCount));
        Assert.Equal(("2 errors", "1 warning", "3 info"), (panel.ErrorText, panel.WarningText, panel.InfoText));
        Assert.Equal((true, true, true), (panel.HasErrors, panel.HasWarnings, panel.HasInfos));
        Assert.Equal((false, false, false), (new DiagnosticsPanel().HasErrors, new DiagnosticsPanel().HasWarnings, new DiagnosticsPanel().HasInfos));
        Assert.Equal("1 error", Panel(Error("a")).ErrorText);
    }

    [Fact]
    public void Counts_are_announced_as_diagnostics_arrive()
    {
        var panel = new DiagnosticsPanel();
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        panel.Add(Warning("a"));
        Assert.Contains(nameof(DiagnosticsPanel.WarningCount), changed);
        Assert.Contains(nameof(DiagnosticsPanel.WarningText), changed);
        Assert.Contains(nameof(DiagnosticsPanel.HasWarnings), changed);
        Assert.Contains(nameof(DiagnosticsPanel.Latest), changed);
    }

    [Theory]
    [InlineData(DiagnosticFilter.All, 3)]
    [InlineData(DiagnosticFilter.WarningsAndErrors, 2)]
    [InlineData(DiagnosticFilter.Errors, 1)]
    public void The_severity_filter_picks_the_entries(DiagnosticFilter filter, int count)
    {
        var panel = Panel(Info("a"), Warning("a"), Error("a"));
        panel.Filter = filter;
        Assert.Equal(count, panel.Entries.Count);
        panel.Add(Info("b"));
        Assert.Equal(filter == DiagnosticFilter.All ? count + 1 : count, panel.Entries.Count);
    }

    [Fact]
    public void The_filter_field_matches_message_code_or_source_ignoring_case()
    {
        var panel = Panel(Error("disk.img › Finder", "archive.fork-crc", "CRC mismatch"), Warning("disk.img › System", "sound.unknown-format", "Unknown format 3"),
            Info("other.sit", "x.y", "nothing"));
        panel.Search = "crc";
        Assert.Equal(["archive.fork-crc"], panel.Entries.Select(e => e.Code));
        panel.Search = "SOUND.";
        Assert.Equal(["sound.unknown-format"], panel.Entries.Select(e => e.Code));
        panel.Search = "disk.img";
        Assert.Equal(2, panel.Entries.Count);
        panel.Add(Info("disk.img › Read Me"));
        panel.Add(Info("elsewhere"));
        Assert.Equal(3, panel.Entries.Count);
        panel.Search = "";
        Assert.Equal(5, panel.Entries.Count);
    }

    [Fact]
    public void By_file_off_is_the_flat_list_in_arrival_order()
    {
        var a = Info("a");
        var b = Error("b");
        var c = Warning("a");
        var panel = Panel(a, b, c);
        panel.ByFile = false;
        Assert.Equal<object>([a, b, c], panel.Rows);
        var d = Error("c");
        panel.Add(d);
        Assert.Equal<object>([a, b, c, d], panel.Rows);
    }

    [Fact]
    public void Sorting_by_severity_cycles_errors_first_info_first_and_arrival()
    {
        var a = Info("a");
        var b = Error("a");
        var c = Warning("a");
        var d = Error("a");
        var panel = Panel(a, b, c, d);
        panel.ByFile = false;
        Assert.Equal(SeveritySort.None, panel.Sort);
        panel.SortBySeverityCommand.Execute(null);
        Assert.Equal(SeveritySort.ErrorsFirst, panel.Sort);
        Assert.Equal<object>([b, d, c, a], panel.Rows);
        var e = Warning("a");
        panel.Add(e);
        Assert.Equal<object>([b, d, c, e, a], panel.Rows);
        panel.SortBySeverityCommand.Execute(null);
        Assert.Equal(SeveritySort.InfoFirst, panel.Sort);
        Assert.Equal<object>([a, c, e, b, d], panel.Rows);
        panel.SortBySeverityCommand.Execute(null);
        Assert.Equal(SeveritySort.None, panel.Sort);
        Assert.Equal<object>([a, b, c, d, e], panel.Rows);
    }

    [Fact]
    public void Grouped_rows_put_each_file_s_diagnostics_under_its_header()
    {
        var a1 = Error("disk.img › Finder");
        var b1 = Warning("disk.img › System");
        var a2 = Warning("disk.img › Finder");
        var panel = Panel(a1, b1, a2);
        Assert.True(panel.ByFile);
        var groups = panel.Groups;
        Assert.Equal(["disk.img › Finder", "disk.img › System"], groups.Select(g => g.Path));
        Assert.Equal<object>([groups[0], a1, a2, groups[1], b1], panel.Rows);
        Assert.Equal("1 error · 1 warning", groups[0].Counts);
        Assert.Equal("1 warning", groups[1].Counts);
        Assert.Equal(NodeKind.Input, groups[0].Kind); // no node: the input itself
    }

    [Fact]
    public void Info_only_groups_start_collapsed_until_a_problem_arrives_or_the_user_opens_them()
    {
        var i1 = Info("x");
        var panel = Panel(i1, Info("x"));
        var group = Assert.Single(panel.Groups);
        Assert.True(group.IsInfoOnly);
        Assert.False(group.IsExpanded);
        Assert.Equal<object>([group], panel.Rows);
        Assert.Equal("2 info", group.Counts);

        var w = Warning("x");
        panel.Add(w);
        Assert.False(group.IsInfoOnly);
        Assert.True(group.IsExpanded);
        Assert.Equal(3, panel.Rows.Count - 1);
        Assert.Same(w, panel.Rows[3]);

        // Toggled by the user, a group keeps its state.
        panel.SelectedRow = group;                    // a click on a header toggles it
        Assert.False(group.IsExpanded);
        Assert.Null(panel.SelectedRow);
        Assert.Equal<object>([group], panel.Rows);
        panel.Add(Error("x"));
        Assert.False(group.IsExpanded);
        Assert.Equal<object>([group], panel.Rows);
        panel.SelectedRow = group;
        Assert.True(group.IsExpanded);
        Assert.Equal(5, panel.Rows.Count);
    }

    [Fact]
    public void Groups_count_and_show_only_what_the_filters_let_through()
    {
        var panel = Panel(Info("info.sit"), Error("bad.img"), Info("bad.img"));
        panel.Filter = DiagnosticFilter.Errors;
        var group = Assert.Single(panel.Rows.OfType<DiagnosticGroup>());
        Assert.Equal("bad.img", group.Path);
        Assert.Equal("1 error", group.Counts);
        Assert.Equal(2, panel.Rows.Count);
        panel.Filter = DiagnosticFilter.All;
        Assert.Equal(["info.sit", "bad.img"], panel.Rows.OfType<DiagnosticGroup>().Select(g => g.Path));
        Assert.Equal("1 error · 1 info", panel.Rows.OfType<DiagnosticGroup>().Last().Counts);
    }

    [Fact]
    public void Grouped_rows_sort_within_each_group()
    {
        var a = Info("f");
        var b = Error("f");
        var panel = Panel(a, b);
        panel.Sort = SeveritySort.ErrorsFirst;
        var group = Assert.Single(panel.Groups);
        Assert.Equal<object>([group, b, a], panel.Rows);
        var c = Warning("f");
        panel.Add(c);
        Assert.Equal<object>([group, b, c, a], panel.Rows);
    }

    [Fact]
    public void Selecting_a_row_selects_its_diagnostic()
    {
        DiagnosticEntry? chosen = null;
        var panel = new DiagnosticsPanel(e => chosen = e);
        var entry = Error("a");
        panel.Add(entry);
        panel.SelectedRow = entry;
        Assert.Same(entry, chosen);
        Assert.Same(entry, panel.SelectedRow);
    }

    [Fact]
    public void Removing_diagnostics_updates_rows_groups_and_counts()
    {
        var keep = Error("keep");
        var panel = Panel(keep, Error("drop"), Warning("drop"));
        panel.RemoveAll(e => e.Source == "drop");
        Assert.Equal((1, 0), (panel.ErrorCount, panel.WarningCount));
        Assert.Equal(["keep"], panel.Groups.Select(g => g.Path));
        Assert.Equal(2, panel.Rows.Count);
        Assert.Same(keep, panel.Latest);
    }

    [Fact]
    public void Latest_is_the_newest_error_or_warning()
    {
        var panel = new DiagnosticsPanel();
        panel.Add(Info("a"));
        Assert.Null(panel.Latest);
        Assert.False(panel.HasLatest);
        var w = Warning("disk.img › Finder", "archive.fork-crc");
        panel.Add(w);
        panel.Add(Info("b"));
        Assert.Same(w, panel.Latest);
        Assert.True(panel.HasLatest);
        Assert.Equal("in disk.img › Finder", panel.LatestPlace);
        panel.Filter = DiagnosticFilter.Errors;               // not affected by the filters
        Assert.Same(w, panel.Latest);
    }

    [Fact]
    public void The_panel_collapses_to_its_header_and_remembers_its_height()
    {
        var panel = new DiagnosticsPanel();
        Assert.True(panel.IsExpanded);
        Assert.Equal(DiagnosticsPanel.DefaultHeight, panel.PanelHeight);
        Assert.Equal(196, DiagnosticsPanel.DefaultHeight);

        panel.PanelHeight = 260;                              // the splitter dragged
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        panel.ToggleCommand.Execute(null);
        Assert.False(panel.IsExpanded);
        Assert.Equal(DiagnosticsPanel.HeaderHeight, panel.PanelHeight);
        Assert.Contains(nameof(DiagnosticsPanel.PanelHeight), changed);
        panel.PanelHeight = 34;                               // the grid writing back the collapsed height
        panel.ToggleCommand.Execute(null);
        Assert.True(panel.IsExpanded);
        Assert.Equal(260, panel.PanelHeight);

        panel.PanelHeight = 20;                               // never below the header
        Assert.Equal(DiagnosticsPanel.HeaderHeight, panel.PanelHeight);
    }

    // Through the main view-model: groups follow the tree's files, a resource's diagnostics go under its file, and a
    // row selects its node.
    [Fact]
    public async Task The_main_view_model_feeds_the_panel_and_selects_through_it()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-diag-").FullName;
        try
        {
            var fork = new ClassicMac.Resources.ResourceFork();
            fork.Add(new ClassicMac.Resources.Resource(FourCC.FromString("STR "), 128, new byte[] { 2, (byte)'h', (byte)'i' }));
            var bytes = fork.ToArray();
            bytes.AsSpan(8, 4).Clear();                       // the resource's data lies outside the data area
            var disk = new HfsBuilder();
            disk.File(HfsBuilder.Root, "Broken", [], bytes);
            var path = Path.Combine(folder, "broken.img");
            File.WriteAllBytes(path, disk.Build("Disk"));
            var model = new MainViewModel();
            var input = (await model.OpenAsync(path))!;
            var broken = Assert.IsType<FileNode>(input.Children.Single(c => c.Title == "Broken"));
            await broken.EnsureLoadedAsync();

            var panel = model.DiagnosticsPanel;
            Assert.Same(panel.Entries, model.Diagnostics);
            var group = panel.Groups.Single(g => g.Path == "broken.img › Broken");
            Assert.Equal(NodeKind.File, group.Kind);
            var entry = group.Entries.First();
            model.Filter = DiagnosticFilter.Errors;
            Assert.Equal(DiagnosticFilter.Errors, panel.Filter);
            model.Filter = DiagnosticFilter.All;

            model.Selected = input;
            panel.SelectedRow = entry;
            Assert.Same(entry, model.SelectedDiagnostic);
            Assert.Same(broken, model.Selected);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_resource_s_diagnostics_group_under_its_file()
    {
        Assert.Equal(("disk.img › Broken", NodeKind.File), DiagnosticsPanel.GroupOf("disk.img › Broken › 'STR ' 128", NodeKind.Resource, "disk.img › Broken", NodeKind.File));
        Assert.Equal(("disk.img › Broken", NodeKind.File), DiagnosticsPanel.GroupOf("disk.img › Broken", NodeKind.File, "disk.img › Broken", NodeKind.File));
        // Found while reading the input, about a file inside it.
        Assert.Equal(("disk.img › Tools:Read Me", NodeKind.File), DiagnosticsPanel.GroupOf("disk.img › Tools:Read Me", NodeKind.Input, "disk.img", NodeKind.Input));
        Assert.Equal(("disk.img", NodeKind.Input), DiagnosticsPanel.GroupOf("disk.img", NodeKind.Input, "disk.img", NodeKind.Input));
    }
}
