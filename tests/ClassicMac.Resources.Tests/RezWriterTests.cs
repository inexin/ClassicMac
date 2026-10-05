using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Rez;

namespace ClassicMac.Resources.Tests;

// Rez source from a resource fork (docs/formats/output/rez.md): MPW DeRez's output byte for byte, and the portable
// subset both MPW's and Retro68's Rez compile.
public sealed class RezWriterTests
{
    private static Resource Res(string type, short id, byte[] data, string? name = null, ResourceAttributes attributes = ResourceAttributes.None, byte[]? typeBytes = null)
    {
        var resource = new Resource(typeBytes is null ? FourCC.FromString(type) : new FourCC(typeBytes), id, data) { Attributes = attributes };
        if (name is not null)
        {
            resource.Name = new MacString(MacRoman.Encode(name));
        }

        return resource;
    }

    private static ResourceFork Fork(params Resource[] resources)
    {
        var fork = new ResourceFork();
        foreach (var r in resources)
        {
            fork.Add(r);
        }

        return fork;
    }

    private static string Mac(byte[] bytes) => MacRoman.Decode(bytes);

    [Fact]
    public void Data_is_16_bytes_a_line_with_the_comment_at_column_55()
    {
        var data = Encoding.ASCII.GetBytes("*/ and // and \\ and \"").Concat(new byte[] { 0x00, 0x09, 0x0D, 0x7F, 0xA5 }).ToArray();
        var text = Mac(RezWriter.Write(Fork(Res("TEST", 100, data))));

        Assert.Equal(
            "data 'TEST' (100) {\r" +
            "\t$\"2A2F 2061 6E64 202F 2F20 616E 6420 5C20\"            /* *. and // and \\  */\r" +
            "\t$\"616E 6420 2200 090D 7FA5\"                           /* and \".∆¬.• */\r" +
            "};\r\r", text);
    }

    [Fact]
    public void Names_attributes_and_types_are_written_as_DeRez_writes_them()
    {
        var text = Mac(RezWriter.Write(Fork(
            Res("TEST", 1, [], "q\"b\\c\r¥\u0001", (ResourceAttributes)0xC1),
            Res("TEST", 2, [], "", ResourceAttributes.Purgeable | ResourceAttributes.SystemHeap),
            Res("TEST", -5, [], typeBytes: [0x01, 0x41, 0x42, 0x43]),
            Res("TEST", 7, [], "x", typeBytes: [0x61, 0x27, 0x62, 0x5C]),
            Res("TEST", 52, [], typeBytes: [0x00, 0x41, 0x27, 0x42]),
            Res("TEST", 3, [], null, (ResourceAttributes)0x7E))));

        Assert.Equal(
            "data 'TEST' (1, \"q\\\"b\\\\c\\n¥\\0x01\", $C1) {\r};\r\r" +
            "data 'TEST' (2, \"\", sysheap, purgeable) {\r};\r\r" +
            "data '\\0x01ABC' (-5) {\r};\r\r" +
            "data 'a\\'b\\\\' (7, \"x\") {\r};\r\r" +
            "data 'A\\'B' (52) {\r};\r\r" +
            "data 'TEST' (3, $7E) {\r};\r\r", text);
    }

    [Fact]
    public void Raw_names_print_names_and_types_unescaped()
    {
        var text = Mac(RezWriter.Write(Fork(Res("TEST", 1, [], "q\"\\\r\u0001", typeBytes: [0x61, 0x27, 0x5C, 0x01])), new RezOptions { RawNames = true }));

        // The quote, the backslash and (in names) $0D are still escaped; other control bytes are raw.
        Assert.Equal("data 'a\\'\\\\\u0001' (1, \"q\\\"\\\\\\n\u0001\") {\r};\r\r", text);
    }

    [Fact]
    public void The_portable_dialect_writes_only_what_both_compilers_read()
    {
        var diagnostics = new List<Diagnostic>();
        var bytes = RezWriter.Write(Fork(
            Res("TEST", 1, [0x0D, 0xA5], "q\"\\\r¥é", ResourceAttributes.Purgeable | ResourceAttributes.Locked),
            Res("TEST", 2, [], "", (ResourceAttributes)0xC2),
            Res("TEST", 3, [], typeBytes: [0x01, 0x41, 0x42, 0x43])), new RezOptions { Dialect = RezDialect.Portable }, diagnostics);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.All(bytes, b => Assert.True(b is 0x09 or 0x0A or (>= 0x20 and < 0x7F)));                       // ASCII, LF lines
        Assert.Equal(
            "data 'TEST' (1, \"q\\0x22\\0x5C\\0x0D\\0xB4\\0x8E\", purgeable, locked) {\n" +
            "\t$\"0DA5\"                                               /* .. */\n" +
            "};\n\n" +
            "data 'TEST' (2, sysheap) {\n};\n\n" +
            "data '\\0x01ABC' (3) {\n};\n\n", text);
        Assert.Equal(["rez.empty-name", "rez.attributes", "rez.type"], diagnostics.Select(d => d.Code));
    }
}
