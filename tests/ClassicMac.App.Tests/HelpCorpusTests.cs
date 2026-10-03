using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders;

namespace ClassicMac.App.Tests;

// Mac OS 9's help books, read from a disk image the tests do not hold (Apple's files are never committed):
// CLASSICMAC_HELP_CORPUS names an HFS image of a Mac OS 9 startup disk. Every page of System Folder:Help is made ready
// for the web view; the facts are constants.
public sealed class HelpCorpusTests
{
    [Fact]
    public async Task Mac_OS_9_s_help_books_render()
    {
        var image = Environment.GetEnvironmentVariable("CLASSICMAC_HELP_CORPUS");
        if (string.IsNullOrEmpty(image))
        {
            Assert.Skip("Set CLASSICMAC_HELP_CORPUS to a Mac OS 9 startup disk image to run this.");
        }

        var model = new MainViewModel();
        var input = (await model.OpenAsync(image))!;
        await input.EnsureLoadedAsync();
        var help = HelpPagePreview.Find(input, ["System Folder", "Help"])!;
        var pages = new List<FileNode>();
        void Walk(NodeViewModel node)
        {
            foreach (var child in Tree.Contents(node))
            {
                if (child is FileNode file && HelpPagePreview.Applies(file))
                {
                    pages.Add(file);
                }

                Walk(child);
            }
        }

        Walk(help);
        var diagnostics = new List<Diagnostic>();
        var rendered = pages.Select(p => HelpPagePreview.Create(p, DecodeOptions.Default, diagnostics)).ToList();

        Assert.Equal(398, pages.Count);                                                // 386 TEXT/hbwr, 12 .htm of other creators
        Assert.Equal(386, pages.Count(p => p.File.FinderInfo.Creator == FourCC.FromString("hbwr")));
        Assert.All(rendered, r => Assert.StartsWith("data:text/html;charset=utf-8;base64,", r.DataUri));
        Assert.Equal(KnownMissing, diagnostics.Count(d => d.Code == "help.missing-file"));
        Assert.Equal(KnownPictures, rendered.Sum(r => CountOf(r.Html, "data:image/")));
        Assert.Equal("Using Record to start a script", rendered.Single(r => r.Node.Title == "at10.htm").Title);
    }

    // Fitted to the image (what the help books refer to that is not on the disk, such as frames Help Viewer makes).
    private const int KnownMissing = 27, KnownPictures = 513;

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
