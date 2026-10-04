using ClassicMac.App.ViewModels;
using ClassicMac.Core;

namespace ClassicMac.App.Tests;

// The font family preview (design/boards/font-family.md, P6): a sample drawn from the family's strikes, the association
// matrix, the metrics, style and kerning cards; JSON one click away.
public sealed class FontFamilyTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-fond").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private async Task<(MainViewModel Model, InputNode Input, FontFamilyPreview Font)> Open(byte[]? fork = null)
    {
        var path = Path.Combine(folder, "Fonts.rsrc");
        File.WriteAllBytes(path, fork ?? FontFixtures.Fork());
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = Resource(input, "FOND", 128);
        await model.PreviewTask;
        Assert.True(model.Preview.IsFontFamily);
        return (model, input, model.Preview.FontFamily!);
    }

    private static ResourceNode Resource(InputNode input, string type, short id) =>
        input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    private static FontStyleChoice Style(FontFamilyPreview font, string label) => font.Styles.Single(s => s.Label == label);

    [Fact]
    public async Task The_sizes_and_styles_come_from_the_association_table()
    {
        var (_, _, font) = await Open();
        Assert.Equal(["9", "12", "14", "TrueType"], font.Sizes.Select(s => s.Label));
        Assert.Equal("12", font.ChosenSize!.Label);
        Assert.Equal(["Plain", "Bold", "Italic (QuickDraw)", "Bold Italic (QuickDraw)", "Bold Condensed"], font.Styles.Select(s => s.Label));
        Assert.Equal("Plain", font.ChosenStyle!.Label);

        // At 9 pt only plain has a strike: the others are QuickDraw's.
        font.ChosenSize = font.Sizes[0];
        Assert.Equal(["Plain", "Bold (QuickDraw)", "Italic (QuickDraw)", "Bold Italic (QuickDraw)", "Bold Condensed (QuickDraw)"], font.Styles.Select(s => s.Label));
        Assert.Equal("Plain", font.ChosenStyle!.Label);
    }

    [Fact]
    public async Task The_sample_is_drawn_from_the_chosen_strike()
    {
        var (_, _, font) = await Open();
        Assert.Equal("The quick brown fox jumps over the lazy dog 0123456789", font.SampleText);
        Assert.Equal("From 'NFNT' 1002 · 12 pt plain · 1-bit", font.SampleSource);
        Assert.Equal(("NFNT", (short)1002), (font.SampleLink!.Type, font.SampleLink.Id));
        Assert.Null(font.SampleMessage);
        var plain = font.Sample!;
        Assert.Equal(12, plain.Height);                                   // ascent 9 + descent 3
        Assert.Contains(plain.Pixels, p => p == 0xFF000000);

        font.SampleText = "AV";
        var av = font.Sample!;
        Assert.Equal(2 * 6, av.Width);                                    // two 6-pixel glyphs
        Assert.Equal(0xFF000000, av.Pixel(0, 0));
        Assert.Equal(0u, av.Pixel(5, 0));                                 // each glyph's blank last column
        Assert.Equal(0u, av.Pixel(0, 9));                                 // below the baseline

        // Bold has its own strike; italic is QuickDraw's, from the plain strike.
        font.ChosenStyle = Style(font, "Bold");
        Assert.Equal("From 'NFNT' 1003 · 12 pt bold · 1-bit", font.SampleSource);
        Assert.Equal(2 * 7, font.Sample!.Width);
        font.ChosenStyle = Style(font, "Italic (QuickDraw)");
        Assert.Equal("From 'NFNT' 1002 · 12 pt plain, italic by QuickDraw · 1-bit", font.SampleSource);
        Assert.NotEqual(av.Pixels, font.Sample!.Pixels);

        // Characters outside Mac OS Roman draw as the strike's missing symbol (a hollow box).
        font.ChosenStyle = Style(font, "Plain");
        font.SampleText = "日";
        Assert.Equal(5, font.Sample!.Width);
        Assert.Equal(0xFF000000, font.Sample.Pixel(0, 0));
        Assert.Equal(0u, font.Sample.Pixel(1, 1));
    }

    [Fact]
    public async Task Sizes_without_a_strike_and_TrueType_say_so()
    {
        var (_, _, font) = await Open();
        font.ChosenSize = font.Sizes.Single(s => s.Label == "14");
        Assert.Null(font.Sample);
        Assert.Equal("'NFNT' 1004 is not in the open files", font.SampleMessage);
        font.ChosenSize = font.Sizes.Single(s => s.Label == "TrueType");
        Assert.Null(font.Sample);
        Assert.Equal("No preview for outline fonts yet", font.SampleMessage);
        Assert.Equal("From 'sfnt' 1005", font.SampleSource);
        Assert.Equal(("sfnt", (short)1005), (font.SampleLink!.Type, font.SampleLink.Id));
    }

    [Fact]
    public async Task A_family_with_no_strikes_says_so_and_still_lists_them()
    {
        var (_, _, font) = await Open(PreviewTests.Fork(("FOND", 128, "Tester", FontFixtures.Family(withTables: false))));
        Assert.Null(font.Sample);
        Assert.Equal("This family has no strikes in the open files", font.SampleMessage);
        Assert.Equal(4, font.Rows.Count);
        Assert.All(font.Rows.SelectMany(r => r.Cells).Where(c => !c.IsEmpty), c => Assert.True(c.IsMissing));
        Assert.Equal("No kerning", font.KerningSummary);
        Assert.Empty(font.KernPairs);
    }

    [Fact]
    public async Task The_association_matrix_has_sizes_down_and_styles_across()
    {
        var (_, _, font) = await Open();
        Assert.Equal(["Plain", "Bold", "Italic", "Bold Italic", "Bold Condensed"], font.Columns);
        Assert.Equal(["9", "12", "14", "TrueType"], font.Rows.Select(r => r.Label));
        string Cell(FontAssociationCell c) => c.IsEmpty ? "—" : $"{c.Text}{(c.IsMissing ? "!" : "")}{(c.Depth is { } d ? " " + d : "")}";
        Assert.Equal(["NFNT 1001", "—", "—", "—", "—"], font.Rows[0].Cells.Select(Cell));
        Assert.Equal(["NFNT 1002", "NFNT 1003", "—", "—", "NFNT 1006!"], font.Rows[1].Cells.Select(Cell));
        Assert.Equal(["NFNT 1004! 4-bit", "—", "—", "—", "—"], font.Rows[2].Cells.Select(Cell));
        Assert.Equal(["sfnt 1005", "—", "—", "—", "—"], font.Rows[3].Cells.Select(Cell));
        Assert.Equal("Not found in the open files", font.Rows[1].Cells[4].ToolTip);
        Assert.Null(font.Rows[1].Cells[0].ToolTip);
    }

    [Fact]
    public async Task A_cell_selects_its_resource()
    {
        var (model, input, font) = await Open();
        model.PropertyLinks.SelectFontResourceCommand.Execute(font.Rows[1].Cells[1]);
        Assert.Same(Resource(input, "NFNT", 1003), model.Selected);

        // A missing one cannot be selected.
        model.Selected = Resource(input, "FOND", 128);
        await model.PreviewTask;
        font = model.Preview.FontFamily!;
        Assert.False(model.PropertyLinks.SelectFontResourceCommand.CanExecute(font.Rows[1].Cells[4]));
        Assert.False(model.PropertyLinks.SelectFontResourceCommand.CanExecute(font.Rows[0].Cells[1]));
        model.PropertyLinks.SelectFontResourceCommand.Execute(font.SampleLink);
        Assert.Same(Resource(input, "NFNT", 1002), model.Selected);
    }

    [Fact]
    public async Task The_metrics_are_in_ems_and_pixels_at_the_chosen_size()
    {
        var (_, _, font) = await Open();
        string Row(string label) => font.Metrics.Single(r => r.Label == label) is var r ? $"{r.Value} · {r.Note}" : "";
        Assert.Equal("0.750 em · 9 px at 12 pt", Row("Ascent"));
        Assert.Equal("-0.250 em · -3 px at 12 pt", Row("Descent"));
        Assert.Equal("0.063 em · 1 px at 12 pt", Row("Leading"));
        Assert.Equal("1.000 em · 12 px at 12 pt", Row("Max width"));
        Assert.Equal("$20–$7E", font.Metrics.Single(r => r.Label == "First and last character").Value);
        Assert.Equal(["Has width tables", "Fixed width"], font.Flags);
        Assert.Equal("0x8002", font.FlagsRaw);
        font.ChosenSize = font.Sizes[0];
        Assert.Equal("0.750 em · 7 px at 9 pt", Row("Ascent"));
    }

    // A family whose tables run past its data is read as far as it goes, and says so.
    [Fact]
    public async Task A_short_family_says_it_was_read_as_far_as_the_data_goes()
    {
        var (_, _, whole) = await Open();
        Assert.False(whole.IsShort);
        var family = FontFixtures.Family();
        var (_, _, font) = await Open(PreviewTests.Fork(("FOND", 128, "Tester", family[..(54 + 6 * 6 + 6 + 4 * 3)])));
        Assert.True(font.IsShort);
        Assert.Equal("1 table · Plain 3 pairs", font.KerningSummary);
    }

    [Fact]
    public async Task The_style_card_has_the_extras_width_tables_and_the_names()
    {
        var (_, _, font) = await Open();
        Assert.Equal(["Plain", "Bold", "Italic", "Underline", "Outline", "Shadow", "Condense", "Extend"], font.StyleExtras.Select(e => e.Label));
        Assert.Equal(["0", "+0.031", "0", "0", "+0.063", "+0.063", "-0.016", "+0.016"], font.StyleExtras.Select(e => e.Value));
        Assert.Equal([true, false, true, true, false, false, false, false], font.StyleExtras.Select(e => e.IsZero));
        Assert.Equal("None", font.WidthTables);
        Assert.Equal("1", font.FontClass);
        Assert.Equal([("Plain", "Tester"), ("Bold", "Tester-Bold"), ("Italic", "Tester-Italic"), ("Bold Italic", "Tester-BoldItalic")],
            font.StyleNames.Select(n => (n.Style, n.Name)));
    }

    [Fact]
    public async Task Kerning_lists_the_strongest_pairs_and_highlights_one()
    {
        var (_, _, font) = await Open();
        Assert.Equal("1 table · Plain 10 pairs", font.KerningSummary);
        Assert.Equal(8, font.KernPairs.Count);
        Assert.Equal("Show all 10", font.ShowAllPairsLabel);
        Assert.Equal(["AV", "VA", "TA", "To", "Vo", "AT", "oV", "oA"], font.KernPairs.Select(p => p.Pair));
        Assert.Equal(("-0.125 em", "-2 px"), (font.KernPairs[0].Em, font.KernPairs[0].Pixels));
        Assert.Equal("$41 $56", font.KernPairs[0].Codes);
        font.ShowAllPairsCommand.Execute(null);
        Assert.Equal(10, font.KernPairs.Count);
        Assert.Null(font.ShowAllPairsLabel);

        // A pair highlights where it first appears in the sample (glyphs 6 pixels wide).
        font.SampleText = "ToAV";
        Assert.Null(font.Highlight);
        font.SelectedPair = font.KernPairs.Single(p => p.Pair == "AV");
        Assert.Equal((12, 12), (font.Highlight!.Value.X, font.Highlight.Value.Width));
        font.SelectedPair = font.KernPairs.Single(p => p.Pair == "oA");
        Assert.Equal((6, 12), (font.Highlight!.Value.X, font.Highlight.Value.Width));
        font.SelectedPair = font.KernPairs.Single(p => p.Pair == "VA");
        Assert.Null(font.Highlight);                                     // not in the sample
    }

    [Fact]
    public async Task The_header_has_the_family_s_facts_and_an_Aa_tile()
    {
        var (model, input, _) = await Open();
        var header = model.Header!;
        Assert.Equal("Font family in Fonts.rsrc", header.Kind);
        Assert.Equal(["Type", "Family ID", "Version", "Strikes", "Fixed width"], header.Facts.Select(f => f.Label));
        Assert.Equal(["'FOND'", "128", "0", "5 bitmap, 1 TrueType", "Yes"], header.Facts.Select(f => f.Value));
        await model.HeaderIconTask;
        using var icon = SkiaSharp.SKBitmap.Decode(model.HeaderIconPng!);
        Assert.Equal((11, 12), (icon.Width, icon.Height));                // "Aa" in the 12 pt strike (the largest); no "a": the missing symbol
    }

    [Fact]
    public async Task The_JSON_is_one_click_away()
    {
        var (model, _, _) = await Open();
        Assert.True(model.PropertyLinks.ShowsFontFamily);
        Assert.False(model.PropertyLinks.ShowsFontJson);
        Assert.Contains("\"fonts\"", model.Preview.Text);
        model.PropertyLinks.PropertyModeIndex = 1;
        Assert.False(model.PropertyLinks.ShowsFontFamily);
        Assert.True(model.PropertyLinks.ShowsFontJson);
        Assert.True(model.Preview.IsZoomable);
        model.PropertyLinks.PropertyModeIndex = 0;
    }

    [Fact]
    public async Task A_family_that_cannot_be_read_previews_as_JSON_or_nothing()
    {
        var path = Path.Combine(folder, "Bad.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("FOND", 128, "Bad", [0, 1, 2])));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = Resource(input, "FOND", 128);
        await model.PreviewTask;
        Assert.False(model.Preview.IsFontFamily);
        Assert.Null(model.Preview.FontFamily);
    }
}
