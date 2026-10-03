using System.Text;
using ClassicMac.Core;
using ClassicMac.Core.Tests;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Text;
using static ClassicMac.Resources.Decoders.Tests.WordBinaryBuilder;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 97 binary documents ('W8BN'), docs/formats/documents/word-binary.md: fixtures built byte by byte from [MS-DOC].
public class WordBinaryTests
{
    private static WordBinaryBuilder Builder() => new WordBinaryBuilder().Font("Times New Roman").Font("Arial")
        .Style(0x0FFF, [], []);

    private static StyledDocument Read(byte[] data, List<Diagnostic>? diagnostics = null) =>
        WordBinaryDocuments.Read(data, "Report", diagnostics: diagnostics ?? [])!;

    private static TextRun At(StyledText text, int offset) => text.Runs.Single(r => r.Start <= offset && offset < r.Start + r.Length);

    [Fact]
    public void The_text_reads_with_its_character_formatting()
    {
        var text = "Plain bold italic small Arial under\r";
        var data = Builder().Text(text)
            .Chp(6, 10, Sprm(0x0835, 1))
            .Chp(11, 17, Sprm(0x0836, 1))
            .Chp(18, 23, Sprm(0x4A43, Word(16)))                     // 8 point
            .Chp(24, 29, Sprm(0x4A4F, Word(1)))                      // font 1: Arial
            .Chp(30, 35, [.. Sprm(0x2A3E, 1), .. Sprm(0x0838, 1), .. Sprm(0x0839, 1)])
            .Build();
        var diagnostics = new List<Diagnostic>();

        var document = Read(data, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal((DocumentKind.Word, "Report"), (document.Kind, document.Title));
        var chapter = Assert.Single(document.Chapters);
        Assert.Equal(text, chapter.Text.Text);
        Assert.Equal(("Times New Roman", 10, (byte)0), (At(chapter.Text, 0).FontName, At(chapter.Text, 0).Size, At(chapter.Text, 0).Face));
        Assert.True(At(chapter.Text, 6).Bold);
        Assert.True(At(chapter.Text, 11).Italic);
        Assert.Equal(8, At(chapter.Text, 18).Size);
        Assert.Equal("Arial", At(chapter.Text, 24).FontName);
        Assert.Equal((byte)(0x04 | 0x08 | 0x10), At(chapter.Text, 30).Face);
    }

    [Fact]
    public void Two_pieces_one_compressed_and_one_Unicode_read_as_one_text()
    {
        // The compressed piece's $92 and $97 are Windows-1252's quote and dash; the second piece is UTF-16.
        var text = "It’s — Ωmega\r";
        var builder = Builder().Text(text).Chp(0, 9, Sprm(0x0835, 1));
        builder.UnicodeFrom = 7;

        var chapter = Assert.Single(Read(builder.Build()).Chapters);

        Assert.Equal(text, chapter.Text.Text);
        Assert.True(At(chapter.Text, 8).Bold);                                   // a run across the pieces
        Assert.False(At(chapter.Text, 10).Bold);
    }

    [Fact]
    public void Paragraphs_carry_their_alignment_indents_and_spacing()
    {
        var text = "Centred\rIndented\rJustified\r";
        var data = Builder().Text(text)
            .Pap(0, 8, 0, Sprm(0x2403, 1))
            .Pap(8, 17, 0, [.. Sprm(0x840F, Word(720)), .. Sprm(0x840E, Word(240)), .. Sprm(0x8411, Word(-240)),
                .. Sprm(0xA413, Word(120)), .. Sprm(0xA414, Word(60))])
            .Pap(17, 27, 0, Sprm(0x2403, 3))
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal([Justification.Center, Justification.Left, Justification.Full], chapter.Paragraphs.Select(p => p.Justification));
        var indented = chapter.Paragraphs[1];
        Assert.Equal((36.0, 12.0, -12.0, 6.0, 3.0), (indented.LeftIndent, indented.RightIndent, indented.FirstLineIndent,
            indented.SpaceBefore, indented.SpaceAfter));
    }

    [Fact]
    public void Styles_apply_through_their_base_styles_and_toggles_flip_them()
    {
        // Style 0: 12 point. Style 1, based on 0: centred and bold.
        var data = new WordBinaryBuilder().Font("Times New Roman")
            .Style(0x0FFF, [], Sprm(0x4A43, Word(24)))
            .Style(0, Sprm(0x2403, 1), Sprm(0x0835, 1))
            .Text("Heading not\rBody\r").Pap(0, 12, 1).Pap(12, 17, 0)
            .Chp(8, 11, Sprm(0x0835, 0x81))                          // the style's bold, flipped
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal((true, 12), (At(chapter.Text, 0).Bold, At(chapter.Text, 0).Size));
        Assert.False(At(chapter.Text, 8).Bold);
        Assert.Equal([Justification.Center, Justification.Left], chapter.Paragraphs.Select(p => p.Justification));
        Assert.False(At(chapter.Text, 12).Bold);
        Assert.Equal(12, At(chapter.Text, 12).Size);
    }

    [Fact]
    public void Hidden_text_is_left_out_and_caps_are_upper_case()
    {
        var data = Builder().Text("Seen hidden caps\r").Chp(5, 12, Sprm(0x083C, 1)).Chp(12, 16, Sprm(0x083B, 1)).Build();

        Assert.Equal("Seen CAPS\r", Assert.Single(Read(data).Chapters).Text.Text);
    }

    [Fact]
    public void Fields_show_their_result_not_their_instructions()
    {
        var data = Builder().Text("See \u0013 HYPERLINK \"x\" \u0014the link\u0015 here\r").Build();

        Assert.Equal("See the link here\r", Assert.Single(Read(data).Chapters).Text.Text);
    }

    [Fact]
    public void Table_cells_become_tabs_and_rows_lines()
    {
        var data = Builder().Text("A\aB\a\aAfter\r")
            .Pap(0, 2, 0, Sprm(0x2416, 1)).Pap(2, 4, 0, Sprm(0x2416, 1)).Pap(4, 5, 0, [.. Sprm(0x2416, 1), .. Sprm(0x2417, 1)])
            .Build();

        Assert.Equal("A\tB\t\rAfter\r", Assert.Single(Read(data).Chapters).Text.Text);
    }

    [Fact]
    public void Special_characters_map_to_Unicode_and_others_are_dropped()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Builder().Text("a\u001Eb\u001Fc\u0001d\ve\r").Build();

        Assert.Equal("a‑b­cd" + (char)0x2028 + "e\r", Assert.Single(Read(data, diagnostics).Chapters).Text.Text); // \v: a line break
        Assert.Equal(["word.not-shown"], diagnostics.Select(d => d.Code));
    }

