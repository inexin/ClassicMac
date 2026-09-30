using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

public class HfsTests
{
    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] image)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(HfsReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (HfsReader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics)), diagnostics);
    }

    private static (HfsBuilder Builder, byte[] Image) Sample()
    {
        var builder = new HfsBuilder();
        var games = builder.Folder(HfsBuilder.Root, "Games");
        var realmz = builder.Folder(games, "Realmz");
        builder.File(HfsBuilder.Root, "Read Me", Bytes(700, 3), []);
        builder.File(realmz, "Scenario", Bytes(1500, 5), Bytes(300, 7), type: "scen", creator: "RLMZ");
        return (builder, builder.Build("Test Disk"));
    }

    [Fact]
    public void Files_come_out_with_folders_Finder_info_dates_and_forks()
    {
        var (files, diagnostics) = Read(Sample().Image);

        Assert.Empty(diagnostics);
        Assert.Equal(["Games:Realmz:Scenario", "Read Me"], files.Select(f => f.MacPath).Order());
        var scenario = files.Single(f => f.Name.ToMacRoman() == "Scenario");
        Assert.Equal(FourCC.FromString("scen"), scenario.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("RLMZ"), scenario.FinderInfo.Creator);
        Assert.Equal(FinderFlags.HasBeenInited, scenario.FinderInfo.Flags);
        Assert.Equal(0xFE, scenario.FinderInfo.Extended.Span[0]);
        Assert.Equal(new DateTime(1984, 1, 24), scenario.Created!.Value.ToDateTime());
        Assert.Equal(Bytes(1500, 5), scenario.DataFork.ToArray());
        Assert.Equal(Bytes(300, 7), scenario.ResourceFork.ToArray());
    }

    [Fact]
    public void Forks_in_many_extents_use_the_overflow_file()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragments", Bytes(5000, 11), [], fragments: 7); // 10 blocks in 7 extents
        var (files, diagnostics) = Read(builder.Build("Frag"));

        Assert.Empty(diagnostics);
        Assert.Equal(Bytes(5000, 11), Assert.Single(files).DataFork.ToArray());
    }

    [Fact]
    public void Replacing_a_fork_returns_a_new_image_and_preserves_the_other_fork()
    {
        var (_, image) = Sample();
        var original = image.ToArray();
        var replacement = Bytes(300, 19);

        var result = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Games:Realmz:Scenario", HfsFork.Resource, replacement);

        Assert.Equal(original, image);
        var scenario = Assert.Single(Read(result).Files, f => f.MacPath == "Games:Realmz:Scenario");
        Assert.Equal(replacement, scenario.ResourceFork.ToArray());
        Assert.Equal(Bytes(1500, 5), scenario.DataFork.ToArray());
        Assert.Equal(FourCC.FromString("scen"), scenario.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("RLMZ"), scenario.FinderInfo.Creator);
    }

    [Fact]
    public void Growing_a_fork_into_an_adjacent_free_block_round_trips_the_new_bytes()
    {
        var (_, image) = Sample();
        var replacement = Bytes(2000, 23);
        ushort freeBefore = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(1024 + 0x22));

        var result = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Games:Realmz:Scenario", HfsFork.Data, replacement);

        var scenario = Assert.Single(Read(result).Files, f => f.MacPath == "Games:Realmz:Scenario");
        Assert.Equal(replacement, scenario.DataFork.ToArray());
        Assert.Equal(freeBefore - 1, BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(1024 + 0x22)));
    }

    [Fact]
    public void Shrinking_a_fork_reclaims_blocks_and_round_trips_the_new_bytes()
    {
        var (_, image) = Sample();
        var replacement = Bytes(100, 29);
        ushort freeBefore = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(1024 + 0x22));

        var result = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Games:Realmz:Scenario", HfsFork.Data, replacement);

        var scenario = Assert.Single(Read(result).Files, f => f.MacPath == "Games:Realmz:Scenario");
        Assert.Equal(replacement, scenario.DataFork.ToArray());
        Assert.Equal(freeBefore + 2, BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(1024 + 0x22)));
    }

    [Fact]
    public void Growing_a_fragmented_fork_keeps_its_terminal_overflow_record_after_a_leaf_split()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 4 };
        builder.File(HfsBuilder.Root, "Fragments", Bytes(69 * HfsBuilder.Block, 11), [], fragments: 69);
        var image = builder.Build("Split");
        Assert.Empty(Read(image).Diagnostics);
        var replacement = Bytes(71 * HfsBuilder.Block, 17);

        var result = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Fragments", HfsFork.Data, replacement);

        Assert.Equal(replacement, Assert.Single(Read(result).Files).DataFork.ToArray());
        var treeHeader = (HfsBuilder.FirstAllocationBlock * HfsBuilder.Block) + 14;
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(treeHeader)));
        Assert.Equal((uint)23, BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(treeHeader + 6)));
    }

    [Fact]
    public void A_catalog_node_count_that_exceeds_the_offset_table_is_rejected_as_invalid_data()
    {
        var image = Sample().Image;
        var firstCatalogLeaf = (HfsBuilder.FirstAllocationBlock + 3) * HfsBuilder.Block;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(firstCatalogLeaf + 10), ushort.MaxValue);

        Assert.Throws<InvalidDataException>(() => HfsWriter.ReplaceFork(
            ForkData.FromBytes(image), "Read Me", HfsFork.Data, Bytes(700, 3)));
    }

    [Fact]
    public void An_extents_index_that_disagrees_with_its_leaf_is_rejected()
    {
        var builder = new HfsBuilder { ExtentsTreeNodes = 4 };
        builder.File(HfsBuilder.Root, "Fragments", Bytes(72 * HfsBuilder.Block, 13), [], fragments: 72);
        var image = builder.Build("Indexed");
        Assert.Equal(72 * HfsBuilder.Block, Assert.Single(Read(image).Files).DataFork.Length);
        var rootIndex = (HfsBuilder.FirstAllocationBlock + 3) * HfsBuilder.Block;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rootIndex + 22), 2); // first key now points to the second leaf

        Assert.Throws<InvalidDataException>(() => HfsWriter.ReplaceFork(
            ForkData.FromBytes(image), "Fragments", HfsFork.Resource, Array.Empty<byte>()));
    }

    [Fact]
    public void An_extents_node_map_that_marks_the_header_free_is_rejected()
    {
        var image = Sample().Image;
        var mapRecord = HfsBuilder.FirstAllocationBlock * HfsBuilder.Block + 14 + 106 + 128;
        image[mapRecord] &= 0x7F;

        Assert.Throws<InvalidDataException>(() => HfsWriter.ReplaceFork(
            ForkData.FromBytes(image), "Read Me", HfsFork.Data, Bytes(700, 3)));
    }

    [Fact]
    public void A_leaf_link_back_to_a_read_node_stops_the_walk()
    {
        var image = Sample().Image;
        // Catalog leaf 2 (allocation block 4 + 4, after the extents file's two blocks and the catalog header) links
        // forward to leaf 1 again.
        var leaf2 = (HfsBuilder.FirstAllocationBlock + 4) * HfsBuilder.Block;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(leaf2), 1);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, d => d.Code == "hfs.bad-link");
    }

    [Fact]
    public void An_extent_outside_the_volume_leaves_the_fork_empty()
    {
        var (builder, image) = Sample();
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 74), 60000);

        var (files, diagnostics) = Read(image);

        Assert.Contains(files, f => f.DataFork.Length == 0);
        Assert.Contains(diagnostics, d => d.Code == "hfs.extent-outside");
    }

    [Fact]
    public void A_truncated_image_keeps_what_is_there()
    {
        var image = Sample().Image;
        var (files, diagnostics) = Read(image[..^1000]); // the last block is free; this cuts into the resource fork

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, d => d.Code == "hfs.image-truncated");
    }

    [Fact]
    public void Counts_that_differ_from_the_volume_header_are_noted()
    {
        var image = Sample().Image;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x54), 99);

        var (_, diagnostics) = Read(image);

        Assert.Equal("hfs.counts", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void HFS_Plus_signature_is_claimed_and_invalid_volume_is_rejected()
    {
        var image = new byte[4096];
        Encoding.ASCII.GetBytes("H+").CopyTo(image, 1024);

        Assert.True(HfsReader.Instance.CanRead(ForkData.FromBytes(image)));
        Assert.Throws<InvalidDataException>(() => Read(image));
    }

    [Fact]
    public void Disk_images_unwrap_through_MacBinary_and_Disk_Copy()
    {
        var (_, volume) = Sample();
        var macBinary = MacBinary(2, "Test.image", DiskCopy42("Test Disk", volume), [], type: "dImg", creator: "dCpy");
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Test.image.bin"), DataFork = ForkData.FromBytes(macBinary) },
            "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(diagnostics);
        var disk = Assert.Single(Assert.Single(root.Children).Children);
        Assert.Equal("DiskCopy 4.2", disk.Format);
        Assert.Equal(["HFS volume", "HFS volume"], disk.Children.Select(c => c.Format));
        Assert.Contains(root.Leaves(), l => l.File.MacPath == "Games:Realmz:Scenario");
    }

    [Fact]
    public void Corpus_disk_images_read_cleanly()
    {
        if (!CorpusFolders.Any)
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of disk images to run this.");

        string[] extensions = [".img", ".dsk", ".hfv", ".image", ".dc42"];
        var images = CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !CorpusFolders.IsDamageTest(f))
            .Where(f => { try { using var _ = File.OpenRead(f); return true; } catch (IOException) { return false; } }) // skip images in use
            .ToList();
        var files = 0;
        foreach (var path in images)
        {
            var diagnostics = new List<Diagnostic>();
            var root = ContainerUnwrapper.Default.Unwrap(path, diagnostics: diagnostics);
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error || d.Code == "hfs.counts");
            files += root.Leaves().Count();
        }
        TestContext.Current.SendDiagnosticMessage($"{images.Count} disk images, {files} files.");
    }

    [Fact]
    public void Partitioned_disks_unwrap()
    {
        var (_, volume) = Sample();
        var image = PartitionMap(("Driver", "Apple_Driver43", new byte[512]), ("Test Disk", "Apple_HFS", volume));
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("disk.img"), DataFork = ForkData.FromBytes(image) },
            "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(2, root.Leaves().Count());
        Assert.Equal("Apple partition map", Assert.Single(root.Children).Format);
    }
}
