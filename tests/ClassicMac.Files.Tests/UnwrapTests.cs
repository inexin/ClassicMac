using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Tests;
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
            if (node.Children.Count == 0)
            {
                break;
            }
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
        for (var i = 0; i < 3; i++)
        {
            bytes = MacBinary(2, $"Level{i}", bytes, []);
        }

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

    // A volume: Docs:Note.hqx (BinHex with a bad data CRC), Docs:Wrap.bin (MacBinary holding the same BinHex file) and
    // Plain (no container).
    private static byte[] Volume()
    {
        var hqx = Encoding.ASCII.GetBytes(BinHex("Note", "note"u8.ToArray(), [], corruptDataCrc: true));
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Plain", new byte[70_000], []);
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        builder.File(docs, "Note.hqx", hqx, []);
        builder.File(docs, "Wrap.bin", MacBinary(2, "Inner.hqx", hqx, []), []);
        return builder.Build("Vol");
    }

    // A volume's first file sits within 64 KB of its start, inside the window the BinHex and uuencode readers search;
    // their text follows the boot blocks and the MDB, which are not text, so the volume is read as a volume.
    [Theory]
    [InlineData("Note.hqx")]
    [InlineData("Note.uu")]
    public void A_volume_whose_first_file_is_text_encoded_is_read_as_a_volume(string name)
    {
        var text = name.EndsWith(".hqx", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes(BinHex("Note", "note"u8.ToArray(), []))
            : "From: someone\r\n\r\nbegin 644 Note\r\n#86)C\r\n`\r\nend\r\n"u8.ToArray();
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, name, text, []);
        builder.File(HfsBuilder.Root, "Plain", "p"u8.ToArray(), []);
        var diagnostics = new List<Diagnostic>();

        var root = Unwrap(builder.Build("Vol"), diagnostics);

        Assert.Empty(diagnostics);
        Assert.All(root.Children, c => Assert.Equal("HFS volume", c.Format));
        Assert.Equal([name, "Plain"], root.Children.Select(c => c.File.Name.ToMacRoman()).Order());
        Assert.Equal(name.EndsWith(".hqx", StringComparison.Ordinal) ? "BinHex 4.0" : "uuencode", Assert.Single(Named(root, name).Children).Format);
    }

    private static ContainerNode Named(ContainerNode node, string name) =>
        node.Children.Single(c => c.File.Name.ToMacRoman() == name);

    [Fact]
    public void Unwrap_marks_the_containers_below_its_levels_unread()
    {
        var diagnostics = new List<Diagnostic>();
        var volume = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Vol"), DataFork = ForkData.FromBytes(Volume()) },
            "host file", new ContainerContext(null, diagnostics), levels: 1);

        Assert.Equal(3, volume.Children.Count);
        Assert.Equal("BinHex 4.0", Named(volume, "Note.hqx").UnreadFormat);
        Assert.Equal("MacBinary II", Named(volume, "Wrap.bin").UnreadFormat);
        Assert.Null(Named(volume, "Plain").UnreadFormat);
        Assert.All(volume.Children, c => Assert.Empty(c.Children));
        Assert.Equal(3, volume.Leaves().Count());
        Assert.Empty(diagnostics);
    }

    // Counts its probes; never matches.
    private sealed class CountingReader : IContainerReader
    {
        public int Probes;

        public string FormatName => "Counting";

        public bool CanRead(ForkData input)
        {
            Interlocked.Increment(ref Probes);
            return false;
        }

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) => throw new InvalidDataException();
    }

    // Files at the level limit are probed when their format is asked for, not when the volume is read: a volume of
    // thousands of files read to list one folder probes none of them.
    [Fact]
    public void Files_at_the_level_limit_are_probed_when_asked()
    {
        var counting = new CountingReader();
        var unwrapper = new ContainerUnwrapper([counting]);
        var volume = unwrapper.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Vol"), DataFork = ForkData.FromBytes(Volume()) }, "host file", new ContainerContext(null, []), levels: 1);

        Assert.Equal(1, counting.Probes);                                               // the host file
        Assert.Equal("MacBinary II", Named(volume, "Wrap.bin").UnreadFormat);
        Assert.Equal(2, counting.Probes);
        Assert.Null(Named(volume, "Plain").UnreadFormat);
        Assert.Null(Named(volume, "Plain").UnreadFormat);
        Assert.Equal(3, counting.Probes);                                               // once each
    }

    [Fact]
    public void Expand_reads_unread_containers_as_deep_as_asked()
    {
        var context = new ContainerContext(null, []);
        var shallow = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Vol"), DataFork = ForkData.FromBytes(Volume()) }, "host file", context, levels: 1);

        // MacBinary holds one file, BinHex too: neither uses up the level (see below).
        var wrap = ContainerUnwrapper.Default.Expand(Named(shallow, "Wrap.bin"), context, levels: 1);
        Assert.Null(wrap.UnreadFormat);
        var inner = Assert.Single(wrap.Children);
        Assert.Equal("Inner.hqx", inner.File.Name.ToMacRoman());
        Assert.Null(inner.UnreadFormat);
        Assert.Equal("Note", Assert.Single(inner.Children).File.Name.ToMacRoman());

        var full = ContainerUnwrapper.Default.Expand(shallow, context);
        var whole = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Vol"), DataFork = ForkData.FromBytes(Volume()) }, "host file", new ContainerContext(null, []));
        Assert.Equal(Shape(whole), Shape(full));
        Assert.DoesNotContain(full.Leaves(), l => l.UnreadFormat is not null);
        Assert.Same(Named(shallow, "Plain"), Named(full, "Plain"));
    }

    // A container holding one file (a wrapper, a disk image's disk) does not use up a level: its file is read too,
    // since showing the container means showing that file's contents. Here a disk image on a volume holds a disk with
    // a MacBinary file: expanding the image one level reads the disk, and leaves the MacBinary file unread.
    [Fact]
    public void A_container_holding_one_file_does_not_use_up_a_level()
    {
        var inner = new HfsBuilder();
        inner.File(HfsBuilder.Root, "Wrap.bin", MacBinary(2, "Deep", "x"u8.ToArray(), []), []);
        inner.File(HfsBuilder.Root, "Text", "t"u8.ToArray(), []);
        var outer = new HfsBuilder();
        outer.File(HfsBuilder.Root, "Disk.image", DiskCopy42("Inner", inner.Build("Inner")), []);
        outer.File(HfsBuilder.Root, "Other", "o"u8.ToArray(), []);
        var context = new ContainerContext(null, []);
        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Outer"), DataFork = ForkData.FromBytes(outer.Build("Outer")) }, "host file", context, levels: 1);
        var image = Named(root, "Disk.image");
        Assert.Equal("DiskCopy 4.2", image.UnreadFormat);

        var read = ContainerUnwrapper.Default.Expand(image, context, levels: 1);

        var disk = Assert.Single(read.Children);
        Assert.Null(disk.UnreadFormat);
        Assert.Equal("MacBinary II", Named(disk, "Wrap.bin").UnreadFormat);
        Assert.Null(Named(disk, "Text").UnreadFormat);
    }

    // A container that holds the files it is given, and a format whose probe only passes when four probes run at once.
    private sealed class Holder(IReadOnlyList<MacFile> files) : IContainerReader
    {
        public string FormatName => "holder";

        public bool CanRead(ForkData input) => input.Length == 1 && input.ReadPrefix(1)[0] == 0xEE;

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) => files;
    }

    private sealed class Together(int count) : IContainerReader
    {
        private readonly Barrier barrier = new(count);

        public string FormatName => "together";

        public bool CanRead(ForkData input) =>
            input.Length == 2 && barrier.SignalAndWait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) =>
            [new MacFile { Name = MacString.FromMacRoman("inside"), DataFork = ForkData.FromBytes(new byte[] { 9 }) }];
    }

    private static List<MacFile> TogetherFiles() => [.. Enumerable.Range(0, 4).Select(i =>
        new MacFile { Name = MacString.FromMacRoman($"File {i}"), DataFork = ForkData.FromBytes(new byte[] { 1, 2 }) })];

    private static readonly MacFile HolderInput = new() { Name = MacString.FromMacRoman("in"), DataFork = ForkData.FromBytes(new byte[] { 0xEE }) };

    // The files of a container read through are probed side by side (opening a disk probes every file on it); the tree
    // keeps their order.
    [Fact]
    public void A_containers_files_are_probed_at_the_same_time()
    {
        var unwrapper = new ContainerUnwrapper([new Holder(TogetherFiles()), new Together(4)]);

        var root = unwrapper.Unwrap(HolderInput, "host file", new ContainerContext());

        Assert.Equal(["File 0", "File 1", "File 2", "File 3"], root.Children.Select(c => c.File.Name.ToMacRoman()));
        Assert.All(root.Children, c => Assert.Equal("inside", Assert.Single(c.Children).File.Name.ToMacRoman()));
    }

    // Expanding a container whose files were left at the level limit probes them side by side too.
    [Fact]
    public void Expanding_probes_the_unread_files_at_the_same_time()
    {
        var unwrapper = new ContainerUnwrapper([new Holder(TogetherFiles()), new Together(4)]);
        var shallow = unwrapper.Unwrap(HolderInput, "host file", new ContainerContext(), levels: 1);

        var root = unwrapper.Expand(shallow, new ContainerContext());

        Assert.All(root.Children, c => Assert.Equal("inside", Assert.Single(c.Children).File.Name.ToMacRoman()));
    }

    // A probe that fails stops the unwrap as it would in order: the first such file's error is the one thrown.
    // A reader that accepts every file and then fails as a bug would on damaged data.
    private sealed class Overflowing : IContainerReader
    {
        public string FormatName => "overflowing";

        public bool CanRead(ForkData input) => input.Length == 1;

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) => throw new OverflowException();
    }

    // A reader failing on damaged data with an arithmetic or index exception (a ClassicMac bug) does not end the
    // unwrap: the file becomes a leaf and container.reader-fault says what happened.
    [Fact]
    public void A_reader_fault_is_reported_and_the_file_kept()
    {
        var diagnostics = new List<Diagnostic>();
        var root = new ContainerUnwrapper([new Overflowing()]).Unwrap(
            new MacFile { Name = MacString.FromMacRoman("in"), DataFork = ForkData.FromBytes(new byte[] { 1 }) }, "host file",
            new ContainerContext(null, diagnostics));

        Assert.Empty(root.Children);
        var fault = Assert.Single(diagnostics);
        Assert.Equal(("container.reader-fault", DiagnosticSeverity.Error), (fault.Code, fault.Severity));
        Assert.Contains("OverflowException", fault.Message);
    }

    private sealed class Failing : IContainerReader
    {
        public string FormatName => "failing";

        public bool CanRead(ForkData input) => input.Length == 3
            ? throw new InvalidOperationException($"probe {input.ReadPrefix(3)[2]}")
            : false;

        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context) => [];
    }

    [Fact]
    public void The_first_failing_probe_in_order_is_thrown()
    {
        var files = new[] { 0, 7, 3, 9 }.Select(i =>
            new MacFile { Name = MacString.FromMacRoman($"File {i}"), DataFork = ForkData.FromBytes(i == 0 ? new byte[] { 1 } : new byte[] { 0, 0, (byte)i }) }).ToList();
        var unwrapper = new ContainerUnwrapper([new Holder(files), new Failing()]);

        var error = Assert.Throws<InvalidOperationException>(() => unwrapper.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("in"), DataFork = ForkData.FromBytes(new byte[] { 0xEE }) }, "host file", new ContainerContext()));

        Assert.Equal("probe 7", error.Message);
    }

    // The formats and names of a tree, depth first.
    private static string Shape(ContainerNode node) =>
        $"{node.Format}:{node.File.Name.ToMacRoman()}({string.Join(",", node.Children.Select(Shape))})";

    [Fact]
    public void Expand_leaves_a_read_tree_as_it_is()
    {
        var context = new ContainerContext(null, []);
        var whole = Unwrap(MacBinary(2, "Inner", "data"u8.ToArray(), []), []);
        Assert.Same(whole, ContainerUnwrapper.Default.Expand(whole, context));
    }

    [Fact]
    public void Levels_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("x"), DataFork = ForkData.FromBytes(new byte[] { 1 }) }, "host file", new ContainerContext(), levels: 0));

    // A problem inside a nested file says where it is: the Mac path of the file whose container reported it, through
    // the files around it; problems with the input itself have no location.
    [Fact]
    public void Nested_diagnostics_carry_their_location()
    {
        var diagnostics = new List<Diagnostic>();
        Unwrap(Volume(), diagnostics);

        Assert.NotEmpty(diagnostics);
        Assert.Equal(
            ["Docs:Note.hqx", "Docs:Wrap.bin > Inner.hqx"],
            diagnostics.Select(d => d.Location).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Diagnostics_about_the_input_itself_have_no_location()
    {
        var diagnostics = new List<Diagnostic>();
        Unwrap(Encoding.ASCII.GetBytes(BinHex("Note", "note"u8.ToArray(), [], corruptDataCrc: true)), diagnostics);

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Null(d.Location));
    }

    [Fact]
    public void Expanding_one_node_reports_where_it_is_from_that_node()
    {
        var context = new ContainerContext(null, []);
        var shallow = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Vol"), DataFork = ForkData.FromBytes(Volume()) }, "host file", context, levels: 1);
        var diagnostics = new List<Diagnostic>();

        ContainerUnwrapper.Default.Expand(Named(shallow, "Wrap.bin"), new ContainerContext(null, diagnostics));

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.Equal("Inner.hqx", d.Location));
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
    public void AppleDouble_files_named_dot_rsrc_join_too()
    {
        // As The Unarchiver writes forks on other systems: "name" and "name.rsrc" (AppleDouble).
        var path = Write("System Disk", "disk"u8.ToArray());
        Write("System Disk.rsrc", AppleSingle(AppleDoubleMagic, 0x00020000, "", (9, FinderInfo("dImg", "dCpy")), (2, [7])));
        Write("Plain", "x"u8.ToArray());
        Write("Plain.rsrc", "not AppleDouble"u8.ToArray());

        var host = HostFiles.Read(path);

        Assert.Equal(HostLayout.AppleDouble, host.Layout);
        Assert.Equal("System Disk", host.File.Name.ToMacRoman());
        Assert.Equal((FourCC.FromString("dImg"), (byte)7), (host.File.FinderInfo.Type, host.File.ResourceFork.ToArray()[0]));
        Assert.Equal(HostLayout.Plain, HostFiles.Read(Path.Combine(Path.GetDirectoryName(path)!, "Plain")).Layout);
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
        if (!CorpusFolders.Any)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of Mac files to run this.");
        }

        int files = 0, basilisk = 0, withFinderInfo = 0;
        foreach (var path in CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var folderName = Path.GetFileName(Path.GetDirectoryName(path));
            if (folderName is ".rsrc" or ".finf" || Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))
            {
                continue;
            }
            // The harness's deliberately damaged images (checked by their own tests).
            if (CorpusFolders.IsDamageTest(path))
            {
                continue;
            }

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
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && !CorpusFolders.IsDamageTest(path, d.Location)).ToList();
            Assert.False(errors.Count > 0, $"{path}: {string.Join("; ", errors.Select(d => $"{d.Location}: {d.Message}"))}");
            files++;
            if (root.Format == "Basilisk II folder")
            {
                basilisk++;
            }

            if (root.File.FinderInfo.Type != default)
            {
                withFinderInfo++;
            }
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
