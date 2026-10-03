using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Documents;
using static ClassicMac.Resources.Decoders.Tests.DocumentFixtures;

namespace ClassicMac.App.Tests;

// Documents in the viewer: the preview (a chapter at a time, pictures reflowed, links followed) and Convert Documents.
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

    [Fact]
    public async Task A_DOCMaker_document_previews_a_chapter_at_a_time_with_its_pictures_in_the_text()
    {
        var (model, input) = await Open();

        var preview = await Select(model, input.Children.Single(c => c.Title == "Manual"));

        Assert.True(preview.IsDocument);
        Assert.Equal(1, model.SelectedTab);
        var document = preview.Document!;
        Assert.Equal(["Welcome", "Chapter 2"], document.ChapterTitles);
        Assert.Equal(260, document.ColumnWidth);
        // Heading and first line; the two pictures of one line (left, right); the next line; the wide picture scaled to
        // the column; the last line.
        var items = document.Items;
        Assert.Equal([typeof(DocumentTextItem), typeof(DocumentRowItem), typeof(DocumentTextItem), typeof(DocumentRowItem), typeof(DocumentTextItem)],
            items.Select(i => i.GetType()));
        Assert.Equal("Welcome\rThe game begins here.", ((DocumentTextItem)items[0]).Text.Text);
        var row = (DocumentRowItem)items[1];
        Assert.Equal((1, 0, 1), (row.Left.Count, row.Center.Count, row.Right.Count));
        Assert.NotNull(row.Left[0].Png);
        Assert.Equal((260, 65), (((DocumentRowItem)items[3]).Center[0].Width, ((DocumentRowItem)items[3]).Center[0].Height));
        Assert.Null(row.Right[0].Open); // its chapter 9 does not exist

        // The left picture goes to chapter 2; Back returns.
        document.Offset = new Avalonia.Vector(0, 40);
        row.Left[0].Open!.Execute(null);
        Assert.Equal(1, document.ChapterIndex);
        Assert.Equal(default, document.Offset);
        Assert.Equal(Justification.Center, ((DocumentTextItem)document.Items[0]).Justification);
        Assert.True(document.BackCommand.CanExecute(null));
        document.BackCommand.Execute(null);
        Assert.Equal(0, document.ChapterIndex);
        Assert.False(document.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task SimpleText_files_preview_as_documents_only_with_pictures()
    {
        var (model, input) = await Open();

        var readMe = await Select(model, input.Children.Single(c => c.Title == "Read Me"));
        Assert.True(readMe.IsDocument);
        Assert.False(readMe.Document!.HasChapters);
        Assert.Equal([typeof(DocumentTextItem), typeof(DocumentRowItem), typeof(DocumentTextItem)], readMe.Document.Items.Select(i => i.GetType()));

        var plain = await Select(model, input.Children.Single(c => c.Title == "Plain"));
        Assert.True(plain.IsStyledText);
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
        // The centred title, the two left paragraphs together, the indented one.
        var items = preview.Document.Items.Cast<DocumentTextItem>().ToList();
        Assert.Equal(["Title", "First\rSecond", "Indented"], items.Select(i => i.Text.Text));
        Assert.Equal([Justification.Center, Justification.Left, Justification.Left], items.Select(i => i.Justification));
        Assert.Equal(new Avalonia.Thickness(36, 0, 18, 0), items[2].Margin);
        Assert.True(items[0].Text.Runs[0].Bold);
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
