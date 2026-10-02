using System.Text;
using System.Text.Json;
using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources.Export;
using static ClassicMac.Resources.Decoders.Tests.CodeFixtures;

namespace ClassicMac.Resources.Decoders.Tests;

// The code decoders: CODE (segments and the jump table), 'cfrg', and code resources (PEF, standard header, driver,
// package, fat, raw), each writing the data as .bin first, then the listing (.s) and the model (.json).
public class CodeDecoderTests
{
    private static IResourceDecoder DecoderFor(string type) =>
        ResourceDecoders.Create().First(d => d.CanDecode(FourCC.FromString(type)));

    private static (IReadOnlyList<DecodedFile> Files, List<Diagnostic> Diagnostics) Decode(ResourceFork fork, string type, short id)
    {
        var resource = fork.Find(FourCC.FromString(type), id)!;
        var diagnostics = new List<Diagnostic>();
        var files = DecoderFor(type).Decode(new DecodeInput(resource, ResourceDecompression.Default.GetData(resource, fork), fork, diagnostics: diagnostics));
        return (files, diagnostics);
    }

    private static JsonElement Json(IReadOnlyList<DecodedFile> files) =>
        JsonDocument.Parse(files.Single(f => f.Extension == ".json").Content).RootElement.Clone();

    private static string Text(IReadOnlyList<DecodedFile> files, string extension) =>
        Encoding.UTF8.GetString(files.Single(f => f.Extension == extension).Content.Span);

    private static string[] Names(JsonElement array, string property = "name") =>
        array.EnumerateArray().Select(e => e.GetProperty(property).GetString()!).ToArray();

    [Theory]
    [InlineData("CODE", "code.segment")]
    [InlineData("cfrg", "code.cfrg")]
    [InlineData("ncod", "code.resource")]
    [InlineData("ndrv", "code.resource")]
    [InlineData("nsnd", "code.resource")]
    [InlineData("CDEF", "code.resource")]
    [InlineData("WDEF", "code.resource")]
    [InlineData("DRVR", "code.resource")]
    [InlineData("PACK", "code.resource")]
    [InlineData("XCMD", "code.resource")]
    [InlineData("INIT", "code.resource")]
    [InlineData("dcmp", "code.resource")]
    [InlineData("expt", "code.resource")]
    public void The_code_types_have_their_decoder(string type, string name)
    {
        var decoder = DecoderFor(type);
        Assert.Equal((name, 1), (decoder.Name, decoder.Version));
    }

    [Fact]
    public void CODE_0_is_the_data_and_the_jump_table()
    {
        var (files, diagnostics) = Decode(Application(), "CODE", 0);

        Assert.Equal([".bin", ".json"], files.Select(f => f.Extension));
        Assert.Equal(Application0, files[0].Content.ToArray());
        Assert.Empty(diagnostics);
        var json = Json(files);
        Assert.Equal((0x30, 0x100, 16, 0x20), (json.GetProperty("aboveA5").GetInt32(), json.GetProperty("belowA5").GetInt32(),
            json.GetProperty("jumpTableSize").GetInt32(), json.GetProperty("jumpTableOffset").GetInt32()));
        Assert.Equal(("unknown", false, false), (json.GetProperty("model").GetString(), json.GetProperty("farModel").GetBoolean(),
            json.GetProperty("powerPC").GetBoolean()));
        Assert.Equal((1, 4), (json.GetProperty("entry").GetProperty("segment").GetInt32(), json.GetProperty("entry").GetProperty("resourceOffset").GetInt32()));
        var entries = json.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal((1, 0x28, "nearUnloaded", 1, 6, 0xA),
            (entries[1].GetProperty("index").GetInt32(), entries[1].GetProperty("a5Offset").GetInt32(), entries[1].GetProperty("kind").GetString(),
                entries[1].GetProperty("segment").GetInt32(), entries[1].GetProperty("offset").GetInt32(), entries[1].GetProperty("resourceOffset").GetInt32()));
        Assert.False(json.TryGetProperty("originalEntry", out _));
    }

    [Fact]
    public void CODE_0_too_short_is_its_data_with_a_warning()
    {
        var (files, diagnostics) = Decode(Fork(Res("CODE", 0, [0, 0, 0, 1])), "CODE", 0);

        Assert.Equal([".bin"], files.Select(f => f.Extension));
        var d = Assert.Single(diagnostics);
        Assert.Equal((DiagnosticSeverity.Warning, "code.jump-table-unreadable"), (d.Severity, d.Code));
    }

