using System.Text;
using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// PDF documents (docs/formats/documents/pdf.md) preview in the web view, whose engine shows PDFs on Windows (WebView2)
// and macOS (WKWebView); on Linux (WebKitGTK has no PDF viewer) they stay in Hex.
public class PdfPreviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-pdf-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.3\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    private static bool Shown => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private async Task<(MainViewModel Model, InputNode Input)> Open()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Manual", Pdf, [], "PDF ", "CARO");
        disk.File(HfsBuilder.Root, "Downloaded", Pdf, [], "????", "????");
        disk.File(HfsBuilder.Root, "Not one", "%PDF but not"u8.ToArray(), [], "PDF ", "CARO");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var model = new MainViewModel();
        return (model, (await model.OpenAsync(path))!);
    }

    private static async Task<PreviewViewModel> Select(MainViewModel model, InputNode input, string name)
    {
        model.Selected = input.Children.OfType<FileNode>().Single(f => f.Title == name);
        await model.PreviewTask;
        return model.Preview;
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Downloaded")]
    public async Task A_PDF_previews_in_the_web_view(string name)
    {
        var (model, input) = await Open();

        var preview = await Select(model, input, name);

        if (!Shown)
        {
            Assert.Equal(PreviewKind.None, preview.Kind);
            return;
        }

        Assert.Equal(PreviewKind.Pdf, preview.Kind);
        Assert.True(preview.IsWebPage);
        Assert.False(preview.IsDocument);
        Assert.Equal(1, model.SelectedTab);
        var pdf = preview.Pdf!;
        Assert.Same(pdf, preview.Web);
        Assert.StartsWith("data:application/pdf;base64,", pdf.DataUri, StringComparison.Ordinal);
        Assert.Equal(Pdf, Convert.FromBase64String(pdf.DataUri["data:application/pdf;base64,".Length..]));
        Assert.Equal($"PDF 1.3 document, {Pdf.Length} bytes.", pdf.Source);
        Assert.Equal(["Rendered", "Info"], model.HelpPreview.WebModes);
    }

    [Fact]
    public async Task A_file_without_the_header_has_no_PDF_preview()
    {
        var (model, input) = await Open();

        var preview = await Select(model, input, "Not one");

        Assert.NotEqual(PreviewKind.Pdf, preview.Kind);
        Assert.Null(preview.Pdf);
    }
}
