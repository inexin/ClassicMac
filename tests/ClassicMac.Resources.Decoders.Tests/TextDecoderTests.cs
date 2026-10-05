using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

public class TextDecoderTests
{
    private static readonly IReadOnlyList<IResourceDecoder> Decoders = ResourceDecoders.Create();

    private static (IReadOnlyList<DecodedFile> Files, List<Diagnostic> Diagnostics) Decode(Resource resource, params Resource[] others)
    {
        var fork = new ResourceFork();
        fork.Add(resource);
        foreach (var other in others)
        {
            fork.Add(other);
        }

        var diagnostics = new List<Diagnostic>();
        var decoder = Decoders.Single(d => d.CanDecode(resource.Type));
        return (decoder.Decode(new DecodeInput(resource, resource.GetData(), fork, diagnostics: diagnostics)), diagnostics);
    }

    private static Resource Res(string type, short id, byte[] data) => new(FourCC.FromString(type), id, data);

    private static byte[] Pascal(string text) => [(byte)MacRoman.Encode(text).Length, .. MacRoman.Encode(text)];

    private static string Utf8(DecodedFile file) => Encoding.UTF8.GetString(file.Content.Span);

    [Fact]
    public void Strings_become_UTF8_text()
    {
        var (files, diagnostics) = Decode(Res("STR ", 128, Pascal("Café ƒ™\rline 2")));

        Assert.Empty(diagnostics);
        var file = Assert.Single(files);
        Assert.Equal((".txt", "Café ƒ™\nline 2", "macintosh"), (file.Extension, Utf8(file), file.Encoding));
    }

