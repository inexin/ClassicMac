using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.Cli.Tests;

// Text read in the encoding a file's volume says (docs/formats/codecs/text-encodings.md §5): an HFS Plus file's hint, or
// the volume's System file's region, in place of the default Mac OS Roman; an --encoding chosen wins.
public sealed class VolumeEncodingTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-volenc").FullName;
    private static readonly byte[] Nihongo = [0x93, 0xFA, 0x96, 0x7B];                      // 日本 in Mac OS Japanese

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString());
    }

    private static byte[] Fork(params Resource[] resources)
    {
        var fork = new ResourceFork();
        foreach (var resource in resources)
        {
            fork.Add(resource);
        }

        return fork.ToArray();
    }

    private static Resource Str()
    {
        byte[] text = [4, .. Nihongo];
        return new(FourCC.FromString("STR "), 128, text);
    }

    // An HFS Plus volume whose file "Note" is hinted Mac OS Japanese.
    private string Hinted()
    {
        var builder = new HfsPlusBuilder();
        var note = builder.File(HfsPlusBuilder.Root, "Note", Nihongo, Fork(Str()));
        builder.TextEncoding(note, 1);
        var path = Path.Combine(folder, "plus.img");
        File.WriteAllBytes(path, builder.Build("Plus"));
        return path;
    }

    // An HFS volume with a Japanese System (its 'vers' 1 region 14) and a SimpleText file in Japanese.
    private string WithJapaneseSystem()
    {
        var disk = new HfsBuilder();
        var system = disk.Folder(HfsBuilder.Root, "System Folder");
        disk.File(system, "System", [], Fork(new Resource(FourCC.FromString("vers"), 1, new byte[] { 7, 0x50, 0x80, 0, 0, 14, 3, (byte)'7', (byte)'.', (byte)'5', 0 })),
            "zsys", "MACS");
        // One style run from 0 in the system font (family 0, a Roman one: the text's script is the volume's).
        byte[] styl = [0, 1, 0, 0, 0, 0, 0, 12, 0, 9, 0, 0, 0, 0, 0, 12, 0, 0, 0, 0, 0, 0];
        disk.File(HfsBuilder.Root, "Read Me", [.. Nihongo, 0x0D], Fork(Str(), new Resource(FourCC.FromString("styl"), 128, styl)), "TEXT", "ttxt");
        var path = Path.Combine(folder, "hfs.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static string Texts(string directory) =>
        string.Concat(Directory.EnumerateFiles(directory, "*.txt", SearchOption.AllDirectories).Select(File.ReadAllText));

    [Fact]
    public void Extract_reads_a_file_in_its_volume_s_encoding_unless_one_is_chosen()
    {
        var disk = Hinted();

        Assert.Equal(ExitCodes.Success, Run("extract", disk, "-o", Path.Combine(folder, "a")).Code);
        Assert.Equal(ExitCodes.Success, Run("extract", disk, "-o", Path.Combine(folder, "b"), "--encoding", "greek").Code);

        Assert.Contains("日本", Texts(Path.Combine(folder, "a")), StringComparison.Ordinal);
        Assert.DoesNotContain("日本", Texts(Path.Combine(folder, "b")), StringComparison.Ordinal);
    }

    [Fact]
    public void Cat_reads_a_hinted_file_and_a_resource_in_the_volume_s_encoding()
    {
        var disk = Hinted();

        var (code, output, error) = Run("cat", disk + ":Note");
        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("日本", output, StringComparison.Ordinal);
        Assert.Contains("日本", Run("cat", disk + ":Note:#rsrc:STR :128").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Japanese_System_makes_its_volume_s_files_Japanese()
    {
        var disk = WithJapaneseSystem();

        Assert.Contains("日本", Run("cat", disk + ":Read Me").Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, Run("extract", disk, "-o", Path.Combine(folder, "x")).Code);
        Assert.Contains("日本", Texts(Path.Combine(folder, "x")), StringComparison.Ordinal);
        var (code, _, error) = Run("convert", disk, "-o", Path.Combine(folder, "docs"));
        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("日本", string.Concat(Directory.EnumerateFiles(Path.Combine(folder, "docs"), "*.html", SearchOption.AllDirectories).Select(File.ReadAllText)),
            StringComparison.Ordinal);
    }
    // The write commands write names in --encoding: a new folder's name is stored as its bytes, and listed back so.
    [Fact]
    public void Names_are_written_in_the_chosen_encoding()
    {
        var disk = Path.Combine(folder, "blank.img");
        Assert.Equal(ExitCodes.Success, Run("format", disk, "--size", "800K").Code);
        var edited = Path.Combine(folder, "edited.img");

        var (code, _, error) = Run("mkdir", disk + ":漢字", "-o", edited, "--encoding", "japanese");

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("漢字", Run("ls", edited, "--encoding", "japanese").Output, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, Run("rename", edited + ":漢字", "日本", "--in-place", "--encoding", "japanese").Code);
        Assert.Contains("日本", Run("ls", edited, "--encoding", "japanese").Output, StringComparison.Ordinal);
        Assert.NotEqual(ExitCodes.Success, Run("mkdir", edited + ":한", "--in-place", "--encoding", "japanese").Code);
    }
}
