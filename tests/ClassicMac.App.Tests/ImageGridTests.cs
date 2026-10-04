using ClassicMac.App.ViewModels;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The image preview (design/boards/main-window.md, P1): a card per image with its type and size, an icon's whole family
// of members, its masks on request, the Finder states strip, the summary line, and rows of cards for a virtualising list.
public sealed class ImageGridTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-grid").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A 32 × 32 1-bit icon and mask: a filled square in a solid mask.
    internal static byte[] IconAndMask() => [.. Enumerable.Repeat((byte)0x0F, 128), .. Enumerable.Repeat((byte)0xFF, 128)];

    private async Task<(MainViewModel Model, InputNode Input)> Open(params (string Type, short Id, string? Name, byte[] Data)[] resources)
    {
        var path = Path.Combine(folder, "Icons.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(resources));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    private static async Task Select(MainViewModel model, InputNode input, string type, short id)
    {
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>()
            .Single(r => r.Resource.Id == id);
        await model.PreviewTask;
    }

    // ICN#, icl8 and ics# 128 (one family) and an ICN# 129 alone.
    private Task<(MainViewModel Model, InputNode Input)> Family() => Open(
        ("ICN#", 128, null, IconAndMask()), ("icl8", 128, null, Enumerable.Repeat((byte)0xE3, 1024).ToArray()),
        ("ics#", 128, null, [.. Enumerable.Repeat((byte)0x0F, 32), .. Enumerable.Repeat((byte)0xFF, 32)]), ("ICN#", 129, null, IconAndMask()));

    [Fact]
    public async Task An_icon_shows_its_family_as_cards_with_types_and_sizes()
    {
        var (model, input) = await Family();
        await Select(model, input, "icl8", 128);
        var preview = model.Preview;
        Assert.Equal(["'ICN#'", "'icl8'", "'ics#'"], preview.Images.Select(i => i.Title));
        Assert.Equal(["32×32 · 1-bit", "32×32 · 8-bit", "16×16 · 1-bit"], preview.Images.Select(i => i.Detail));
        Assert.Equal("3 members · 4× · nearest neighbour · 32-bit screen", model.ImageGrid.ImageSummary);

        await Select(model, input, "ICN#", 129);                              // alone: one member
        Assert.Equal(["'ICN#'"], model.Preview.Images.Select(i => i.Title));
        Assert.StartsWith("1 member · ", model.ImageGrid.ImageSummary);
    }

    [Fact]
    public async Task Masks_show_on_request()
    {
        var (model, input) = await Family();
        await Select(model, input, "ICN#", 128);
        Assert.True(model.ImageGrid.HasMasks);
        Assert.False(model.ImageGrid.ShowMasks);
        Assert.Equal(3, model.Images.Count);
        model.ImageGrid.ShowMasks = true;
        Assert.Equal(["'ICN#'", "'ICN#' mask", "'icl8'", "'ics#'", "'ics#' mask"], model.Images.Select(i => i.Image.Title));
        Assert.Equal((32, 32), (model.Images[1].Image.Width, model.Images[1].Image.Height));
    }

    [Fact]
    public async Task The_Finder_states_are_twelve_labelled_icons_at_1x_or_2x()
    {
        var (model, input) = await Family();
        await Select(model, input, "ICN#", 128);
        Assert.True(model.ImageGrid.HasFinderStates);
        Assert.True(model.ImageGrid.ShowFinderStates);
        Assert.Equal(["Normal", "Selected", "Disabled", "Offline", "Open", "Essential", "Hot", "In Progress", "Cool", "Personal", "Project 1", "Project 2"],
            model.Preview.FinderStates.Select(s => s.Caption));
        Assert.All(model.Preview.FinderStates, s => Assert.Equal((32, 32), (s.Width, s.Height)));
        Assert.Equal(4, model.Zoom);
        Assert.All(model.ImageGrid.FinderStateItems, s => Assert.Equal(2, s.Zoom));      // 2× when the zoom is 2 or more
        model.Zoom = 1;
        Assert.All(model.ImageGrid.FinderStateItems, s => Assert.Equal(1, s.Zoom));
        model.ImageGrid.ShowFinderStates = false;
        Assert.DoesNotContain(model.ImageGrid.ImageRows, r => r is FinderStatesRow);
        model.ImageGrid.ShowFinderStates = true;
        Assert.IsType<FinderStatesRow>(model.ImageGrid.ImageRows[^1]);
        model.ImageGrid.ImageViewportWidth = 300;                                       // the strip wraps inside the margins
        Assert.Equal(276, ((FinderStatesRow)model.ImageGrid.ImageRows[^1]).MaxWidth);
    }

    [Fact]
    public async Task Other_images_have_no_family_masks_or_states()
    {
        var (model, input) = await Open(("PICT", 128, null, PreviewTests.Picture));
        await Select(model, input, "PICT", 128);
        Assert.Equal(("'PICT'", "4×4"), (model.Preview.Images[0].Title, model.Preview.Images[0].Detail));
        Assert.False(model.ImageGrid.HasMasks);
        Assert.False(model.ImageGrid.HasFinderStates);
        Assert.Equal("1 image · 4× · nearest neighbour · 32-bit screen", model.ImageGrid.ImageSummary);
    }

    [Fact]
    public async Task Hundreds_of_images_are_laid_out_in_rows_for_the_viewport()
    {
        // 500 small icons in one 'SICN'.
        var (model, input) = await Open(("SICN", 128, null, Enumerable.Range(0, 500 * 32).Select(i => (byte)(i * 7)).ToArray()));
        await Select(model, input, "SICN", 128);
        Assert.Equal(500, model.Images.Count);
        Assert.Equal(("'SICN' #1", "16×16 · 1-bit"), (model.Images[0].Image.Title, model.Images[0].Image.Detail));
        Assert.StartsWith("500 images · ", model.ImageGrid.ImageSummary);
        var card = model.ImageGrid.ImageCardWidth;
        Assert.True(card >= 16 * model.Zoom + 24);

        model.ImageGrid.ImageViewportWidth = 24 + 4 * (card + 12);                      // four cards, their gaps and the margins
        var rows = model.ImageGrid.ImageRows.OfType<ImageRow>().ToList();
        Assert.Equal(125, rows.Count);
        Assert.All(rows, r => Assert.Equal(4, r.Items.Count));
        Assert.Equal(model.Images[4], rows[1].Items[0]);
        model.ImageGrid.ImageViewportWidth = 10;                                        // narrower than a card: one a row
        Assert.Equal(500, model.ImageGrid.ImageRows.Count);
    }
}
