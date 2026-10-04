using System.Buffers.Binary;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli.Tests;

// `repair` (docs/cli.md §3.2): First Aid's repair of an HFS volume, its changes printed, the volume verified again; a
// volume that appears to be OK or cannot be repaired is not written.
public sealed class RepairCommandTests : IDisposable
{
    private const int Mdb = 1024;

    private readonly string folder = Directory.CreateTempSubdirectory("cm-repair").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Out => Path.Combine(folder, "out.img");

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error, new MemoryStream()).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private string Volume(string name, Action<byte[]>? damage = null)
    {
        var image = HfsWriter.Format(1024 * 1024, "First Aid");
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", new byte[100], ReadOnlyMemory<byte>.Empty, FinderInfo.Empty);
        damage?.Invoke(image);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    // drFilCnt + 1: the MDB needs minor repair [Verified: B6].
    private static void WrongFileCount(byte[] image) =>
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(Mdb + 0x54), BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 0x54)) + 1);

    // The alternate MDB's signature cleared: Disk First Aid says the disk is not HFS.
    private static void NoAlternate(byte[] image) => image.AsSpan(image.Length - 1024, 2).Clear();

    [Fact]
    public void A_volume_that_needs_repair_is_repaired_into_a_new_file()
    {
        var input = Volume("count.img", WrongFileCount);
        var before = File.ReadAllBytes(input);

        var (code, output, _) = Run("repair", input, "-o", Out);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("repair (master directory block written from the values First Aid computed)\n", output);
        Assert.Contains("first aid: The volume “First Aid” was repaired successfully.\n", output);
        Assert.Contains($"Wrote {Out}\n", output);
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(Out).AsSpan(Mdb + 0x54)));
        Assert.Equal(ExitCodes.Success, Run("check", Out).Code);
    }

    [Fact]
    public void A_dry_run_lists_the_repairs_and_writes_nothing()
    {
        var input = Volume("count.img", WrongFileCount);
        var before = File.ReadAllBytes(input);

        var (code, output, error) = Run("repair", input, "--dry-run");

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("repair (master directory block", output);
        Assert.Contains("Dry run: nothing written.\n", output);
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Fact]
    public void In_place_repairs_the_input_and_keeps_the_original()
    {
        var input = Volume("count.img", WrongFileCount);
        var before = File.ReadAllBytes(input);

        Assert.Equal(ExitCodes.Success, Run("repair", input, "--in-place").Code);

        Assert.Equal(before, File.ReadAllBytes(input + ".orig"));
        Assert.Equal(ExitCodes.Success, Run("check", input).Code);
    }

    [Fact]
    public void A_volume_that_appears_to_be_OK_is_not_written()
    {
        var (code, output, _) = Run("repair", Volume("ok.img"), "-o", Out);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("first aid: The volume “First Aid” appears to be OK.\n", output);
        Assert.Contains("Nothing to repair: nothing written.\n", output);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void A_volume_First_Aid_cannot_repair_is_not_written_and_fails()
    {
        var (code, output, _) = Run("repair", Volume("alt.img", NoAlternate), "-o", Out);

        Assert.Equal(ExitCodes.Damaged, code);
        Assert.Contains("first aid: This is not an HFS disk.", output);
        Assert.Contains("Nothing written.\n", output);
        Assert.False(File.Exists(Out));
    }

    [Fact]
    public void Json_has_the_changes_and_First_Aid_before_and_after()
    {
        var (code, output, _) = Run("repair", Volume("count.img", WrongFileCount), "--dry-run", "--json");

        Assert.Equal(ExitCodes.Success, code);
        var root = JsonDocument.Parse(output).RootElement;
        Assert.NotEqual(0, root.GetProperty("changes").GetArrayLength());
        Assert.Equal("needsRepair", root.GetProperty("firstAidBefore").GetProperty("verdict").GetString());
        Assert.Equal(58, root.GetProperty("firstAidBefore").GetProperty("problems")[0].GetProperty("number").GetInt32());
        Assert.Equal("appearsOk", root.GetProperty("firstAid").GetProperty("verdict").GetString());
        Assert.Equal("The volume “First Aid” was repaired successfully.", root.GetProperty("firstAid").GetProperty("summary").GetString());
    }
}
