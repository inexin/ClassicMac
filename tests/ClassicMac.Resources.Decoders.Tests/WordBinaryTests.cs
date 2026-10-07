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

    // A fast save's property changes ([MS-DOC] §2.9.208 Prm): a Prm0 (one sprm, by isprm, and its operand byte) or a
    // Prm1 (the Clx's Prc it names), applied over the piece's text; a paragraph's from its mark's piece.
    [Fact]
    public void A_Prm0_centres_the_paragraph_whose_mark_is_in_its_piece()
    {
        var builder = Builder().Text("One\rTwo\r");
        builder.Prm = 0x010A;                                                       // isprm 5 (sprmPJc), 1: centred
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(builder.Build(), diagnostics).Chapters);

        Assert.All(chapter.Paragraphs, p => Assert.Equal(Justification.Center, p.Justification));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_Prm0_sets_a_character_property()
    {
        var builder = Builder().Text("Bold\r");
        builder.Prm = (0x55 << 1) | (1 << 8);                                        // isprm $55 (sprmCFBold), on

        Assert.True(Assert.Single(Read(builder.Build()).Chapters).Text.Runs[0].Bold);
    }

    [Fact]
    public void A_Prm1_applies_the_Prc_it_names()
    {
        var builder = Builder().Text("Both\r");
        builder.Prcs.Add(Sprm(0x0836, 1));                                          // Prc 0: italic, not named
        builder.Prcs.Add([.. Sprm(0x0835, 1), .. Sprm(0x2403, 2)]);                 // Prc 1: bold, right-aligned
        builder.Prm = (1 << 1) | 1;

        var chapter = Assert.Single(Read(builder.Build()).Chapters);

        Assert.True(chapter.Text.Runs[0].Bold);
        Assert.False(chapter.Text.Runs[0].Italic);
        Assert.Equal(Justification.Right, chapter.Paragraphs[0].Justification);
    }

    [Fact]
    public void A_Prm1_naming_no_Prc_is_reported_and_left_out()
    {
        var builder = Builder().Text("Text\r");
        builder.Prm = 0x0101;                                                       // Prc $80, of none
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

    // --- Notes and pictures ---

    private static byte[] Le32(params int[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    // A footnote ([MS-DOC] PlcffndRef, PlcffndTxt) or endnote (PlcfendRef, PlcfendTxt): its auto-numbered reference
    // ($02 with sprmCFSpec) shows its number, and its text, after the main text, is the chapter's note.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_note_s_reference_shows_its_number_and_its_text_follows_the_main_text(bool endnote)
    {
        var builder = Builder().Text("See\u0002 x.\r\u0002 Note.\r")
            .Chp(3, 4, Sprm(0x0855, 1)).Chp(8, 9, Sprm(0x0855, 1));
        if (endnote)
        {
            builder.EndnoteLength = 8;
        }
        else
        {
            builder.FootnoteLength = 8;
        }

        builder.Tables[endnote ? 46 : 2] = [.. Le32(3, 9), 1, 0];                    // the reference at CP 3, auto-numbered
        builder.Tables[endnote ? 47 : 3] = Le32(0, 8, 9);
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(builder.Build(), diagnostics).Chapters);

        var note = Assert.Single(chapter.Notes);
        Assert.Equal("See1 x.\r1 Note.\r", chapter.Text.Text);
        Assert.Equal((1, "1", 3, 8, 16), (note.Number, note.Mark, note.Reference, note.Start, note.End));
        Assert.Empty(diagnostics);
    }

    // A PICF ([MS-DOC] PICFAndOfficeArtData): lcb, cbHeader $44, mm $64 (an Office Art shape), the goal size in twips at
    // +28 and +30 and the scale in thousandths at +32 and +34; then the shape's records, a blip among them.
    private static byte[] Picf(int width, int height, byte[] records)
    {
        var header = new byte[0x44];
        BitConverter.GetBytes(0x44 + records.Length).CopyTo(header, 0);
        BitConverter.GetBytes((short)0x44).CopyTo(header, 4);
        BitConverter.GetBytes((short)0x64).CopyTo(header, 6);
        BitConverter.GetBytes((short)(width * 20)).CopyTo(header, 28);
        BitConverter.GetBytes((short)(height * 20)).CopyTo(header, 30);
        BitConverter.GetBytes((short)1000).CopyTo(header, 32);
        BitConverter.GetBytes((short)500).CopyTo(header, 34);                         // half height
        return [.. header, .. records];
    }

    // An Office Art record ([MS-ODRAW] OfficeArtRecordHeader): version and instance, type, length.
    private static byte[] Record(int version, int instance, int type, byte[] body) =>
        [.. BitConverter.GetBytes((ushort)(version | instance << 4)), .. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes(body.Length), .. body];

    // An FBSE around a blip: btWin32, btMacOS, a UID, tag, size, cRef, foDelay, unused, cbName 0, unused (36 bytes).
    private static byte[] Fbse(int type, byte[] blip) => Record(2, type, 0xF007, [(byte)type, (byte)type, .. new byte[16], 0, 0,
        .. BitConverter.GetBytes(blip.Length), 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. blip]);

    // A picture character ($01, sprmCFSpec) whose sprmCPicLocation is 0: its PICF at the start of the Data stream.
    private static WordBinaryBuilder PictureDocument(byte[] data)
    {
        var builder = Builder().Text("\u0001\r").Chp(0, 1, [.. Sprm(0x0855, 1), .. Sprm(0x6A03, 0, 0, 0, 0)]);
        builder.Data = data;
        return builder;
    }

    [Fact]
    public void A_PICT_blip_is_inflated_into_the_chapter_s_picture()
    {
        byte[] pict = [0, 30, 0, 0, 0, 0, 0, 20, 0, 40, 0x11, 0x01, 0xFF];             // a version 1 PICT, 40 × 20
        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(pict);
        }

        // OfficeArtBlipPICT: a UID, then the metafile header: cbSize, rcBounds, ptSize, cbSave, compression 0 (deflate), filter.
        byte[] header = [.. BitConverter.GetBytes(pict.Length), .. new byte[24], .. BitConverter.GetBytes((int)deflated.Length), 0, 0xFE];
        var blip = Record(0, 0x542, 0xF01C, [.. new byte[16], .. header, .. deflated.ToArray()]);
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(PictureDocument(Picf(40, 20, Record(0xF, 0, 0xF004, Fbse(4, blip)))).Build(), diagnostics).Chapters);

        var picture = Assert.Single(chapter.Pictures);
        Assert.Equal((" \r", 0, PictureFormat.Pict, 40, 10), (chapter.Text.Text, picture.Anchor, picture.Format, picture.Width, picture.Height));
        Assert.Equal(pict, picture.Picture!.Value.ToArray());
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(0xF01E, 0x6E0, PictureFormat.Png)]
    [InlineData(0xF01D, 0x46A, PictureFormat.Jpeg)]
    public void A_PNG_or_JPEG_blip_is_the_picture_as_stored(int type, int instance, PictureFormat format)
    {
        byte[] image = [1, 2, 3, 4, 5];
        var blip = Record(0, instance, type, [.. new byte[16], 0xFF, .. image]);         // a UID, the tag, the file

        var chapter = Assert.Single(Read(PictureDocument(Picf(8, 8, Fbse(type - 0xF018, blip))).Build()).Chapters);

        var picture = Assert.Single(chapter.Pictures);
        Assert.Equal(format, picture.Format);
        Assert.Equal(image, picture.Picture!.Value.ToArray());
    }

    [Fact]
    public void A_picture_whose_data_is_missing_is_reported_and_left_out()
    {
        var diagnostics = new List<Diagnostic>();

        var chapter = Assert.Single(Read(PictureDocument([1, 2, 3]).Build(), diagnostics).Chapters);

        Assert.Empty(chapter.Pictures);
        Assert.Equal("\r", chapter.Text.Text);
        Assert.Equal(["word.bad-picture"], diagnostics.Select(d => d.Code));
    }
}
