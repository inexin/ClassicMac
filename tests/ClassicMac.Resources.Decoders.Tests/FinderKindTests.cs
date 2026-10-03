using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;

namespace ClassicMac.Resources.Decoders.Tests;

// A document's kind as the Finder names it (docs/formats/resources/finder.md §1.4, §2.3): the creator application's 'kind'
// resources, then the application's name, then the System's kinds for standard types.
public class FinderKindTests
{
    private static FourCC F(string s) => FourCC.FromString(s);

    private static byte[] Pascal(string text) => [(byte)MacRoman.Encode(text).Length, .. MacRoman.Encode(text)];

    // A 'kind' resource: signature, region, a zero word, the count, then (type, Pascal string) entries, each word-aligned.
    internal static byte[] Kind(string signature, short region, params (string Type, string Kind)[] entries)
    {
        var bytes = new List<byte>([.. MacRoman.Encode(signature), (byte)(region >> 8), (byte)region, 0, 0, (byte)(entries.Length >> 8), (byte)entries.Length]);
        foreach (var (type, kind) in entries)
        {
            bytes.AddRange(MacRoman.Encode(type));
            bytes.AddRange(Pascal(kind));
            if (bytes.Count % 2 != 0)
            {
                bytes.Add(0);
            }
        }

        return [.. bytes];
    }

    private static ResourceFork Fork(params (string Type, short Id, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, data) in resources)
        {
            fork.Add(new Resource(F(type), id, data));
        }

        return fork;
    }

    [Fact]
    public void A_kind_resource_reads_its_signature_region_and_entries()
    {
        var kind = FinderResources.ReadKind(Kind("ttxt", 0, ("apnm", "SimpleText"), ("TEXT", "SimpleText text document"), ("PICT", "pic")), out var complete);
        Assert.True(complete);
        Assert.Equal((F("ttxt"), (short)0), (kind.Signature, kind.Region));
        Assert.Equal([(F("apnm"), "SimpleText"), (F("TEXT"), "SimpleText text document"), (F("PICT"), "pic")], kind.Entries);
        Assert.Equal("SimpleText", kind.ApplicationName);
        Assert.Equal("pic", kind.KindOf(F("PICT")));
        Assert.Null(kind.KindOf(F("MooV")));

        var noName = FinderResources.ReadKind(Kind("ABCD", 3, ("TEXT", "t")), out _);
        Assert.Equal((short)3, noName.Region);
        Assert.Null(noName.ApplicationName);
    }

    [Fact]
    public void A_short_kind_resource_keeps_what_was_read()
    {
        var data = Kind("ttxt", 0, ("TEXT", "SimpleText text document"), ("PICT", "SimpleText picture"));
        var cut = FinderResources.ReadKind(data.AsMemory(0, data.Length - 5), out var complete);
        Assert.False(complete);
        Assert.Equal([(F("TEXT"), "SimpleText text document")], cut.Entries);
        var header = FinderResources.ReadKind(data.AsMemory(0, 6), out complete);
        Assert.False(complete);
        Assert.Equal((F("ttxt"), 0), (header.Signature, header.Entries.Count));
    }

    private static FinderKindResolver Resolver(out int[] reads, IEnumerable<ResourceFork>? system = null)
    {
        var counts = new int[2];
        reads = counts;
        var simpleText = Fork(("kind", 128, Kind("ttxt", 0, ("apnm", "SimpleText"), ("TEXT", "SimpleText text document"), ("PICT", "SimpleText picture"))),
            ("kind", 129, Kind("ABCD", 0, ("TEXT", "someone else's"))));                    // another signature's: not used
        var teach = Fork(("BNDL", 128, [.. "TCH "u8, 0, 0, 0, 0, .. "FREF"u8, 0, 0, 0, 0, 0, 128]), ("FREF", 128, [.. "TEXT"u8, 0, 0, 0]));
        return new FinderKindResolver(
        [
            new FinderApplicationSource(F("ttxt"), "SimpleText 1.4", () => { counts[0]++; return simpleText; }),
            new FinderApplicationSource(F("TCH "), "Teach", () => { counts[1]++; return teach; }),
        ], system ?? []);
    }

    [Fact]
    public void The_creator_applications_kind_names_the_document()
    {
        var resolver = Resolver(out var reads);
        var kind = resolver.Find(F("TEXT"), F("ttxt"))!;
        Assert.Equal(new FinderKind("SimpleText text document", FinderKindSource.ApplicationKind, "SimpleText", 128), kind);
        Assert.Equal("SimpleText picture", resolver.Find(F("PICT"), F("ttxt"))!.Text);

        // A type the application does not name: its name (from 'apnm') and "document".
        Assert.Equal(new FinderKind("SimpleText document", FinderKindSource.ApplicationName, "SimpleText", null), resolver.Find(F("MooV"), F("ttxt")));
        Assert.Equal(1, reads[0]);                                            // the fork is read once and cached
        Assert.Equal(0, reads[1]);                                            // and only the creator's
    }

    [Fact]
    public void An_application_with_no_kind_resource_names_its_documents_by_its_file_name()
    {
        var resolver = Resolver(out var reads);
        Assert.Equal(new FinderKind("Teach document", FinderKindSource.ApplicationName, "Teach", null), resolver.Find(F("TEXT"), F("TCH ")));
        Assert.Equal(1, reads[1]);
        Assert.Equal("Teach", resolver.ApplicationName(F("TCH ")));
        Assert.Equal("SimpleText", resolver.ApplicationName(F("ttxt")));
        Assert.Null(resolver.ApplicationName(F("WXYZ")));
    }

    [Fact]
    public void Without_the_application_the_systems_standard_kinds_apply_else_nothing()
    {
        var system = Fork(("kind", -16550, Kind("istd", 0, ("PICT", "PICT document"), ("TEXT", "Text document"))));
        var resolver = Resolver(out var reads, [system]);
        Assert.Equal(new FinderKind("PICT document", FinderKindSource.SystemKind, null, -16550), resolver.Find(F("PICT"), F("WXYZ")));
        Assert.Null(resolver.Find(F("ZZZZ"), F("WXYZ")));                     // left to the caller's table
        Assert.Equal(0, reads[0] + reads[1]);
        // The application's own kinds come first.
        Assert.Equal("SimpleText text document", resolver.Find(F("TEXT"), F("ttxt"))!.Text);
    }

    [Fact]
    public void A_damaged_application_fork_or_kind_resource_is_passed_over()
    {
        var bad = new FinderKindResolver(
        [
            new FinderApplicationSource(F("BAD1"), "Broken", () => throw new InvalidDataException("damaged")),
            new FinderApplicationSource(F("BAD2"), "Short", () => Fork(("kind", 128, [.. "BAD2"u8, 0]))),
            new FinderApplicationSource(F("NONE"), "No fork", () => null),
        ], []);
        Assert.Equal("Broken document", bad.Find(F("TEXT"), F("BAD1"))!.Text);
        Assert.Equal("Short document", bad.Find(F("TEXT"), F("BAD2"))!.Text);
        Assert.Equal("No fork document", bad.Find(F("TEXT"), F("NONE"))!.Text);
    }

    [Theory]
    [InlineData("APPL", true)]
    [InlineData("APPC", true)]
    [InlineData("APPD", true)]
    [InlineData("appe", true)]
    [InlineData("TEXT", false)]
    public void Applications_are_found_by_their_file_types(string type, bool application) =>
        Assert.Equal(application, FinderKindResolver.IsApplicationType(F(type)));
}