    [Fact]
    public void A_string_longer_than_its_resource_is_cut_and_reported()
    {
        var (files, diagnostics) = Decode(Res("STR ", 128, [10, (byte)'a', (byte)'b']));
        Assert.Equal("ab", Utf8(files[0]));
        Assert.Equal("text.string-short", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void String_lists_become_JSON()
    {
        var (files, diagnostics) = Decode(Res("STR#", 129, [0, 3, .. Pascal("Human"), .. Pascal("Élf"), .. Pascal("")]));

        Assert.Empty(diagnostics);
        var json = JsonDocument.Parse(files[0].Content);
        Assert.Equal(["Human", "Élf", ""], json.RootElement.GetProperty("strings").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(".json", files[0].Extension);
    }

    [Fact]
    public void A_string_list_shorter_than_its_count_keeps_what_is_there()
    {
        var (files, diagnostics) = Decode(Res("STR#", 129, [0, 3, .. Pascal("one"), 5, (byte)'t', (byte)'w']));

        var strings = JsonDocument.Parse(files[0].Content).RootElement.GetProperty("strings").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(["one", "tw"], strings);
        Assert.Equal("text.string-list-short", Assert.Single(diagnostics).Code);
    }

    // A styl: count, then (start, height, ascent, font, face, filler, size, r, g, b) per run.
    private static byte[] Styl(params (int Start, short Font, byte Face, short Size, ushort R, ushort G, ushort B)[] runs)
    {
        var data = new byte[2 + runs.Length * 20];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)runs.Length);
        for (var i = 0; i < runs.Length; i++)
        {
            var e = data.AsSpan(2 + i * 20);
            var (start, font, face, size, r, g, b) = runs[i];
            BinaryPrimitives.WriteInt32BigEndian(e, start);
            BinaryPrimitives.WriteInt16BigEndian(e[4..], 16);
            BinaryPrimitives.WriteInt16BigEndian(e[6..], 12);
            BinaryPrimitives.WriteInt16BigEndian(e[8..], font);
            e[10] = face;
            BinaryPrimitives.WriteInt16BigEndian(e[12..], size);
            BinaryPrimitives.WriteUInt16BigEndian(e[14..], r);
            BinaryPrimitives.WriteUInt16BigEndian(e[16..], g);
            BinaryPrimitives.WriteUInt16BigEndian(e[18..], b);
        }
        return data;
    }

    [Fact]
    public void Styled_text_becomes_text_and_RTF()
    {
        var text = MacRoman.Encode("Title\rBold {x} \\ Café");
        var styl = Styl((0, 20, 0x01 | 0x04, 18, 0, 0, 0xFFFF), (6, 3, 0x01, 12, 0, 0, 0), (11, 3, 0x02, 12, 0xFFFF, 0, 0));

        var (files, diagnostics) = Decode(Res("TEXT", 128, text), Res("styl", 128, styl));

        Assert.Empty(diagnostics);
        Assert.Equal([".txt", ".rtf"], files.Select(f => f.Extension));
        Assert.Equal("Title\nBold {x} \\ Café", Utf8(files[0]));
        Assert.Equal(
            "{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1\n" +
            "{\\fonttbl{\\f0 Times;}{\\f1 Geneva;}}\n" +
            "{\\colortbl;\\red0\\green0\\blue255;\\red0\\green0\\blue0;\\red255\\green0\\blue0;}\n" +
            "\\plain\\f0\\fs36\\cf1\\b\\ul Title\\par\n" +
            "\\plain\\f1\\fs24\\cf2\\b Bold \\plain\\f1\\fs24\\cf3\\i \\{x\\} \\\\ Caf\\u233?}\n",
            Encoding.ASCII.GetString(files[1].Content.Span));
    }

    [Fact]
    public void Styled_text_gives_runs_over_the_whole_text()
    {
        var text = MacRoman.Encode("Title\rBody é");
        var styl = Styl((0, 20, 0x01, 18, 0xFFFF, 0, 0), (6, 4, 0x02, 0, 0, 0, 0));

        var styled = Text.StyledText.Read(text, styl);

        Assert.Equal("Title\rBody é", styled.Text);
        Assert.True(styled.Complete);
        Assert.Equal(2, styled.Runs.Count);
        var (title, body) = (styled.Runs[0], styled.Runs[1]);
        Assert.Equal((0, 6, "Times", 18, true, (byte)255), (title.Start, title.Length, title.FontName, title.Size, title.Bold, title.Red));
        Assert.Equal((6, 6, "Monaco", 12, true), (body.Start, body.Length, body.FontName, body.Size, body.Italic));

        var plain = Text.StyledText.Read(text, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((0, 12, "Geneva"), (plain.Runs.Single().Start, plain.Runs.Single().Length, plain.Runs.Single().FontName));
    }

    // Runs apply as TEUseStyleScrap applies them: in stored order, the first from the start, each to the next run's
    // start (either way round), a later one overwriting; a duplicate start gives an empty run, so the later one wins.
    [Fact]
    public void Style_runs_apply_as_TextEdit_applies_them()
    {
        var text = MacRoman.Encode("abcdefghij");
        // Stored order: Times from 4 (but the first run starts at 0), Monaco from 8, Geneva from 2 (out of order), then
        // Chicago and Courier both from 6 (Courier wins).
        var styl = Styl((4, 20, 0, 12, 0, 0, 0), (8, 4, 0, 12, 0, 0, 0), (2, 3, 0, 12, 0, 0, 0), (6, 0, 0, 12, 0, 0, 0),
            (6, 22, 0, 12, 0, 0, 0));

        var runs = Text.StyledText.Read(text, styl).Runs;

        // Times [0,8), then Monaco [2,8) backwards over it, Geneva [2,6), Chicago empty, Courier [6,10).
        Assert.Equal([(0, 2, "Times"), (2, 4, "Geneva"), (6, 4, "Courier")], runs.Select(r => (r.Start, r.Length, r.FontName)));

        // A run reaching the end stops the rest: here Times runs to a start past the text (and a negative start is past
        // it too, compared unsigned), so Monaco and Geneva are never applied.
        foreach (var past in new[] { 20, -1 })
        {
            var cut = Styl((0, 20, 0, 12, 0, 0, 0), (past, 4, 0, 12, 0, 0, 0), (3, 3, 0, 12, 0, 0, 0));
            Assert.Equal([(0, 10, "Times")], Text.StyledText.Read(text, cut).Runs.Select(r => (r.Start, r.Length, r.FontName)));
        }
    }

    [Fact]
    public void Text_without_styl_is_text_only_and_styl_alone_is_JSON()
    {
        var (files, _) = Decode(Res("TEXT", 200, MacRoman.Encode("plain")));
        Assert.Equal([".txt"], files.Select(f => f.Extension));

        (files, _) = Decode(Res("styl", 128, Styl((0, 4, 0x02, 9, 1, 2, 3))));
        var run = JsonDocument.Parse(files[0].Content).RootElement.GetProperty("runs")[0];
        Assert.Equal(("Monaco", 9, 2), (run.GetProperty("fontName").GetString(), run.GetProperty("size").GetInt32(), run.GetProperty("face").GetInt32()));
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x84, 0x80, 0x00 }, "4.8.4", "final")]
    [InlineData(new byte[] { 0x01, 0x00, 0x60, 0x03 }, "1.0b3", "beta")]
    [InlineData(new byte[] { 0x10, 0x25, 0x20, 0x12 }, "10.2.5d12", "development")] // BCD, as in Apple's own files
    [InlineData(new byte[] { 0x03, 0x00, 0x60, 0x0F }, "3.0b15", "beta")] // not BCD: binary, as some developers wrote it
    [InlineData(new byte[] { 0x02, 0x10, 0x40, 0x01 }, "2.1a1", "alpha")]
    public void Versions_display_in_their_usual_form(byte[] numbers, string display, string stage)
    {
        byte[] data = [.. numbers, 0x00, 0x00, .. Pascal("4.8.4"), .. Pascal("4.8.4 © 1986-1998 Green Mountain Software")];

        var (files, diagnostics) = Decode(Res("vers", 1, data));

        Assert.Empty(diagnostics);
        var json = JsonDocument.Parse(files[0].Content).RootElement;
        Assert.Equal((display, stage), (json.GetProperty("display").GetString(), json.GetProperty("stage").GetString()));
        Assert.Equal("4.8.4 © 1986-1998 Green Mountain Software", json.GetProperty("longVersion").GetString());
    }

