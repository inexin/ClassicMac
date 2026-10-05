using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.Tests;

// Export ▸ Images as WebP and Export ▸ Loadable Fonts (output/webp.md, outline-fonts.md §3.1): the exports write images
// as lossless WebP and TrueType fonts made loadable; both off by default, kept between sessions; previews unchanged.
public sealed class ExportFormatTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-exportformat").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void The_choices_are_off_by_default_and_kept()
    {
        var settings = new MemorySettingsStore();
        var model = new MainViewModel(settings);
        Assert.False(model.WebPImages);
        Assert.False(model.LoadableFonts);
        Assert.Same(PngEncoder.Instance, model.ExportActions.CurrentDecodeOptions.ImageEncoder);
        Assert.False(model.ExportActions.CurrentDecodeOptions.LoadableFonts);

        model.WebPImages = true;
        model.LoadableFonts = true;

        Assert.Same(WebPEncoder.Instance, model.ExportActions.CurrentDecodeOptions.ImageEncoder);
        Assert.True(model.ExportActions.CurrentDecodeOptions.LoadableFonts);
        Assert.Equal(("webp", true), (settings.Load().ImageFormat, settings.Load().LoadableFonts));
        var again = new MainViewModel(settings);                                             // the next session
        Assert.Equal((true, true), (again.WebPImages, again.LoadableFonts));
    }

    // Extract All writes the icon as WebP and the font made loadable; the preview stays PNG.
    [Fact]
    public async Task Extract_all_writes_WebP_images_and_loadable_fonts()
    {
        var fork = new ResourceFork();
        var icon = new byte[256];
        icon[0] = 0xF0;
        fork.Add(new Resource(FourCC.FromString("ICN#"), 128, icon));
        var sfnt = ClassicMac.Graphics.Tests.Fonts.TrueTypeBuilder.Mac().Build();
        fork.Add(new Resource(FourCC.FromString("sfnt"), 128, sfnt));
        var path = Path.Combine(folder, "Things.rsrc");
        File.WriteAllBytes(path, fork.ToArray());
        var model = new MainViewModel(new MemorySettingsStore()) { FilePicker = new EditTestsBase.Picker(folder) };
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.WebPImages = true;
        model.LoadableFonts = true;

        model.Selected = input;
        await model.ExportActions.ExtractAllCommand.ExecuteAsync(null);

        var target = Directory.GetDirectories(folder).Single();
        Assert.True(File.Exists(Path.Combine(target, "ICN#", "128.webp")));
        Assert.Equal(ClassicMac.Graphics.Fonts.LoadableFont.Make(sfnt), File.ReadAllBytes(Path.Combine(target, "sfnt", "128.ttf")));
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "ICN#").Children[0];
        await model.PreviewTask;
        Assert.NotEmpty(model.Preview.Images);
    }
}
