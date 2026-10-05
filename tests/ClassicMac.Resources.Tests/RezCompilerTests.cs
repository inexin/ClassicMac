using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Rez;

namespace ClassicMac.Resources.Tests;

// Compiling Rez source (docs/formats/output/rez.md §2): data, read and include statements, as MPW 3.6's Rez compiles
// them, and back from what RezWriter writes.
public sealed class RezCompilerTests
{
    private static ResourceFork Compile(string source, List<Diagnostic>? diagnostics = null, RezCompileOptions? options = null) =>
        RezCompiler.Compile(MacRoman.Encode(source), options ?? new RezCompileOptions(), diagnostics ?? [])
        ?? throw new InvalidOperationException(string.Join("; ", (diagnostics ?? []).Select(d => d.Message)));

    private static byte[] Data(string source, RezCompileOptions? options = null) => Compile(source, options: options).Resources.Single().GetData().ToArray();

    [Theory]
    [InlineData("\"\\n\"", new byte[] { 0x0D })]
    [InlineData("\"\\r\"", new byte[] { 0x0A })]
    [InlineData("\"a\\\\b\"", new byte[] { 0x61, 0x5C, 0x62 })]
    [InlineData("\"\\0x41\\0X42\"", new byte[] { 0x41, 0x42 })]
    [InlineData("\"\\$43\\$4a\"", new byte[] { 0x43, 0x4A })]
    [InlineData("\"\\101\\377\"", new byte[] { 0x41, 0xFF })]
    [InlineData("\"\\t\\b\\v\\f\\?\\'\\\"\"", new byte[] { 0x09, 0x08, 0x0B, 0x0C, 0x7F, 0x27, 0x22 })]
    [InlineData("\"\\d\"", new byte[] { 0x64 })]
    [InlineData("\"\\0d065\\0b01000010\"", new byte[] { 0x41, 0x42 })]
    [InlineData("\"\\0x414\"", new byte[] { 0x41, 0x34 })]
    [InlineData("$\"0102 03 04 05\" \"x\" $\"FF\"", new byte[] { 1, 2, 3, 4, 5, 0x78, 0xFF })]
    [InlineData("$$Format(\"%d-%s\", 5, \"x\")", new byte[] { 0x35, 0x2D, 0x78 })]
    public void Data_takes_strings_hex_strings_and_MPWs_escapes(string body, byte[] expected)
    {
        Assert.Equal(expected, Data($"data 'TEST' (1) {{ {body} }};"));
    }

    [Fact]
    public void Retro68s_escapes_swap_n_and_r()
    {
        Assert.Equal([0x0A, 0x0D], Data("data 'TEST' (1) { \"\\n\\r\" };", new RezCompileOptions { Retro68Escapes = true }));
        Assert.Equal([0x0D], Data("data 'TEST' (1) { \"\\n\" };"));
    }

    [Theory]
    [InlineData("$17", 23)]
    [InlineData("0x18", 24)]
    [InlineData("0b11001", 25)]
    [InlineData("032", 26)]
    [InlineData("-5", -5)]
    public void Ids_take_every_integer_form(string id, short expected)
    {
        Assert.Equal(expected, Compile($"DATA 'TEST' ({id}) {{ }};").Resources.Single().Id);
    }

    [Fact]
    public void Types_names_and_attributes_are_read_as_MPW_reads_them()
    {
        var diagnostics = new List<Diagnostic>();
        var fork = Compile("""
            data '\0x01ABC' (50) { };
            data 'A\'B' (52) { };
            data 'TEST' (40, "q\"b\0x5Cc\0x0D\0xA5\0x01") { };
            data 'TEST' (41, "") { };
            data 'TEST' (42, $"414243") { };
            data 'TEST' (35, SYSHEAP, Purgeable) { };
            data 'TEST' (30, $80) { $"07" };
            data 'TEST' (34, "nm", $41) { };
            Data 'TEST' (36, locked, nonpreload) { };
            """, diagnostics);

        Assert.Equal(new FourCC([0x01, 0x41, 0x42, 0x43]), fork.Resources[0].Type);
        Assert.Equal(new FourCC([0x00, 0x41, 0x27, 0x42]), fork.Resources[1].Type);       // right-justified
        Assert.Equal([0x71, 0x22, 0x62, 0x5C, 0x63, 0x0D, 0xA5, 0x01], fork.Find(FourCC.FromString("TEST"), 40)!.Name!.Value.Bytes.ToArray());
        Assert.Null(fork.Find(FourCC.FromString("TEST"), 41)!.Name);                           // "" is no name
        Assert.Equal("ABC", fork.Find(FourCC.FromString("TEST"), 42)!.Name!.Value.ToMacRoman());
        Assert.Equal(ResourceAttributes.SystemHeap | ResourceAttributes.Purgeable, fork.Find(FourCC.FromString("TEST"), 35)!.Attributes);
        Assert.Equal((ResourceAttributes)0x80, fork.Find(FourCC.FromString("TEST"), 30)!.Attributes);
        Assert.Equal((ResourceAttributes)0x41, fork.Find(FourCC.FromString("TEST"), 34)!.Attributes);
        Assert.Equal(ResourceAttributes.Locked, fork.Find(FourCC.FromString("TEST"), 36)!.Attributes);
        Assert.Equal(["rez.reserved-attributes", "rez.reserved-attributes"], diagnostics.Select(d => d.Code));
    }

