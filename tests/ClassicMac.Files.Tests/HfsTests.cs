using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;
using static ClassicMac.Files.Tests.Fixtures;

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
    public void The_volume_reports_its_dates_from_the_mdb()
    {
        var (_, image) = Sample();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x02), 3_000_000_000);   // drCrDate
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x06), 3_000_000_100);   // drLsMod
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x40), 3_000_000_200);   // drVolBkUp
        var info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal(("HFS", new MacDate(3_000_000_000), new MacDate(3_000_000_100), new MacDate(3_000_000_200)),
            (info.Format, info.Created, info.Modified, info.BackedUp));
        Assert.False(info.UtcAfterCreation);

        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x40), 0);                // never backed up
        Assert.Null(HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!.BackedUp);
        Assert.Null(HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(new byte[4096])));  // not a volume
    }

    [Fact]
    public void The_volume_reports_its_name_space_counts_and_locks_from_the_mdb()
    {
        var (_, image) = Sample();
        var mdb = image.AsSpan(1024);
        var info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;

        Assert.Equal("Test Disk", info.Name);                                              // drVN
        Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(mdb[0x14..]), info.BlockSize);   // drAlBlkSiz
        Assert.Equal(BinaryPrimitives.ReadUInt16BigEndian(mdb[0x12..]), info.TotalBlocks); // drNmAlBlks
        Assert.Equal(BinaryPrimitives.ReadUInt16BigEndian(mdb[0x22..]), info.FreeBlocks);  // drFreeBks
        Assert.Equal(info.FreeBlocks * info.BlockSize, info.FreeBytes);
        Assert.Equal(info.TotalBlocks * info.BlockSize, info.TotalBytes);
        Assert.Equal((2L, 2L), (info.Files, info.Folders));                                // drFilCnt, drDirCnt
        Assert.Equal((false, false), (info.SoftwareLocked, info.HardwareLocked));

        image[1024 + 0x0A] |= 0x80;                                                        // drAtrb bit 15
        image[1024 + 0x0B] |= 0x80;                                                        // drAtrb bit 7
        info = HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(image))!;
        Assert.Equal((true, true), (info.SoftwareLocked, info.HardwareLocked));
    }

    [Fact]
    public void Unwrapping_a_volume_keeps_its_dates_on_the_node_that_holds_it()
    {
        var (_, image) = Sample();
        var host = new MacFile { Name = MacString.FromMacRoman("Disk.img"), DataFork = ForkData.FromBytes(image) };
        var root = ContainerUnwrapper.Default.Unwrap(host, "host file", new ContainerContext());
        Assert.Equal(HfsReader.Instance.ReadVolumeInfo(host.DataFork), root.Volume);
        Assert.NotNull(root.Volume);
        Assert.All(root.Children, c => Assert.Null(c.Volume));                              // files on it are not volumes
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
    public void A_file_whose_filFlags_bit_0_is_set_is_locked()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Locked", [], [], locked: true);
        builder.File(HfsBuilder.Root, "Open", [], [], thread: true);

        var (files, _) = Read(builder.Build("Disk"));

        Assert.True(files.Single(f => f.Name.ToMacRoman() == "Locked").IsLocked);
        Assert.False(files.Single(f => f.Name.ToMacRoman() == "Open").IsLocked);
    }

    [Fact]
    public void Folders_come_out_with_their_window_and_icon_info()
    {
        var info = FolderFinderInfo.Read(FolderFinderInfoTests.Sample);
        var rootInfo = new FolderFinderInfo { WindowBounds = new MacRect(50, 60, 250, 460), ScrollPosition = new MacPoint(0, 4) };
        var builder = new HfsBuilder { RootInfo = rootInfo };
        var games = builder.Folder(HfsBuilder.Root, "Games", info);
        builder.Folder(games, "Realmz");
        builder.File(games, "Read Me", [], []);
        var image = builder.Build("Test Disk");
        var diagnostics = new List<Diagnostic>();

        var folders = HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal(["", "Games", "Games:Realmz"], folders.Select(f => f.MacPath).Order());
        var root = folders.Single(f => f.IsRoot);
        Assert.Equal("Test Disk", root.Name.ToMacRoman());
        Assert.Equal(rootInfo, root.FinderInfo);
        Assert.All(folders.Where(f => !f.IsRoot), f => Assert.Null(f.FreeBytes));
        var read = folders.Single(f => f.MacPath == "Games");
        Assert.False(read.IsRoot);
        Assert.Empty(read.FolderPath);
        Assert.Equal(info, read.FinderInfo);
        Assert.Equal(new DateTime(1984, 1, 24), read.Created!.Value.ToDateTime());
        Assert.Equal(new DateTime(1984, 1, 24, 0, 1, 0), read.Modified!.Value.ToDateTime());
        var inner = folders.Single(f => f.MacPath == "Games:Realmz");
        Assert.Equal(FolderFinderInfo.Empty, inner.FinderInfo);
        Assert.Null(inner.Created);
        // Read is unchanged: files only.
        Assert.Equal(["Games:Read Me"], HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()).Select(f => f.MacPath));
    }

    // The root carries the volume's free space as the MDB records it: drFreeBks (+$22) × drAlBlkSiz (+$14).
    [Fact]
    public void The_root_folder_carries_the_volumes_free_space()
    {
        var image = new HfsBuilder().Build("Disk");
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x22), 7);
        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 0x14));

        var root = HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext()).Single(f => f.IsRoot);

        Assert.Equal(7L * blockSize, root.FreeBytes);
    }

    [Fact]
    public void A_folder_whose_parent_is_missing_is_reported_and_its_path_starts_there()
    {
        var builder = new HfsBuilder();
        builder.Folder(999, "Lost");
        var diagnostics = new List<Diagnostic>();

        var folders = HfsReader.Instance.ReadFolders(ForkData.FromBytes(builder.Build("Disk")), new ContainerContext(diagnostics: diagnostics));

        Assert.Equal("Lost", folders.Single(f => !f.IsRoot).MacPath);
        Assert.Contains(diagnostics, d => d.Code == "hfs.orphan");
    }

    [Fact]
    public void ReadFolders_rejects_what_is_not_a_volume() =>
        Assert.Throws<InvalidDataException>(() => HfsReader.Instance.ReadFolders(ForkData.FromBytes(new byte[2048]), new ContainerContext()));

    [Fact]
    public void Files_keep_the_Finder_info_the_builder_writes()
    {
        var builder = new HfsBuilder();
        var finder = new FinderInfo
        {
            Type = FourCC.FromString("APPL"),
            Creator = FourCC.FromString("ABCD"),
            Flags = FinderFlags.HasCustomIcon,
            Location = new MacPoint(12, 34),
        };
        builder.File(HfsBuilder.Root, "App", [], [], info: finder);

        var (files, _) = Read(builder.Build("Disk"));

        Assert.Equal(finder.ToArray(), Assert.Single(files).FinderInfo.ToArray());
    }

    [Fact]
    public void Forks_in_many_extents_use_the_overflow_file()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragments", Bytes(5000, 11), [], fragments: 7); // 10 blocks in 5 extents
        var (files, diagnostics) = Read(builder.Build("Frag"));

        Assert.Empty(diagnostics);
        Assert.Equal(Bytes(5000, 11), Assert.Single(files).DataFork.ToArray());
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 7)]
    public void A_fork_overflow_key_must_name_the_first_block_after_the_previous_extents(
        int overflowRecordIndex, ushort invalidStartBlock)
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(9 * HfsBuilder.Block, 11);
        builder.File(HfsBuilder.Root, "Fragments", expected, [], fragments: 9);
        byte[] image = builder.Build("Frag");
        SetOverflowRecordStartBlock(image, overflowRecordIndex, invalidStartBlock);

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected, Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.overflow-start" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_classic_hfs_btree_with_a_non512_byte_node_size_warns_but_remains_readable(bool catalogTree)
    {
        byte[] image = Sample().Image;
        SetHfsTreeNodeSize(image, catalogTree, nodeSize: 1024);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 0xFF)]
    [InlineData(9, 1)]
    [InlineData(11, 2)]
    [InlineData(12, 1)]
    public void A_classic_hfs_btree_with_an_invalid_header_node_descriptor_warns_but_remains_readable(
        int descriptorOffset, int value)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        image[treeOffset + descriptorOffset] = checked((byte)value);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_classic_hfs_btree_with_a_node_count_that_disagrees_with_its_fork_warns_but_remains_readable(
        bool catalogTree)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 22), 1);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(14)]
    public void A_classic_hfs_btree_with_inconsistent_leaf_totals_warns_but_remains_readable(int headerOffset)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + headerOffset), uint.MaxValue);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_classic_hfs_btree_with_an_invalid_root_or_depth_warns_but_remains_readable(bool invalidRoot)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        if (invalidRoot)
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 2), uint.MaxValue);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(treeOffset + 14), 0);
        }

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false, 37)]
    [InlineData(true, 7)]
    public void A_classic_hfs_btree_with_an_invalid_maximum_key_length_warns_but_remains_readable(
        bool catalogTree, ushort expectedKeyLength)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree);
        ushort invalidKeyLength = checked((ushort)(expectedKeyLength - 1));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(treeOffset + 14 + 20), invalidKeyLength);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 122)]
    [InlineData(2, 250)]
    public void A_classic_hfs_btree_with_invalid_header_record_boundaries_warns_but_remains_readable(
        int recordIndex, ushort invalidStart)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        int offsetTableEntry = HfsBuilder.Block - 2 * (recordIndex + 1);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(treeOffset + offsetTableEntry), invalidStart);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_fork_ending_inside_a_node_warns_but_remains_readable()
    {
        byte[] image = Sample().Image;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(1024 + 0x92), 4 * HfsBuilder.Block - 1);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-header" &&
            diagnostic.Message.Contains("partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_classic_hfs_btree_with_a_broken_leaf_backward_link_warns_but_remains_readable()
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        int secondLeafOffset = treeOffset + 2 * HfsBuilder.Block;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(secondLeafOffset + 4), uint.MaxValue);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-link" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_leaf_with_an_invalid_height_stops_the_leaf_chain()
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        image[treeOffset + 2 * HfsBuilder.Block + 9] = 2;

        var (files, diagnostics) = Read(image);

        Assert.NotEmpty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.not-leaf" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void A_classic_hfs_catalog_with_out_of_order_keys_warns_but_still_reads_files()
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        int firstLeafOffset = treeOffset + HfsBuilder.Block;
        int secondRecordStart = BinaryPrimitives.ReadUInt16BigEndian(
            image.AsSpan(firstLeafOffset + HfsBuilder.Block - 8));
        image[firstLeafOffset + secondRecordStart + 7] = (byte)'A';
        int firstRecordStart = BinaryPrimitives.ReadUInt16BigEndian(
            image.AsSpan(firstLeafOffset + HfsBuilder.Block - 6));
        byte[] firstKey = ReadCatalogKey(image, firstLeafOffset, firstRecordStart);
        byte[] secondKey = ReadCatalogKey(image, firstLeafOffset, secondRecordStart);
        Assert.Equal("Games", Encoding.ASCII.GetString(firstKey, 7, firstKey[6]));
        Assert.Equal("Aead Me", Encoding.ASCII.GetString(secondKey, 7, secondKey[6]));
        Assert.True(HfsCatalogKeys.CompareCatalogKeys(firstKey, secondKey) > 0);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.key-order" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(6, 32)]
    public void A_classic_hfs_catalog_with_a_malformed_key_reports_it_and_keeps_other_files(int keyOffset, byte value)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        int firstLeafOffset = treeOffset + HfsBuilder.Block;
        int fileRecordStart = BinaryPrimitives.ReadUInt16BigEndian(
            image.AsSpan(firstLeafOffset + HfsBuilder.Block - 8));
        image[firstLeafOffset + fileRecordStart + keyOffset] = value;

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-record" &&
            diagnostic.Severity == DiagnosticSeverity.Warning &&
            diagnostic.Message.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_classic_hfs_catalog_with_mac_os_key_lengths_that_leave_out_the_alignment_byte_reads_cleanly()
    {
        // Mac OS writes ckrKeyLen = 6 + n (the Finder's "Desktop DB" has 16, threads 6); hfsutils counts the pad byte.
        var builder = new HfsBuilder { UncountedKeyPadding = true };
        var games = builder.Folder(HfsBuilder.Root, "Games");
        builder.File(HfsBuilder.Root, "ReadMe", Bytes(700, 3), [], thread: true);
        builder.File(games, "Desktop DB", Bytes(300, 5), []);
        byte[] image = builder.Build("Mac OS Keys");
        int leaf = GetHfsTreeOffset(image, catalogTree: true) + HfsBuilder.Block;
        int firstRecord = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(leaf + HfsBuilder.Block - 2));
        Assert.Equal(image[leaf + firstRecord + 6] + 6, image[leaf + firstRecord]);

        var (files, diagnostics) = Read(image);

        Assert.Empty(diagnostics);
        Assert.Equal(["Games:Desktop DB", "ReadMe"], files.Select(f => f.MacPath).Order());
    }

    [Fact]
    public void A_classic_hfs_catalog_with_duplicate_file_ids_reports_them_but_keeps_both_files()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Alpha", [0xA1], []);
        builder.File(HfsBuilder.Root, "Beta", [0xB2], []);
        byte[] image = builder.Build("Duplicate IDs");
        int catalogTree = GetHfsTreeOffset(image, catalogTree: true);
        int secondLeaf = catalogTree + 2 * HfsBuilder.Block;
        int[] recordData = Enumerable.Range(0, 2).Select(index =>
        {
            int start = BinaryPrimitives.ReadUInt16BigEndian(
                image.AsSpan(secondLeaf + HfsBuilder.Block - 2 * (index + 1)));
            int keyEnd = start + 1 + image[secondLeaf + start];
            int dataStart = (keyEnd + 1) & ~1;
            Assert.Equal((byte)2, image[secondLeaf + dataStart]);
            return dataStart;
        }).ToArray();
        uint firstId = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(secondLeaf + recordData[0] + 20));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(secondLeaf + recordData[1] + 20), firstId);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.duplicate-id" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_catalog_with_duplicate_folder_ids_reports_them_but_keeps_files()
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        var folders = GetCatalogRecordDataOffsets(image, treeOffset, recordType: 1);
        Assert.Equal(3, folders.Count);
        uint firstFolderId = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(folders[1] + 6));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(folders[2] + 6), firstFolderId);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.duplicate-id" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_classic_hfs_catalog_with_a_reserved_object_id_reports_it_but_keeps_files(bool folderRecord)
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        int dataOffset = folderRecord
            ? GetCatalogRecordDataOffsets(image, treeOffset, recordType: 1)[2]
            : GetCatalogRecordDataOffsets(image, treeOffset, recordType: 2)[0];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dataOffset + (folderRecord ? 6 : 20)), 15);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.reserved-id" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_extents_tree_with_duplicate_keys_warns_but_still_reads_the_fork()
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(9 * HfsBuilder.Block, 11);
        builder.File(HfsBuilder.Root, "Fragments", expected, [], fragments: 9);
        byte[] image = builder.Build("Duplicate extent keys");
        SetOverflowRecordStartBlock(image, recordIndex: 1, startBlock: 3);

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected, Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.key-order" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_whose_node_map_marks_the_header_free_warns_but_remains_readable()
    {
        byte[] image = Sample().Image;
        int mapOffset = GetHfsTreeOffset(image, catalogTree: true) + 14 + 106 + 128;
        image[mapOffset] &= 0x7F;

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_whose_node_map_marks_a_leaf_free_warns_but_remains_readable()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragments", Bytes(9 * HfsBuilder.Block, 11), [], fragments: 9);
        byte[] image = builder.Build("Leaf map");
        int treeOffset = GetHfsTreeOffset(image, catalogTree: false);
        int mapOffset = treeOffset + 14 + 106 + 128;
        image[mapOffset] &= 0xBF; // Node 1 is the first extents-overflow leaf.
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 26), 3);

        var (files, diagnostics) = Read(image);

        Assert.Equal(9 * HfsBuilder.Block, Assert.Single(files).DataFork.Length);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_whose_free_node_count_disagrees_with_its_map_warns_but_remains_readable()
    {
        byte[] image = Sample().Image;
        int treeOffset = GetHfsTreeOffset(image, catalogTree: true);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 26), 99);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_node_map_can_continue_in_a_linked_map_node()
    {
        var (image, _, _) = ClassicHfsMapContinuationFixture();

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_classic_hfs_btree_with_a_truncated_map_node_chain_warns_but_remains_readable()
    {
        var (image, treeOffset, _) = ClassicHfsMapContinuationFixture();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset), 0);

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_classic_hfs_btree_with_an_invalid_map_node_chain_warns_but_remains_readable(bool cycle)
    {
        var (image, treeOffset, mapNodeOffset) = ClassicHfsMapContinuationFixture();
        if (cycle)
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(mapNodeOffset), 2048);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset), 2049);
        }

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_with_a_map_node_marked_free_warns_but_remains_readable()
    {
        var (image, treeOffset, mapNodeOffset) = ClassicHfsMapContinuationFixture();
        image[mapNodeOffset + 14] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 26), 2048);

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_classic_hfs_btree_with_a_malformed_map_node_warns_but_remains_readable()
    {
        var (image, _, mapNodeOffset) = ClassicHfsMapContinuationFixture();
        image[mapNodeOffset + 8] = 1;

        var (files, diagnostics) = Read(image);

        Assert.Single(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bad-btree-map" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_malformed_extents_overflow_key_is_reported()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragments", Bytes(9 * HfsBuilder.Block, 11), [], fragments: 9);
        byte[] image = builder.Build("Frag");
        SetOverflowRecordKeyLength(image, recordIndex: 0, keyLength: 6);

        var (_, diagnostics) = Read(image);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.overflow-record" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Extra_bytes_in_an_extents_overflow_record_are_reported_and_the_defined_extent_is_used()
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(5000, 11);
        builder.File(HfsBuilder.Root, "Fragments", expected, [], fragments: 7);
        byte[] image = builder.Build("Frag");
        ExtendOverflowRecordData(image, recordIndex: 0);

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected, Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.overflow-record" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
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
        var (files, diagnostics) = Read(image[..^2024]); // Skip the alternate-MDB sector, then cut into the resource fork.

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
    public void A_missing_alternate_mdb_is_reported_without_preventing_reads()
    {
        var image = Sample().Image;
        image.AsSpan(image.Length - 1024, 2).Clear();

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.alternate-mdb" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Free_block_count_mismatch_is_reported_without_preventing_reads()
    {
        var image = Sample().Image;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x22), 0);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.free-blocks" &&
            diagnostic.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void Free_block_count_ignores_bitmap_padding_after_the_last_allocation_block()
    {
        var image = Sample().Image;
        int allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(1024 + 0x12));
        int paddingBit = allocationBlocks & 7;
        Assert.NotEqual(0, paddingBit);
        image[3 * HfsBuilder.Block + allocationBlocks / 8] |= (byte)(0x80 >> paddingBit);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "hfs.free-blocks");
    }

    [Fact]
    public void A_catalog_extent_marked_free_is_reported_without_preventing_reads()
    {
        var image = Sample().Image;
        int catalogStartBlock = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(1024 + 0x96));
        image[3 * HfsBuilder.Block + catalogStartBlock / 8] &= (byte)~(0x80 >> (catalogStartBlock & 7));

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void An_extents_overflow_extent_marked_free_is_reported_without_preventing_reads()
    {
        var image = Sample().Image;
        const int extentsOverflowLeafBlock = 1;
        image[3 * HfsBuilder.Block + extentsOverflowLeafBlock / 8] &=
            unchecked((byte)~(0x80 >> (extentsOverflowLeafBlock & 7)));

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData(74)]
    [InlineData(86)]
    public void A_file_fork_extent_marked_free_is_reported_without_losing_fork_data(int extentOffset)
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Forks", Bytes(700, 3), Bytes(600, 7));
        byte[] image = builder.Build("Test Disk");
        int startBlock = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(builder.FirstFileRecordOffset + extentOffset));
        image[3 * HfsBuilder.Block + startBlock / 8] &= (byte)~(0x80 >> (startBlock & 7));

        var (files, diagnostics) = Read(image);

        var file = Assert.Single(files);
        Assert.Equal(Bytes(700, 3), file.DataFork.ToArray());
        Assert.Equal(Bytes(600, 7), file.ResourceFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_fork_overflow_extent_marked_free_is_reported_without_losing_fork_data()
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(5000, 11);
        builder.File(HfsBuilder.Root, "Fragments", expected, [], fragments: 7);
        byte[] image = builder.Build("Frag");

        // The fixture's first three extents start at allocation blocks 6, 9 and 12. Its overflow record
        // supplies the next extents at 15 and 18; clear block 15's bitmap bit.
        const int overflowExtentStartBlock = 15;
        image[3 * HfsBuilder.Block + overflowExtentStartBlock / 8] &=
            unchecked((byte)~(0x80 >> (overflowExtentStartBlock & 7)));

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected, Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_fork_extentAfterLogicalEofStillMustBeAllocated()
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(700, 3);
        builder.File(HfsBuilder.Root, "Forks", expected, [], fragments: 2);
        byte[] image = builder.Build("Test Disk");
        int secondExtent = BinaryPrimitives.ReadUInt16BigEndian(
            image.AsSpan(builder.FirstFileRecordOffset + 78));
        Assert.NotEqual(0, secondExtent);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 26), 100);
        image[3 * HfsBuilder.Block + secondExtent / 8] &= unchecked((byte)~(0x80 >> (secondExtent & 7)));

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected.AsSpan(0, 100).ToArray(), Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_fork_overflow_extentAfterLogicalEofStillMustBeAllocated()
    {
        var builder = new HfsBuilder();
        byte[] expected = Bytes(5000, 11);
        builder.File(HfsBuilder.Root, "Fragments", expected, [], fragments: 7);
        byte[] image = builder.Build("Frag");

        const int overflowExtentStartBlock = 15;
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 26), 100);
        image[3 * HfsBuilder.Block + overflowExtentStartBlock / 8] &=
            unchecked((byte)~(0x80 >> (overflowExtentStartBlock & 7)));

        var (files, diagnostics) = Read(image);

        Assert.Equal(expected.AsSpan(0, 100).ToArray(), Assert.Single(files).DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_zeroLengthForkWithADeclaredExtentStillRequiresAllocatedBlocks()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Empty", [], []);
        builder.File(HfsBuilder.Root, "Payload", [0x42], []);
        byte[] image = builder.Build("Test Disk");
        const int freeAllocationBlock = 7;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 74),
            freeAllocationBlock);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 76), 1);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(builder.FirstFileRecordOffset + 30), HfsBuilder.Block);

        var (files, diagnostics) = Read(image);

        Assert.Equal(0, Assert.Single(files, file => file.Name.ToMacRoman() == "Empty").DataFork.Length);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.extent-unallocated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void A_volume_bitmap_outside_the_image_is_reported_without_preventing_reads()
    {
        var image = Sample().Image;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(1024 + 0x0E), ushort.MaxValue);

        var (files, diagnostics) = Read(image);

        Assert.Equal(2, files.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "hfs.bitmap-truncated" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
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
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of disk images to run this.");
        }

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
            Assert.DoesNotContain(diagnostics, d => (d.Severity == DiagnosticSeverity.Error || d.Code == "hfs.counts") && !CorpusFolders.IsDamageTest(path, d.Location));
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

    private static void SetOverflowRecordStartBlock(byte[] image, int recordIndex, ushort startBlock)
    {
        int leafOffset = (HfsBuilder.FirstAllocationBlock + 1) * HfsBuilder.Block;
        Span<byte> leaf = image.AsSpan(leafOffset, HfsBuilder.Block);
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
        Assert.InRange(recordIndex, 0, recordCount - 1);
        int recordStart = BinaryPrimitives.ReadUInt16BigEndian(
            leaf[(HfsBuilder.Block - 2 * (recordIndex + 1))..]);
        Assert.Equal((byte)7, leaf[recordStart]);
        BinaryPrimitives.WriteUInt16BigEndian(leaf[(recordStart + 6)..], startBlock);
    }

    private static (byte[] Image, int TreeOffset, int MapNodeOffset) ClassicHfsMapContinuationFixture()
    {
        const int mapNodeIndex = 2048;
        var builder = new HfsBuilder { ExtentsTreeNodes = mapNodeIndex + 1 };
        builder.File(HfsBuilder.Root, "Empty", [], []);
        byte[] image = builder.Build("Map nodes");
        int treeOffset = GetHfsTreeOffset(image, catalogTree: false);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset), mapNodeIndex);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(treeOffset + 14 + 26), 2047);
        int mapNodeOffset = treeOffset + mapNodeIndex * HfsBuilder.Block;
        image[mapNodeOffset + 8] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mapNodeOffset + 10), 1);
        image[mapNodeOffset + 14] = 0x80; // The map node itself is allocated.
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mapNodeOffset + HfsBuilder.Block - 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mapNodeOffset + HfsBuilder.Block - 4), HfsBuilder.Block - 6);
        return (image, treeOffset, mapNodeOffset);
    }

    private static void SetOverflowRecordKeyLength(byte[] image, int recordIndex, byte keyLength)
    {
        int leafOffset = (HfsBuilder.FirstAllocationBlock + 1) * HfsBuilder.Block;
        Span<byte> leaf = image.AsSpan(leafOffset, HfsBuilder.Block);
        int recordStart = BinaryPrimitives.ReadUInt16BigEndian(
            leaf[(HfsBuilder.Block - 2 * (recordIndex + 1))..]);
        leaf[recordStart] = keyLength;
    }

    private static void ExtendOverflowRecordData(byte[] image, int recordIndex)
    {
        int leafOffset = (HfsBuilder.FirstAllocationBlock + 1) * HfsBuilder.Block;
        Span<byte> leaf = image.AsSpan(leafOffset, HfsBuilder.Block);
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
        Assert.Equal(recordIndex, recordCount - 1);
        int endOffset = HfsBuilder.Block - 2 * (recordCount + 1);
        ushort end = BinaryPrimitives.ReadUInt16BigEndian(leaf[endOffset..]);
        leaf[end] = 0xCC;
        BinaryPrimitives.WriteUInt16BigEndian(leaf[endOffset..], checked((ushort)(end + 1)));
    }

    private static void SetHfsTreeNodeSize(byte[] image, bool catalogTree, ushort nodeSize)
    {
        int treeOffset = GetHfsTreeOffset(image, catalogTree);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(treeOffset + 14 + 18), nodeSize);
    }

    private static int GetHfsTreeOffset(byte[] image, bool catalogTree)
    {
        int mdbOffset = 1024;
        int extentRecordOffset = catalogTree ? 0x96 : 0x86;
        int allocationStart = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdbOffset + 0x1C));
        int treeStart = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdbOffset + extentRecordOffset));
        return checked((allocationStart + treeStart) * HfsBuilder.Block);
    }

    private static byte[] ReadCatalogKey(byte[] image, int leafOffset, int recordOffset)
    {
        int length = image[leafOffset + recordOffset] + 1;
        return image.AsSpan(leafOffset + recordOffset, length).ToArray();
    }

    private static List<int> GetCatalogRecordDataOffsets(byte[] image, int treeOffset, byte recordType)
    {
        var offsets = new List<int>();
        for (int nodeIndex = 1; nodeIndex <= 2; nodeIndex++)
        {
            int nodeOffset = treeOffset + nodeIndex * HfsBuilder.Block;
            int records = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(nodeOffset + 10));
            for (int index = 0; index < records; index++)
            {
                int start = BinaryPrimitives.ReadUInt16BigEndian(
                    image.AsSpan(nodeOffset + HfsBuilder.Block - 2 * (index + 1)));
                int end = BinaryPrimitives.ReadUInt16BigEndian(
                    image.AsSpan(nodeOffset + HfsBuilder.Block - 2 * (index + 2)));
                int dataStart = start + 1 + image[nodeOffset + start];
                dataStart = (dataStart + 1) & ~1;
                if (dataStart < end && image[nodeOffset + dataStart] == recordType)
                {
                    offsets.Add(nodeOffset + dataStart);
                }
            }
        }
        return offsets;
    }
}
