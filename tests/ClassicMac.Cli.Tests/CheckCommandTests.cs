using System.Buffers.Binary;
using System.Text.Json;
using ClassicMac.Files.Tests;

namespace ClassicMac.Cli.Tests;

// `check` (docs/cli.md §2.7): an input read through, its diagnostics, and a plain HFS volume's writer checks.
public sealed class CheckCommandTests : IDisposable
{
    private const int Mdb = 2 * HfsBuilder.Block;

    private readonly string folder = Directory.CreateTempSubdirectory("cm-check").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error, new MemoryStream()).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private string Damaged()
    {
        var disk = WritableDisk.Build(folder, "damaged.img");
        var image = File.ReadAllBytes(disk);
        var free = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x22));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(Mdb + 0x22), (ushort)(free - 1));
        File.WriteAllBytes(disk, image);
        return disk;
    }

    [Fact]
    public void A_sound_volume_passes()
    {
        var disk = WritableDisk.Build(folder);

        var (code, output, _) = Run("check", disk);

        Assert.Equal(0, code);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal("volume: passes the writer's checks", lines[^2]);
        Assert.Equal("0 errors, 0 warnings", lines[^1]);
    }

    [Fact]
    public void A_volume_the_writer_refuses_fails_with_the_fault()
    {
        var (code, output, _) = Run("check", Damaged());

        Assert.Equal(1, code);
        Assert.Contains("volume: The HFS volume free-block count disagrees with its allocation bitmap.\n", output);
    }

    // A volume the writer refuses counts as an error in the summary, not only in its own line.
    [Fact]
    public void A_volume_fault_is_counted_with_the_errors()
    {
        var disk = WritableDisk.Build(folder, "counts.img");
        var image = File.ReadAllBytes(disk);
        image[Mdb + 0x57]++;                                                            // drFilCnt one too many
        File.WriteAllBytes(disk, image);

        var json = JsonDocument.Parse(Run("check", disk, "--json").Output).RootElement;
        var diagnosed = json.GetProperty("diagnostics").EnumerateArray().Count(d => d.GetProperty("severity").GetString() == "error");

        Assert.False(json.GetProperty("volume").GetProperty("passes").GetBoolean());
        Assert.Equal(diagnosed + 1, json.GetProperty("errors").GetInt32());
        Assert.Matches($@"\n{diagnosed + 1} errors?, ", Run("check", disk).Output);
    }

    [Fact]
    public void JSON_gives_the_diagnostics_the_volume_s_fault_and_the_counts()
    {
        var (code, output, _) = Run("check", Damaged(), "--json");

        Assert.Equal(1, code);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.EndsWith("damaged.img", json.GetProperty("input").GetString());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("diagnostics").ValueKind);
        var volume = json.GetProperty("volume");
        Assert.False(volume.GetProperty("passes").GetBoolean());
        Assert.Equal("The HFS volume free-block count disagrees with its allocation bitmap.", volume.GetProperty("fault").GetString());
        Assert.True(json.GetProperty("errors").GetInt32() >= 0);
        Assert.True(json.GetProperty("warnings").GetInt32() >= 0);

        (code, output, _) = Run("check", WritableDisk.Build(folder), "--json");
        Assert.Equal(0, code);
        json = JsonDocument.Parse(output).RootElement;
        Assert.True(json.GetProperty("volume").GetProperty("passes").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("volume").GetProperty("fault").ValueKind);
        Assert.Equal(0, json.GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void An_input_that_is_no_volume_has_no_volume_check()
    {
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "hello");

        var (code, output, _) = Run("check", file);
        Assert.Equal(0, code);
        Assert.DoesNotContain("volume:", output);

        (_, output, _) = Run("check", file, "--json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(output).RootElement.GetProperty("volume").ValueKind);
    }

    [Fact]
    public void A_file_inside_a_container_inside_the_volume_is_named_through_the_container()
    {
        var inner = new HfsBuilder();
        inner.File(HfsBuilder.Root, "Broken", [], [0, 0, 1, 0, 0, 0, 0x7F, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0]);
        var outer = new HfsBuilder();
        var images = outer.Folder(HfsBuilder.Root, "Images");
        outer.File(images, "Inner.img", inner.Build("Inner"), [], type: "rohd", creator: "ddsk");
        var disk = Path.Combine(folder, "outer.img");
        File.WriteAllBytes(disk, outer.Build("Outer"));

        var (_, output, _) = Run("check", disk, "--deep");

        Assert.Contains("outer.img > Images:Inner.img > Broken: error", output);
    }

    // Without --deep, containers stored in the input are not opened: their damage is their contents', not the volume's.
    [Fact]
    public void Containers_inside_the_input_are_opened_only_with_deep()
    {
        var inner = new HfsBuilder();
        inner.File(HfsBuilder.Root, "Broken", [], [0, 0, 1, 0, 0, 0, 0x7F, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0]);
        var outer = new HfsBuilder();
        var images = outer.Folder(HfsBuilder.Root, "Images");
        outer.File(images, "Inner.img", inner.Build("Inner"), [], type: "rohd", creator: "ddsk");
        var disk = Path.Combine(folder, "outer.img");
        File.WriteAllBytes(disk, outer.Build("Outer"));

        var (code, output, _) = Run("check", disk);

        Assert.Equal(0, code);
        Assert.DoesNotContain("Broken", output);
        Assert.Contains("1 container in it not opened (--deep checks inside them)", output);
        Assert.Equal(1, Run("check", disk, "--deep").Code);
        var json = System.Text.Json.JsonDocument.Parse(Run("check", disk, "--json").Output).RootElement;
        Assert.Equal(1, json.GetProperty("notOpened").GetInt32());
    }

    [Fact]
    public void A_damaged_resource_fork_in_the_volume_is_reported_as_an_error()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Broken", [], [0, 0, 1, 0, 0, 0, 0x7F, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0]);   // a map far past the end
        var disk = Path.Combine(folder, "broken.img");
        File.WriteAllBytes(disk, builder.Build("Disk"));

        var (code, output, _) = Run("check", disk);

        Assert.Equal(1, code);
        Assert.Matches(@"broken\.img > .*Broken.*: error: .*\[", output);
        Assert.Contains("volume: passes the writer's checks\n", output);
        Assert.DoesNotMatch(@"^0 errors", output.TrimEnd('\n').Split('\n')[^1]);
    }
}
