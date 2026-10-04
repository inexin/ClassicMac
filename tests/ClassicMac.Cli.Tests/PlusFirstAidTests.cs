using System.Buffers.Binary;
using ClassicMac.Files.Tests;

namespace ClassicMac.Cli.Tests;

// `check` and `repair` on an HFS Plus volume image (docs/cli.md §2.7, §3.2).
public sealed class PlusFirstAidTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-plus").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = new CommandLine(output, new StringWriter(), new MemoryStream()).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"));
    }

    private string Volume(bool damaged)
    {
        var builder = new HfsPlusBuilder();
        builder.File(HfsPlusBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var image = builder.Build("Plus");
        if (damaged)
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 32), 5);       // fileCount
        }

        var path = Path.Combine(folder, damaged ? "damaged.img" : "sound.img");
        File.WriteAllBytes(path, image);
        return path;
    }

    [Fact]
    public void Check_runs_First_Aid_on_an_HFS_Plus_volume()
    {
        var sound = Run("check", Volume(damaged: false)).Output;
        Assert.Contains("first aid: The volume “Plus” appears to be OK.\n", sound);
        Assert.DoesNotContain("volume:", sound);                                    // the writer's checks are HFS's

        var (code, output) = Run("check", Volume(damaged: true));

        Assert.Equal(1, code);
        Assert.Contains("first aid: Problem:  Volume Header needs minor repair, 0, 0\n", output);
    }

    // A file's thread made a folder thread: the reader refuses the volume, but First Aid still checks and repairs it.
    [Fact]
    public void A_volume_the_reader_refuses_still_gets_First_Aid()
    {
        var builder = new HfsPlusBuilder();
        uint file = builder.File(HfsPlusBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var image = builder.Build("Plus");
        var thread = HfsPlusBuilder.CatalogKey(file, "");
        byte[] pattern = [.. thread, 0, 4];
        int at = image.AsSpan().IndexOf(pattern);
        image[at + thread.Length + 1] = 3;
        var path = Path.Combine(folder, "refused.img");
        File.WriteAllBytes(path, image);

        var (code, output) = Run("check", path);

        Assert.Equal(1, code);
        Assert.Contains("first aid: Problem:  Missing thread record", output);
        Assert.Contains("first aid: The volume “Plus” needs to be repaired.\n", output);
        var repaired = Path.Combine(folder, "repaired.img");
        Assert.Equal(0, Run("repair", path, "-o", repaired).Code);
        Assert.Equal(0, Run("check", repaired).Code);
    }

    [Fact]
    public void Repair_repairs_an_HFS_Plus_volume()
    {
        var output = Path.Combine(folder, "out.img");

        var (code, text) = Run("repair", Volume(damaged: true), "-o", output);

        Assert.Equal(0, code);
        Assert.Contains("first aid: The volume “Plus” was repaired successfully.\n", text);
        Assert.Equal(0, Run("check", output).Code);
    }
}
