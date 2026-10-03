using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// Word 4 and 5 for the Macintosh ('WDBN'), docs/formats/documents/word-mac.md: fixtures built byte by byte.
public class MacWordTests
{
    private static readonly FourCC Wdbn = FourCC.FromString("WDBN");

    // Geneva for style 0 (the Normal style), Times by name in the font table.
    private static MacWordBuilder Builder() => new MacWordBuilder()
        .Font(3, "Geneva").Font(20, "Times")
        .Style([0x00, 0x10, 0x00, 0x03], []);

    private static StyledDocument Read(byte[] data, List<Diagnostic>? diagnostics = null) =>
        MacWordDocuments.Read(data, "Letter", diagnostics: diagnostics ?? [])!;

    [Fact]
    public void The_text_reads_with_its_character_formatting()
    {
        // "Plain Bold Italic Small Times Under\r"
        var text = "Plain Bold Italic Small Times Under\r";
        var data = Builder().Text(text)
            .Chp(6, 10, 0x80)                                              // bold toggled
            .Chp(11, 17, 0x40)                                             // italic toggled
            .Chp(18, 23, 0x00, 0x08, 0x00, 0x00, 0x14)                     // size 10 (20 half points)
            .Chp(24, 29, 0x00, 0x10, 0x00, 0x14)                           // font 20
            .Chp(30, 35, 0x18, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02)   // outline, shadow, underline
            .Build();
        var diagnostics = new List<Diagnostic>();

        var document = Read(data, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal((DocumentKind.Word, "Letter"), (document.Kind, document.Title));
        var chapter = Assert.Single(document.Chapters);
        Assert.Equal(text, chapter.Text.Text);
        TextRun At(int offset) => chapter.Text.Runs.Single(r => r.Start <= offset && offset < r.Start + r.Length);
        Assert.Equal(("Geneva", 12, (byte)0), (At(0).FontName, At(0).Size, At(0).Face));   // the style's font, 12 by default
        Assert.True(At(6).Bold);
        Assert.False(At(6).Italic);
        Assert.True(At(11).Italic);
        Assert.Equal(10, At(18).Size);
        Assert.Equal(("Times", (short)20), (At(24).FontName, At(24).FontId));
        Assert.Equal((byte)(0x04 | 0x08 | 0x10), At(30).Face);                              // underline, outline, shadow
        Assert.Equal(text.Length, chapter.Text.Runs.Sum(r => r.Length));
    }

    [Fact]
    public void A_style_with_bold_makes_a_toggle_plain()
    {
        var data = new MacWordBuilder().Font(3, "Geneva").Style([0x80, 0x18, 0x00, 0x03, 0x18], [])
            .Text("Head Plain\r").Chp(5, 10, 0x80).Build();

        var text = Assert.Single(Read(data).Chapters).Text;

        Assert.Equal((true, 12), (text.Runs[0].Bold, text.Runs[0].Size));                   // the style: bold, 12 (24 half points)
        Assert.False(text.Runs.Single(r => r.Start == 5).Bold);                                 // toggled back
        Assert.Equal("Geneva", text.Runs[0].FontName);
    }

    [Fact]
    public void Hidden_text_is_left_out_and_all_caps_is_upper_case()
    {
        var data = Builder().Text("Seen hidden caps\r").Chp(5, 12, 0x01).Chp(12, 16, 0x02).Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal("Seen CAPS\r", chapter.Text.Text);
        Assert.Equal(chapter.Text.Text.Length, chapter.Text.Runs.Sum(r => r.Length));
    }

    [Fact]
    public void Paragraphs_carry_their_alignment_indents_and_spacing()
    {
        var text = "Centred\rIndented\rJustified\rRight\r";
        var data = Builder().Text(text)
            .Pap(0, 8, 0, 0x05, 0x01)                                          // centred
            .Pap(8, 17, 0, 0x11, 0x02, 0xD0, 0x13, 0xFF, 0x10, 0x10, 0x00, 0xF0, 0x15, 0x00, 0x78, 0x16, 0x00, 0x3C)
            .Pap(17, 27, 0, 0x05, 0x03)                                        // justified
            .Pap(27, 33, 0, 0x05, 0x02)                                        // right
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal([0, 8, 17, 27], chapter.Paragraphs.Select(p => p.Start));
        Assert.Equal([Justification.Center, Justification.Left, Justification.Full, Justification.Right],
            chapter.Paragraphs.Select(p => p.Justification));
        var indented = chapter.Paragraphs[1];
        Assert.Equal((36.0, 12.0, -12.0, 6.0, 3.0), (indented.LeftIndent, indented.RightIndent, indented.FirstLineIndent,
            indented.SpaceBefore, indented.SpaceAfter));                             // twips to points
        Assert.Same(indented, chapter.ParagraphAt(12));
    }

    [Fact]
    public void A_style_gives_its_paragraphs_their_alignment()
    {
        var data = new MacWordBuilder().Style([0x00, 0x10, 0x00, 0x03], []).Style(null, [0x05, 0x01])
            .Text("Title\rBody\r").Pap(0, 6, 1).Pap(6, 11, 0).Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal([Justification.Center, Justification.Left], chapter.Paragraphs.Select(p => p.Justification));
    }

    [Fact]
    public void Line_breaks_stay_in_their_paragraph_and_table_cells_become_tabs()
    {
        // A line break in a paragraph, then a row of two cells and the row's end mark.
        var text = "One\vtwo\rA\aB\a\a";
        var data = Builder().Text(text)
            .Pap(0, 8, 0)
            .Pap(8, 10, 0, 0x18, 0x01)                                         // in a table
            .Pap(10, 12, 0, 0x18, 0x01)
            .Pap(12, 13, 0, 0x18, 0x01, 0x19, 0x01)                            // the row's end
            .Build();

        var chapter = Assert.Single(Read(data).Chapters);

        Assert.Equal("One\u2028two\rA\tB\t\r", chapter.Text.Text);
        Assert.Equal(new DocumentTable(8, 13, []), Assert.Single(chapter.Tables) with { CellEdges = [] });
    }

    [Fact]
    public void Special_characters_map_to_Unicode_and_others_are_dropped()
    {
        var data = Builder().Text("a\u001Eb\u001Fc\u0001d\u0002e\r").Build();
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(data, diagnostics).Chapters);

        Assert.Equal("a‑b­cde\r", chapter.Text.Text);                // non-breaking and optional hyphens
        Assert.Equal(["word.not-shown"], diagnostics.Select(d => d.Code));
    }