    [Fact]
    public void The_application_diagnostics_go_to_CODE_0()
    {
        // Entry 1 names CODE 2, which is missing.
        var fork = Fork(Res("CODE", 0, Code0(NearEntry(0, 1), NearEntry(0, 2))), Res("CODE", 1, Code1, "Main"));

        Assert.Contains(Decode(fork, "CODE", 0).Diagnostics, d => d.Code == "m68k.segment-missing");
        Assert.DoesNotContain(Decode(fork, "CODE", 1).Diagnostics, d => d.Code == "m68k.segment-missing");
    }

    [Fact]
    public void A_segment_is_its_data_its_listing_and_its_model()
    {
        var fork = Application();
        var (files, diagnostics) = Decode(fork, "CODE", 1);

        Assert.Equal([".bin", ".s", ".json"], files.Select(f => f.Extension));
        Assert.Equal(Code1, files[0].Content.ToArray());
        Assert.Empty(diagnostics);
        var expected = CodeListing.ForSegment(CodeApplication.Read(fork, new List<Diagnostic>()), 1).Text;
        Assert.Equal(expected, Text(files, ".s"));
        Assert.Contains("jsr 42(a5)  ; CODE 1:+$A", expected, StringComparison.Ordinal);

        var json = Json(files);
        Assert.Equal((1, "Main", "unknown", true), (json.GetProperty("segment").GetInt32(), json.GetProperty("name").GetString(),
            json.GetProperty("model").GetString(), json.GetProperty("readable").GetBoolean()));
        var header = json.GetProperty("header");
        Assert.Equal((false, 0, 2), (header.GetProperty("far").GetBoolean(), header.GetProperty("firstNearOffset").GetInt32(), header.GetProperty("nearCount").GetInt32()));
        Assert.Equal([0, 1], json.GetProperty("jumpTableEntries").EnumerateArray().Select(e => e.GetProperty("index").GetInt32()));
        Assert.Equal([4, 0xA], json.GetProperty("functions").EnumerateArray().Select(e => e.GetProperty("offset").GetInt32()));
        Assert.Equal(["entry", "jumpTable"], json.GetProperty("functions").EnumerateArray().Select(e => e.GetProperty("source").GetString()));
        var reference = json.GetProperty("references").EnumerateArray().First();
        Assert.Equal((4, "jumpTable"), (reference.GetProperty("offset").GetInt32(), reference.GetProperty("kind").GetString()));
        Assert.Equal(0, json.GetProperty("relocations").GetArrayLength());
    }

    [Fact]
    public void Segments_of_one_fork_share_its_application()
    {
        // Two segments decoded in turn give the listings ForSegment gives; the second reuses the application read for the first.
        var fork = Fork(Res("CODE", 0, Code0(NearEntry(0, 1), NearEntry(0, 2))), Res("CODE", 1, Code1), Res("CODE", 2, Bytes("0001 0001 4E75")));
        var app = CodeApplication.Read(fork, new List<Diagnostic>());
        var decoder = DecoderFor("CODE");
        foreach (short id in new short[] { 1, 2, 1 })
        {
            var resource = fork.Find(FourCC.FromString("CODE"), id)!;
            var files = decoder.Decode(new DecodeInput(resource, resource.GetData(), fork));
            Assert.Equal(CodeListing.ForSegment(app, id).Text, Text(files, ".s"));
        }
    }

    [Fact]
    public void A_far_segment_lists_its_relocations()
    {
        // Far model; CODE 1 code at $28: lea ($00000100).l,a0 (A5); jsr ($00000034).l (PC); 34 rts; lists at $36.
        var code = Bytes("41F9 0000 0100 4EB9 0000 0034 4E75 1500 1800");
        var far = new BigEndianWriter();
        far.WriteUInt16(0xFFFF);
        far.WriteUInt16(0);
        foreach (uint v in new uint[] { 0, 1, 0, 0, 0x36, 0, 0x38, 0, 0 }) far.WriteUInt32(v);
        far.WriteBytes(code);
        var farEntry = Words(1, 0xA9F0, 0, 0x28);
        var fork = Fork(Res("CODE", 0, Code0(farEntry, Words(0, 0xFFFF, 0, 0))), Res("CODE", 1, far.ToArray()));

        var json = Json(Decode(fork, "CODE", 1).Files);

        Assert.Equal("mpwFar", json.GetProperty("model").GetString());
        var header = json.GetProperty("header");
        Assert.Equal((true, 0x36, 0x38), (header.GetProperty("far").GetBoolean(), header.GetProperty("a5RelocationOffset").GetInt32(),
            header.GetProperty("pcRelocationOffset").GetInt32()));
        Assert.Equal([(0x2A, "a5"), (0x30, "segment")],
            json.GetProperty("relocations").EnumerateArray().Select(r => (r.GetProperty("offset").GetInt32(), r.GetProperty("base").GetString())));
    }

