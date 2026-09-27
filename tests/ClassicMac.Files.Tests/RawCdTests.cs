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
    private static byte[] Raw(byte[] cooked, int sectorSize, int mode = 1)
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
            var frame = i + 150;
            sector[12] = (byte)(frame / 75 / 60);
            sector[13] = (byte)(frame / 75 % 60);
            sector[14] = (byte)(frame % 75);
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
}
