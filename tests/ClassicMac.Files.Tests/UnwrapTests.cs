using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class UnwrapTests
{
    private static ContainerNode Unwrap(byte[] bytes, List<Diagnostic> diagnostics, ContainerReadOptions? options = null) =>
        ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("input"), DataFork = ForkData.FromBytes(bytes) },
            "host file", new ContainerContext(options, diagnostics));

    [Fact]
    public void Nested_containers_unwrap_to_a_tree()
    {
        // A MacBinary II file, BinHexed, stored as AppleSingle.
        var macBinary = MacBinary(2, "Inner", "data"u8.ToArray(), [1, 2]);
        var binHex = Encoding.ASCII.GetBytes(BinHex("Inner.bin", macBinary, []));
        var appleSingle = AppleSingle(AppleSingleMagic, 0x00020000, "", (3, "Inner.hqx"u8.ToArray()), (1, binHex));
        var diagnostics = new List<Diagnostic>();

        var root = Unwrap(appleSingle, diagnostics);

        Assert.Empty(diagnostics);
        var formats = new List<string>();
        for (var node = root; ; node = Assert.Single(node.Children))
        {
            formats.Add(node.Format);
            if (node.Children.Count == 0) break;
        }
        Assert.Equal(["host file", "AppleSingle", "BinHex 4.0", "MacBinary II"], formats);
        var leaf = Assert.Single(root.Leaves());
        Assert.Equal("Inner", leaf.File.Name.ToMacRoman());
        Assert.Equal("data"u8.ToArray(), leaf.File.DataFork.ToArray());
        Assert.Equal([1, 2], leaf.File.ResourceFork.ToArray());
    }

    [Fact]
    public void Plain_files_are_their_own_leaf()
    {
        var diagnostics = new List<Diagnostic>();
        var root = Unwrap("just text"u8.ToArray(), diagnostics);
        Assert.Empty(root.Children);
        Assert.Same(root, Assert.Single(root.Leaves()));
    }

    [Fact]
    public void Nesting_stops_at_the_limit()
    {
        var bytes = MacBinary(2, "Deep", "x"u8.ToArray(), []);
        for (var i = 0; i < 3; i++) bytes = MacBinary(2, $"Level{i}", bytes, []);
        var diagnostics = new List<Diagnostic>();

        Unwrap(bytes, diagnostics, ContainerReadOptions.Default with { MaxNestingDepth = 2 });

        Assert.Equal("container.too-deep", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Expansion_stops_at_the_limit()
    {
        var bytes = MacBinary(2, "Big", new byte[2000], []);
        var diagnostics = new List<Diagnostic>();

        Unwrap(bytes, diagnostics, ContainerReadOptions.Default with { MaxExpandedBytesPerInput = 1000 });

        Assert.Equal("container.too-large", Assert.Single(diagnostics).Code);
    }
}

public class HostFilesTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Basilisk_II_folders_give_Finder_info_and_the_resource_fork()
    {
        var path = Write("Icon%0D", "data"u8.ToArray());
        Write(".rsrc/Icon%0D", [1, 2, 3]);
        Write(".finf/Icon%0D", FinderInfo("icon", "MACS", flags: 0x4000));

        var host = HostFiles.Read(path);

        Assert.Equal(HostLayout.BasiliskII, host.Layout);
        Assert.Equal(2, host.Companions.Count);
        Assert.Equal("Icon\r", host.File.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("icon"), host.File.FinderInfo.Type);
        Assert.Equal(FinderFlags.IsInvisible, host.File.FinderInfo.Flags);
        Assert.Equal("data"u8.ToArray(), host.File.DataFork.ToArray());
        Assert.Equal([1, 2, 3], host.File.ResourceFork.ToArray());
    }

    [Fact]
    public void Basilisk_II_names_map_through_Windows_1252()
    {
        // SheepShaver (Windows) hands name bytes over unconverted: host 'Ž' (1252 $8E) is Mac 'é' ($8E).
        var path = Write("Hax 1.0 \u017D%3F", []);
        Write(".finf/Hax 1.0 \u017D%3F", FinderInfo("fold", "MACS"));

        var name = HostFiles.Read(path).File.Name;

        Assert.Equal([.. "Hax 1.0 "u8, 0x8E, (byte)'?'], name.Bytes.ToArray());
    }

    [Fact]
    public void AppleDouble_pairs_join_the_header_file()
    {
        var path = Write("photo", "pixels"u8.ToArray());
        Write("._photo", AppleSingle(AppleDoubleMagic, 0x00020000, "", (9, FinderInfo("PICT", "8BIM")), (2, [4, 5])));

        var host = HostFiles.Read(path);

        Assert.Equal(HostLayout.AppleDouble, host.Layout);
        Assert.Equal("photo", host.File.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("PICT"), host.File.FinderInfo.Type);
        Assert.Equal("pixels"u8.ToArray(), host.File.DataFork.ToArray());
        Assert.Equal([4, 5], host.File.ResourceFork.ToArray());
    }

    [Fact]
    public void Plain_files_have_only_a_data_fork()
    {
        var host = HostFiles.Read(Write("notes.txt", "hi"u8.ToArray()));
        Assert.Equal(HostLayout.Plain, host.Layout);
        Assert.Equal(0, host.File.ResourceFork.Length);
        Assert.Equal("notes.txt", host.File.Name.ToMacRoman());
    }

    [Fact]
    public void Unmappable_and_long_names_are_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(diagnostics: diagnostics);

        Assert.Equal("??", HostFiles.ToMacName("日本", basilisk: false, context).ToMacRoman());
        Assert.Equal(255, HostFiles.ToMacName(new string('a', 300), basilisk: false, context).Length);
        Assert.Equal(["host.name-too-long", "host.name-unmappable"], diagnostics.Select(d => d.Code).Order());
    }

    [Fact]
    public void Corpus_host_files_unwrap()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        if (string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus))
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of Mac files to run this.");

        int files = 0, basilisk = 0, withFinderInfo = 0;
        foreach (var path in Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories))
        {
            var folderName = Path.GetFileName(Path.GetDirectoryName(path));
            if (folderName is ".rsrc" or ".finf" || Path.GetFileName(path).StartsWith("._")) continue;
            // The harness's deliberately damaged NDIF images (checked by NdifTests against OS 9's results).
            if (path.Contains($"{Path.DirectorySeparatorChar}ndiftest{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var diagnostics = new List<Diagnostic>();
            ContainerNode root;
            try
            {
                root = ContainerUnwrapper.Default.Unwrap(path, diagnostics: diagnostics);
            }
            catch (IOException)
            {
                continue; // in use (an emulator holding a disk image)
            }
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            files++;
            if (root.Format == "Basilisk II folder") basilisk++;
            if (root.File.FinderInfo.Type != default) withFinderInfo++;
        }
        TestContext.Current.SendDiagnosticMessage(
            $"{files} host files; {basilisk} in Basilisk II folders; {withFinderInfo} with a file type.");
    }

    // A format split across files: "SEG1" in the first file's data fork, the rest in a sibling called "part 2".
    private sealed class SegmentReader : IContainerReader
    {
        public string FormatName => "segmented";

        public bool CanRead(ForkData input) => input.ReadPrefix(4).AsSpan().SequenceEqual("SEG1"u8);

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var rest = context.Siblings?.Invoke().FirstOrDefault(f => f.Name.ToMacRoman() == "part 2");
            return [new MacFile { Name = MacString.FromMacRoman("joined"), DataFork = ForkData.FromBytes(input.ToArray().Concat(rest?.DataFork.ToArray() ?? []).ToArray()) }];
        }
    }

    [Fact]
    public void Readers_find_sibling_files_on_the_host_and_in_containers()
    {
        var unwrapper = new ContainerUnwrapper([new SegmentReader()]);
        var folder = Directory.CreateTempSubdirectory("classicmac-siblings-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "part 1"), "SEG1abc");
            File.WriteAllText(Path.Combine(folder, "part 2"), "def");
            var host = unwrapper.Unwrap(Path.Combine(folder, "part 1"));
            Assert.Equal("SEG1abcdef"u8.ToArray(), Assert.Single(host.Leaves()).File.DataFork.ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // The same two files side by side in an HFS volume.
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "part 1", "SEG1abc"u8.ToArray(), []);
        builder.File(HfsBuilder.Root, "part 2", "def"u8.ToArray(), []);
        var root = unwrapper.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("disk"), DataFork = ForkData.FromBytes(builder.Build("Disk")) },
            "host file", new ContainerContext());
        Assert.Contains(root.Leaves(), l => l.File.DataFork.ToArray().AsSpan().SequenceEqual("SEG1abcdef"u8));
    }
}