    // Text that says its script (text-encodings.md §5): a styled run's font family, a 'vers' resource's region.
    [Fact]
    public void Styled_runs_in_a_Japanese_font_read_as_Mac_OS_Japanese()
    {
        byte[] text = [.. "Name: "u8, 0x93, 0xFA, 0x96, 0x7B];                    // 日本 in Mac OS Japanese
        var styl = Styl((0, 3, 0, 12, 0, 0, 0), (6, 0x4000, 0, 12, 0, 0, 0));      // Geneva, then the first Japanese family

        var styled = Text.StyledText.Read(text, styl);

        Assert.Equal("Name: 日本", styled.Text);
        Assert.Equal([(0, 6), (6, 2)], styled.Runs.Select(r => (r.Start, r.Length)));
        var manual = Text.StyledText.Read(text, styl, DecodeOptions.Default with { AutomaticEncoding = false });
        Assert.Equal(10, manual.Text.Length);                                       // as Mac OS Roman, a byte a character
    }

    [Fact]
    public void A_version_reads_in_its_regions_encoding_and_writes_back_in_it()
    {
        byte[] japanese = [0x93, 0xFA, 0x96, 0x7B];
        byte[] data = [0x01, 0x00, 0x80, 0x00, 0x00, 14, 3, .. "1.0"u8, (byte)(japanese.Length + 4), .. "1.0 "u8, .. japanese];   // region 14, verJapan

        var (files, _) = Decode(Res("vers", 1, data));
        Assert.Equal("1.0 日本", JsonDocument.Parse(files[0].Content).RootElement.GetProperty("longVersion").GetString());

        var version = Text.VersionResource.Read(data)!;
        Assert.Equal("1.0 日本", version.LongVersion);
        Assert.Equal(data, version.Write());
    }

    [Fact]
    public void Too_short_versions_are_left_raw()
    {
        var (files, diagnostics) = Decode(Res("vers", 1, [1, 0, 0x80]));
        Assert.Empty(files);
        Assert.Equal("text.vers-short", Assert.Single(diagnostics).Code);
    }
    // Decoders for a file's own encoding (its volume's, text-encodings.md §5): they read in it when the export reads in
    // the default Mac OS Roman with AutomaticEncoding on; an encoding chosen, or AutomaticEncoding off, wins. One set per
    // encoding.
    [Fact]
    public void A_file_s_encoding_replaces_only_the_default()
    {
        byte[] nihongo = [4, 0x93, 0xFA, 0x96, 0x7B];
        string Read(Func<MacTextEncoding, IReadOnlyList<IResourceDecoder>> decodersFor)
        {
            var resource = Res("STR ", 128, nihongo);
            var decoder = decodersFor(MacTextEncoding.Japanese).First(d => d.CanDecode(resource.Type));
            var fork = new ResourceFork();
            fork.Add(resource);
            return System.Text.Encoding.UTF8.GetString(decoder.Decode(new DecodeInput(resource, resource.GetData(), fork)).First().Content.Span);
        }

        var automatic = ResourceDecoders.CreateFor(DecodeOptions.Default);
        Assert.Equal("日本", Read(automatic));
        Assert.Same(automatic(MacTextEncoding.Japanese), automatic(MacTextEncoding.Japanese));
        Assert.NotEqual("日本", Read(ResourceDecoders.CreateFor(DecodeOptions.Default with { TextEncoding = MacTextEncoding.Greek })));
        Assert.NotEqual("日本", Read(ResourceDecoders.CreateFor(DecodeOptions.Default with { AutomaticEncoding = false })));
        Assert.NotEmpty(ResourceDecoders.CreateDocumentConvertersFor(DecodeOptions.Default)(MacTextEncoding.Japanese));
    }
}
