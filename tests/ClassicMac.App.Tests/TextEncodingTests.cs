using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// View ▸ Text Encoding (docs/formats/codecs/text-encodings.md §5): names and text in the chosen Mac encoding, kept
// between sessions; names already in the tree are retitled.
public sealed class TextEncodingTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-encoding-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static readonly byte[] Nihongo = [0x93, 0xFA, 0x96, 0x7B];             // 日本 in Mac OS Japanese

    private string Disk()
    {
        var disk = new HfsBuilder();
        var docs = disk.Folder(HfsBuilder.Root, MacRoman.Decode(Nihongo));
        disk.File(docs, MacRoman.Decode(Nihongo), [.. Nihongo, 0x0D], [], "TEXT", "ttxt");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    [Fact]
    public async Task Names_and_text_follow_the_chosen_encoding_and_it_is_kept()
    {
        var settings = new MemorySettingsStore();
        var model = new MainViewModel(settings);
        var input = (await model.OpenAsync(Disk()))!;
        var docs = input.Children.Single(n => n.Kind == NodeKind.Folder);
        Assert.NotEqual("日本", docs.Title);                                        // Mac OS Roman by default

        model.TextEncoding = MacTextEncoding.Japanese;

        Assert.Equal("日本", docs.Title);                                           // retitled in place
        await docs.EnsureLoadedAsync();
        var file = docs.Children.Single();
        Assert.Equal("日本", file.Title);
        model.Selected = file;
        await model.PreviewTask;
        Assert.StartsWith("日本", model.Preview.Text, StringComparison.Ordinal);
        Assert.Equal("x-mac-japanese", settings.Load().TextEncoding);

        var again = new MainViewModel(settings);                                    // the next session
        Assert.Equal(MacTextEncoding.Japanese, again.TextEncoding);
        var reopened = (await again.OpenAsync(Disk()))!;
        Assert.Equal("日本", reopened.Children.Single(n => n.Kind == NodeKind.Folder).Title);
    }

    [Fact]
    public void The_menu_offers_every_encoding_and_checks_the_chosen_one()
    {
        var model = new MainViewModel();

        Assert.Equal(Enum.GetValues<MacTextEncoding>().Length, model.ShellActions.TextEncodingChoices.Count);
        Assert.Equal(("Mac OS Roman", MacTextEncoding.Roman), (model.ShellActions.TextEncodingChoices[0].Label, model.ShellActions.TextEncodingChoices[0].Encoding));
        model.ShellActions.SetTextEncodingCommand.Execute(MacTextEncoding.Korean);
        Assert.Equal(MacTextEncoding.Korean, model.TextEncoding);
    }
    // A file whose volume says its encoding (here an HFS Plus hint, text-encodings.md §5) previews in it while the app's
    // encoding is the default Mac OS Roman; another encoding chosen wins.
    [Fact]
    public async Task A_file_previews_in_its_volume_s_encoding()
    {
        var builder = new HfsPlusBuilder();
        var fork = new ClassicMac.Resources.ResourceFork();
        byte[] text = [4, .. Nihongo];
        fork.Add(new ClassicMac.Resources.Resource(FourCC.FromString("STR "), 128, text));
        var note = builder.File(HfsPlusBuilder.Root, "Note", [], fork.ToArray());
        builder.TextEncoding(note, 1);
        var path = Path.Combine(folder, "plus.img");
        File.WriteAllBytes(path, builder.Build("Plus"));
        var model = new MainViewModel(new MemorySettingsStore());
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.Single(n => n.Title == "Note");
        await file.EnsureLoadedAsync();
        var str = file.Children.OfType<ResourceTypeNode>().Single().Children.Single();
        await str.EnsureLoadedAsync();

        model.Selected = str;
        await model.PreviewTask;
        Assert.Contains("日本", model.Preview.Text, StringComparison.Ordinal);

        model.TextEncoding = MacTextEncoding.Greek;
        model.Selected = file;
        model.Selected = str;
        await model.PreviewTask;
        Assert.DoesNotContain("日本", model.Preview.Text, StringComparison.Ordinal);
    }
}
