using System.Runtime.CompilerServices;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// Documents Word 4.0, 5.1a, 6.0 and 98 wrote, with known content (Word/CONTENTS.txt; made in SheepShaver and QEMU for
// ClassicMac, our own text): the reader against what Word itself saved (word-mac.md §7, word-binary.md §7).
public class WordSampleTests
{
    internal static string Folder([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Word");

    // A sample's data fork (the files are MacBinary).
    internal static byte[] DataFork(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(Folder(), name + ".bin"));
        var length = (file[83] << 24) | (file[84] << 16) | (file[85] << 8) | file[86];
        return file[128..(128 + length)];
    }

    private static StyledDocument Read(string name, List<Diagnostic>? diagnostics = null)
    {
        var document = MacWordDocuments.Read(DataFork(name), name, diagnostics: diagnostics ?? []);
        Assert.NotNull(document);
        return document!;
    }

    private static string[] Paragraphs(StyledDocument document) => document.Chapters[0].Text.Text.TrimEnd('\r').Split('\r');

    // The run at the first character of the first occurrence of `text`.
    private static TextRun RunAt(StyledDocument document, string text)
    {
        var chapter = document.Chapters[0];
        var at = chapter.Text.Text.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{text}\" is not in the text");
        return chapter.Text.Runs.Single(r => r.Start <= at && at < r.Start + r.Length);
    }

    private static ParagraphFormat FormatAt(StyledDocument document, string text)
    {
        var chapter = document.Chapters[0];
        return chapter.ParagraphAt(chapter.Text.Text.IndexOf(text, StringComparison.Ordinal))!;
    }

    [Theory]
    [InlineData("w51")]
    [InlineData("w4")]
    public void Plain_text_reads_with_its_Mac_characters_tab_and_line_break(string version)
    {
        var diagnostics = new List<Diagnostic>();
        var document = Read(version + "-plain", diagnostics);

        Assert.Equal(["Plain text test document.", "The quick brown fox jumps over the lazy dog 0123456789.",
            "Mac Roman: é ü ß • ™ © “quoted” – en — em.", "Tab:\tafter tab. Line one\u2028line two."], Paragraphs(document));
        Assert.Empty(diagnostics);
        // Normal paragraphs have no spacing (not Heading 1's 12 pt before and 3 pt after).
        Assert.All(document.Chapters[0].Paragraphs, p => Assert.Equal((0.0, 0.0), (p.SpaceBefore, p.SpaceAfter)));
        Assert.Equal(("New York", 12, (byte)0), (RunAt(document, "Plain").FontName, RunAt(document, "Plain").Size, RunAt(document, "Plain").Face));
    }

    [Theory]
    [InlineData("w51")]
    [InlineData("w4")]
    public void Character_formats_read_as_Word_set_them(string version)
    {
        var document = Read(version + "-formats");

        Assert.Equal((byte)0, RunAt(document, "Format: Normal").Face);
        Assert.True(RunAt(document, "Bold run").Bold);
        Assert.True(RunAt(document, "Italic run").Italic);
        Assert.True(RunAt(document, "Underline run").Underline);
        Assert.True(RunAt(document, "Outline run").Outline);
        Assert.True(RunAt(document, "Shadow run").Shadow);
        Assert.True(RunAt(document, "Small caps run").SmallCaps);
        Assert.False(RunAt(document, "Bold run").SmallCaps);
        Assert.Contains("ALL CAPS RUN.", document.Chapters[0].Text.Text);
        Assert.DoesNotContain("Hidden run", document.Chapters[0].Text.Text);
        Assert.Equal("Times", RunAt(document, "Times run").FontName);
        Assert.Equal("Geneva", RunAt(document, "Geneva run").FontName);
        Assert.Equal(9, RunAt(document, "Size 9 run").Size);
        Assert.Equal(12, RunAt(document, "Size 12 run").Size);
        Assert.Equal(24, RunAt(document, "Size 24 run").Size);
        // Word's colour 5 is red, QuickDraw's redColor.
        var red = RunAt(document, "Red run");
        Assert.Equal(((byte)0xDD, (byte)0x08, (byte)0x06), (red.Red, red.Green, red.Blue));
        Assert.Equal((0, 0, 0), (RunAt(document, "Bold run").Red, RunAt(document, "Bold run").Green, RunAt(document, "Bold run").Blue));
        Assert.True(RunAt(document, "bold-italic").Bold && RunAt(document, "bold-italic").Italic);
    }

