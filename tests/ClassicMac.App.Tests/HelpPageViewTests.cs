using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Documents;

namespace ClassicMac.App.Tests;

// Apple Help pages in the preview (docs/formats/resources/help-pages.md): rendered by a web view from the page made
// ready (pictures inline, links to the disk's files), Rendered | Source, a link to a page of the disk selecting it.
public sealed class HelpPageViewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-help").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0];

    // Help: page (TEXT/hbwr) with a picture, a link to next.htm, a help: link and a link to a missing page; next.htm
    // (TEXT/ttxt); gfx:i.gif. A plain TEXT file beside them is not a help page.
    private string Book()
    {
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var help = disk.Folder(HfsBuilder.Root, "Help");
        var gfx = disk.Folder(help, "gfx");
        disk.File(help, "page", MacRoman.Encode("""
            <html><head><title>A page</title><meta name="AppleTitle" content="The page"></head>
            <body><img src="gfx/i.gif"> Caf<!-- --> <a href="next.htm#s">Next</a> <a href="help:openbook='Mac Help'">Book</a> <a href="gone.htm">Gone</a></body></html>
            """), [], type: "TEXT", creator: "hbwr");
        disk.File(help, "next.htm", "<p>Next page</p>"u8.ToArray(), []);
        disk.File(help, "plain", "just text"u8.ToArray(), []);
        disk.File(gfx, "i.gif", Gif, [], type: "GIFf", creator: "8BIM");
        var path = Path.Combine(folder, "help.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static async Task<(MainViewModel Model, InputNode Input)> Open(string path)
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    private static FolderNode HelpFolder(InputNode input) => input.Children.OfType<FolderNode>().Single(f => f.Title == "Help");

    private static FileNode Child(NodeViewModel parent, string name) => parent.Children.OfType<FileNode>().Single(f => f.Title == name);

    [Fact]
    public async Task A_help_page_previews_as_the_page_made_ready_for_a_web_view()
    {
        var (model, input) = await Open(Book());
        model.Selected = Child(HelpFolder(input), "page");
        await model.PreviewTask;

        Assert.True(model.Preview.IsHelpPage);
        Assert.True(model.Preview.HasPreview);
        var page = model.Preview.Help!;
        Assert.Equal("The page", page.Title);
        Assert.Equal(["Help"], page.Folder);
        Assert.StartsWith("<html><head><title>A page</title>", page.Source);
        Assert.Contains("data:image/gif;base64,", page.Html);
        Assert.Contains($"href=\"{HelpPages.Origin}Help/next.htm#s\" target=\"_top\"", page.Html);
        Assert.Equal(HelpPages.DataUri(page.Html), page.DataUri);
    }

    [Fact]
    public async Task Other_text_files_are_not_help_pages()
    {
        var (model, input) = await Open(Book());
        model.Selected = Child(HelpFolder(input), "plain");
        await model.PreviewTask;
        Assert.False(model.Preview.IsHelpPage);
        model.Selected = Child(HelpFolder(input), "next.htm");                        // an .htm file is one
        await model.PreviewTask;
        Assert.True(model.Preview.IsHelpPage);
        Assert.Equal("next.htm", model.Preview.Help!.Title);
    }

    [Fact]
    public async Task A_link_to_a_page_of_the_disk_selects_it_and_others_say_why_not()
    {
        var (model, input) = await Open(Book());
        var page = Child(HelpFolder(input), "page");
        model.Selected = page;
        await model.PreviewTask;

        Assert.True(model.HelpPreview.FollowHelpLink(model.Preview.Help!.DataUri));                // the page itself loads
        Assert.Null(model.HelpPreview.HelpStatus);

        Assert.False(model.HelpPreview.FollowHelpLink("help:openbook='Mac Help'"));
        Assert.Equal("A Help Viewer command (not followed here): help:openbook='Mac Help'", model.HelpPreview.HelpStatus);
        Assert.Same(page, model.Selected);
        Assert.False(model.HelpPreview.FollowHelpLink("http://www.apple.com/"));
        Assert.StartsWith("An address outside the disk", model.HelpPreview.HelpStatus);
        Assert.False(model.HelpPreview.FollowHelpLink(HelpPages.Origin + "Help/gone.htm"));
        Assert.Equal("Not on the disk: Help:gone.htm", model.HelpPreview.HelpStatus);

        Assert.False(model.HelpPreview.FollowHelpLink(HelpPages.Origin + "Help/next.htm#s"));      // not followed by the web view…
        Assert.Same(Child(HelpFolder(input), "next.htm"), model.Selected);            // …the tree selects it instead
        await model.PreviewTask;
        Assert.Equal("next.htm", model.Preview.Help!.Title);
        Assert.Null(model.HelpPreview.HelpStatus);

        // Hovering a link says where it goes.
        model.HelpPreview.HoverHelpLink(HelpPages.Origin + "Help/page");
        Assert.Equal("Opens Help:page", model.HelpPreview.HelpStatus);
        model.HelpPreview.HoverHelpLink(null);
        Assert.Null(model.HelpPreview.HelpStatus);
    }

    [Fact]
    public async Task Rendered_or_Source_and_the_source_when_there_is_no_web_engine()
    {
        var (model, input) = await Open(Book());
        model.Selected = Child(HelpFolder(input), "page");
        await model.PreviewTask;
        Assert.Equal(["Rendered", "Source"], model.HelpPreview.WebModes);
        Assert.Equal(0, model.HelpPreview.HelpModeIndex);
        Assert.True(model.HelpPreview.ShowsHelpRendered);
        Assert.False(model.HelpPreview.ShowsHelpSource);
        model.HelpPreview.HelpModeIndex = 1;
        Assert.False(model.HelpPreview.ShowsHelpRendered);
        Assert.True(model.HelpPreview.ShowsHelpSource);
        model.HelpPreview.HelpModeIndex = 0;

        model.HelpPreview.WebEngineMessage = "No web engine: install the WebView2 runtime.";
        Assert.False(model.HelpPreview.ShowsHelpRendered);
        Assert.True(model.HelpPreview.ShowsHelpSource);
        Assert.True(model.HelpPreview.HasWebEngineMessage);
    }

    // In a headless window there is no native web view: the page shows as its source, with why.
    [Fact]
    public void The_window_falls_back_to_the_source_without_a_web_view() => Headless.OnUiThread(() =>
    {
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        var open = model.OpenAsync(Book());
        Headless.Pump(open);
        Headless.Pump(open.Result!.EnsureLoadedAsync());
        model.Selected = Child(HelpFolder(open.Result!), "page");
        Headless.Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("This window has no native web view, so the page shows as text.", model.HelpPreview.WebEngineMessage);
        Assert.True(window.Named<ListBox>("HelpMode")!.IsEffectivelyVisible);
        var source = window.Named<SelectableTextBlock>("HelpSource")!;
        Assert.True(source.IsEffectivelyVisible);
        Assert.StartsWith("<html><head>", source.Text);
        Assert.True(window.Named<TextBlock>("HelpEngineMessage")!.IsEffectivelyVisible);
        Assert.False(window.Named<Panel>("HelpHost")!.IsEffectivelyVisible);
        window.Close();
    });
}
