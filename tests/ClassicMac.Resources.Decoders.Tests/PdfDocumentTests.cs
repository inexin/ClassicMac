using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// PDF documents (docs/formats/documents/pdf.md): found by their header, written out as they are.
public sealed class PdfDocumentTests
{
    private static readonly FourCC Pdf = FourCC.FromString("PDF ");
    private static readonly FourCC Caro = FourCC.FromString("CARO");

    private static byte[] Document(string prefix = "") =>
        Encoding.ASCII.GetBytes(prefix + "%PDF-1.3\n%âã\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    [Fact]
    public void The_header_gives_the_version()
    {
        Assert.Equal("1.3", PdfDocuments.Version(Document()));
        Assert.Equal("1.7", PdfDocuments.Version("%PDF-1.7\r%"u8.ToArray()));
        Assert.Equal("2.0", PdfDocuments.Version("%PDF-2.0"u8.ToArray()));
    }

    [Fact]
    public void The_header_may_follow_other_bytes_within_the_first_1024()
    {
        Assert.Equal("1.3", PdfDocuments.Version(Document(new string(' ', 1000))));
        Assert.Null(PdfDocuments.Version(Document(new string(' ', 1020))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF")]
    [InlineData("%PDF-")]
    [InlineData("%PDF-x.y")]
    [InlineData("%!PS-Adobe-3.0")]
    public void Other_bytes_are_no_PDF(string text) => Assert.Null(PdfDocuments.Version(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void A_conversion_writes_the_document_as_it_is()
    {
        var data = Document();
        var converter = ResourceDecoders.CreateDocumentConverters().Single(c => c.Name == "document.pdf");
        var diagnostics = new List<Diagnostic>();
        var input = new DocumentInput(new ResourceFork(), () => data, Pdf, Caro, "Manual", ReadOptions.Default, diagnostics);

        var files = converter.Convert(input);

        var file = Assert.Single(files);
        Assert.Equal("document.pdf", file.Path);
        Assert.Equal(data, file.Content.ToArray());
        Assert.Empty(diagnostics);
        Assert.True(DataForkDocuments.Applies(Pdf));
    }

    [Fact]
    public void A_PDF_typed_file_without_the_header_is_reported_and_not_written()
    {
        var converter = ResourceDecoders.CreateDocumentConverters().Single(c => c.Name == "document.pdf");
        var diagnostics = new List<Diagnostic>();
        var input = new DocumentInput(new ResourceFork(), () => "not a PDF"u8.ToArray(), Pdf, Caro, "Manual", ReadOptions.Default, diagnostics);

        Assert.Empty(converter.Convert(input));
        Assert.Equal("pdf.no-header", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Only_files_of_type_PDF_are_converted()
    {
        var converter = ResourceDecoders.CreateDocumentConverters().Single(c => c.Name == "document.pdf");
        var read = false;
        var input = new DocumentInput(new ResourceFork(), () =>
        {
            read = true;
            return Document();
        }, FourCC.FromString("TEXT"), Caro, "Manual", ReadOptions.Default, []);

        Assert.Empty(converter.Convert(input));
        Assert.False(read);
    }
}