    // A fast-saved document is one with a piece table (zone 18); the flag byte's $04 is set in every Word 5.1a document,
    // fast saved or not, and $08 when it has a picture [Verified: Word 5.1a and 4.0 documents made for ClassicMac].
    [Theory]
    [InlineData(0x04)]
    [InlineData(0x0C)]
    public void The_flag_byte_alone_does_not_make_a_document_fast_saved(byte flags)
    {
        var builder = Builder().Text("Text\r");
        builder.Flags = flags;
        var diagnostics = new List<Diagnostic>();

        var document = MacWordDocuments.Read(builder.Build(), "Saved", diagnostics: diagnostics);

        Assert.NotNull(document);
        Assert.Equal("Text", document!.Chapters[0].Text.Text.TrimEnd('\r'));
        Assert.DoesNotContain(diagnostics, d => d.Code == "word.fast-saved");
    }

    // A fast-saved document's text is its pieces in order (zone 18: a $01 property block, then $02 and the pieces' n + 1
    // character positions and n descriptors: flags, FC, property word), word-mac.md §1.9.
    [Fact]
    public void A_fast_saved_document_reads_through_its_piece_table()
    {
        var builder = Builder().Text("Hello World\r");
        builder.Flags = 0x24;                                                  // two fast saves, as Word 5 counts them
        builder.PieceTable =
        [
            1, 0, 2, 0x80, 0,                                                  // a property block: not applied
            2, 0, 28,                                                          // the piece table: 28 bytes
            0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0, 12,                               // positions 0, 6, 12
            0, 2, 0, 0, 1, 6, 0, 0,                                            // piece 1: "World\r", at FC $106
            0, 2, 0, 0, 1, 0, 0, 0,                                            // piece 2: "Hello ", at FC $100
        ];
        var diagnostics = new List<Diagnostic>();

        var document = MacWordDocuments.Read(builder.Build(), "Saved", diagnostics: diagnostics);

        Assert.Equal("World\rHello ", document!.Chapters[0].Text.Text);
        Assert.DoesNotContain(diagnostics, d => d.Code == "word.fast-saved");
    }