    [Theory]
    [InlineData("w51")]
    [InlineData("w4")]
    public void Paragraph_formats_and_the_heading_style_read_as_Word_set_them(string version)
    {
        var document = Read(version + "-paragraphs");

        var heading = FormatAt(document, "Heading One");
        Assert.Equal((12.0, 3.0), (heading.SpaceBefore, heading.SpaceAfter));
        var title = RunAt(document, "Heading One");
        Assert.Equal(("Geneva", 14, true), (title.FontName, title.Size, title.Bold));
        Assert.Equal(Justification.Left, FormatAt(document, "Left aligned").Justification);
        Assert.Equal(Justification.Center, FormatAt(document, "Centered paragraph").Justification);
        Assert.Equal(Justification.Right, FormatAt(document, "Right aligned").Justification);
        Assert.Equal(Justification.Full, FormatAt(document, "Justified paragraph").Justification);
        Assert.Equal(36.0, FormatAt(document, "Left indent 0.5").LeftIndent);
        Assert.Equal(72.0, FormatAt(document, "Right indent 1 inch").RightIndent);
        Assert.Equal((0.0, 36.0), (FormatAt(document, "First line indent").LeftIndent, FormatAt(document, "First line indent").FirstLineIndent));
        Assert.Equal((36.0, -36.0), (FormatAt(document, "Hanging indent").LeftIndent, FormatAt(document, "Hanging indent").FirstLineIndent));
        Assert.Equal((6.0, 12.0), (FormatAt(document, "Space before 6").SpaceBefore, FormatAt(document, "Space before 6").SpaceAfter));
        var normal = FormatAt(document, "Normal paragraph after");
        Assert.Equal((0.0, 0.0, 0.0), (normal.SpaceBefore, normal.SpaceAfter, normal.LeftIndent));
        Assert.Equal("New York", RunAt(document, "Normal paragraph after").FontName);
    }

