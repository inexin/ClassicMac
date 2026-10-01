using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Iso;
using static ClassicMac.Files.Tests.IsoBuilder;

namespace ClassicMac.Files.Tests;

public class RawCdTests
{
    private static byte[] Disc()
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("README.TXT;1", Encoding.ASCII.GetBytes("hello from the disc")));
        builder.Add("", new Rec("PAIR.BIN;1", Encoding.ASCII.GetBytes("RSRC"), Flags: 4, SystemUse: AA("APPL", "RLMZ", 0)));
        builder.Add("", new Rec("PAIR.BIN;1", Encoding.ASCII.GetBytes("DATA")));
        return builder.Build();
    }

    // Raw sectors around 2048-byte blocks: mode 1 (sync, header, data at 16), mode 2 form 1 (subheader, data at 24),
    // or 2336-byte mode 2 sectors (subheader, data at 8). Error correction bytes are left zero; nothing reads them.
    private static byte[] Raw(byte[] cooked, int sectorSize, int mode = 1, int start = 0)
    {
        var sectors = cooked.Length / 2048;
        var raw = new byte[sectors * sectorSize];
        for (var i = 0; i < sectors; i++)
        {
            var sector = raw.AsSpan(i * sectorSize, sectorSize);
            var data = cooked.AsSpan(i * 2048, 2048);
            if (sectorSize == 2336)
            {
                data.CopyTo(sector[8..]);
                continue;
            }
            sector[0] = 0;
            sector[1..11].Fill(0xFF);
            sector[11] = 0;
            var frame = start + i + 150;
            sector[12] = Bcd(frame / 75 / 60);
            sector[13] = Bcd(frame / 75 % 60);
            sector[14] = Bcd(frame % 75);
            sector[15] = (byte)mode;
            data.CopyTo(sector[(mode == 1 ? 16 : 24)..]);
        }
        return raw;
    }

    private static IReadOnlyList<ContainerNode> Leaves(MacFile file, Func<IEnumerable<MacFile>>? siblings = null)
    {
        var diagnostics = new List<Diagnostic>();
        var root = ContainerUnwrapper.Default.Unwrap(file, "host file", new ContainerContext(diagnostics: diagnostics, siblings: siblings));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return root.Leaves().ToList();
    }

    private static MacFile File(string name, byte[] data) => new() { Name = MacString.FromMacRoman(name), DataFork = ForkData.FromBytes(data) };

    [Theory]
    [InlineData(2352, 1)]
    [InlineData(2352, 2)]
    [InlineData(2336, 2)]
    public void Raw_sector_images_read_as_the_disc(int sectorSize, int mode)
    {
        var raw = Raw(Disc(), sectorSize, mode);

        Assert.True(RawCdReader.Instance.CanRead(ForkData.FromBytes(raw)));
        var leaves = Leaves(File("disc.bin", raw));

        Assert.Equal(["README.TXT", "PAIR.BIN"], leaves.Select(l => l.File.Name.ToMacRoman()));
        Assert.Equal("hello from the disc"u8.ToArray(), leaves[0].File.DataFork.ToArray());
        Assert.Equal("RSRC"u8.ToArray(), leaves[1].File.ResourceFork.ToArray());
    }

    [Fact]
    public void Cooked_images_and_other_data_are_not_raw()
    {
        Assert.False(RawCdReader.Instance.CanRead(ForkData.FromBytes(Disc())));
        Assert.False(RawCdReader.Instance.CanRead(ForkData.FromBytes(new byte[2352 * 20])));
    }

    [Fact]
    public void Cue_sheets_read_their_first_data_track_from_the_file_beside_them()
    {
        // One .bin: the data track, then two seconds of audio after it.
        var data = Raw(Disc(), 2352);
        var dataSectors = data.Length / 2352;
        byte[] bin = [.. data, .. new byte[2352 * 150]];
        var audioStart = $"{dataSectors / 75 / 60:D2}:{dataSectors / 75 % 60:D2}:{dataSectors % 75:D2}";
        var cue = $"""
            REM made by a test
            FILE "disc image.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 {audioStart}
                INDEX 01 {audioStart}

            """;
        var cueFile = File("disc image.cue", Encoding.ASCII.GetBytes(cue.Replace("\n", "\r\n")));

        Assert.True(CueSheetReader.Instance.CanRead(cueFile.DataFork));
        var leaves = Leaves(cueFile, () => [File("disc image.bin", bin), File("other.txt", [1])]);

        Assert.Equal(["README.TXT", "PAIR.BIN"], leaves.Select(l => l.File.Name.ToMacRoman()));

        // Without the .bin beside it, the cue sheet cannot be read.
        var diagnostics = new List<Diagnostic>();
        ContainerUnwrapper.Default.Unwrap(cueFile, "host file", new ContainerContext(diagnostics: diagnostics, siblings: () => []));
        Assert.Contains(diagnostics, d => d.Code == "container.unreadable");
    }

    private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);

    private static string Msf(int frames) => $"{frames / 75 / 60:D2}:{frames / 75 % 60:D2}:{frames % 75:D2}";

    private const int Gap = 10;

    // Two sessions: the first has OLD.TXT and A.TXT; the second, from absolute sector Start (after a gap), has
    // descriptors whose root lists A.TXT where the first session put it and B.TXT in its own sectors.
    private static (byte[] First, byte[] Second, int Start) Sessions()
    {
        var first = new IsoBuilder();
        first.Add("", new Rec("OLD.TXT;1", "only in session 1"u8.ToArray()));
        first.Add("", new Rec("A.TXT;1", "alpha from session 1"u8.ToArray()));
        var one = first.Build();
        var a = Enumerable.Range(0, one.Length / 2048).Single(i => one.AsSpan(i * 2048).StartsWith("alpha"u8));

        var start = one.Length / 2048 + Gap;
        var second = new IsoBuilder { Origin = start };
        second.Add("", new Rec("A.TXT;1", "alpha from session 1"u8.ToArray(), At: a));
        second.Add("", new Rec("B.TXT;1", "beta from session 2"u8.ToArray()));
        return (one, second.Build(), start);
    }

    private static void AssertLastSession(IReadOnlyList<ContainerNode> leaves)
    {
        Assert.Equal(["A.TXT", "B.TXT"], leaves.Select(l => l.File.Name.ToMacRoman()));
        Assert.Equal("alpha from session 1"u8.ToArray(), leaves[0].File.DataFork.ToArray());
        Assert.Equal("beta from session 2"u8.ToArray(), leaves[1].File.DataFork.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cue_sheets_read_the_last_session_of_2048_byte_tracks(bool sessionLines)
    {
        // One file holding the whole disc, the gap between the sessions included.
        var (one, two, start) = Sessions();
        byte[] bin = [.. one, .. new byte[Gap * 2048], .. two];
        var cue = $"""
            FILE "disc.bin" BINARY
            {(sessionLines ? "REM SESSION 01" : "")}
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            {(sessionLines ? "REM SESSION 02" : "")}
              TRACK 02 MODE1/2048
                INDEX 01 {Msf(start)}

            """;
        var leaves = Leaves(File("disc.cue", Encoding.ASCII.GetBytes(cue)), () => [File("disc.bin", bin)]);

        AssertLastSession(leaves);
    }

    [Fact]
    public void Cue_sheets_read_the_last_session_of_raw_tracks_placed_by_their_headers()
    {
        // One file per track, the gap between the sessions left out: the second track's sector headers give its address.
        var (one, two, start) = Sessions();
        var cue = """
            REM SESSION 01
            FILE "disc (Track 1).bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            REM SESSION 02
            FILE "disc (Track 2).bin" BINARY
              TRACK 02 MODE2/2352
                INDEX 01 00:00:00

            """;
        var leaves = Leaves(File("disc.cue", Encoding.ASCII.GetBytes(cue)),
            () => [File("disc (Track 1).bin", Raw(one, 2352)), File("disc (Track 2).bin", Raw(two, 2352, mode: 2, start: start))]);

        AssertLastSession(leaves);
    }

    [Fact]
    public void Raw_images_of_a_whole_disc_read_the_session_after_the_jump_in_address()
    {
        var (one, two, start) = Sessions();
        byte[] raw = [.. Raw(one, 2352), .. Raw(two, 2352, start: start)];

        AssertLastSession(Leaves(File("disc.bin", raw)));
    }

    [Fact]
    public void A_last_session_whose_root_lies_before_it_counts_from_its_start()
    {
        // Block numbers counted from the session's start (root extent < D): the disc is mapped from there.
        var (one, _, start) = Sessions();
        var relative = new IsoBuilder();
        relative.Add("", new Rec("B.TXT;1", "beta from session 2"u8.ToArray()));
        byte[] bin = [.. one, .. new byte[Gap * 2048], .. relative.Build()];
        var cue = $"""
            FILE "disc.bin" BINARY
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
              TRACK 02 MODE1/2048
                INDEX 01 {Msf(start)}

            """;
        var leaves = Leaves(File("disc.cue", Encoding.ASCII.GetBytes(cue)), () => [File("disc.bin", bin)]);

        Assert.Equal(["B.TXT"], leaves.Select(l => l.File.Name.ToMacRoman()));
        Assert.Equal("beta from session 2"u8.ToArray(), leaves[0].File.DataFork.ToArray());
    }

    [Fact]
    public void A_session_without_descriptors_falls_back_to_sector_16()
    {
        // The second session's track holds no volume: the first session's descriptors are read.
        var (one, _, start) = Sessions();
        byte[] bin = [.. one, .. new byte[Gap * 2048], .. new byte[20 * 2048]];
        var cue = $"""
            FILE "disc.bin" BINARY
            REM SESSION 01
              TRACK 01 MODE1/2048
                INDEX 01 00:00:00
            REM SESSION 02
              TRACK 02 MODE1/2048
                INDEX 01 {Msf(start)}

            """;
        var leaves = Leaves(File("disc.cue", Encoding.ASCII.GetBytes(cue)), () => [File("disc.bin", bin)]);

        Assert.Equal(["OLD.TXT", "A.TXT"], leaves.Select(l => l.File.Name.ToMacRoman()));
    }
}