    [Fact]
    public void A_piece_table_that_cannot_be_read_is_reported()
    {
        var builder = Builder().Text("Text\r");
        builder.PieceTable = [2, 0, 40, 0, 0];                                 // says 40 bytes, holds 2
        var diagnostics = new List<Diagnostic>();

        Assert.Null(MacWordDocuments.Read(builder.Build(), "Saved", diagnostics: diagnostics));
        Assert.Equal(["word.bad-pieces"], diagnostics.Select(d => d.Code));
    }

    [Theory]
    [InlineData(0xFE, 0x34, 0x00, 0x00, "word.unsupported-version")]   // Word 3
    [InlineData(0xFE, 0x37, 0x00, 0x30, "word.unsupported-version")]   // an unknown version
    public void Other_versions_are_reported(byte b0, byte b1, byte b2, byte b3, string code)
    {
        var data = Builder().Text("Text\r").Build();
        (data[0], data[1], data[2], data[3]) = (b0, b1, b2, b3);
        var diagnostics = new List<Diagnostic>();

        Assert.Null(MacWordDocuments.Read(data, "Old", diagnostics: diagnostics));
        Assert.Equal([code], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void Word_4_reads_like_Word_5()
    {
        var builder = Builder().Text("Four\r").Chp(0, 4, 0x80);
        builder.Version = 0x1C;

        var chapter = Assert.Single(Read(builder.Build()).Chapters);

        Assert.True(chapter.Text.Runs[0].Bold);
    }

    [Fact]
    public void Not_a_Word_file_is_none()
    {
        var diagnostics = new List<Diagnostic>();

        Assert.Null(MacWordDocuments.Read(new byte[300], "Zero", diagnostics: diagnostics));
        Assert.Null(MacWordDocuments.Read(new byte[10], "Short", diagnostics: diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_bad_table_is_reported_and_the_text_still_reads()
    {
        var data = Builder().Text("Still here\r").Chp(0, 5, 0x80).Build();
        // The character bin table's FC points past the end of the file.
        var at = 0x40 + 6 * 9;
        data[at] = 0x7F;
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(data, diagnostics).Chapters);

        Assert.Equal("Still here\r", chapter.Text.Text);
        Assert.False(chapter.Text.Runs[0].Bold);
        Assert.Equal(["word.bad-zone"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void An_unknown_sprm_ends_its_paragraph_s_properties()
    {
        var data = Builder().Text("Odd\r").Pap(0, 4, 0, 0x05, 0x01, 0x7E, 0x05, 0x02).Build();
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(data, diagnostics).Chapters);

        Assert.Equal(Justification.Center, chapter.Paragraphs[0].Justification);   // read up to the unknown one
        Assert.Equal(["word.unknown-sprm"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void StyledDocuments_reads_a_WDBN_file_and_writes_it_as_HTML()
    {
        var data = Builder().Text("Centred\r").Pap(0, 8, 0, 0x05, 0x01).Chp(0, 7, 0x80).Build();
        var diagnostics = new List<Diagnostic>();

        var document = StyledDocuments.Read(data, null, Wdbn, "Letter", diagnostics: diagnostics)!;
        var files = HtmlDocuments.Write(document);

        Assert.Equal(DocumentKind.Word, document.Kind);
        var index = Encoding.UTF8.GetString(files.Single(f => f.Path == "index.html").Content.Span);
        Assert.Contains("text-align:center", index);
        Assert.Contains("Centred", index);
    }

    [Fact]
    public void The_document_converter_converts_a_WDBN_file_with_no_resource_fork()
    {
        var data = Builder().Text("Hello\r").Build();
        var converter = Assert.Single(ResourceDecoders.CreateDocumentConverters(DecodeOptions.Default));
        var input = new DocumentInput(new ResourceFork(), () => data, Wdbn, FourCC.FromString("MSWD"), "Letter", ReadOptions.Default, []);

        var files = converter.Convert(input);

        Assert.Contains(files, f => f.Path == "index.html");
    }
}
