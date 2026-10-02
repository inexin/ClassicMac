using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class UuencodeTests
{
    // Historical uuencode: 45 bytes a line, count character, 4 characters per 3 bytes, zero as the backquote.
    private static string Uu(string name, byte[] data, string newline = "\n", bool spaceForZero = false)
    {
        char Enc(int v) => v == 0 && !spaceForZero ? '`' : (char)(v + 0x20);
        var text = new StringBuilder($"begin 644 {name}{newline}");
        for (var at = 0; at < data.Length; at += 45)
        {
            var n = Math.Min(45, data.Length - at);
            text.Append(Enc(n));
            for (var g = 0; g < n; g += 3)
            {
                int b0 = data[at + g], b1 = g + 1 < n ? data[at + g + 1] : 0, b2 = g + 2 < n ? data[at + g + 2] : 0;
                text.Append(Enc(b0 >> 2)).Append(Enc((b0 & 3) << 4 | b1 >> 4))
                    .Append(Enc((b1 & 15) << 2 | b2 >> 6)).Append(Enc(b2 & 63));
            }
            text.Append(newline);
        }
        return text.Append(Enc(0)).Append(newline).Append("end").Append(newline).ToString();
    }

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(string text)
    {
        var input = ForkData.FromBytes(Encoding.ASCII.GetBytes(text));
        Assert.True(UuencodeReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (UuencodeReader.Instance.Read(input, new ContainerContext(null, diagnostics)), diagnostics);
    }

    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)(i * 7)).ToArray();

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", true)]
    public void Decodes_a_block_with_any_line_break(string newline, bool spaceForZero)
    {
        var data = Bytes(100); // two full lines and a short one
        var (files, diagnostics) = Read("From: someone\nSubject: a file\n\nHere it is:\n" + Uu("Read Me.txt", data, newline, spaceForZero));
        Assert.Empty(diagnostics);
        var file = Assert.Single(files);
        Assert.Equal("Read Me.txt", file.Name.ToMacRoman());
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(0, file.ResourceFork.Length);
    }

    [Fact]
    public void Known_vector()
    {
        // "Cat" encodes as #0V%T; the name keeps only the last path component.
        var (files, diagnostics) = Read("begin 644 /tmp/cat.txt\n#0V%T\n`\nend\n");
        Assert.Empty(diagnostics);
        Assert.Equal("cat.txt", files[0].Name.ToMacRoman());
        Assert.Equal("Cat"u8.ToArray(), files[0].DataFork.ToArray());
    }

    [Fact]
    public void Stripped_trailing_spaces_decode_as_zeros()
    {
        // A mailer stripped the trailing spaces (zeros) of the data line and the space of the zero-count line.
        var text = Uu("z", [1, 0, 0, 0, 0, 0], spaceForZero: true);
        var stripped = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd(' ')));
        var (files, diagnostics) = Read(stripped);
        Assert.Empty(diagnostics);
        Assert.Equal([1, 0, 0, 0, 0, 0], files[0].DataFork.ToArray());
    }

    [Fact]
    public void Several_blocks_are_several_files()
    {
        var (files, diagnostics) = Read(Uu("a", [1, 2, 3]) + "\n-- \nsignature\n\n" + Uu("b", [4, 5]));
        Assert.Empty(diagnostics);
        Assert.Equal(["a", "b"], files.Select(f => f.Name.ToMacRoman()));
        Assert.Equal([4, 5], files[1].DataFork.ToArray());
    }

    [Fact]
    public void Base64_blocks_decode()
    {
        var (files, diagnostics) = Read("begin-base64 644 hello.bin\r\nSGVsbG8s\r\nIHdvcmxk\r\nIQ==\r\n====\r\n");
        Assert.Empty(diagnostics);
        Assert.Equal("hello.bin", files[0].Name.ToMacRoman());
        Assert.Equal("Hello, world!"u8.ToArray(), files[0].DataFork.ToArray());
    }

    [Fact]
    public void A_bad_line_is_skipped()
    {
        var text = Uu("x", Bytes(90)).Split('\n');
        text[1] = "M" + new string('~', 60);
        var (files, diagnostics) = Read(string.Join("\n", text));
        Assert.Equal("uuencode.bad-line", Assert.Single(diagnostics).Code);
        Assert.Equal(Bytes(90)[45..], files[0].DataFork.ToArray());
    }

    [Fact]
    public void A_missing_end_line_is_a_warning()
    {
        var (files, diagnostics) = Read(Uu("x", [9, 8, 7]).Replace("end\n", ""));
        Assert.Equal("uuencode.missing-end", Assert.Single(diagnostics).Code);
        Assert.Equal([9, 8, 7], files[0].DataFork.ToArray());
    }

    [Fact]
    public void Truncated_text_keeps_what_was_decoded()
    {
        var text = Uu("x", Bytes(200));
        var (files, diagnostics) = Read(text[..(text.IndexOf('\n') + 1 + 62 * 2)]);
        Assert.Equal("uuencode.truncated", Assert.Single(diagnostics).Code);
        Assert.Equal(Bytes(90), files[0].DataFork.ToArray());
    }

    [Fact]
    public void Text_without_a_begin_line_is_not_uuencode()
    {
        Assert.False(UuencodeReader.Instance.CanRead(ForkData.FromBytes("begin the story here\nend\n"u8.ToArray())));
        Assert.False(UuencodeReader.Instance.CanRead(ForkData.FromBytes("we begin 644 x\n"u8.ToArray())));
        Assert.False(UuencodeReader.Instance.CanRead(ForkData.FromBytes(new byte[300])));
    }

    // As BinHex's marker: the begin line must follow text only (binhex.md §2).
    [Fact]
    public void A_begin_line_is_recognised_only_after_text()
    {
        var uu = Encoding.ASCII.GetBytes(Uu("x", "abc"u8.ToArray()));
        byte[] text = [.. "Subject: caf\t\f\v\r\n"u8, 0xE9, 0x8E, (byte)'\n'];
        Assert.True(UuencodeReader.Instance.CanRead(ForkData.FromBytes((byte[])[.. text, .. uu])));
        foreach (var control in new byte[] { 0x00, 0x01, 0x08, 0x0E, 0x1B, 0x1F })
            Assert.False(UuencodeReader.Instance.CanRead(ForkData.FromBytes((byte[])[.. text, control, (byte)'\n', .. uu])));
    }

    [Fact]
    public void Read_finds_a_begin_line_after_binary_data()
    {
        var uu = Encoding.ASCII.GetBytes(Uu("x", "abc"u8.ToArray()));
        var diagnostics = new List<Diagnostic>();
        var file = Assert.Single(UuencodeReader.Instance.Read(ForkData.FromBytes((byte[])[0, 1, (byte)'\n', .. uu]), new ContainerContext(null, diagnostics)));
        Assert.Empty(diagnostics);
        Assert.Equal("abc"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void A_uuencoded_MacBinary_file_unwraps_to_the_Mac_file()
    {
        var macBinary = MacBinary(2, "Inner", "data"u8.ToArray(), [1, 2]);
        var diagnostics = new List<Diagnostic>();
        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Inner.bin.uu"), DataFork = ForkData.FromBytes(Encoding.ASCII.GetBytes(Uu("Inner.bin", macBinary, "\r\n"))) },
            "host file", new ContainerContext(null, diagnostics));

        Assert.Empty(diagnostics);
        var uu = Assert.Single(root.Children);
        Assert.Equal("uuencode", uu.Format);
        Assert.Equal("Inner.bin", uu.File.Name.ToMacRoman());
        var leaf = Assert.Single(uu.Children);
        Assert.Equal("MacBinary II", leaf.Format);
        Assert.Equal("Inner", leaf.File.Name.ToMacRoman());
        Assert.Equal("data"u8.ToArray(), leaf.File.DataFork.ToArray());
        Assert.Equal([1, 2], leaf.File.ResourceFork.ToArray());
    }
}
