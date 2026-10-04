using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli.Tests;

// `format` (docs/cli.md §3.4): a new, empty HFS volume image.
public sealed class FormatCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-format").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private static IReadOnlyList<MacFile> Files(string image) => HfsReader.Instance.Read(ForkData.FromFile(image), new ContainerContext());

    [Fact]
    public void Format_writes_a_new_volume_that_the_other_commands_take()
    {
        var disk = Path.Combine(folder, "blank.img");

        var (code, output, error) = Run("format", disk, "--size", "20MiB", "--name", "Blank");

        Assert.True(code == ExitCodes.Success, error);
        Assert.Equal($"Wrote {disk} (HFS \"Blank\", 20,971,520 bytes, 512-byte blocks)\n", output);
        Assert.Equal(20L << 20, new FileInfo(disk).Length);
        Assert.Equal("Blank", HfsReader.Instance.ReadVolumeInfo(ForkData.FromFile(disk))!.Name);
        Assert.Equal(ExitCodes.Success, Run("check", disk).Code);
        var note = Path.Combine(folder, "note.txt");
        File.WriteAllText(note, "Hi");
        Assert.Equal(ExitCodes.Success, Run("put", note, disk + ":", "--in-place").Code);
    }

    [Fact]
    public void Resize_grows_a_volume_image()
    {
        var disk = Path.Combine(folder, "small.img");
        Assert.Equal(ExitCodes.Success, Run("format", disk, "--size", "800K", "--name", "Small").Code);
        var grown = Path.Combine(folder, "grown.img");

        var (code, output, error) = Run("resize", disk, "--size", "1440K", "-o", grown);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("resize (to 1,474,560 bytes)", output);
        Assert.Equal(1440 * 1024, new FileInfo(grown).Length);
        Assert.Equal(800 * 1024, new FileInfo(disk).Length);
        Assert.Equal(ExitCodes.Success, Run("check", grown).Code);
        var shrunk = Path.Combine(folder, "shrunk.img");
        Assert.Equal(ExitCodes.Success, Run("resize", grown, "--size", "400K", "-o", shrunk).Code);    // an empty volume shrinks
        Assert.Equal(400 * 1024, new FileInfo(shrunk).Length);
        Assert.Equal(ExitCodes.Success, Run("check", shrunk).Code);
        var big = Path.Combine(folder, "big.img");
        Assert.Equal(ExitCodes.Success, Run("resize", disk, "--size", "40M", "-o", big).Code);           // past 65,535 blocks of 512 bytes
        Assert.Equal(ExitCodes.Success, Run("check", big).Code);
        Assert.Equal(ExitCodes.Usage, Run("resize", disk, "--size", "3G", "-o", big).Code);             // past what is made in memory
    }

    // defrag (docs/cli.md §3.4): a volume whose free space lies in gaps between a file's pieces laid out again.
    [Fact]
    public void Defrag_lays_every_fork_out_whole()
    {
        var image = HfsWriter.Format(2 * 1024 * 1024, "Frag");
        for (var i = 0; i < 20; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Pad {i:D2}", new byte[1024], Array.Empty<byte>(), FinderInfo.Empty);
        }

        for (var i = 0; i < 20; i += 2)
        {
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), $"Pad {i:D2}");
        }

        var free = new BigEndianReader(image).ReadUInt16At(1024 + 0x22);
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Spread", new byte[(free - 4) * 512], Array.Empty<byte>(), FinderInfo.Empty);
        var disk = Path.Combine(folder, "frag.img");
        File.WriteAllBytes(disk, image);
        var defragmented = Path.Combine(folder, "defrag.img");

        var (code, output, error) = Run("defrag", disk, "-o", defragmented);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("defragment (every fork in one extent, the free space in one run at the end)", output);
        Assert.Equal(ExitCodes.Success, Run("check", defragmented).Code);
        Assert.Equal(Files(disk).Select(f => f.MacPath), Files(defragmented).Select(f => f.MacPath));
        Assert.Equal(image, File.ReadAllBytes(disk));
    }

    [Fact]
    public void An_existing_file_is_replaced_only_with_overwrite()
    {
        var disk = Path.Combine(folder, "blank.img");
        File.WriteAllText(disk, "keep");

        Assert.Equal(ExitCodes.IoError, Run("format", disk, "--size", "800K", "--name", "Blank").Code);
        Assert.Equal("keep", File.ReadAllText(disk));
        Assert.Equal(ExitCodes.Success, Run("format", disk, "--size", "800K", "--name", "Blank", "--overwrite").Code);
        Assert.Equal(800 * 1024, new FileInfo(disk).Length);
    }

    [Fact]
    public void JSON_describes_the_volume_and_bad_sizes_or_names_are_usage_errors()
    {
        var disk = Path.Combine(folder, "blank.img");
        var json = JsonDocument.Parse(Run("format", disk, "--size", "800K", "--name", "Blank", "--json").Output).RootElement;
        Assert.Equal((disk, "Blank", 819200L, 512L), (json.GetProperty("written")[0].GetString(), json.GetProperty("name").GetString(),
            json.GetProperty("size").GetInt64(), json.GetProperty("blockSize").GetInt64()));

        Assert.Equal(ExitCodes.Usage, Run("format", Path.Combine(folder, "a.img"), "--size", "100K", "--name", "Blank").Code);
        Assert.Equal(ExitCodes.Usage, Run("format", Path.Combine(folder, "b.img"), "--size", "800K", "--name", "A:B").Code);
        Assert.False(File.Exists(Path.Combine(folder, "a.img")));
    }
}
