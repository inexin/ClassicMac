using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.Resources.Decoders.Tests;

// A user-supplied type and creator database (TCDB's spreadsheet layout, finder.md §2.6), read from tiny xlsx files (XlsxBuilder).
public class TypeCreatorDatabaseTests
{
    private static FourCC F(string s) => FourCC.FromString(s);

    private static readonly object?[] Header = ["File Name", "Type", "Creator", "Comments", "Category", "Extension", "Dup", "Credit"];

    // The spreadsheet's text is Mac Roman read as Windows-1252: 'Ñ' is the em dash ($D1), 'ð' the Apple logo ($F0), 'ª' ™.
    private static readonly object?[][] Rows =
    [
        Header,
        ["SimpleTextÑAIFF Sound File", "AIFF", "ttxt", "SimpleText", "Sound AIFF", "AIF", " "],
        ["SimpleTextÑsecond row, ignored", "AIFF", "ttxt", "SimpleText", "Sound AIFF", "AIF", " "],
        ["calendar.gif", "GIFf", "ttxt", "SimpleText/TeachText", "Graphics GIF", "GIF", " "],
        ["Default", "CNFG", "ttxt", "SimpleText/TeachText", " ", " ", " "],
        ["SimpleText/TeachText", "APPL", "ttxt", " ", "Application", " ", " "],
        ["PhotoshopªÑPicture file", "8BPS", "****", "Unspecified Creator", "Graphics Photoshop", "PSD", " "],
        [" ", "4DET", "????", "Unspecified Creator", "4D data file", " ", " "],
        ["Marathon", "APPL", 26.2, " ", "Application/Game", " ", " "],
        ["MarathonÑMusic", "msik", 26.2, "Marathon", "Game", " ", " "],
        ["Odd", "ðððð", "Odd1", "Odd Tools", "Thing", " ", " "],
        ["Too long", "TEXTS", "Long", "Long", "Text", " ", " "],
        [" ", "****", "MSWD", "Microsoft Word", "Common Creators", " ", " "],
        ["Nothing", "NOPE", "none", " ", " ", " ", " "],
    ];

    private static TypeCreatorDatabase Load(object?[][]? rows = null, bool inline = false) =>
        TypeCreatorDatabase.Load(new MemoryStream(XlsxBuilder.Xlsx(rows ?? Rows, inline)));

    [Fact]
    public void A_pairs_kind_is_its_example_names_application_and_document_parts()
    {
        var kind = Load().Find(F("AIFF"), F("ttxt"));
        Assert.NotNull(kind);
        Assert.Equal("SimpleText AIFF Sound File", kind.Text);         // the first row of the pair wins
        Assert.Equal(FinderKindSource.Database, kind.Source);
        Assert.Equal("SimpleText", kind.Application);
        Assert.Null(kind.ResourceId);
    }

    [Fact]
    public void Without_a_separator_the_category_names_it_else_the_application()
    {
        var database = Load();
        Assert.Equal("Graphics GIF", database.Find(F("GIFf"), F("ttxt"))?.Text);
        Assert.Equal("SimpleText/TeachText document", database.Find(F("CNFG"), F("ttxt"))?.Text);
        Assert.Null(database.Find(F("NOPE"), F("none")));              // nothing to call it
    }

    [Fact]
    public void Any_creator_rows_name_a_type_for_every_creator()
    {
        var database = Load();
        var photoshop = database.Find(F("8BPS"), F("WXYZ"));
        Assert.Equal("Photoshop™ Picture file", photoshop?.Text);      // the text decoded back to Mac Roman
        Assert.Null(photoshop?.Application);                            // "Unspecified Creator" is no application
        Assert.Equal("4D data file", database.Find(F("4DET"), F("ABCD"))?.Text);
    }

    [Fact]
    public void Applications_are_named_by_their_APPL_rows_and_common_creator_rows()
    {
        var database = Load();
        Assert.Equal("SimpleText/TeachText", database.ApplicationName(F("ttxt")));
        Assert.Equal("Microsoft Word", database.ApplicationName(F("MSWD")));
        Assert.Null(database.ApplicationName(F("WXYZ")));
    }

