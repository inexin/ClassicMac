using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli.Tests;

// `ndif` (docs/cli.md §3.4): a new Disk Copy 6 image of a disk, as an AppleDouble pair or a Basilisk II entry, whole or
// in parts.
public sealed class NdifCommandTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-ndif").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private string Volume()
    {
        var path = Path.Combine(folder, "disk.img");
        var image = HfsWriter.Format(800 * 1024, "Source");
        File.WriteAllBytes(path, HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", "hello"u8.ToArray(), ReadOnlyMemory<byte>.Empty, FinderInfo.Empty));
        return path;
    }

    private static byte[] Disk(string imagePath)
    {
        var host = HostFiles.Read(imagePath);
        return Assert.Single(NdifReader.Instance.Read(host.File, new ContainerContext(siblings: HostFiles.Siblings(imagePath)))).DataFork.ToArray();
    }

    [Fact]
    public void A_disk_becomes_a_compressed_image_beside_its_resource_fork()
    {
        var disk = Volume();
        var image = Path.Combine(folder, "out", "Source.img");

        var (code, output, error) = Run("ndif", disk, image);

        Assert.True(code == ExitCodes.Success, error);
        Assert.StartsWith($"Wrote {image} (NDIF ADC, \"Source\", 819,200-byte disk", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "out", "._Source.img")));                // AppleDouble by default
        Assert.Equal(File.ReadAllBytes(disk), Disk(image));
        Assert.Equal(ExitCodes.Success, Run("check", image).Code);
    }

    [Theory]
    [InlineData("read-write", "dimg", 10)]
    [InlineData("read-only", "rohd", 10)]
    [InlineData("kencode", "rohd", 10)]
    [InlineData("adc", "rohd", 11)]
    public void The_format_chooses_the_kind_of_image(string format, string type, int version)
    {
        var disk = Volume();
        var image = Path.Combine(folder, format + ".img");

        var (code, _, error) = Run("ndif", disk, image, "--format", format, "--layout", "basilisk");

        Assert.True(code == ExitCodes.Success, error);
        Assert.True(File.Exists(Path.Combine(folder, ".rsrc", format + ".img")));
        var host = HostFiles.Read(image).File;
        Assert.Equal(type, host.FinderInfo.Type.ToString());
        var map = ClassicMac.Resources.ResourceFork.Read(host.ResourceFork.ToArray()).Find(FourCC.FromString("bcem"), 128)!.GetData().ToArray();
        Assert.Equal(version, map[1]);
        Assert.Equal(File.ReadAllBytes(disk), Disk(image));
    }

    [Fact]
    public void Segments_write_the_parts_of_a_segmented_image()
    {
        var disk = Volume();
        var image = Path.Combine(folder, "Split.img");

        var (code, output, error) = Run("ndif", disk, image, "--format", "read-only", "--segments", "3", "--json");

        Assert.True(code == ExitCodes.Success, error);
        using var json = JsonDocument.Parse(output);
        var written = json.RootElement.GetProperty("written").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([Path.Combine(folder, "Split 1of3"), Path.Combine(folder, "Split 2of3"), Path.Combine(folder, "Split 3of3")], written);
        Assert.Equal("read-only", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("segments").GetInt32());
        Assert.Equal(File.ReadAllBytes(disk), Disk(written[0]!));
    }

    // A disk image the editor opens (here an NDIF image) gives its disk; any other plain file is taken as a disk as it is.
    [Fact]
    public void A_disk_image_gives_its_disk_and_a_plain_file_is_one()
    {
        var disk = Volume();
        var first = Path.Combine(folder, "first.img");
        Assert.Equal(ExitCodes.Success, Run("ndif", disk, first, "--format", "kencode").Code);
        var second = Path.Combine(folder, "second.img");

        var (code, _, error) = Run("ndif", first, second, "--format", "adc", "--chunk-size", "64");

        Assert.True(code == ExitCodes.Success, error);
        Assert.Equal(File.ReadAllBytes(disk), Disk(second));
        var raw = Path.Combine(folder, "raw.bin");
        File.WriteAllBytes(raw, new byte[50 * 512]);
        Assert.Equal(ExitCodes.Success, Run("ndif", raw, Path.Combine(folder, "raw.img")).Code);
    }

    [Fact]
    public void Bad_requests_are_refused()
    {
        var disk = Volume();
        var image = Path.Combine(folder, "x.img");
        File.WriteAllText(Path.Combine(folder, "odd.bin"), "not whole sectors");

        Assert.Equal(ExitCodes.Usage, Run("ndif", Path.Combine(folder, "odd.bin"), image).Code);
        Assert.Equal(ExitCodes.Usage, Run("ndif", disk, image, "--segments", "1").Code);
        Assert.Equal(ExitCodes.Usage, Run("ndif", disk, image, "--chunk-size", "0").Code);
        Assert.Equal(ExitCodes.Success, Run("ndif", disk, image).Code);
        var (code, _, error) = Run("ndif", disk, image);
        Assert.Equal(ExitCodes.IoError, code);                                                   // exists
        Assert.Contains("--overwrite", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, Run("ndif", disk, image, "--overwrite").Code);
    }
    // The write commands edit a segmented image through part 1 and write every part (ndif.md §3.4).
    [Fact]
    public void A_segmented_image_is_edited_through_its_first_part()
    {
        var disk = Volume();
        Assert.Equal(ExitCodes.Success, Run("ndif", disk, Path.Combine(folder, "Seg.img"), "--segments", "3").Code);
        var output = Path.Combine(folder, "out", "Edited 1of3");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var (code, _, error) = Run("mkdir", Path.Combine(folder, "Seg 1of3") + ":Docs", "-o", output);

        Assert.True(code == ExitCodes.Success, error);
        Assert.True(File.Exists(Path.Combine(folder, "out", "Edited 3of3")));
        var (listed, list, _) = Run("ls", output);
        Assert.Equal(ExitCodes.Success, listed);
        Assert.Contains("Docs", list, StringComparison.Ordinal);
    }
}