    [Fact]
    public void A_segment_without_CODE_0_is_listed_alone()
    {
        var (files, diagnostics) = Decode(Fork(Res("CODE", 1, Code1)), "CODE", 1);

        Assert.Equal([".bin", ".s", ".json"], files.Select(f => f.Extension));
        Assert.StartsWith("; 'CODE' 1: 68k segment, near header\n", Text(files, ".s"), StringComparison.Ordinal);
        Assert.Empty(diagnostics);
        var json = Json(files);
        Assert.False(json.TryGetProperty("model", out _));
        Assert.Equal(0, json.GetProperty("jumpTableEntries").GetArrayLength());
    }

    [Fact]
    public void A_segment_with_a_damaged_CODE_0_is_listed_alone_with_a_warning()
    {
        var (files, diagnostics) = Decode(Fork(Res("CODE", 0, [0, 0]), Res("CODE", 1, Code1)), "CODE", 1);

        Assert.StartsWith("; 'CODE' 1: 68k segment, near header\n", Text(files, ".s"), StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Code == "code.listing-application" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_far_segment_alone_lists_its_A5_relocations()
    {
        var code = Bytes("41F9 0000 0100 4E75 1500");
        var far = new BigEndianWriter();
        far.WriteUInt16(0xFFFF);
        far.WriteUInt16(0);
        foreach (uint v in new uint[] { 0, 0, 0, 0, 0x30, 0, 0, 0, 0 }) far.WriteUInt32(v);
        far.WriteBytes(code);

        var json = Json(Decode(Fork(Res("CODE", 1, far.ToArray())), "CODE", 1).Files);

        Assert.Equal([(0x2A, "a5")],
            json.GetProperty("relocations").EnumerateArray().Select(r => (r.GetProperty("offset").GetInt32(), r.GetProperty("base").GetString())));
    }

    [Fact]
    public void A_segment_too_short_for_its_header_has_no_header()
    {
        var (files, diagnostics) = Decode(Fork(Res("CODE", 1, [0x4E, 0x75])), "CODE", 1);

        Assert.Equal(JsonValueKind.Null, Json(files).GetProperty("header").ValueKind);
        Assert.Contains(diagnostics, d => d.Code == "m68k.segment-header");
    }

    [Fact]
    public void A_compressed_segment_that_cannot_be_decompressed_is_reported_on_CODE_0_and_not_listed()
    {
        // 'dcmp' 99 does not exist: the data stays compressed.
        var stored = new BigEndianWriter();
        stored.WriteUInt32(CompressedResourceHeader.Signature);
        stored.WriteUInt16(CompressedResourceHeader.Length);
        stored.WriteByte(9);
        stored.WriteByte(1);
        stored.WriteUInt32(Code1.Length);
        stored.WriteInt16(99);
        stored.WriteUInt16(0);
        stored.WriteByte(0);
        stored.WriteByte(0);
        stored.WriteBytes(Code1);
        var fork = Fork(Res("CODE", 0, Application0), Res("CODE", 1, stored.ToArray(), attributes: ResourceAttributes.Compressed));

        Assert.Contains(Decode(fork, "CODE", 0).Diagnostics, d => d.Code == "code.compressed");
        var (files, _) = Decode(fork, "CODE", 1);
        Assert.Contains("compressed and not readable", Text(files, ".s"), StringComparison.Ordinal);
        Assert.False(Json(files).GetProperty("readable").GetBoolean());
    }

    [Fact]
    public void The_bootstrap_entry_is_in_the_jump_table_model()
    {
        // CODE 2 enters at +$10: it holds the table's offset ($20) at +4 and the original entry (CODE 1 +$8) at +8.
        var code2 = Bytes("0000 0001 0000 0020 0004 3F3C 0001 A9F0 4E75");
        var fork = Fork(Res("CODE", 0, Code0(NearEntry(0xC, 2))), Res("CODE", 1, Code1), Res("CODE", 2, code2));

        var json = Json(Decode(fork, "CODE", 0).Files);

        Assert.Equal((2, 0x10), (json.GetProperty("entry").GetProperty("segment").GetInt32(), json.GetProperty("entry").GetProperty("resourceOffset").GetInt32()));
        Assert.Equal((1, 8), (json.GetProperty("originalEntry").GetProperty("segment").GetInt32(), json.GetProperty("originalEntry").GetProperty("resourceOffset").GetInt32()));
    }

    [Fact]
    public void A_cfrg_is_its_data_and_its_members()
    {
        var data = Cfrg(("App", CfrgWhere.DataFork, 0x10, 0x200, 0, 0), ("Lib", CfrgWhere.Resource, 0, 0, 0x6E6C6962, 128));
        var (files, diagnostics) = Decode(Fork(Res("cfrg", 0, data)), "cfrg", 0);

        Assert.Equal([".bin", ".json"], files.Select(f => f.Extension));
        Assert.Empty(diagnostics);
        var json = Json(files);
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        var members = json.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["App", "Lib"], Names(json.GetProperty("members")));
        Assert.Equal(("pwpc", "application", "dataFork", 0x10, 0x200, 0x10000),
            (members[0].GetProperty("architecture").GetString(), members[0].GetProperty("usage").GetString(), members[0].GetProperty("where").GetString(),
                members[0].GetProperty("offset").GetInt32(), members[0].GetProperty("length").GetInt32(), members[0].GetProperty("usage1").GetInt32()));
        Assert.Equal(("resource", "nlib", 128), (members[1].GetProperty("where").GetString(), members[1].GetProperty("resourceType").GetString(),
            members[1].GetProperty("resourceId").GetInt32()));
        Assert.Equal((0x01108000, 0x01000000, 1), (members[0].GetProperty("currentVersion").GetInt32(), members[0].GetProperty("oldDefVersion").GetInt32(),
            members[0].GetProperty("updateLevel").GetInt32()));
    }

    [Fact]
    public void A_cfrg_that_does_not_read_is_its_data_with_a_warning()
    {
        var (files, diagnostics) = Decode(Fork(Res("cfrg", 0, [0, 1])), "cfrg", 0);

        Assert.Equal([".bin"], files.Select(f => f.Extension));
        Assert.Equal("code.cfrg-unreadable", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_native_code_resource_is_its_fragment()
    {
        var (files, diagnostics) = Decode(Fork(Res("ncod", 5, Fragment(), "Native")), "ncod", 5);

        Assert.Equal([".bin", ".s", ".json"], files.Select(f => f.Extension));
        Assert.Empty(diagnostics);
        Assert.StartsWith("; 'ncod' 5 \"Native\": PowerPC fragment ('pwpc'), 3 sections\n", Text(files, ".s"), StringComparison.Ordinal);
        var json = Json(files);
        Assert.Equal(("ncod", 5, "pef"), (json.GetProperty("type").GetString(), json.GetProperty("id").GetInt32(), json.GetProperty("format").GetString()));
        var fragment = json.GetProperty("fragment");
        Assert.Equal("pwpc", fragment.GetProperty("architecture").GetString());
        Assert.Equal(["code", "unpackedData", "loader"], fragment.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
        Assert.Equal((1, 0), (fragment.GetProperty("main").GetProperty("section").GetInt32(), fragment.GetProperty("main").GetProperty("offset").GetInt32()));
        var import = Assert.Single(fragment.GetProperty("imports").EnumerateArray());
        Assert.Equal(("InterfaceLib", "InitGraf", "tVector", false), (import.GetProperty("library").GetString(), import.GetProperty("name").GetString(),
            import.GetProperty("class").GetString(), import.GetProperty("weak").GetBoolean()));
        var export = Assert.Single(fragment.GetProperty("exports").EnumerateArray());
        Assert.Equal(("Helper", 1, 0x14), (export.GetProperty("name").GetString(), export.GetProperty("section").GetInt32(), export.GetProperty("value").GetInt32()));
        Assert.Equal(["main", ".InitGraf", "Helper"], Names(json.GetProperty("functions")));
        Assert.Equal(0, json.GetProperty("functions")[1].GetProperty("section").GetInt32());
        Assert.Contains(json.GetProperty("references").EnumerateArray(), r => r.GetProperty("kind").GetString() == "glue"
            && r.GetProperty("text").GetString() == "InterfaceLib::InitGraf");
    }

    [Fact]
    public void A_fragment_too_short_for_its_header_is_its_data_with_a_warning()
    {
        var (files, diagnostics) = Decode(Fork(Res("ncod", 5, [.. "Joy!peff"u8, 0, 0])), "ncod", 5);

        Assert.Equal([".bin"], files.Select(f => f.Extension));
        Assert.Equal("code.pef-unreadable", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_standard_header_is_in_the_model()
    {
        var (files, _) = Decode(Fork(Res("CDEF", 0, StandardHeader)), "CDEF", 0);

        Assert.StartsWith("; 'CDEF' 0: 68k code resource\n; Standard header: 'CDEF' 0, version $0001\n", Text(files, ".s"), StringComparison.Ordinal);
        var json = Json(files);
        Assert.Equal("68k", json.GetProperty("format").GetString());
        var header = json.GetProperty("standardHeader");
        Assert.Equal(("CDEF", 0, 1, 0xC), (header.GetProperty("type").GetString(), header.GetProperty("id").GetInt32(), header.GetProperty("version").GetInt32(),
            header.GetProperty("entry").GetInt32()));
        Assert.Equal(["entry", "main"], Names(json.GetProperty("functions")));
    }

    [Fact]
    public void A_driver_is_in_the_model()
    {
        var json = Json(Decode(Fork(Res("DRVR", 12, Driver, ".D")), "DRVR", 12).Files);

        var driver = json.GetProperty("driver");
        Assert.Equal((".D", 0x4F00), (driver.GetProperty("name").GetString(), driver.GetProperty("flags").GetInt32()));
        Assert.Equal((0x18, 0x1A, 0x1A, 0, 0x1C), (driver.GetProperty("open").GetInt32(), driver.GetProperty("prime").GetInt32(),
            driver.GetProperty("control").GetInt32(), driver.GetProperty("status").GetInt32(), driver.GetProperty("close").GetInt32()));
        Assert.Equal(["Open", "Prime", "Close"], Names(json.GetProperty("functions")));
    }

    [Fact]
    public void A_package_is_in_the_model()
    {
        var json = Json(Decode(Fork(Res("PACK", 3, Package)), "PACK", 3).Files);

        var package = json.GetProperty("package");
        Assert.Equal(("PACK", 3, 0, 1), (package.GetProperty("type").GetString(), package.GetProperty("id").GetInt32(),
            package.GetProperty("firstSelector").GetInt32(), package.GetProperty("lastSelector").GetInt32()));
        Assert.Equal([(0, 0x12), (1, 0)], package.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("selector").GetInt32(), e.GetProperty("offset").GetInt32())));
    }

    [Fact]
    public void A_fat_resource_has_its_routine_descriptor_and_fragment()
    {
        var (files, _) = Decode(Fork(Res("CDEF", 1, Fat())), "CDEF", 1);

        var json = Json(files);
        var descriptor = json.GetProperty("routineDescriptor");
        Assert.Equal(0, descriptor.GetProperty("offset").GetInt32());
        var routine = Assert.Single(descriptor.GetProperty("routines").EnumerateArray());
        Assert.Equal((0x3BB0, true, 0x20, true), (routine.GetProperty("procInfo").GetInt32(), routine.GetProperty("powerPC").GetBoolean(),
            routine.GetProperty("targetOffset").GetInt32(), routine.GetProperty("pef").GetBoolean()));
        var fragment = Assert.Single(json.GetProperty("fragments").EnumerateArray());
        Assert.Equal("pwpc", fragment.GetProperty("architecture").GetString());
        Assert.Equal(["main", ".InitGraf", "Helper"], Names(fragment.GetProperty("functions")));
        Assert.Contains("; 'CDEF' 1 routine 0: PowerPC fragment ('pwpc')", Text(files, ".s"), StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_code_is_entered_at_its_start()
    {
        var json = Json(Decode(Fork(Res("FKEY", 3, Bytes("7000 4E75"))), "FKEY", 3).Files);

        Assert.Equal("68k", json.GetProperty("format").GetString());
        Assert.False(json.TryGetProperty("standardHeader", out _));
        Assert.Equal(["entry"], Names(json.GetProperty("functions")));
        Assert.Equal(0, json.GetProperty("fragments").GetArrayLength());
    }
}
