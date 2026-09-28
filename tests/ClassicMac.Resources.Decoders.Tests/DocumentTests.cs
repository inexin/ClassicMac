using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;
using static ClassicMac.Resources.Decoders.Tests.DocumentFixtures;

namespace ClassicMac.Resources.Decoders.Tests;

public class DocumentTests
{
    [Fact]
    public void DOCMaker_documents_read_as_DOCMaker_4_8_reads_them()
    {
        var diagnostics = new List<Diagnostic>();

        var document = StyledDocuments.Read(ReadOnlyMemory<byte>.Empty, DocMaker(), FourCC.FromString("APPL"), "Manual", diagnostics: diagnostics)!;

        Assert.Equal((DocumentKind.DocMaker, "Manual", 2), (document.Kind, document.Title, document.Chapters.Count));
        var (one, two) = (document.Chapters[0], document.Chapters[1]);
        Assert.Equal(("Welcome", "Chapter 2"), (one.Title, two.Title)); // no STR 2002: "Chapter" and the number
        Assert.Equal(300 - 15 - 20 - 5, one.ColumnWidth); // window width − scroll bar − margins
        Assert.Equal((Justification.Left, Justification.Center), (one.Justification, two.Justification));
        Assert.Equal((new Rgb(255, 255, 255), new Rgb(0xCC, 0xDD, 0xFF)), (one.Background, two.Background)); // clut entry k − 1
        Assert.Equal(("Palatino", 18, true), (one.Text.Runs[0].FontName, one.Text.Runs[0].Size, one.Text.Runs[0].Bold));
        Assert.Equal(22, one.Text.Runs[0].LineHeight);

        // Three pInf for four option-spaces: the last is a plain space.
        Assert.Equal([1001, 1002, 1003], one.Pictures.Select(p => (int)p.PictureId));
        var (left, right, wide) = (one.Pictures[0], one.Pictures[1], one.Pictures[2]);
        Assert.Equal((PictureAlignment.Left, 4, 4), (left.Alignment, left.Width, left.Height));
        Assert.Equal(new PictureAction(1, true, 2, -1), left.Action);
        Assert.Equal((PictureAlignment.Right, false, 40), (right.Alignment, right.Action.Highlights, right.Width)); // an invisible button
        Assert.Equal((PictureAlignment.Center, false, 400), (wide.Alignment, wide.NoScale, wide.Width));
        Assert.Equal(one.Text.Text.IndexOf(' '), left.Anchor); // the first option-space
        Assert.Equal(14, Assert.Single(two.Pictures).Action.Code);

        Assert.Equal(new ContentsEntry(1, 8, 20, "The game"), Assert.Single(document.Contents));
        Assert.Equal(["document.bad-link"], diagnostics.Select(d => d.Code)); // the link to chapter 9
    }

    [Fact]
    public void SimpleText_documents_show_PICT_1000_plus_k_at_the_k_th_option_space()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("styl"), 128, Styl((0, 16, 12, 22, 0, 12, 0))));
        fork.Add(new Resource(FourCC.FromString("PICT"), 1000, Picture(10, 10)));
        fork.Add(new Resource(FourCC.FromString("PICT"), 1002, Picture(20, 10))); // 1001 is missing: a gap
        byte[] text = [.. "A\r"u8, 0xCA, .. "\r"u8, 0xCA, .. "\r"u8, 0xCA];

        var document = StyledDocuments.Read(text, fork, FourCC.FromString("TEXT"), "Read Me")!;

        Assert.Equal(DocumentKind.SimpleText, document.Kind);
        var chapter = Assert.Single(document.Chapters);
        Assert.Equal([(1000, 2), (1002, 6)], chapter.Pictures.Select(p => ((int)p.PictureId, p.Anchor)));
        Assert.All(chapter.Pictures, p => Assert.Equal(PictureAlignment.Center, p.Alignment));
        Assert.Equal("Courier", chapter.Text.Runs[0].FontName);
    }

    [Fact]
    public void Other_files_are_not_documents()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR "), 128, Pascal("hi")));
        Assert.Null(StyledDocuments.Read("plain"u8.ToArray(), fork, FourCC.FromString("TEXT"), "x")); // no styl, no pictures
        Assert.Null(StyledDocuments.Read("plain"u8.ToArray(), null, FourCC.FromString("APPL"), "x"));
    }
}
