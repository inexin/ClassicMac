using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class BinHexTests
{
    private static (MacFile File, List<Diagnostic> Diagnostics) Read(string text, ContainerReadOptions? options = null)
    {
        var input = ForkData.FromBytes(Encoding.ASCII.GetBytes(text));
        Assert.True(BinHexReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        var file = Assert.Single(BinHexReader.Instance.Read(input, new ContainerContext(options, diagnostics)));
        return (file, diagnostics);
    }

    [Fact]
    public void Decodes_name_Finder_info_and_both_forks()
    {
        var data = Encoding.ASCII.GetBytes("Hello from 1991");
        byte[] resource = [0, 0, 1, 0, 0x90, 0x90, 7, 7, 7, 7, 7, 7, 7, 0x90, 1];
        var (file, diagnostics) = Read(BinHex("Read Me", data, resource, type: "APPL", creator: "RLMZ", flags: 0x2100));

        Assert.Empty(diagnostics);
        Assert.Equal("Read Me", file.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("APPL"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("RLMZ"), file.FinderInfo.Creator);
        Assert.Equal(FinderFlags.HasBundle | FinderFlags.HasBeenInited, file.FinderInfo.Flags);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Fact]
    public void Long_runs_expand()
    {
        var data = new byte[1000]; // runs of zeros, 255 at a time
        var (file, diagnostics) = Read(BinHex("Zeros", data, []));
        Assert.Empty(diagnostics);
        Assert.Equal(data, file.DataFork.ToArray());
    }

    [Fact]
    public void A_CRC_mismatch_is_a_warning()
    {
        var (file, diagnostics) = Read(BinHex("Bad", [1, 2, 3], [], corruptDataCrc: true));
        Assert.Equal([1, 2, 3], file.DataFork.ToArray());
        Assert.Equal("binhex.crc", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Truncated_text_keeps_what_was_decoded()
    {
        var text = BinHex("Cut", new byte[200], Enumerable.Range(0, 200).Select(i => (byte)i).ToArray());
        var (file, diagnostics) = Read(text[..(text.Length / 2)]);
        Assert.Contains(diagnostics, d => d.Code == "binhex.truncated");
        Assert.Contains(diagnostics, d => d.Code == "binhex.fork-truncated");
        Assert.True(file.ResourceFork.Length < 200);
    }

    [Fact]
    public void Characters_outside_the_alphabet_stop_decoding()
    {
        var text = BinHex("Odd", [1, 2, 3], []);
        var end = text.LastIndexOf(':') - 4; // inside the resource fork's CRC, after the header and data
        var (_, diagnostics) = Read(text[..end] + "~" + text[end..]);
        Assert.Contains(diagnostics, d => d.Code == "binhex.bad-character");
    }

    [Fact]
    public void Without_a_header_the_input_is_unusable()
    {
        var text = BinHex("Odd", [1, 2, 3], []);
        var start = text.IndexOf(':', text.IndexOf("BinHex 4.0)")) + 5;
        Assert.Throws<InvalidDataException>(() => Read(text[..start] + "~" + text[start..]));
    }

    [Fact]
    public void Expansion_is_limited()
    {
        var text = BinHex("Big", new byte[5000], []);
        Assert.Throws<InvalidDataException>(() =>
            Read(text, ContainerReadOptions.Default with { MaxExpandedBytesPerInput = 1000 }));
    }

    [Fact]
    public void Text_without_the_marker_is_not_BinHex()
    {
        Assert.False(BinHexReader.Instance.CanRead(ForkData.FromBytes(":just a colon:"u8.ToArray())));
        Assert.False(BinHexReader.Instance.CanRead(ForkData.FromBytes(new byte[300])));
    }

    // Text before the marker may be any mail or news text, 8-bit characters, tabs and form feeds included; a
    // control character other than those, such as a zero byte, means the marker is inside binary data (a disk image
    // holding a .hqx file).
    [Fact]
    public void The_marker_is_recognised_only_after_text()
    {
        var hqx = Encoding.ASCII.GetBytes(BinHex("Note", "note"u8.ToArray(), []));
        byte[] text = [.. "Subject: caf\t\f\v\r\n"u8, 0xE9, 0x8E, (byte)'\n'];
        Assert.True(BinHexReader.Instance.CanRead(ForkData.FromBytes((byte[])[.. text, .. hqx])));
        foreach (var control in new byte[] { 0x00, 0x01, 0x08, 0x0E, 0x1B, 0x1F })
            Assert.False(BinHexReader.Instance.CanRead(ForkData.FromBytes((byte[])[.. text, control, .. hqx])));
    }

    // Read itself, as BinHex decoders do, takes the first marker wherever it is.
    [Fact]
    public void Read_finds_the_marker_after_binary_data()
    {
        var hqx = Encoding.ASCII.GetBytes(BinHex("Note", "note"u8.ToArray(), []));
        var diagnostics = new List<Diagnostic>();
        var file = Assert.Single(BinHexReader.Instance.Read(ForkData.FromBytes((byte[])[0, 1, 2, .. hqx]), new ContainerContext(null, diagnostics)));
        Assert.Empty(diagnostics);
        Assert.Equal("note"u8.ToArray(), file.DataFork.ToArray());
    }
}
