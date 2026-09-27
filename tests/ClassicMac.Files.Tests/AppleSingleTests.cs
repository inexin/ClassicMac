using ClassicMac.Core;
using ClassicMac.Files.Containers;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class AppleSingleTests
{
    private static readonly ContainerReadOptions Utc = ContainerReadOptions.Default with { TimeZone = TimeZoneInfo.Utc };

    private static (MacFile File, List<Diagnostic> Diagnostics) Read(
        byte[] bytes, AppleSingleReader? reader = null, string? hostName = null, ContainerReadOptions? options = null)
    {
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(options ?? Utc, diagnostics,
            hostName is null ? null : MacString.FromMacRoman(hostName));
        var input = ForkData.FromBytes(bytes);
        reader ??= AppleSingleReader.AppleSingle;
        Assert.True(reader.CanRead(input));
        return (Assert.Single(reader.Read(input, context)), diagnostics);
    }

    [Fact]
    public void Version_2_AppleSingle_carries_a_whole_file()
    {
        var bytes = AppleSingle(AppleSingleMagic, 0x00020000, "",
            (3, "Read Me"u8.ToArray()),
            (9, FinderInfo("TEXT", "ttxt", flags: 0x0100, v: 10, h: 20)),
            (8, Int32s(0, 86400, 0, int.MinValue)),
            (1, "hello"u8.ToArray()),
            (2, [1, 2, 3]));

        var (file, diagnostics) = Read(bytes);

        Assert.Empty(diagnostics);
        Assert.Equal("Read Me", file.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal(FinderFlags.HasBeenInited, file.FinderInfo.Flags);
        Assert.Equal(new MacPoint(10, 20), file.FinderInfo.Location);
        Assert.Equal(0xFE, file.FinderInfo.Extended.Span[0]);
        Assert.Equal(new DateTime(2000, 1, 1), file.Created!.Value.ToDateTime());
        Assert.Equal(new DateTime(2000, 1, 2), file.Modified!.Value.ToDateTime());
        Assert.Equal("hello"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal([1, 2, 3], file.ResourceFork.ToArray());
    }

    [Fact]
    public void Version_2_dates_are_converted_to_the_readers_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var bytes = AppleSingle(AppleSingleMagic, 0x00020000, "", (8, Int32s(3600, 3600, 0, 0)));

        var (file, _) = Read(bytes, options: ContainerReadOptions.Default with { TimeZone = zone });

        Assert.Equal(new DateTime(2000, 1, 1, 3, 0, 0), file.Created!.Value.ToDateTime());
    }

    [Fact]
    public void Unknown_dates_are_left_out()
    {
        var (file, _) = Read(AppleSingle(AppleSingleMagic, 0x00020000, "", (8, Int32s(int.MinValue, int.MinValue, 0, 0))));
        Assert.Null(file.Created);
        Assert.Null(file.Modified);
    }

    [Fact]
    public void Version_1_Macintosh_file_info_holds_Mac_dates()
    {
        var bytes = AppleSingle(AppleSingleMagic, 0x00010000, "Macintosh",
            (7, UInt32s(2_526_595_200, 2_526_595_260, 0, 0)),
            (9, FinderInfo("APPL", "MACS")));

        var (file, diagnostics) = Read(bytes, hostName: "Finder");

        Assert.Empty(diagnostics);
        Assert.Equal("Finder", file.Name.ToMacRoman()); // no Real Name entry: the host name
        Assert.Equal(new DateTime(1984, 1, 24), file.Created!.Value.ToDateTime());
        Assert.Equal(new DateTime(1984, 1, 24, 0, 1, 0), file.Modified!.Value.ToDateTime());
    }

    [Fact]
    public void AppleDouble_header_files_have_no_data_fork()
    {
        var bytes = AppleSingle(AppleDoubleMagic, 0x00020000, "",
            (9, FinderInfo("PICT", "8BIM")),
            (2, [9, 9]));

        var (file, _) = Read(bytes, AppleSingleReader.AppleDouble, hostName: "photo");

        Assert.Equal(0, file.DataFork.Length);
        Assert.Equal([9, 9], file.ResourceFork.ToArray());
        Assert.False(AppleSingleReader.AppleSingle.CanRead(ForkData.FromBytes(bytes)));
        Assert.Equal("AppleDouble", AppleSingleReader.AppleDouble.FormatName);
    }

    [Fact]
    public void Short_Finder_info_is_padded()
    {
        var (file, _) = Read(AppleSingle(AppleSingleMagic, 0x00020000, "", (9, FinderInfo("TEXT", "ttxt")[..16])));
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(new byte[16], file.FinderInfo.Extended.ToArray());
    }

    [Fact]
    public void Damaged_and_unknown_entries_are_reported()
    {
        var bytes = AppleSingle(AppleSingleMagic, 0x00020000, "", (1, [1, 2, 3, 4]), (99, [0]), (4, [0]));
        // Point the data fork past the end.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(26 + 4), 1000);

        var (file, diagnostics) = Read(bytes);

        Assert.Equal(0, file.DataFork.Length);
        Assert.Equal(["applesingle.entry-out-of-range", "applesingle.no-name", "applesingle.unknown-entry"],
            diagnostics.Select(d => d.Code).Order());
    }

    [Fact]
    public void A_truncated_entry_table_is_an_error()
    {
        var bytes = AppleSingle(AppleSingleMagic, 0x00020000, "", (1, [1]))[..30];
        var (_, diagnostics) = Read(bytes, hostName: "x");
        Assert.Equal("applesingle.entries-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Other_data_is_not_AppleSingle()
    {
        var unknownVersion = AppleSingle(AppleSingleMagic, 0x00030000, "");
        Assert.False(AppleSingleReader.AppleSingle.CanRead(ForkData.FromBytes(unknownVersion)));
        Assert.False(AppleSingleReader.AppleSingle.CanRead(ForkData.FromBytes(unknownVersion[..3])));
        Assert.False(AppleSingleReader.AppleSingle.CanRead(ForkData.FromBytes("plain text, not a container"u8.ToArray())));
    }
}