    [Fact]
    public void Codes_are_Mac_Roman_bytes_and_numbers_or_wrong_lengths_are_skipped()
    {
        var database = Load();
        Assert.Equal("Thing", database.Find(new FourCC(0xF0F0F0F0), F("Odd1"))?.Text);
        Assert.Null(database.Find(F("msik"), F("26.2")));               // Excel made the creator a number
        Assert.Null(database.ApplicationName(F("26.2")));
        Assert.Equal(6, database.Count);                                // pairs and any-creator types (APPL rows only name)
    }

    [Fact]
    public void Inline_strings_and_columns_found_by_their_headers_are_read()
    {
        object?[][] rows =
        [
            ["Creator", "Type", "File Name", "Comments", "Category"],
            ["ttxt", "TEXT", "SimpleTextÑText File", "SimpleText", "Text File"],
        ];
        Assert.Equal("SimpleText Text File", Load(rows, inline: true).Find(F("TEXT"), F("ttxt"))?.Text);
    }

    [Fact]
    public void A_file_that_is_not_such_a_spreadsheet_is_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => TypeCreatorDatabase.Load(new MemoryStream([1, 2, 3, 4])));
        Assert.Throws<InvalidDataException>(() => TypeCreatorDatabase.Load(new MemoryStream(XlsxBuilder.Zip(("xl/workbook.xml", "<workbook/>")))));
        Assert.Throws<InvalidDataException>(() => TypeCreatorDatabase.Load(new MemoryStream(XlsxBuilder.Zip(("xl/worksheets/sheet1.xml", "<worksheet><sheetData>")))));
        Assert.Throws<InvalidDataException>(() => Load([["A", "B"], ["x", "y"]]));      // no Type and Creator columns
    }

    [Fact]
    public void The_database_comes_after_the_Finder_and_the_built_in_table()
    {
        var database = Load(
        [
            Header,
            ["SimpleTextÑtheir words", "TEXT", "ttxt", "SimpleText", "Text File"],
            ["ThingÑthing document", "THNG", "Thng", "Thing Maker", "Thing"],
            ["Thing Maker", "APPL", "Thg2", " ", "Application"],
            [" ", "ANYT", "****", "Unspecified Creator", "Anything file"],
            ["SimpleTextÑodd file", "ODDF", "ttxt", "SimpleText", "Odd"],
        ]);
        Assert.Equal("SimpleText odd file", KnownKinds.Resolve(null, F("ODDF"), F("ttxt"), database).Text);   // before "SimpleText document"
        Assert.Equal("SimpleText document", KnownKinds.Resolve(null, F("ODDF"), F("ttxt")).Text);
        var table = KnownKinds.Resolve(null, F("TEXT"), F("ttxt"), database);
        Assert.Equal(("SimpleText text document", FinderKindSource.BuiltIn), (table.Text, table.Source));
        var system = KnownKinds.Resolve(null, F("APPL"), F("Thg2"), database);
        Assert.Equal("application program", system.Text);
        var pair = KnownKinds.Resolve(null, F("THNG"), F("Thng"), database);
        Assert.Equal(("Thing thing document", FinderKindSource.Database), (pair.Text, pair.Source));
        Assert.Equal("Anything file", KnownKinds.Resolve(null, F("ANYT"), F("ZZZZ"), database).Text);
        var named = KnownKinds.Resolve(null, F("ZZZZ"), F("Thg2"), database);
        Assert.Equal(("Thing Maker document", FinderKindSource.Database, "Thing Maker"), (named.Text, named.Source, named.Application));
        Assert.Equal("document", KnownKinds.Resolve(null, F("ZZZZ"), F("Thg2")).Text);
        Assert.Equal("TCDB (your copy)", KnownKinds.Describe(pair));
    }
}
