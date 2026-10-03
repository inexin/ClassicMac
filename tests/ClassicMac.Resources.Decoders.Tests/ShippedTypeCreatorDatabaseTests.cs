using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.Resources.Decoders.Tests;

// The type and creator data ClassicMac ships (TCDB, as its own UTF-8 TSV, gzipped; finder.md §2.6): the format on small
// fixtures, the shipped resource itself, and its place in the kinds' order.
public class ShippedTypeCreatorDatabaseTests
{
    private static FourCC F(string s) => FourCC.FromString(s);

    private const string Head = "# test data\nType\tCreator\tFile name\tComments\tCategory\tExtension\n";

    private static TypeCreatorDatabase Read(string rows) =>
        TypeCreatorDatabase.ReadTsv(new StringReader(Head + rows));

    [Fact]
    public void Rows_give_kinds_and_application_names_by_the_spreadsheets_rules()
    {
        var database = Read(
            "AIFF\tttxt\tSimpleText—AIFF Sound File\tSimpleText\tSound AIFF\tAIF\n" +
            "AIFF\tttxt\tSimpleText—second, ignored\tSimpleText\tSound AIFF\tAIF\n" +
            "GIFf\tttxt\tcalendar.gif\tSimpleText/TeachText\tGraphics GIF\tGIF\n" +
            "APPL\tttxt\tSimpleText/TeachText\t\tApplication\t\n" +
            "8BPS\t****\tPhotoshop™—Picture file\tUnspecified Creator\tGraphics Photoshop\tPSD\n" +
            "****\tMSWD\t\tMicrosoft Word\tCommon Creators\t\n");
        Assert.Equal(new FinderKind("SimpleText AIFF Sound File", FinderKindSource.Database, "SimpleText", null), database.Find(F("AIFF"), F("ttxt")));
        Assert.Equal("Graphics GIF", database.Find(F("GIFf"), F("ttxt"))?.Text);
        Assert.Equal("Photoshop™ Picture file", database.Find(F("8BPS"), F("ABCD"))?.Text);
        Assert.Equal("SimpleText/TeachText", database.ApplicationName(F("ttxt")));
        Assert.Equal("Microsoft Word", database.ApplicationName(F("MSWD")));
        Assert.Equal(FinderKindSource.Database, database.Source);
        Assert.Equal(6, database.RecordCount);
        Assert.Equal(3, database.Count);
    }

    [Fact]
    public void Codes_are_four_bytes_with_escapes_for_the_others()
    {
        var database = Read(
            "\\xF0\\xF0\\xF0\\xF0\tOdd1\tOdd—thing\t\t\t\n" +
            " MUD\tMu\\x5Cd\tMud—file\t\t\t\n" +
            "TEXT\tabc\tShort—skipped\t\t\t\n" +
            "#BIN\tBnHq\tBinHex—binary file\t\t\t\n");                             // a record, not a comment
        Assert.Equal("Odd thing", database.Find(new FourCC(0xF0F0F0F0), F("Odd1"))?.Text);
        Assert.Equal("Mud file", database.Find(F(" MUD"), F("Mu\\d"))?.Text);       // spaces kept inside codes
        Assert.Null(database.Find(F("TEXT"), F("abc ")));
        Assert.Equal("BinHex binary file", database.Find(F("#BIN"), F("BnHq"))?.Text);
        Assert.Equal((3, 4), (database.Count, database.RecordCount));
    }

    [Theory]
    [InlineData("Type\tCreator\tFile name\n")]                                   // too few columns in the header
    [InlineData("Kind\tCreator\tFile name\tComments\tCategory\tExtension\n")]    // not the header
    [InlineData(Head + "TEXT\tttxt\tonly three\n")]                              // a row too short
    [InlineData(Head + "TE\\xZZ\tttxt\tbad escape\t\t\t\n")]
    public void A_damaged_file_is_invalid_data(string text) =>
        Assert.Throws<InvalidDataException>(() => TypeCreatorDatabase.ReadTsv(new StringReader(text)));

    [Fact]
    public void The_shipped_data_loads_once_with_every_record()
    {
        if (!TypeCreatorDatabase.HasShippedData)
        {
            Assert.Skip("This build has no tcdb.tsv.gz (tools/TcdbData writes it).");
        }

        var shipped = TypeCreatorDatabase.Shipped;
        Assert.Same(shipped, TypeCreatorDatabase.Shipped);
        Assert.Equal(FinderKindSource.Database, shipped.Source);
        Assert.Equal(44_181, shipped.RecordCount);                             // TCDB 2003.10 less filext.com's and odd codes
        Assert.Equal("Microsoft Word", shipped.ApplicationName(F("MSWD")));
        Assert.Equal("TCDB", KnownKinds.Describe(shipped.Find(F("AIFF"), F("ttxt"))!));
    }

    [Fact]
    public void The_shipped_data_comes_after_the_Finder_and_the_built_in_table_and_a_users_copy_replaces_it()
    {
        if (!TypeCreatorDatabase.HasShippedData)
        {
            Assert.Skip("This build has no tcdb.tsv.gz (tools/TcdbData writes it).");
        }

        Assert.Equal(FinderKindSource.BuiltIn, KnownKinds.Resolve(null, F("TEXT"), F("ttxt")).Source);
        var shipped = KnownKinds.Resolve(null, F("DCFL"), F("MSWD"));
        Assert.Equal((FinderKindSource.Database, "TCDB"), (shipped.Source, KnownKinds.Describe(shipped)));
        var users = Read("ZZZZ\tWXYZ\tWidget—widget file\tWidget\t\t\n");
        Assert.Equal("Widget widget file", KnownKinds.Resolve(null, F("ZZZZ"), F("WXYZ"), users).Text);
        Assert.Equal("document", KnownKinds.Resolve(null, F("ZZZZ"), F("WXYZ")).Text);
    }
}
