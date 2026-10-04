using ClassicMac.App.ViewModels;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The property view (design/boards/property-view.md, P2): a JSON preview without a form reads as labelled values in
// cards, with the JSON one click away.
public sealed class PropertyViewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-props").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void Scalars_become_rows_with_plain_labels()
    {
        var cards = PropertyView.FromJson("""{"display":"1.2b3","bugFix":2,"fontFamilyId":20,"isFinal":true,"locked":false,"note":null}""", "Version");
        var card = Assert.Single(cards);
        Assert.Equal("Version", card.Caption);
        Assert.Equal(["Display", "Bug fix", "Font family ID", "Is final", "Locked", "Note"], card.Rows.Select(r => r.Label));
        Assert.Equal(("1.2b3", false), (card.Rows[0].Value, card.Rows[0].IsMono));     // words as text
        Assert.Equal(("2", true), (card.Rows[1].Value, card.Rows[1].IsMono));           // numbers in mono
        Assert.Equal((true, "Yes"), (card.Rows[3].Flag, card.Rows[3].Value));            // booleans as a dot and Yes / No
        Assert.Equal((false, "No"), (card.Rows[4].Flag, card.Rows[4].Value));
        Assert.Null(card.Rows[0].Flag);
        Assert.Equal("—", card.Rows[5].Value);
    }

    [Fact]
    public void A_name_beside_its_number_reads_as_the_name_with_the_number_raw()
    {
        var row = Assert.Single(Assert.Single(PropertyView.FromJson("""{"font":20,"fontName":"Times"}""", "Run")).Rows);
        Assert.Equal(("Font", "Times", "20"), (row.Label, row.Value, row.Raw));
    }

    [Fact]
    public void Rectangles_and_points_are_coordinates_with_the_size_after()
    {
        var card = Assert.Single(PropertyView.FromJson("""{"bounds":{"top":40,"left":40,"bottom":322,"right":540},"where":{"v":3,"h":4}}""", "Window"));
        Assert.Equal(("Bounds", "40, 40, 322, 540", true, "500 × 282 pixels"), (card.Rows[0].Label, card.Rows[0].Value, card.Rows[0].IsMono, card.Rows[0].Note));
        Assert.Equal(("Where", "3, 4", true), (card.Rows[1].Label, card.Rows[1].Value, card.Rows[1].IsMono));
    }

    [Fact]
    public void Objects_and_lists_of_objects_get_cards_of_their_own()
    {
        var cards = PropertyView.FromJson(
            """{"name":"Geneva","widths":[6,7,8],"empty":[],"style":{"plain":true,"extra":{"kern":1}},"runs":[{"start":0},{"start":6}]}""", "Font");
        Assert.Equal(["Font", "Style", "Style › Extra", "Run 1", "Run 2"], cards.Select(c => c.Caption));
        Assert.Equal([("Name", "Geneva"), ("Widths", "6, 7, 8"), ("Empty", "None")], cards[0].Rows.Select(r => (r.Label, r.Value)));
        Assert.Equal(("Start", "6"), (cards[4].Rows[0].Label, cards[4].Rows[0].Value));
    }

    [Fact]
    public void Long_lists_stop_with_a_note_to_see_the_JSON()
    {
        var json = "{\"items\":[" + string.Join(",", Enumerable.Range(0, 130).Select(i => $"{{\"n\":{i}}}")) + "]}";
        var cards = PropertyView.FromJson(json, "List");
        Assert.Equal(PropertyView.MaxCards + 1, cards.Count);
        Assert.Equal("Item 100", cards[^2].Caption);
        var more = Assert.Single(cards[^1].Rows);
        Assert.Equal("30 more items; see JSON.", more.Value);
    }

    [Fact]
    public void A_row_copies_as_decimal_hex_or_JSON()
    {
        var rows = Assert.Single(PropertyView.FromJson("""{"flags":30730,"name":"Hi","offset":-2}""", "X")).Rows;
        Assert.Equal(("30730", "0x780A", "30730"), (rows[0].Decimal, rows[0].Hex, rows[0].Json));
        Assert.Equal((null, null, "\"Hi\""), (rows[1].Decimal, rows[1].Hex, rows[1].Json));
        Assert.Equal(("-2", "-0x2"), (rows[2].Decimal, rows[2].Hex));
    }

    private sealed class CopyShell : IShell
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

    [Fact]
    public async Task Copy_puts_a_rows_value_on_the_clipboard()
    {
        var shell = new CopyShell();
        var model = new MainViewModel();
        model.ShellActions.Shell = shell;
        Assert.False(model.PropertyLinks.CopyPropertyCommand.CanExecute(null));
        await model.PropertyLinks.CopyPropertyCommand.ExecuteAsync("0x780A");
        Assert.Equal("0x780A", shell.Copied);
    }

    [Fact]
    public void Text_that_is_not_JSON_has_no_cards() => Assert.Empty(PropertyView.FromJson("not json", "X"));

    [Fact]
    public async Task A_style_resource_previews_as_properties_with_a_JSON_toggle()
    {
        var path = Path.Combine(folder, "Styles.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("styl", 128, null, PreviewTests.Styl((0, 20, 1, 18, 0, 0, 0), (6, 4, 0, 10, 0xFFFF, 0, 0)))));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single().Children[0];
        await model.PreviewTask;

        Assert.True(model.Preview.IsJson);
        Assert.False(model.PropertyLinks.ShowJson);                                       // Properties by default
        Assert.True(model.PropertyLinks.ShowsProperties);
        Assert.False(model.PropertyLinks.ShowsJsonText);
        Assert.Equal(["Run 1", "Run 2"], model.Preview.PropertyCards.Select(c => c.Caption));
        Assert.True(model.Preview.HasProperties);
        var font = model.Preview.PropertyCards[0].Rows.Single(r => r.Label == "Font");
        Assert.Equal(("Times", "20"), (font.Value, font.Raw));

        Assert.Equal(["Properties", "JSON"], PropertyLinks.PropertyModes);
        Assert.Equal(0, model.PropertyLinks.PropertyModeIndex);
        model.PropertyLinks.PropertyModeIndex = 1;                                        // JSON one click away
        Assert.True(model.PropertyLinks.ShowJson);
        Assert.False(model.PropertyLinks.ShowsProperties);
        Assert.True(model.PropertyLinks.ShowsJsonText);
        Assert.StartsWith("{", model.Preview.Text);

        model.Selected = input.Children.OfType<ResourceTypeNode>().Single();  // the choice holds for the session
        await model.PreviewTask;
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single().Children[0];
        await model.PreviewTask;
        Assert.True(model.PropertyLinks.ShowJson);
        Assert.True(model.PropertyLinks.ShowsJsonText);
    }
}
