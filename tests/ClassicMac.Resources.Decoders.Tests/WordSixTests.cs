using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 6 and 95 documents ('W6BN'), docs/formats/documents/word-binary.md §4.1: fixtures built byte by byte.
public class WordSixTests
{
    private static WordSixBuilder Builder() => new WordSixBuilder().Font("Times").Font("Helvetica").Style(0x0FFF, [], []);

    private static StyledDocument Read(byte[] data, List<Diagnostic>? diagnostics = null) =>
        WordBinaryDocuments.Read(data, "Memo", diagnostics: diagnostics ?? [])!;

    private static TextRun At(StyledText text, int offset) => text.Runs.Single(r => r.Start <= offset && offset < r.Start + r.Length);

    [Fact]
    public void The_text_reads_in_Mac_OS_Roman_with_its_character_formatting()
    {
        var text = "Café bold italic small Helvetica under\r";
        var data = Builder().Text(text)
            .Chp(5, 9, 85, 1)                                   // sprmCFBold
            .Chp(10, 16, 86, 1)                                 // sprmCFItalic
            .Chp(17, 22, 99, 16, 0)                             // sprmCHps: 8 point
            .Chp(23, 32, 93, 1, 0)                              // sprmCFtc: font 1
            .Chp(33, 38, 94, 1, 88, 1)                          // sprmCKul, sprmCFOutline
            .Build();
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(data, diagnostics).Chapters);

        Assert.Empty(diagnostics);
        Assert.Equal(text, chapter.Text.Text);                  // é is $8E in Mac OS Roman
        Assert.Equal(("Times", 10), (At(chapter.Text, 0).FontName, At(chapter.Text, 0).Size));
        Assert.True(At(chapter.Text, 5).Bold);
        Assert.True(At(chapter.Text, 10).Italic);
        Assert.Equal(8, At(chapter.Text, 17).Size);
        Assert.Equal("Helvetica", At(chapter.Text, 23).FontName);
        Assert.Equal((byte)(0x04 | 0x08), At(chapter.Text, 33).Face);
    }

    [Fact]
    public void Windows_text_reads_in_Windows_1252()
    {
        var builder = Builder().Text("é\r");
        builder.Chse = 0;

        Assert.Equal("é\r", Assert.Single(Read(builder.Build()).Chapters).Text.Text);
    }

    [Fact]
    public void Paragraphs_carry_their_alignment_indents_and_spacing()
    {
        var data = Builder().Text("Centred\rIndented\r")
            .Pap(0, 8, 0, 5, 1)                                                 // sprmPJc
            .Pap(8, 17, 0, 17, 0xD0, 0x02, 16, 0xF0, 0x00, 19, 0x10, 0xFF, 21, 0x78, 0x00, 22, 0x3C, 0x00)
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal([Justification.Center, Justification.Left], chapter.Paragraphs.Select(p => p.Justification));
        var indented = chapter.Paragraphs[1];
        Assert.Equal((36.0, 12.0, -12.0, 6.0, 3.0), (indented.LeftIndent, indented.RightIndent, indented.FirstLineIndent,
            indented.SpaceBefore, indented.SpaceAfter));
    }

    [Fact]
    public void Styles_apply_through_their_base_and_plain_resets_to_the_style()
    {
        var data = new WordSixBuilder().Font("Times")
            .Style(0x0FFF, [], [99, 24, 0])                                     // 12 point
            .Style(0, [5, 1], [85, 1])                                          // centred, bold
            .Text("Title plain\rBody\r").Pap(0, 12, 1).Pap(12, 17, 0)
            .Chp(6, 11, 83)                                                     // sprmCPlain: the style's properties
            .Chp(12, 16, 85, 1, 83)                                             // bold, then plain
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal((true, 12), (At(chapter.Text, 0).Bold, At(chapter.Text, 0).Size));
        Assert.True(At(chapter.Text, 6).Bold);                                  // the style's bold stays
        Assert.False(At(chapter.Text, 12).Bold);
        Assert.Equal([Justification.Center, Justification.Left], chapter.Paragraphs.Select(p => p.Justification));
    }

    [Fact]
    public void Tables_and_fields_read_as_in_Word_97()
    {
        var data = Builder().Text("A\aB\a\a\u0013 PAGE \u00141\u0015\r")
            .Pap(0, 2, 0, 24, 1).Pap(2, 4, 0, 24, 1).Pap(4, 5, 0, 24, 1, 25, 1)
            .Build();

        Assert.Equal("A\tB\t\r1\r", Assert.Single(Read(data).Chapters).Text.Text);
    }

    [Fact]
    public void A_fast_saved_document_reads_through_its_piece_table()
    {
        var builder = Builder().Text("Pieces\r").Chp(0, 6, 85, 1);
        builder.PieceTable = true;
        builder.Flags = 0x0004;

        var chapter = Assert.Single(Read(builder.Build()).Chapters);

        Assert.Equal("Pieces\r", chapter.Text.Text);
        Assert.True(chapter.Text.Runs[0].Bold);
    }

    [Fact]
    public void An_unknown_sprm_ends_its_property_list()
    {
        var data = Builder().Text("Odd\r").Chp(0, 3, 85, 1, 53, 0, 86, 1).Build();
        var diagnostics = new List<Diagnostic>();

        var run = Assert.Single(Read(data, diagnostics).Chapters).Text.Runs[0];

        Assert.Equal((true, false), (run.Bold, run.Italic));
        Assert.Equal(["word.bad-sprm"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void An_encrypted_document_is_reported()
    {
        var builder = Builder().Text("Secret\r");
        builder.Flags = 0x0100;
        var diagnostics = new List<Diagnostic>();

        Assert.Null(WordBinaryDocuments.Read(builder.Build(), "Secret", diagnostics: diagnostics));
        Assert.Equal(["word.encrypted"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void StyledDocuments_reads_a_W6BN_file()
    {
        var data = Builder().Text("Six\r").Build();

        var document = StyledDocuments.Read(data, null, FourCC.FromString("W6BN"), "Memo")!;

        Assert.Equal(("Six\r", DocumentKind.Word), (document.Chapters[0].Text.Text, document.Kind));
    }
}
