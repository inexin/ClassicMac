using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Tests;

namespace ClassicMac.Cli.Tests;

public class UnpackTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-unpack-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    // An HFS disk holding a file, a file whose name needs escaping, and in a folder an NDIF image of another HFS
    // volume (with the image's resource fork, as a volume stores it).
    private string Disk()
    {
        var inner = new HfsBuilder();
        inner.File(HfsBuilder.Root, "Deep", Bytes(1500, 5), Bytes(200, 7), type: "APPL", creator: "RLMZ");
        inner.File(HfsBuilder.Root, "Other", Bytes(10, 1), []);
        var innerVolume = inner.Build("Inner");
        Array.Resize(ref innerVolume, 200 * 512);
        var (ndifData, ndifResource) = NdifBuilder.Build(innerVolume, "Inner", (200, NdifBuilder.Kind.Raw));

        var outer = new HfsBuilder();
        outer.File(HfsBuilder.Root, "Read Me", Bytes(700, 3), []);
        outer.File(HfsBuilder.Root, "a/b", Bytes(10, 9), []);
        var games = outer.Folder(HfsBuilder.Root, "Games");
        outer.File(games, "Inner.img", ndifData, ndifResource, type: "rohd", creator: "ddsk");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, outer.Build("Outer"));
        return path;
    }

    [Theory]
    [InlineData("appledouble", HostLayout.AppleDouble)]
    [InlineData("basilisk", HostLayout.BasiliskII)]
    public void Disks_unpack_to_folders_that_read_back(string layout, HostLayout expected)
    {
        var target = Path.Combine(folder, "out-" + layout);
        var (code, output, error) = Run("unpack", Disk(), "-o", target, "--layout", layout);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Contains("4 files", output);
        var readMe = HostFiles.Read(Path.Combine(target, "Read Me"));
        Assert.Equal(expected, readMe.Layout);
        Assert.Equal(Bytes(700, 3), readMe.File.DataFork.ToArray());
        // SheepShaver's folders cannot hold a '/', so the Basilisk layout replaces it (and warns).
        var ab = HostFiles.Read(Path.Combine(target, expected == HostLayout.BasiliskII ? "a_b" : "a%2Fb")).File;
        Assert.Equal(expected == HostLayout.BasiliskII ? "a_b" : "a/b", ab.Name.ToMacRoman());
        Assert.Equal(expected == HostLayout.BasiliskII, error.Contains("unpack.name-changed", StringComparison.Ordinal));
        // The NDIF image in Games is replaced by a folder of its volume's files.
        var tree = string.Join(", ", Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(target, f)));
        Assert.True(File.Exists(Path.Combine(target, "Games", "Inner.img", "Deep")), tree);
        var deep = HostFiles.Read(Path.Combine(target, "Games", "Inner.img", "Deep")).File;
        Assert.Equal(Bytes(1500, 5), deep.DataFork.ToArray());
        Assert.Equal(Bytes(200, 7), deep.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("RLMZ"), deep.FinderInfo.Creator);
    }

    [Fact]
    public void Existing_files_are_kept_unless_overwriting()
    {
        var disk = Disk();
        var target = Path.Combine(folder, "out");
        Assert.Equal(ExitCodes.Success, Run("unpack", disk, "-o", target).Code);
        Assert.Equal(ExitCodes.IoError, Run("unpack", disk, "-o", target).Code);
        Assert.Equal(ExitCodes.Success, Run("unpack", disk, "-o", target, "--overwrite").Code);
    }

    // Every disk image in the corpus unpacks, and what was written reads back as the files the image holds: same
    // count, and the same type, creator and fork contents for each.
    [Fact]
    public void Corpus_images_unpack_and_read_back()
    {
        if (!CorpusFolders.Any)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of disk images to run this.");
        }

        string[] extensions = [".img", ".dsk", ".iso", ".hfv", ".image", ".smi"];
        var images = CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.') && !CorpusFolders.IsDamageTest(f))
            .ToList();
        var checkedImages = 0;
        foreach (var image in images)
        {
            var diagnostics = new List<Diagnostic>();
            Input input;
            try
            {
                input = Input.Open(new FileInfo(image), ContainerReadOptions.Default, diagnostics);
            }
            catch (IOException)
            {
                continue; // in use
            }
            if (input.Root.Children.Count == 0)
            {
                continue;
            }

            var target = Path.Combine(folder, $"corpus{checkedImages++}");
            var (code, _, error) = Run("unpack", image, "-o", target, "--layout", "basilisk", "-q");
            Assert.True(code is ExitCodes.Success or ExitCodes.Damaged, $"{image}: {error}");

            static string Key(MacFile f) =>
                $"{f.FinderInfo.Type}/{f.FinderInfo.Creator} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.DataFork.ToArray()))} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.ResourceFork.ToArray()))}";
            var expected = input.Leaves.Select(l => Key(l.Node.File)).Order().ToList();
            var written = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.'))
                .Select(f => Key(HostFiles.Read(f).File)).Order().ToList();
            Assert.True(expected.SequenceEqual(written), $"{image}: {expected.Count} files inside, {written.Count} written back differently.");
        }
        TestContext.Current.SendDiagnosticMessage($"{checkedImages} images unpacked and read back.");
    }

    [Fact]
    public void Encoding_names_the_host_files_in_that_script()
    {
        var disk = new ClassicMac.Files.Tests.HfsBuilder();
        disk.File(ClassicMac.Files.Tests.HfsBuilder.Root, ClassicMac.Core.MacRoman.Decode([0x93, 0xFA, 0x96, 0x7B]), "hi"u8.ToArray(), []);
        var path = Path.Combine(folder, "japanese.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var target = Path.Combine(folder, "out");

        var (code, _, error) = Run("unpack", path, "-o", target, "--encoding", "japanese");

        Assert.True(code == ExitCodes.Success, error);
        Assert.True(File.Exists(Path.Combine(target, "日本")));
    }
}
