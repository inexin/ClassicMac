using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

public class PalettePreviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-palette-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public async Task Palettes_preview_as_swatches()
    {
        // A colour table of 18 entries: two rows of 16-pixel swatches.
        byte[] clut = [0, 0, 0, 0, 0, 0, 0, 17, .. Enumerable.Range(0, 18).SelectMany(i => new byte[] { 0, (byte)i, (byte)(i * 14), 0, 0, 0, 0, 0 })];
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Colours", [], PreviewTests.Fork(("clut", 128, null, clut)));
        var path = Path.Combine(folder, "c.img");
        File.WriteAllBytes(path, disk.Build("C"));
        var model = new MainViewModel();
        var file = (FileNode)(await model.OpenAsync(path))!.Children.Single();
        await file.EnsureLoadedAsync();

        model.Selected = InterfacePreviewTests.Resource(file, "clut", 128);
        await model.PreviewTask;

        var image = Assert.Single(model.Preview.Images);
        Assert.Equal((256, 32, "18 colours"), (image.Width, image.Height, image.Caption));
        Assert.Equal(("'clut'", "18 colours"), (image.Title, image.CardDetail)); // the card: type, then the count
    }
}
