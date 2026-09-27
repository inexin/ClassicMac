using System.Buffers.Binary;
using ClassicMac.Core;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class MacBinaryTests
{
    private static readonly MacBinaryReader[] Readers = [MacBinaryReader.III, MacBinaryReader.II, MacBinaryReader.I];

    private static MacBinaryReader? Detect(byte[] bytes) =>
        Readers.FirstOrDefault(r => r.CanRead(ForkData.FromBytes(bytes)));

    private static (MacFile File, List<Diagnostic> Diagnostics) Read(byte[] bytes)
    {
        var reader = Detect(bytes) ?? throw new Xunit.Sdk.XunitException("Not detected as MacBinary.");
        var diagnostics = new List<Diagnostic>();
        return (Assert.Single(reader.Read(ForkData.FromBytes(bytes), new ContainerContext(diagnostics: diagnostics))),
            diagnostics);
    }

    [Fact]
    public void Crc16_is_XMODEM()
    {
        Assert.Equal(0x31C3, Crc16.Compute("123456789"u8));
    }

    [Theory]
    [InlineData(1, "MacBinary I")]
    [InlineData(2, "MacBinary II")]
    [InlineData(3, "MacBinary III")]
    public void Each_version_is_detected_by_its_own_reader(int version, string format)
    {
        var bytes = MacBinary(version, "Read Me", "hello"u8.ToArray(), [1, 2, 3]);
        Assert.Equal(format, Detect(bytes)?.FormatName);
    }

    [Fact]
    public void MacBinary_II_carries_name_Finder_info_dates_and_both_forks()
    {
        var data = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
        var bytes = MacBinary(2, "Read Me", data, [1, 2, 3], type: "APPL", creator: "RLMZ", flags: 0x2100,
            created: 2_526_595_200, modified: 2_526_595_260);

        var (file, diagnostics) = Read(bytes);

        Assert.Empty(diagnostics);
        Assert.Equal("Read Me", file.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("APPL"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("RLMZ"), file.FinderInfo.Creator);
        Assert.Equal(FinderFlags.HasBundle | FinderFlags.HasBeenInited, file.FinderInfo.Flags);
        Assert.Equal(new MacPoint(30, 40), file.FinderInfo.Location);
        Assert.Equal(new DateTime(1984, 1, 24), file.Created!.Value.ToDateTime());
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal([1, 2, 3], file.ResourceFork.ToArray());
    }

    [Fact]
    public void MacBinary_I_has_only_the_high_flag_byte()
    {
        var (file, _) = Read(MacBinary(1, "Old", [7], [], flags: 0x2001)); // the low byte ($01) is not stored
        Assert.Equal(FinderFlags.HasBundle, file.FinderInfo.Flags);
        Assert.Equal(0, file.ResourceFork.Length);
    }

    [Fact]
    public void MacBinary_III_keeps_script_and_extended_flags()
    {
        var (file, _) = Read(MacBinary(3, "Neu", [7], []));
        Assert.Equal(7, file.FinderInfo.Extended.Span[8]);
        Assert.Equal(0x80, file.FinderInfo.Extended.Span[9]);
    }

    [Fact]
    public void A_secondary_header_is_skipped()
    {
        var (file, _) = Read(MacBinary(2, "Sec", [5, 6], [8], secondaryLength: 40));
        Assert.Equal([5, 6], file.DataFork.ToArray());
        Assert.Equal([8], file.ResourceFork.ToArray());
    }

    [Fact]
    public void Truncated_forks_keep_what_is_there()
    {
        var bytes = MacBinary(2, "Cut", new byte[300], [])[..200];
        var (file, diagnostics) = Read(bytes);
        Assert.Equal(72, file.DataFork.Length);
        Assert.Equal("macbinary.fork-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_bad_CRC_is_not_MacBinary_II()
    {
        var bytes = MacBinary(2, "Bad", [1], []);
        bytes[124] ^= 0xFF;
        Assert.Null(Detect(bytes)); // and not I either: bytes 99–125 are not zero
    }

    [Fact]
    public void Other_files_are_not_MacBinary()
    {
        var rawFork = new byte[300];
        BinaryPrimitives.WriteUInt32BigEndian(rawFork, 256); // a resource fork header: byte 1 (name length) is 0
        byte[] png = [0x89, .. "PNG\r\n\x1a\n"u8, .. new byte[200]];
        var text = System.Text.Encoding.ASCII.GetBytes(new string('x', 300));
        var longName = MacBinary(2, "Name", [1], []);
        longName[1] = 64;

        Assert.All(new[] { rawFork, png, text, longName, new byte[100] }, bytes => Assert.Null(Detect(bytes)));
    }
}
