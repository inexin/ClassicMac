using System.Buffers.Binary;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli.Tests;

// `check` runs First Aid on each HFS volume (docs/cli.md §2.7): Disk First Aid's problem lines and its verdict.
public sealed class CheckFirstAidTests : IDisposable
{
    private const int Mdb = 1024;

    private readonly string folder = Directory.CreateTempSubdirectory("cm-firstaid").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = new CommandLine(output, new StringWriter(), new MemoryStream()).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"));
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

    // drFilCnt + 1: Disk First Aid says the MDB needs minor repair (detail 2) [Verified: B6].
    private static void WrongFileCount(byte[] image) =>
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(Mdb + 0x54), BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(Mdb + 0x54)) + 1);

    [Fact]
    public void A_sound_volume_appears_to_be_OK()
    {
        var (code, output) = Run("check", Volume("ok.img"));

        Assert.Equal(0, code);
        Assert.Contains("first aid: The volume “First Aid” appears to be OK.\n", output);
        Assert.EndsWith("0 errors, 0 warnings\n", output);
    }

    [Fact]
    public void A_volume_that_needs_repair_lists_its_problems_and_fails()
    {
        var (code, output) = Run("check", Volume("count.img", WrongFileCount));

        Assert.Equal(1, code);
        Assert.Contains("first aid: Problem:  Master Directory Block needs minor repair, 2, 0\n", output);
        Assert.Contains("first aid: The volume “First Aid” needs to be repaired.\n", output);
        Assert.Contains("1 error,", output);
    }

    [Fact]
    public void JSON_gives_the_verdict_and_the_problems()
    {
        var (code, output) = Run("check", Volume("count.img", WrongFileCount), "--json");

        Assert.Equal(1, code);
        var firstAid = JsonDocument.Parse(output).RootElement.GetProperty("firstAid");
        Assert.Equal("needsRepair", firstAid.GetProperty("verdict").GetString());
        Assert.Equal("The volume “First Aid” needs to be repaired.", firstAid.GetProperty("summary").GetString());
        var problem = Assert.Single(firstAid.GetProperty("problems").EnumerateArray());
        Assert.Equal(58, problem.GetProperty("number").GetInt32());
        Assert.Equal("Master Directory Block needs minor repair", problem.GetProperty("message").GetString());
        Assert.Equal((2, 0), (problem.GetProperty("arg2").GetInt64(), problem.GetProperty("arg3").GetInt64()));
        Assert.Equal("Checking volume info.", problem.GetProperty("stage").GetString());
        Assert.True(problem.GetProperty("repairable").GetBoolean());
        Assert.Equal("diskFirstAid", problem.GetProperty("origin").GetString());
    }

    [Fact]
    public void An_input_that_is_no_volume_has_no_First_Aid()
    {
        var path = Path.Combine(folder, "plain.txt");
        File.WriteAllText(path, "hello");

        var (_, output) = Run("check", path, "--json");

        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(output).RootElement.GetProperty("firstAid").ValueKind);
    }
}
