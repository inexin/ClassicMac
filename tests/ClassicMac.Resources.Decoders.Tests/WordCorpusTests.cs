using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;

namespace ClassicMac.Resources.Decoders.Tests;

// With CLASSICMAC_WORD_CORPUS set to a folder of Word documents' data forks (each named as the Mac file), the real
// documents read as docs/formats/documents/word-mac.md §7 says. The facts are constants of the samples; the samples are
// never committed.
public class WordCorpusTests
{
    private static string? Corpus => Environment.GetEnvironmentVariable("CLASSICMAC_WORD_CORPUS") is { Length: > 0 } folder ? folder : null;

    [Fact]
    public void A_fast_saved_Word_5_document_reads_through_its_piece_table()
    {
        if (Corpus is not { } folder)
        {
            Assert.Skip("CLASSICMAC_WORD_CORPUS is not set.");
            return;
        }

        // A Word 5 document from a game's CD, fast saved twice.
        var data = File.ReadAllBytes(Path.Combine(folder, "Divinty Spell list (MW)"));
        Assert.Equal(132608, data.Length);

        var facts = MacWordDocuments.Inspect(data)!;
        Assert.Equal((5, true, 2, 25562), (facts.Version, facts.FastSaved, facts.FastSaves, facts.TextLength));
        Assert.Equal(39, facts.Fonts.Count);
        Assert.Equal(("Chicago", "New York", "Geneva", "Times"), (facts.Fonts[0], facts.Fonts[2], facts.Fonts[3], facts.Fonts[20]));
        Assert.Equal((3, 1347, 4443, 218), (facts.Styles, facts.CharacterRuns, facts.ParagraphRuns, facts.BoldRuns));

        // Its 46 pieces put the whole main text together, in order (word-mac.md §1.9).
        var diagnostics = new List<Diagnostic>();
        var document = MacWordDocuments.Read(data, "Divinty Spell list (MW)", diagnostics: diagnostics)!;
        var text = document.Chapters[0].Text.Text;
        Assert.StartsWith("Formatted to be printed in Landscape orientation.", text, StringComparison.Ordinal);
        Assert.Contains("Plague", text, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Code == "word.fast-saved");
    }
}