    [Theory]
    [InlineData("w51")]
    [InlineData("w4")]
    public void A_table_reads_as_rows_of_cells_with_its_cell_edges(string version)
    {
        var diagnostics = new List<Diagnostic>();
        var document = Read(version + "-table", diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(["Table follows.", "A1\tB1\tC1\t", "A2\tB2\tC2\t", "A3\tB3\tC3\t", "Text after table."], Paragraphs(document));
        var chapter = document.Chapters[0];
        var table = Assert.Single(chapter.Tables);
        Assert.Equal(chapter.Text.Text.IndexOf("A1", StringComparison.Ordinal), table.Start);
        Assert.Equal(chapter.Text.Text.IndexOf("Text after", StringComparison.Ordinal), table.End);
        // Cell edges in points: the row's left edge (minus the gap), then each cell's right edge (2880, 5760, 8640 twips).
        Assert.Equal([-5.4, 144.0, 288.0, 432.0], table.CellEdges);
    }

    // The "-fast" files are edited documents Word saved in full (CONTENTS.txt): the edits read in place.
    [Theory]
    [InlineData("w51", "Tab:\tafter tab. Line one\u2028line two. EDITED END")]
    [InlineData("w4", "Tab:\tafter tab. Line one EDITED END\u2028line two.")]
    public void Edited_documents_read_with_their_edits(string version, string last)
    {
        var document = Read(version + "-plain-fast");

        Assert.Equal(["FAST START Plain text test document.", "The EDITED MIDDLE quick brown fox jumps over the lazy dog 0123456789.",
            "Mac Roman: FAST MID é ü ß • ™ © “quoted” – en — em.", last], Paragraphs(document));
        Assert.True(Read(version + "-formats-fast").Chapters[0].Text.Text.Contains("Format: Itali FAST MIDc run.", StringComparison.Ordinal));
        Assert.True(RunAt(Read(version + "-formats-fast"), " FAST MIDc").Italic);
    }

    // Word 6.0 for the Macintosh ('W6BN'): a compound file whose FIB starts $A5DC, nFib 104; Word 98 ('W8BN'): $A5EC,
    // nFib 193, the Word 97 format (word-binary.md §4.1).
    private static StyledDocument ReadWord6(string name, List<Diagnostic>? diagnostics = null)
    {
        var document = WordBinaryDocuments.Read(DataFork(name), name, diagnostics: diagnostics ?? []);
        Assert.NotNull(document);
        return document!;
    }

    [Theory]
    [InlineData("w6")]
    [InlineData("w98")]
    public void Word_6_and_98_plain_text_reads(string version)
    {
        var diagnostics = new List<Diagnostic>();
        var document = ReadWord6(version + "-plain", diagnostics);

        Assert.Equal(["Plain text test document.", "The quick brown fox jumps over the lazy dog 0123456789.",
            "Mac Roman: é ü ß • ™ © “quoted” – en — em.", "Tab:\tafter tab. Line one\u2028line two."], Paragraphs(document));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("FAST START Plain text test document.", Paragraphs(ReadWord6(version + "-plain-fast"))[0]);
    }

    [Theory]
    [InlineData("w6")]
    [InlineData("w98")]
    public void Word_6_and_98_character_and_paragraph_formats_read(string version)
    {
        var formats = ReadWord6(version + "-formats");
        Assert.True(RunAt(formats, "Bold run").Bold);
        Assert.True(RunAt(formats, "Italic run").Italic);
        Assert.True(RunAt(formats, "Underline run").Underline);
        Assert.True(RunAt(formats, "Outline run").Outline);
        Assert.True(RunAt(formats, "Shadow run").Shadow);
        Assert.Contains("ALL CAPS RUN.", formats.Chapters[0].Text.Text);
        Assert.DoesNotContain("Hidden run", formats.Chapters[0].Text.Text);
        Assert.Equal("Times", RunAt(formats, "Times run").FontName);
        Assert.Equal("Geneva", RunAt(formats, "Geneva run").FontName);
        Assert.Equal((9, 24), (RunAt(formats, "Size 9 run").Size, RunAt(formats, "Size 24 run").Size));
        Assert.False(RunAt(formats, "Format: Normal").Bold);
        Assert.True(RunAt(formats, "Small caps run").SmallCaps);
        var red = RunAt(formats, "Red run");
        Assert.Equal(((byte)0xFF, (byte)0x00, (byte)0x00), (red.Red, red.Green, red.Blue));     // colour 6, red

        var paragraphs = ReadWord6(version + "-paragraphs");
        Assert.Equal(Justification.Center, FormatAt(paragraphs, "Centered paragraph").Justification);
        Assert.Equal(Justification.Right, FormatAt(paragraphs, "Right aligned").Justification);
        Assert.Equal(36.0, FormatAt(paragraphs, "Left indent 0.5").LeftIndent);
        Assert.Equal((36.0, -36.0), (FormatAt(paragraphs, "Hanging indent").LeftIndent, FormatAt(paragraphs, "Hanging indent").FirstLineIndent));
        Assert.Equal((6.0, 12.0), (FormatAt(paragraphs, "Space before 6").SpaceBefore, FormatAt(paragraphs, "Space before 6").SpaceAfter));
        Assert.Equal((0.0, 0.0), (FormatAt(paragraphs, "Normal paragraph after").SpaceBefore, FormatAt(paragraphs, "Normal paragraph after").SpaceAfter));
    }

    [Theory]
    [InlineData("w6")]
    [InlineData("w98")]
    public void Word_6_and_98_table_reads_as_rows_of_cells(string version)
    {
        var document = ReadWord6(version + "-table");
        var table = Assert.Single(document.Chapters[0].Tables);
        Assert.Equal([-5.4, 144.0, 288.0, 432.0], table.CellEdges);
        Assert.Equal(["Table follows.", "A1\tB1\tC1\t", "A2\tB2\tC2\t", "A3\tB3\tC3\t", "Text after table."], Paragraphs(document));
    }

    // Word 98's fast saves (fComplex 1, cQuickSaves 1): the text through the piece table, each insertion with the
    // formatting of the character before it.
    [Fact]
    public void Word_98_fast_saved_documents_read_with_their_edits()
    {
        var diagnostics = new List<Diagnostic>();
        var plain = ReadWord6("w98-plain-fast", diagnostics);
        Assert.Equal(["FAST START Plain text test document.", "The quick brown fox jumps over the lazy dog 0123456789.",
            "Mac Roman: FAST MID é ü ß • ™ © “quoted” – en — em.", "Tab:\tafter tab. Line one\u2028line two."], Paragraphs(plain));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var formats = ReadWord6("w98-formats-fast");
        Assert.Equal("FAST START Format: Normal run.", Paragraphs(formats)[0]);
        Assert.True(RunAt(formats, " FAST MIDc").Italic);
        Assert.True(RunAt(formats, "Bold run").Bold);
        Assert.DoesNotContain("Hidden run", formats.Chapters[0].Text.Text);

        var paragraphs = ReadWord6("w98-paragraphs-fast");
        Assert.Equal("FAST START Heading One", Paragraphs(paragraphs)[0]);
        Assert.Equal(Justification.Center, FormatAt(paragraphs, " FAST MIDCentered").Justification);
        Assert.Equal((36.0, -36.0), (FormatAt(paragraphs, "Hanging indent").LeftIndent, FormatAt(paragraphs, "Hanging indent").FirstLineIndent));

        var table = ReadWord6("w98-table-fast");
        Assert.Single(table.Chapters[0].Tables);
        Assert.Equal(["FAST START Table follows.", "A1\tB1\tC1\t", "A2 FAST MID\tB2\tC2\t", "A3\tB3\tC3\t", "Text after table."], Paragraphs(table));

        Assert.Contains("Text with a f FAST MIDootnote", ReadWord6("w98-picture-footnote-fast").Chapters[0].Text.Text, StringComparison.Ordinal);
    }

    // Each version's picture and note (Word 4.0 and 5.1a kept the RTF's footnote a footnote; Word 6.0 and 98 made it an
    // endnote): the picture where its character was, as Word stored it (Word 98 made it a PNG), and the note's number at
    // its reference and at the start of its text, which follows the main text.
    [Theory]
    [InlineData("w4-picture-footnote", "Text with a footnote1 here.")]
    [InlineData("w51-picture-footnote", "Text with a footnote1 here.")]
    [InlineData("w6-picture-footnote", "Text with a footnote1 here.")]
    [InlineData("w98-picture-footnote", "Text with a footnote1 here.")]
    [InlineData("w4-picture-footnote-fast", "Text EDITED  FAST MIDMIDDLE with a footnote1 here.")]
    [InlineData("w51-picture-footnote-fast", "Text EDITED  FAST MIDMIDDLE with a footnote1 here.")]
    [InlineData("w6-picture-footnote-fast", "Text with a f FAST MIDootnote1 here.")]
    [InlineData("w98-picture-footnote-fast", "Text with a f FAST MIDootnote1 here.")]
    public void A_picture_and_a_note_read_where_Word_put_them(string name, string referenceLine)
    {
        var diagnostics = new List<Diagnostic>();
        var document = name.StartsWith("w4", StringComparison.Ordinal) || name.StartsWith("w51", StringComparison.Ordinal)
            ? Read(name, diagnostics) : ReadWord6(name, diagnostics);
        var chapter = document.Chapters[0];
        var text = chapter.Text.Text;

        var note = Assert.Single(chapter.Notes);
        var main = text[..note.Start].TrimEnd('\r').Split('\r');
        Assert.Equal(4, main.Length);
        Assert.EndsWith("Picture follows:", main[0], StringComparison.Ordinal);
        Assert.Equal([" ", referenceLine], main[1..3]);
        Assert.Equal((1, "1"), (note.Number, note.Mark));
        Assert.Equal(text.IndexOf("1 here.", StringComparison.Ordinal), note.Reference);
        Assert.Equal("1 This is the footnote.", text[note.Start..note.End].TrimEnd('\r'));

        var picture = Assert.Single(chapter.Pictures);
        Assert.Equal(text.IndexOf(' ', StringComparison.Ordinal), picture.Anchor);
        Assert.Equal((64, 64), (picture.Width, picture.Height));
        Assert.Equal(name.StartsWith("w98", StringComparison.Ordinal) ? PictureFormat.Png : PictureFormat.Pict, picture.Format);
        var drawn = new List<Diagnostic>();
        Assert.NotNull(DocumentPictures.Draw(chapter, picture, DecodeOptions.Default, drawn));
        Assert.Empty(drawn);
        Assert.DoesNotContain(diagnostics, d => d.Code is "word.not-shown" or "word.bad-picture");
    }

    [Fact]
    public void The_HTML_links_a_note_s_reference_and_text_and_shows_the_picture()
    {
        var files = HtmlDocuments.Write(ReadWord6("w98-picture-footnote"));
        var page = Encoding.UTF8.GetString(files.Single(f => f.Path == "index.html").Content.Span);

        Assert.Contains("footnote<sup><a href=\"#note-1\" id=\"ref-1\">1</a></sup> here.", page);
        Assert.Contains("<section class=\"notes\">", page);
        Assert.Contains("<a id=\"note-1\"></a>", page);
        Assert.Contains("<a id=\"note-1\"></a>\n<p class=\"s0\"><sup><a href=\"#ref-1\">1</a></sup>", page);
        Assert.Contains(" This is the footnote.", page);
        var image = Assert.Single(files, f => f.Path.StartsWith("images/", StringComparison.Ordinal));
        Assert.EndsWith(".png", image.Path, StringComparison.Ordinal);
        Assert.Contains($"<img src=\"{image.Path}\"", page);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], image.Content.Span[..4].ToArray());
    }

    // A Word 98 fast save whose only change is a centred paragraph: piece 1, the 2nd paragraph's mark, carries Prm0
    // $010A (isprm 5, sprmPJc, 1).
    [Fact]
    public void A_Word_98_fast_save_s_property_change_applies()
    {
        var diagnostics = new List<Diagnostic>();
        var document = ReadWord6("w98-prm-fast", diagnostics);

        Assert.Equal(Paragraphs(ReadWord6("w98-plain")), Paragraphs(document));
        Assert.Equal(Justification.Center, FormatAt(document, "The quick brown fox").Justification);
        Assert.Equal(Justification.Left, FormatAt(document, "Plain text test").Justification);
        Assert.Equal(Justification.Left, FormatAt(document, "Mac Roman").Justification);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void The_HTML_has_line_breaks_small_caps_colour_and_a_real_table()
    {
        // The page and its stylesheet.
        static string Html(string name) => string.Concat(HtmlDocuments.Write(Read(name))
            .Where(f => f.Path is "index.html" or "style.css").Select(f => Encoding.UTF8.GetString(f.Content.Span)));

        Assert.Contains("Line one<br>line two.", Html("w51-plain"));
        var formats = Html("w51-formats");
        Assert.Contains("font-variant:small-caps", formats);
        Assert.Contains("color:#dd0806", formats);
        var table = Html("w51-table");
        Assert.Contains("<table", table);
        Assert.Contains("<td class=\"s0\">A1</td>", table);
        Assert.Contains("<td class=\"s0\">C3</td></tr>", table);
        Assert.Contains("<col style=\"width:149.4px\">", table);           // the first cell: −5.4 to 144 points
        Assert.Equal(3, table.Split("<tr>").Length - 1);
    }
}
