using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.Resources.Decoders.Tests;

// ClassicMac's own table of known kinds, applications and resource types (finder.md §5), and the order that puts it
// after what the volume's applications and System say.
public class KnownKindsTests
{
    private static FourCC F(string s) => FourCC.FromString(s);

    [Theory]
    [InlineData("TEXT", "ttxt", "SimpleText text document")]
    [InlineData("TEXT", "hbwr", "Apple Help page")]
    [InlineData("WDBN", "MSWD", "Microsoft Word 3–5 document")]
    [InlineData("SIT!", "SIT!", "StuffIt archive")]
    [InlineData("SIT!", "SITx", "StuffIt archive")]                 // the type alone, any creator
    [InlineData("PACT", "CPCT", "Compact Pro archive")]
    [InlineData("PNTG", "MPNT", "MacPaint document")]
    [InlineData("STAK", "WILD", "HyperCard stack")]
    [InlineData("rohd", "ddsk", "Disk Copy read-only disk image")]
    [InlineData("TEXT", "ABCD", "text document")]                   // a type known for any creator
    public void Documents_are_named_by_type_and_creator_then_by_type(string type, string creator, string kind) =>
        Assert.Equal(kind, KnownKinds.Document(F(type), F(creator)));

    [Fact]
    public void Unknown_documents_have_no_kind_in_the_table() => Assert.Null(KnownKinds.Document(F("ZZZZ"), F("WXYZ")));

    [Theory]
    [InlineData("APPL", "application program")]
    [InlineData("APPC", "application program")]
    [InlineData("INIT", "system extension")]
    [InlineData("FFIL", "font suitcase")]
    [InlineData("zsys", "system file")]
    [InlineData("cdev", "control panel")]
    [InlineData("clpt", "text clipping")]
    [InlineData("clpx", "clipping")]
    [InlineData("ilht", "web page location")]
    [InlineData("appe", "system extension")]
    public void System_types_have_the_Finders_kinds_in_ClassicMacs_words(string type, string kind) =>
        Assert.Equal(kind, KnownKinds.System(F(type)));

    [Fact]
    public void Applications_and_resource_types_are_named()
    {
        Assert.Equal("SimpleText", KnownKinds.ApplicationName(F("ttxt")));
        Assert.Equal("Microsoft Word", KnownKinds.ApplicationName(F("MSWD")));
        Assert.Null(KnownKinds.ApplicationName(F("WXYZ")));
        Assert.Equal(("Text style", "Text styles"), KnownKinds.ResourceType(F("styl")));
        Assert.Equal(("Kind list", "Kind lists"), KnownKinds.ResourceType(F("kind")));
        Assert.Null(KnownKinds.ResourceType(F("WXYZ")));
    }

    private static FinderKindResolver Volume() => new(
    [
        new FinderApplicationSource(F("ttxt"), "SimpleText", () =>
        {
            var fork = new ResourceFork();
            fork.Add(new Resource(F("kind"), 128, FinderKindTests.Kind("ttxt", 0, ("TEXT", "SimpleText's own words"))));
            return fork;
        }),
        new FinderApplicationSource(F("kcmr"), "Keychain Access", () =>
        {
            var fork = new ResourceFork();
            fork.Add(new Resource(F("kind"), 128, FinderKindTests.Kind("kcmr", 0, ("APPL", "Keychain Access"))));
            return fork;
        }),
    ], [Standard()]);

    private static ResourceFork Standard()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(F("kind"), -16550, FinderKindTests.Kind("istd", 0, ("TEXT", "Text document"), ("PICT", "PICT document"))));
        return fork;
    }

    [Fact]
    public void The_volume_comes_first_then_the_table_then_the_systems_standard_kinds()
    {
        var volume = Volume();
        Assert.Equal(new FinderKind("SimpleText's own words", FinderKindSource.ApplicationKind, "SimpleText", 128), KnownKinds.Resolve(volume, F("TEXT"), F("ttxt")));
        Assert.Equal(new FinderKind("SimpleText document", FinderKindSource.ApplicationName, "SimpleText", null), KnownKinds.Resolve(volume, F("MooV"), F("ttxt")));

        // The creator's application is not there: the table's pair, then the System's standard kind, then the table's type.
        Assert.Equal(new FinderKind("Apple Help page", FinderKindSource.BuiltIn, "Apple Help Viewer", null), KnownKinds.Resolve(volume, F("TEXT"), F("hbwr")));
        Assert.Equal(new FinderKind("Text document", FinderKindSource.SystemKind, null, -16550), KnownKinds.Resolve(volume, F("TEXT"), F("WXYZ")));
        Assert.Equal(new FinderKind("StuffIt archive", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(volume, F("SIT!"), F("WXYZ")));

        // A known application that is not here: "<name> document"; nothing known: "document".
        Assert.Equal(new FinderKind("Microsoft Word document", FinderKindSource.BuiltIn, "Microsoft Word", null), KnownKinds.Resolve(volume, F("ZZZZ"), F("MSWD")));
        Assert.Equal(new FinderKind("document", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(volume, F("ZZZZ"), F("WXYZ")));
        Assert.Equal(new FinderKind("document", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("ZZZZ"), F("WXYZ")));
    }

    [Fact]
    public void Applications_and_system_files_take_the_Finders_kind_even_when_they_name_one()
    {
        var volume = Volume();
        // Keychain Access's 'kind' names APPL, but the Finder decides an application's kind itself [Code: Finder 9.2.2].
        Assert.Equal(new FinderKind("application program", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(volume, F("APPL"), F("kcmr")));
        Assert.Equal(new FinderKind("control panel", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("cdev"), F("WXYZ")));
        Assert.Equal(new FinderKind("system file", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("FNDR"), F("MACS")));
        Assert.Equal(new FinderKind("system file", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("FNDR"), F("fred")));   // Login
        Assert.Equal(new FinderKind("system file", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("CLIP"), F("MACS")));
        Assert.Equal(new FinderKind("SimpleText edition", FinderKindSource.BuiltIn, "SimpleText", null), KnownKinds.Resolve(volume, F("edtp"), F("ttxt")));
        Assert.Equal(new FinderKind("edition", FinderKindSource.BuiltIn, null, null), KnownKinds.Resolve(null, F("edtp"), F("WXYZ")));
    }

    [Theory]
    [InlineData(FinderKindSource.ApplicationKind, "SimpleText", (short)128, "from SimpleText’s 'kind' 128")]
    [InlineData(FinderKindSource.ApplicationName, "SimpleText", (short)128, "from SimpleText’s name in its 'kind' 128")]
    [InlineData(FinderKindSource.ApplicationName, "Teach", null, "from Teach, by its file name")]
    [InlineData(FinderKindSource.SystemKind, null, (short)-16550, "from the System’s 'kind' -16550")]
    [InlineData(FinderKindSource.FinderKind, null, (short)6902, "from the Finder’s 'STR ' 6902")]
    [InlineData(FinderKindSource.FinderKind, null, (short)5100, "from the Finder’s 'STR#' 5100")]
    [InlineData(FinderKindSource.BuiltIn, null, null, "built-in")]
    public void Sources_are_described(FinderKindSource source, string? application, short? id, string text) =>
        Assert.Equal(text, KnownKinds.Describe(new FinderKind("k", source, application, id)));
}
