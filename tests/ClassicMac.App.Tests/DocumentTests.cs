using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Documents;
using static ClassicMac.Resources.Decoders.Tests.DocumentFixtures;

namespace ClassicMac.App.Tests;

// Documents in the viewer: the preview (their HTML export in the web view, a page at a time, links followed) and Convert Documents.
public class DocumentTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-document-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class Picker(string output) : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(output);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) => Task.FromResult<string?>(null);
    }

    // An HFS disk with a DOCMaker manual, a SimpleText read-me with a picture, and one without.
    internal static string Disk(string folder)
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Manual", [], DocMaker().ToArray(), type: "APPL", creator: "Dk@P");
        var readMe = new ResourceFork();
        readMe.Add(new Resource(ClassicMac.Core.FourCC.FromString("PICT"), 1000, Picture(10, 10)));
        disk.File(HfsBuilder.Root, "Read Me", [.. "Hello\r"u8, 0xCA, .. "\rBye"u8], readMe.ToArray(), type: "TEXT", creator: "ttxt");
        disk.File(HfsBuilder.Root, "Plain", "Just text"u8.ToArray(), [], type: "TEXT", creator: "ttxt");
        var path = Path.Combine(folder, "Disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private async Task<(MainViewModel Model, InputNode Input)> Open(string? output = null)
    {
        var model = new MainViewModel { FilePicker = output is null ? null : new Picker(output) };
        var input = (await model.OpenAsync(Disk(folder)))!;
        return (model, input);
    }

    private static async Task<PreviewViewModel> Select(MainViewModel model, NodeViewModel node)
    {
        model.Selected = node;
        await model.PreviewTask;
        return model.Preview;
    }

    // The page as the web view gets it: the data: URI's HTML (and its fragment, if any).
    private static string HtmlOf(DocumentWebPreview document)
    {
        var uri = document.DataUri;
        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        var base64 = (hash < 0 ? uri : uri[..hash])["data:text/html;charset=utf-8;base64,".Length..];
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    // Documents are previewed as the HTML that Convert Documents writes, in the web view the help pages use
    // (docs/PLAN.md "Documents previewed through their HTML export").
    [Fact]
    public async Task A_DOCMaker_document_previews_its_HTML_a_chapter_at_a_time()
    {
        var (model, input) = await Open();

        var preview = await Select(model, input.Children.Single(c => c.Title == "Manual"));

        Assert.True(preview.IsDocument);
        Assert.True(preview.IsWebPage);
        Assert.Equal(1, model.SelectedTab);
        var document = preview.Document!;
        Assert.Same(document, preview.Web);
        // The contents page, then a page per chapter; the first chapter shows first.
        Assert.Equal(["Contents", "Welcome", "Chapter 2"], document.ChapterTitles);
        Assert.True(document.HasChapters);
        Assert.Equal(1, document.ChapterIndex);
        Assert.Equal("chapter-01.html", document.Page);
        var html = HtmlOf(document);
        Assert.Contains("The game begins here.", html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"data:image/png;base64,", html, StringComparison.Ordinal); // pictures inline
        Assert.DoesNotContain("<link", html, StringComparison.Ordinal); // the stylesheet inline too
        Assert.StartsWith("data:text/html;charset=utf-8;base64,", model.WebUri, StringComparison.Ordinal);
        // The text, for the Text view and when there is no web view.
        Assert.Contains("Welcome", document.Source, StringComparison.Ordinal);
        Assert.Contains("The game begins here.", document.Source, StringComparison.Ordinal);
        Assert.Equal(["Rendered", "Text"], model.WebModes);

        // A link to another chapter turns its page (the web view's navigation is cancelled), with its anchor.
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(model.FollowHelpLink(ClassicMac.Resources.Decoders.Documents.HelpPages.Origin + "chapter-02.html#p0"));
        Assert.Equal(2, document.ChapterIndex);
        Assert.EndsWith("#p0", document.DataUri, StringComparison.Ordinal);
        Assert.Contains(nameof(MainViewModel.WebUri), changed);
        Assert.True(document.BackCommand.CanExecute(null));

        // A "back" picture (the HTML's history.back()) goes back.
        Assert.False(model.FollowHelpLink("javascript:history.back()"));
        Assert.Equal(1, document.ChapterIndex);
        Assert.False(document.BackCommand.CanExecute(null));

        // Choosing a chapter in the list turns to it; the contents page is one too.
        document.ChapterIndex = 0;
        Assert.Equal("index.html", document.Page);
        Assert.Contains("chapter-02.html", HtmlOf(document), StringComparison.Ordinal);

        // A link to a page the document does not have is not followed.
        Assert.False(model.FollowHelpLink(ClassicMac.Resources.Decoders.Documents.HelpPages.Origin + "chapter-09.html"));
        Assert.Equal(0, document.ChapterIndex);
        Assert.NotNull(model.HelpStatus);
    }

    [Fact]
    public async Task SimpleText_files_preview_as_documents_only_with_pictures()
    {
        var (model, input) = await Open();

        var readMe = await Select(model, input.Children.Single(c => c.Title == "Read Me"));
        Assert.True(readMe.IsDocument);
        Assert.False(readMe.Document!.HasChapters);
        Assert.Equal("index.html", readMe.Document.Page);
        Assert.Contains("<img src=\"data:image/png;base64,", HtmlOf(readMe.Document), StringComparison.Ordinal);

        var plain = await Select(model, input.Children.Single(c => c.Title == "Plain"));
        Assert.True(plain.IsStyledText);
        Assert.False(plain.IsWebPage);
    }

    [Fact]
    public async Task A_Word_document_previews_with_its_paragraphs_alignment_and_indents()
    {
        var word = new ClassicMac.Resources.Decoders.Tests.MacWordBuilder().Font(3, "Geneva").Style([0x00, 0x10, 0x00, 0x03], [])
            .Text("Title\rFirst\rSecond\rIndented\r")
            .Pap(0, 6, 0, 0x05, 0x01).Pap(19, 28, 0, 0x11, 0x02, 0xD0, 0x10, 0x01, 0x68)
            .Chp(0, 5, 0x80).Build();
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Letter", word, [], type: "WDBN", creator: "MSWD");
        var path = Path.Combine(folder, "Word.img");
        File.WriteAllBytes(path, disk.Build("Word"));
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        var model = new MainViewModel { FilePicker = new Picker(output) };
        var input = (await model.OpenAsync(path))!;

        // Converted, though it has no resource fork.
        model.Selected = input;
        await model.ConvertDocumentsCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(output, "Word documents", "index.html")), model.Status);

        var preview = await Select(model, input.Children.Single(c => c.Title == "Letter"));

        Assert.True(preview.IsDocument);
        Assert.Equal(DocumentKind.Word, preview.Document!.Document.Kind);
        Assert.False(preview.Document.HasChapters);
        // The preview is the converted page: the centred title, the indented paragraph.
        var html = HtmlOf(preview.Document);
        var written = File.ReadAllText(Path.Combine(output, "Word documents", "index.html"));
        Assert.Contains("text-align:center", html, StringComparison.Ordinal);
        Assert.Contains("margin-left:", html, StringComparison.Ordinal);
        Assert.Contains("Indented", html, StringComparison.Ordinal);
        Assert.Contains("Indented", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_documents_writes_each_as_HTML_and_extract_adds_them()
    {
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        var (model, input) = await Open(output);
        model.Selected = input;
        Assert.True(model.ConvertDocumentsCommand.CanExecute(null));

        await model.ConvertDocumentsCommand.ExecuteAsync(null);

        Assert.Equal("2 documents to " + Path.Combine(output, "Disk documents") + ".", model.Status);
        Assert.True(File.Exists(Path.Combine(output, "Disk documents", "Manual", "chapter-02.html")));
        Assert.True(File.Exists(Path.Combine(output, "Disk documents", "Read Me", "index.html")));

        await model.ExtractAllCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(output, "Disk resources", "Manual", "document", "index.html")));

        // A file with no document: nothing is written.
        model.Selected = input.Children.Single(c => c.Title == "Plain");
        await model.ConvertDocumentsCommand.ExecuteAsync(null);
        Assert.Equal("No documents in Plain.", model.Status);
        Assert.False(Directory.Exists(Path.Combine(output, "Plain documents")));
    }
}