    [Theory]
    [InlineData("data 'TEST' (32, sysheap, changed) { };", "rez.syntax")]
    [InlineData("data 'TEST' (33, locked, unlocked) { };", "rez.conflicting-attributes")]
    [InlineData("data 'TEST' (33, $7E) { };", "rez.invalid-attributes")]
    [InlineData("data 'TEST' (14) { \"\\0x4\" };", "rez.syntax")]
    [InlineData("data 'TEST' (1) { }; data 'TEST' (1) { };", "rez.duplicate")]
    [InlineData("type 'TST2' { integer; }; resource 'TST2' (1) { 1 };", "rez.typed")]
    [InlineData("data 'TEST' (1) { $\"0\" };", "rez.syntax")]
    public void Errors_stop_the_compile_as_MPWs_Rez_does(string source, string code)
    {
        var diagnostics = new List<Diagnostic>();

        Assert.Null(RezCompiler.Compile(MacRoman.Encode(source), new RezCompileOptions(), diagnostics));
        Assert.Contains(diagnostics, d => d.Code == code && d.Severity == DiagnosticSeverity.Error && d.Message.Contains("line 1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hex_string_type_makes_no_resource_and_no_error()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(Compile("data $\"01414243\" (51) { };", diagnostics).Resources);
        Assert.Equal("rez.hex-type", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Read_and_include_take_files_beside_the_source()
    {
        var other = new ResourceFork();
        other.Add(new Resource(FourCC.FromString("TEST"), 100, new byte[] { 9 }) { Attributes = (ResourceAttributes)0x7E, Name = new MacString([]) });
        other.Add(new Resource(FourCC.FromString("ICON"), 128, new byte[] { 8 }));
        var options = new RezCompileOptions
        {
            ReadFile = name => name == "e01.r" ? Encoding.ASCII.GetBytes("abc") : throw new FileNotFoundException(name),
            ReadResourceFork = name => name == "crafted" ? other : throw new FileNotFoundException(name),
        };

        var fork = Compile("""
            read 'TEST' (60) "e01.r";
            data 'TEST' (61) { $$Read("e01.r") };
            include "crafted" 'TEST' (100);
            """, options: options);

        Assert.Equal("abc"u8.ToArray(), fork.Find(FourCC.FromString("TEST"), 60)!.GetData().ToArray());
        Assert.Equal("abc"u8.ToArray(), fork.Find(FourCC.FromString("TEST"), 61)!.GetData().ToArray());
        var included = fork.Find(FourCC.FromString("TEST"), 100)!;
        Assert.Equal(((ResourceAttributes)0x7E, 0), (included.Attributes, included.Name!.Value.Length));      // verbatim
        Assert.Null(fork.Find(FourCC.FromString("ICON"), 128));
        Assert.Equal(3, Compile("include \"crafted\"; data 'X   ' (1) { };", options: options).Resources.Count);
    }

    [Fact]
    public void What_RezWriter_writes_compiles_back_to_the_same_fork()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("TEST"), 100, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())
        {
            Name = new MacString([0x71, 0x22, 0x5C, 0x0D, 0xB4, 0x01]),
            Attributes = (ResourceAttributes)0xC1,
        });
        fork.Add(new Resource(new FourCC([0x00, 0x41, 0x27, 0x42]), -5, new byte[] { 0x2A, 0x2F }) { Attributes = ResourceAttributes.Purgeable });
        fork.Add(new Resource(FourCC.FromString("STR "), 128, new byte[] { 2, 0x48, 0x69 }) { Name = new MacString("x"u8) });

        foreach (var dialect in new[] { RezDialect.Mpw, RezDialect.Portable })
        {
            var source = RezWriter.Write(fork, new RezOptions { Dialect = dialect });
            var back = RezCompiler.Compile(source, new RezCompileOptions(), [])!;
            Assert.Equal(fork.Resources.Select(r => (r.Type, r.Id, r.GetData().ToArray().Length)), back.Resources.Select(r => (r.Type, r.Id, r.GetData().ToArray().Length)));
            Assert.Equal(fork.Resources.Select(r => r.GetData().ToArray()), back.Resources.Select(r => r.GetData().ToArray()));
            Assert.Equal(fork.Resources.Select(r => r.Name?.Bytes.ToArray()), back.Resources.Select(r => r.Name?.Bytes.ToArray()));
        }
    }
}