    [Theory]
    [InlineData((ushort)0x0300, "encrypted")]
    [InlineData((ushort)0x8300, "obfuscated")]
    public void A_password_protected_document_is_reported_and_not_read(ushort flags, string kind)
    {
        var data = Builder().Text("Secret\r");
        data.Flags = flags;
        var diagnostics = new List<Diagnostic>();

        Assert.Null(WordBinaryDocuments.Read(data.Build(), "Secret", diagnostics: diagnostics));

        var reported = Assert.Single(diagnostics);
        Assert.Equal(("word.encrypted", DiagnosticSeverity.Error), (reported.Code, reported.Severity));
        Assert.Contains(kind, reported.Message);
    }

    [Fact]
    public void The_table_stream_is_0Table_when_the_FIB_says_so()
    {
        var builder = Builder().Text("Zero\r").Chp(0, 4, Sprm(0x0835, 1));
        builder.Flags = 0;

        Assert.True(Assert.Single(Read(builder.Build()).Chapters).Text.Runs[0].Bold);
    }

    [Fact]
    public void A_fast_save_s_property_changes_are_reported()
    {
        var builder = Builder().Text("Text\r");
        builder.Prm = 0x0101;
        var diagnostics = new List<Diagnostic>();

        Assert.Equal("Text\r", Assert.Single(Read(builder.Build(), diagnostics).Chapters).Text.Text);
        Assert.Equal(["word.piece-properties"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void A_FIB_older_than_Word_6_s_is_reported()
    {
        var builder = Builder().Text("Two\r");
        builder.NFib = 0x002D;
        var diagnostics = new List<Diagnostic>();

        Assert.Null(WordBinaryDocuments.Read(builder.Build(), "Six", diagnostics: diagnostics));
        Assert.Equal(["word.unsupported-version"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Other_files_are_none_or_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(WordBinaryDocuments.Read(new byte[600], "Zero", diagnostics: diagnostics));
        Assert.Empty(diagnostics);

        var other = new CompoundFileBuilder().Stream("Workbook", new byte[100]).Build();
        Assert.Null(WordBinaryDocuments.Read(other, "Sheet", diagnostics: diagnostics));
        Assert.Equal(["word.bad-container"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void StyledDocuments_reads_a_W8BN_file_and_writes_it_as_HTML()
    {
        var data = Builder().Text("Centred\r").Pap(0, 8, 0, Sprm(0x2403, 1)).Build();

        var document = StyledDocuments.Read(data, null, FourCC.FromString("W8BN"), "Report")!;
        var index = Encoding.UTF8.GetString(HtmlDocuments.Write(document).Single(f => f.Path == "index.html").Content.Span);

        Assert.Equal(DocumentKind.Word, document.Kind);
        Assert.Contains("text-align:center", index);
    }
}
