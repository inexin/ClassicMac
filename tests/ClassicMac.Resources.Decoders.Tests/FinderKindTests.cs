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

        Assert.Equal((1, 0), (reads[0], reads[1]));                           // the creator's fork, read once and kept

        // A type the application does not name: its 'apnm' kind and "document" (step 2, before its file name). Not found
        // among the creator's files, every file with a bundle is read once, since a 'kind' may be signed for another.
        Assert.Equal(new FinderKind("SimpleText document", FinderKindSource.ApplicationName, "SimpleText", 128), resolver.Find(F("MooV"), F("ttxt")));
        Assert.Equal((1, 1), (reads[0], reads[1]));

        // A 'kind' serves the creator it is signed for, whichever file holds it [Code: Finder 9.2.2].
        Assert.Equal(new FinderKind("someone else's", FinderKindSource.ApplicationKind, null, 129), resolver.Find(F("TEXT"), F("ABCD")));
        Assert.Equal((1, 1), (reads[0], reads[1]));
    }

    private static byte[] RawKind(string signature, short region, short reserved, params (string Type, string Kind)[] entries)
    {
        var data = Kind(signature, region, entries);
        data[6] = (byte)(reserved >> 8);
        data[7] = (byte)reserved;
        return data;
    }

    [Fact]
    public void Only_valid_kinds_of_the_systems_region_are_used_and_strings_are_cut_to_63()
    {
        var fork = Fork(("kind", 128, RawKind("ABCD", 3, 0, ("TEXT", "French"))),                // another region
            ("kind", 129, RawKind("ABCD", 0, 1, ("TEXT", "reserved word set"))),                    // ignored
            ("kind", 130, RawKind("ABCD", 0, 0, ("TEXT", new string('x', 70)), ("PICT", "pic"))));
        var resolver = new FinderKindResolver([new FinderApplicationSource(F("ABCD"), "App", () => fork)], []);
        Assert.Equal((new string('x', 63), (short?)130), (resolver.Find(F("TEXT"), F("ABCD"))!.Text, resolver.Find(F("TEXT"), F("ABCD"))!.ResourceId));
        var french = new FinderKindResolver([new FinderApplicationSource(F("ABCD"), "App", () => fork)], [], region: 3);
        Assert.Equal("French", french.Find(F("TEXT"), F("ABCD"))!.Text);
        Assert.Equal("App document", french.Find(F("PICT"), F("ABCD"))!.Text);                 // region 3 has no PICT
    }

    [Fact]
    public void A_kind_signed_for_another_creator_does_not_replace_the_creators_own()
    {
        // ColorSync-like: an extension (creator 'Sync') names 'sync' files; another file signed 'sync' only adds.
        var own = Fork(("kind", 128, Kind("sync", 0, ("prof", "own profile"))));
        var other = Fork(("kind", 128, Kind("sync", 0, ("prof", "intruder"), ("cswi", "workflow"))));
        var resolver = new FinderKindResolver(
        [
            new FinderApplicationSource(F("sync"), "Owner", () => own),
            new FinderApplicationSource(F("Sync"), "ColorSync Extension", () => other) { IsApplication = false },
        ], []);
        Assert.Equal("own profile", resolver.Find(F("prof"), F("sync"))!.Text);
        Assert.Equal("workflow", resolver.Find(F("cswi"), F("sync"))!.Text);
    }

    [Fact]
    public void The_finders_and_systems_strings_come_from_the_volume()
    {
        static byte[] StrList(params string[] items) =>
            [(byte)(items.Length >> 8), (byte)items.Length, .. items.SelectMany(i => (byte[])[(byte)i.Length, .. MacRoman.Encode(i)])];
        byte[] fmap = [.. "INIT"u8, 0, 0, 0, 2, .. "cdev"u8, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        var finder = Fork(("STR ", 6902, [(byte)"Programm".Length, .. MacRoman.Encode("Programm")]), ("fmap", 5111, fmap),
            ("STR#", 5100, StrList("Kontrollfeld", "Systemerweiterung")));
        var systemResources = Fork(("STR#", -16552, StrList("Dokument", "^0-Dokument")));
        var resolver = new FinderKindResolver([new FinderApplicationSource(F("TCH "), "Teach", () => null)], [systemResources, finder]);
        Assert.Equal(new FinderKind("Programm", FinderKindSource.FinderKind, null, 6902), resolver.FindFinderKind(F("APPL")));
        Assert.Equal(new FinderKind("Systemerweiterung", FinderKindSource.FinderKind, null, 5100), resolver.FindFinderKind(F("INIT")));
        Assert.Equal("Kontrollfeld", resolver.FindFinderKind(F("cdev"))!.Text);
        Assert.Null(resolver.FindFinderKind(F("TEXT")));
        Assert.Equal("Teach-Dokument", resolver.Find(F("TEXT"), F("TCH "))!.Text);
        Assert.Equal("Dokument", resolver.DocumentWord);
        Assert.Equal(64, resolver.DocumentOf(new string('n', 80)).Length);

        var none = new FinderKindResolver([], []);
        Assert.Null(none.FindFinderKind(F("APPL")));
        Assert.Null(none.DocumentWord);
        Assert.Equal("Teach document", none.DocumentOf("Teach"));
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
        Assert.Equal((1, 1), (reads[0], reads[1]));                           // no file has that creator: every one is read once
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

    private sealed record VolumeFile(string Name, string Type, string Creator, ushort Flags, Func<ResourceFork?> Fork);

    [Fact]
    public void A_volumes_files_give_the_applications_and_the_systems_forks_read_when_needed()
    {
        var reads = new List<string>();
        Func<ResourceFork?> Reading(string name, ResourceFork? fork) => () => { reads.Add(name); return fork; };
        var simpleText = Fork(("kind", 128, Kind("ttxt", 0, ("TEXT", "SimpleText text document"))));
        var system = Fork(("kind", -16550, Kind("istd", 0, ("PICT", "PICT document"))));
        VolumeFile[] files =
        [
            new("SimpleText", "APPL", "ttxt", 0, Reading("SimpleText", simpleText)),
            new("Iomega Driver", "INIT", "gZip", 0x2000, Reading("Iomega Driver", Fork(("kind", 128, Kind("gZip", 0, ("INIT", "Iomega Extension")))))),
            new("Read Me", "TEXT", "ttxt", 0, Reading("Read Me", null)),                          // a document: never read
            new("System Resources", "zsyr", "MACS", 0, Reading("System Resources", system)),
            new("Notes", "ttro", "ttxt", 0x2000, Reading("Notes", null)),                         // a bundle but not first for ttxt
        ];
        var resolver = FinderKindResolver.ForFiles(files, f => F(f.Type), f => F(f.Creator), f => f.Flags, f => f.Name, f => f.Fork());
        Assert.Empty(reads);                                                                     // nothing read yet
        Assert.Equal("SimpleText text document", resolver.Find(F("TEXT"), F("ttxt"))!.Text);
        Assert.Equal(["SimpleText", "Notes"], reads);                         // the files with that creator
        Assert.Equal("Iomega Extension", resolver.Find(F("INIT"), F("gZip"))!.Text);           // a file with a bundle counts too
        Assert.Equal("PICT document", resolver.Find(F("PICT"), F("WXYZ"))!.Text);
        Assert.Equal(["SimpleText", "Notes", "Iomega Driver", "System Resources"], reads);
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
