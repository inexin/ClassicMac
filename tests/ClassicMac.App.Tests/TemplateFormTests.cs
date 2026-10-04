using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.App.Tests;

// The template form on the read-then-edit host (design/boards/template-form.md, E6): fields as a card, lists of item
// cards with nested lists as compact tables, read-only counts kept in step, the note, and the template panel.
public sealed class TemplateFormTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-tmplform").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A BNDL-like template: owner, ID, a counted list of types, each with a counted list of (local ID, resource ID).
    private static readonly (string, string)[] Bundle =
    [
        ("Owner name", "TNAM"), ("Owner ID", "DWRD"), ("Number of types", "OCNT"), ("*****", "LSTC"),
        ("Type", "TNAM"), ("Number of IDs", "OCNT"), ("*****", "LSTC"), ("Local ID", "DWRD"), ("Resource ID", "DWRD"), ("*****", "LSTE"),
        ("*****", "LSTE"),
    ];

    // ABCD, 0; ICN# with (0,128) (1,129); FREF with (0,128); then 2 bytes the template does not cover.
    private static readonly byte[] Data =
    [
        .. "ABCD"u8, 0, 0, 0, 2,
        .. "ICN#"u8, 0, 2, 0, 0, 0, 0x80, 0, 1, 0, 0x81,
        .. "FREF"u8, 0, 1, 0, 0, 0, 0x80,
        0xEE, 0xEE,
    ];

    private static TemplateForm Form(string source = "Template: TMPL 1000 “BNDL” in ResEdit") =>
        new(new Resource(FourCC.FromString("BNDL"), 128, Data), ResourceTemplate.Parse(EditTests.Tmpl(Bundle)), source, Data);

    [Fact]
    public void Fields_read_as_values_with_their_type_codes()
    {
        var form = Form();
        Assert.True(form.HasReadOnlyView);
        var owner = Assert.IsType<TemplateScalarRow>(form.Fields[0]);
        Assert.Equal(("Owner name", "'ABCD'", "TNAM"), (owner.Label, owner.DisplayText, owner.Type));
        Assert.Equal("0", ((TemplateScalarRow)form.Fields[1]).DisplayText);
        var count = Assert.IsType<TemplateScalarRow>(form.Fields[2]);
        Assert.True(count.IsReadOnly);
        Assert.Equal(("2", "kept in step with the list"), (count.DisplayText, count.CountNote));
        Assert.Null(owner.CountNote);
        Assert.Equal((true, false), (owner.IsEditableText, count.IsEditableText));   // a count never gets an input
    }

    [Fact]
    public void Flags_read_as_yes_or_no()
    {
        byte[] data = [0x01, 0x00];
        var form = new TemplateForm(new Resource(FourCC.FromString("Rsrc"), 1, data), ResourceTemplate.Parse(EditTests.Tmpl(("On", "BOOL"))), "", data);
        var flag = Assert.IsType<TemplateScalarRow>(form.Fields[0]);
        Assert.Equal("Yes", flag.DisplayText);
        flag.Flag = false;
        Assert.Equal("No", flag.DisplayText);
    }

    [Fact]
    public void Lists_have_a_heading_a_count_and_item_cards()
    {
        var form = Form();
        var types = Assert.IsType<TemplateListRow>(form.Fields[3]);
        Assert.Equal(("Number of types", "2 items", "Add Type"), (types.Heading, types.Summary, types.AddLabel));
        Assert.False(types.IsCompact);
        var icn = types.Items[0];
        Assert.Equal(("1)", "'ICN#'", "2 items"), (icn.Title, icn.KeyText, icn.ItemSummary));
        Assert.Equal(("2)", "'FREF'", "1 item"), (types.Items[1].Title, types.Items[1].KeyText, types.Items[1].ItemSummary));

        // The nested list is a compact table: its columns, "Add Local ID".
        var ids = Assert.IsType<TemplateListRow>(icn.Fields[2]);
        Assert.True(ids.IsCompact);
        Assert.Equal(["Local ID", "Resource ID"], ids.Columns);
        Assert.Equal("Add Local ID", ids.AddLabel);
        Assert.Equal(["0", "128"], ids.Items[0].Fields.OfType<TemplateScalarRow>().Select(f => f.DisplayText));
    }

    [Fact]
    public void A_list_without_a_count_is_named_by_its_label()
    {
        byte[] data = [0, 1, 0, 2];
        var form = new TemplateForm(new Resource(FourCC.FromString("Rsrc"), 1, data),
            ResourceTemplate.Parse(EditTests.Tmpl(("Values", "LSTB"), ("Value", "DWRD"), ("*****", "LSTE"))), "", data);
        var list = Assert.IsType<TemplateListRow>(form.Fields[0]);
        Assert.Equal(("Values", "Add Value"), (list.Heading, list.AddLabel));
        var starred = new TemplateForm(new Resource(FourCC.FromString("Rsrc"), 1, data),
            ResourceTemplate.Parse(EditTests.Tmpl(("*****", "LSTB"), ("Value", "DWRD"), ("*****", "LSTE"))), "", data);
        Assert.Equal("Items", Assert.IsType<TemplateListRow>(starred.Fields[0]).Heading);
    }

    [Fact]
    public void Adding_inserting_and_removing_keep_the_counts_and_size_in_step()
    {
        var form = Form();
        var types = (TemplateListRow)form.Fields[3];
        var count = (TemplateScalarRow)form.Fields[2];
        Assert.Equal(Data.Length, form.DraftLength);

        types.AddCommand.Execute(null);
        Assert.Equal(("3", "3 items"), (count.Text, types.Summary));
        Assert.Equal(Data.Length + 6, form.DraftLength);        // a type (4) and its count (2)
        types.InsertCommand.Execute(types.Items[0]);
        Assert.Equal("4", count.Text);
        Assert.Equal("'    '", types.Items[0].KeyText);
        types.RemoveCommand.Execute(types.Items[0]);
        types.RemoveCommand.Execute(types.Items[2]);
        Assert.Equal("2", count.Text);
        Assert.Equal(Data, form.BuildData());

        // A nested list's count and its item's summary follow too.
        var ids = (TemplateListRow)types.Items[1].Fields[2];
        ids.AddCommand.Execute(null);
        Assert.Equal(("2", "2 items"), (((TemplateScalarRow)types.Items[1].Fields[1]).Text, types.Items[1].ItemSummary));
    }

    [Fact]
    public void Editing_reaches_every_row_and_is_not_an_edit()
    {
        var form = Form();
        var edits = 0;
        form.Edited += (_, _) => edits++;
        var types = (TemplateListRow)form.Fields[3];
        var nested = (TemplateListRow)types.Items[0].Fields[2];

        form.IsEditing = true;

        Assert.Equal(0, edits);                                    // the mode is no edit
        Assert.All(form.Fields, f => Assert.True(f.IsEditing));
        Assert.True(nested.IsEditing);
        Assert.True(nested.Items[0].Fields[0].IsEditing);
        types.AddCommand.Execute(null);
        Assert.True(types.Items[^1].Fields[0].IsEditing);          // new items take the mode
        var afterAdd = edits;
        Assert.True(afterAdd > 0);
        form.IsEditing = false;
        Assert.False(types.Items[^1].Fields[0].IsEditing);
        Assert.Equal(afterAdd, edits);
    }

    [Fact]
    public void The_note_says_what_the_template_does_not_cover()
    {
        Assert.Equal("2 bytes after the template's last field are kept.", Form().Note);
        Assert.True(Form().HasNote);
    }

    [Fact]
    public void The_template_panel_shows_where_it_was_found_and_its_fields()
    {
        var form = Form();
        Assert.Equal("Template: TMPL 1000 “BNDL” in ResEdit", form.Source);
        Assert.Equal("The resource’s own file is searched first, then the other open files whose resources are read, in the order they were opened; the first 'TMPL' named for the type is used.",
            TemplateForm.SourceRule);
        Assert.Equal(
        [
            (0, "Owner name", "TNAM"), (0, "Owner ID", "DWRD"), (0, "Number of types", "OCNT"), (0, "*****", "LSTC"),
            (1, "Type", "TNAM"), (1, "Number of IDs", "OCNT"), (1, "*****", "LSTC"), (2, "Local ID", "DWRD"), (2, "Resource ID", "DWRD"),
            (1, "*****", "LSTE"), (0, "*****", "LSTE"),
        ], form.Outline.Select(l => (l.Depth, l.Label, l.Type)));
        Assert.Equal(16, form.Outline[4].Indent);
    }

    // In the model: the template form opens read only; while editing, the header's Size follows the draft.
    [Fact]
    public async Task The_header_size_follows_the_draft_while_editing()
    {
        var path = Path.Combine(folder, "Bundle.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("BNDL", 128, null, Data), ("TMPL", 1000, "BNDL", EditTests.Tmpl(Bundle))));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "BNDL").Children[0];
        await model.PreviewTask;
        var form = Assert.IsType<TemplateForm>(model.Form);
        Assert.True(model.FormEditing.ShowsForm);
        string Size() => model.InspectorActions.Header!.Facts.Single(f => f.Label == "Size").Value;
        Assert.Equal(new InspectorFact("Shown through", "'TMPL' 1000 “BNDL” in Bundle.rsrc", false), model.InspectorActions.Header!.Facts.Single(f => f.Label == "Shown through"));
        Assert.Equal("'TMPL' 1000 “BNDL” in Bundle.rsrc", form.ShownThrough);
        Assert.Equal($"{Data.Length} bytes", Size());

        model.FormEditing.EditFormCommand.Execute(null);
        var changed = new List<string?>();
        model.InspectorActions.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        ((TemplateListRow)form.Fields[3]).AddCommand.Execute(null);
        Assert.Contains(nameof(InspectorActions.Header), changed);
        Assert.Equal($"{Data.Length + 6} bytes", Size());
        model.FormEditing.CancelFormCommand.Execute(null);
        Assert.Equal($"{Data.Length} bytes", Size());
    }

    // In the window: read only, the note banner, item cards, the template panel; Edit shows inputs and the list buttons.
    [Fact]
    public void The_template_form_in_the_window() => Headless.OnUiThread(() =>
    {
        var path = Path.Combine(folder, "Window.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("BNDL", 128, null, Data), ("TMPL", 1000, "BNDL", EditTests.Tmpl(Bundle))));
        var model = new MainViewModel();
        var window = new ClassicMac.App.Views.MainWindow { DataContext = model };
        window.Show();
        var open = model.OpenAsync(path);
        Headless.Pump(open);
        Headless.Pump(open.Result!.EnsureLoadedAsync());
        model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "BNDL").Children[0];
        Headless.Pump(model.PreviewTask);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var cards = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Grid>().Single(g => g.Name == "TemplateCards");
        List<string?> Shown() => [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(cards).OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
        List<string?> Buttons() => [.. Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(cards).OfType<Avalonia.Controls.Button>()
            .Where(b => b.IsEffectivelyVisible).Select(b => b.Content?.ToString())];
        Assert.Contains("2 bytes after the template's last field are kept.", Shown());
        Assert.Contains("'ABCD'", Shown());
        Assert.Contains("kept in step with the list", Shown());
        Assert.Contains("Number of types", Shown());
        Assert.Contains("'ICN#'", Shown());
        Assert.Contains("TEMPLATE", Shown());
        Assert.Contains("Local ID", Shown());
        Assert.Contains("128", Shown());
        Assert.DoesNotContain("Add Type", Buttons());

        model.FormEditing.EditFormCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("Add Type", Buttons());
        Assert.Contains("Add Local ID", Buttons());
        Assert.Contains("Insert before", Buttons());
        Assert.Contains("Remove", Buttons());
        Assert.DoesNotContain("'ABCD'", Shown());
        Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(cards).OfType<Avalonia.Controls.TextBox>(),
            t => t.IsEffectivelyVisible && t.Text == "ABCD");
        window.Close();
    });
}
