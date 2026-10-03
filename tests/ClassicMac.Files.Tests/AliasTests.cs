using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Alias records (docs/formats/resources/aliases.md) and resolving them on open volumes: by the target's ID, by the
// stored full path, by name in the parent folder.
public class AliasTests
{
    private static FourCC F(string s) => FourCC.FromString(s);

    [Fact]
    public void An_alias_record_reads_its_fields_and_tagged_data()
    {
        var bytes = AliasBuilder.Alias("Mac OS 9", 0x586, "lnFmSet.htm", 0x588, "TEXT", "hbwr", path: "Mac OS 9:Help:pgs:lnFmSet.htm",
            parentName: "pgs", folderIds: [0x586, 0x582, 0x2BD], extras: [(4, MacRoman.Encode("Server")), (42, [1, 2, 3])]);
        var alias = AliasRecord.Read(bytes, out var complete);
        Assert.True(complete);
        Assert.Equal((2, AliasKind.File), (alias.Version, alias.Kind));
        Assert.Equal(bytes.Length, alias.Size);
        Assert.Equal("Mac OS 9", alias.VolumeName.ToMacRoman());
        Assert.Equal(new MacDate(AliasBuilder.VolumeCreated), alias.VolumeCreated);
        Assert.Equal("BD", alias.VolumeSignatureText);
        Assert.Equal(0x586u, alias.ParentId);
        Assert.Equal("lnFmSet.htm", alias.Name.ToMacRoman());
        Assert.Equal(0x588u, alias.TargetId);
        Assert.Equal(new MacDate(AliasBuilder.TargetCreated), alias.TargetCreated);
        Assert.Equal((F("TEXT"), F("hbwr")), (alias.Type, alias.Creator));
        Assert.Equal((1, 2), (alias.LevelsFrom, alias.LevelsTo));
        Assert.Equal("pgs", alias.ParentName?.ToMacRoman());
        Assert.Equal([0x586u, 0x582u, 0x2BDu], alias.FolderIds);
        Assert.Equal("Mac OS 9:Help:pgs:lnFmSet.htm", alias.FullPath?.ToMacRoman());
        Assert.Equal("Server", alias.Extra(AliasRecord.ServerNameTag)?.ToMacRoman());
        Assert.Equal([0, 1, 2, 4, 42], alias.Extras.Select(e => (int)e.Tag));
        Assert.Equal("server name", alias.Extras[3].Name);
        Assert.Null(alias.Extras[4].Name);
        Assert.Equal("Mac OS 9: Help: pgs: lnFmSet.htm", alias.TargetPath);       // Get Info's "Original:"
    }

    [Fact]
    public void The_target_path_takes_as_many_folders_from_the_full_path_as_the_folder_ids_count()
    {
        var alias = AliasRecord.Read(AliasBuilder.Alias("Disk", 20, "Note", 30, parentName: "Docs"), out _);
        Assert.Equal("Disk: Note", alias.TargetPath);                         // no full path, no folder IDs: a minimal alias
        var one = AliasRecord.Read(AliasBuilder.Alias("Disk", 20, "Note", 30, path: "Disk:A:Docs:Note", folderIds: [20]), out _);
        Assert.Equal("Disk: Docs: Note", one.TargetPath);
        var top = AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Note", 30, path: "Disk:Note"), out _);
        Assert.Equal("Disk: Note", top.TargetPath);                           // parent 2 is the root folder
        var folder = AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Stuff", 31, kind: 1), out _);
        Assert.Equal((AliasKind.Folder, false), (folder.Kind, folder.IsVolume));
        var volume = AliasRecord.Read(AliasBuilder.Alias("Disk", 1, "Disk", 2, kind: 1), out _);
        Assert.Equal((true, "Disk"), (volume.IsVolume, volume.TargetPath));
    }

    [Fact]
    public void Short_unknown_or_unterminated_records()
    {
        Assert.Throws<InvalidDataException>(() => AliasRecord.Read(new byte[149], out _));
        Assert.Throws<InvalidDataException>(() => AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Note", 30, version: 3), out _));
        var open = AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Note", 30, path: "Disk:Note", terminate: false), out var complete);
        Assert.False(complete);                                              // no end tag: what was read is kept
        Assert.Equal("Disk:Note", open.FullPath?.ToMacRoman());
        var cut = AliasBuilder.Alias("Disk", 2, "Note", 30, path: "Disk:Note");
        var cutAlias = AliasRecord.Read(cut.AsMemory(0, cut.Length - 8), out complete);
        Assert.False(complete);                                              // the path runs past the end: left out
        Assert.Null(cutAlias.FullPath);
    }

    [Fact]
    public void Hfs_files_and_folders_carry_their_catalog_ids()
    {
        var disk = new HfsBuilder();
        var folder = disk.Folder(HfsBuilder.Root, "Docs");
        var file = disk.File(folder, "Note", [1], []);
        var image = ForkData.FromBytes(disk.Build("Disk"));
        var files = HfsReader.Instance.Read(image, new ContainerContext());
        Assert.Equal((file, folder), (files.Single().CatalogId, files.Single().ParentId));
        var folders = HfsReader.Instance.ReadFolders(image, new ContainerContext());
        Assert.Equal(folder, folders.Single(f => !f.IsRoot).CatalogId);
        Assert.Equal(2u, folders.Single(f => f.IsRoot).CatalogId);
    }

    // A volume "Disk": folders Old and New; "Note" in New; aliases made against it.
    private static (AliasVolume Volume, uint Old, uint New, uint Note) Volume(string name = "Disk", uint created = AliasBuilder.VolumeCreated)
    {
        var disk = new HfsBuilder();
        var old = disk.Folder(HfsBuilder.Root, "Old");
        var @new = disk.Folder(HfsBuilder.Root, "New");
        var note = disk.File(@new, "Note", [1], []);
        var image = ForkData.FromBytes(disk.Build(name));
        var volume = new AliasVolume(HfsReader.Instance.Read(image, new ContainerContext()), HfsReader.Instance.ReadFolders(image, new ContainerContext()),
            created: new MacDate(created));
        return (volume, old, @new, note);
    }

    [Fact]
    public void An_alias_resolves_by_the_targets_id_first_even_when_it_moved()
    {
        var (volume, old, _, note) = Volume();
        Assert.Equal("Disk", volume.Name.ToMacRoman());
        var moved = AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Note", note, path: "Disk:Old:Note", folderIds: [old]), out _);
        var found = AliasResolver.Resolve(moved, [volume]);
        Assert.True(found.Found);
        Assert.Equal((AliasResolvedBy.TargetId, "Note", "Disk: New: Note"), (found.By, found.File!.Name.ToMacRoman(), found.ResolvedPath));
        Assert.Same(volume, found.Volume);
        Assert.Equal("Disk: Old: Note", found.StoredPath);
    }

    [Fact]
    public void Then_by_name_in_the_parent_folder_then_by_the_stored_path()
    {
        var (volume, _, @new, _) = Volume();
        var byPath = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", 999, "Note", 999, path: "Disk:New:Note"), out _), [volume]);
        Assert.Equal(AliasResolvedBy.FullPath, byPath.By);
        var byName = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("disk", @new, "note", 999), out _), [volume]);
        Assert.Equal((AliasResolvedBy.ParentAndName, "Note"), (byName.By, byName.File?.Name.ToMacRoman()));   // names as HFS compares them
    }

    [Fact]
    public void A_missing_target_or_an_absent_volume_is_not_found_with_the_stored_path()
    {
        var (volume, old, _, _) = Volume();
        var gone = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Gone", 999, path: "Disk:Old:Gone", folderIds: [old]), out _), [volume]);
        Assert.False(gone.Found);
        Assert.Null(gone.By);
        Assert.Equal(("Disk: Old: Gone", "not found"), (gone.StoredPath, gone.How));
        var renamed = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Renamed", old, "Note", 18), out _), [volume]);
        Assert.True(renamed.Found);                                          // the same creation date: the volume was renamed
        var other = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Other", old, "Note", 18, volumeCreated: 1), out _), [volume]);
        Assert.False(other.Found);                                           // neither name nor date: another volume
    }

    // Why an original is not found decides what the viewer shows (aliases.md §5): its volume is open but it is gone; its
    // volume is not open; or it is on a network volume.
    [Fact]
    public void An_unresolved_alias_says_whether_the_original_is_missing_its_disk_is_not_open_or_it_is_on_a_network()
    {
        var (volume, old, _, note) = Volume();
        Assert.Equal(AliasState.Found, AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Note", note), out _), [volume]).State);
        Assert.Equal(AliasState.Missing, AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Gone", 999), out _), [volume]).State);
        Assert.Equal(AliasState.Missing,                                     // the volume renamed (same date): still open
            AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Renamed", old, "Gone", 999), out _), [volume]).State);
        Assert.Equal(AliasState.VolumeNotOpen,
            AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Bag of Holding", 2, "Note", 18, volumeCreated: 1), out _), [volume]).State);
        Assert.Equal(AliasState.VolumeNotOpen, AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Note", note), out _), []).State);

        var share = AliasRecord.Read(AliasBuilder.Alias("Shared", 2, "Plans", 40, volumeCreated: 1, volumeType: 1, volumeAttributes: 0x11,
            extras: [(9, AliasBuilder.AfpMount("Office", "Studio", "Shared", "lars"))]), out _);
        var network = AliasResolver.Resolve(share, [volume]);
        Assert.Equal(AliasState.Network, network.State);
        Assert.Equal(new AliasNetwork("Office", "Studio", "Shared", "lars"), share.Network);
        // An AppleShare volume type alone (no mount information) is a network volume too, its names unknown.
        var bare = AliasRecord.Read(AliasBuilder.Alias("Shared", 2, "Plans", 40, volumeCreated: 1, volumeType: 1), out _);
        Assert.Equal(AliasState.Network, AliasResolver.Resolve(bare, [volume]).State);
        Assert.Equal(new AliasNetwork(null, null, "Shared", null), bare.Network);
        Assert.Null(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Note", note), out _).Network);

        // One sentence each, for the CLI, MCP and the viewer.
        Assert.Equal("The original is not on Disk any more.",
            AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Gone", 999), out _), [volume]).Explanation);
        Assert.Equal("The original is on the hard disk “Bag of Holding”, which is not open.",
            AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Bag of Holding", 2, "Note", 18, volumeCreated: 1), out _), [volume]).Explanation);
        Assert.Equal("The original is on the network volume “Shared” on the server “Studio” (zone “Office”, as “lars”).",
            network.Explanation);
        Assert.Equal("The original is on the network volume “Shared”.", AliasResolver.Resolve(bare, [volume]).Explanation);
        Assert.Equal("Found by its file ID.", AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", old, "Note", note), out _), [volume]).Explanation);
    }

    [Theory]
    [InlineData(0, "hard disk")]
    [InlineData(1, "network volume")]
    [InlineData(2, "400K floppy disk")]
    [InlineData(3, "800K floppy disk")]
    [InlineData(4, "1.4 MB floppy disk")]
    [InlineData(5, "removable disk")]
    [InlineData(-1, null)]
    [InlineData(9, null)]
    public void The_volume_type_names_the_kind_of_disk(short type, string? name) =>
        Assert.Equal(name, AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Note", 18, volumeType: type), out _).VolumeKindName);

    [Fact]
    public void Another_open_volume_with_the_aliass_volume_name_is_searched()
    {
        var (here, _, _, _) = Volume("Here");
        var (there, _, _, note) = Volume("There");
        var found = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("There", 2, "Note", note), out _), [here, there]);
        Assert.Same(there, found.Volume);
        Assert.Equal("There: New: Note", found.ResolvedPath);
    }

    [Fact]
    public void A_folder_alias_resolves_to_the_folder()
    {
        var (volume, old, _, _) = Volume();
        var folder = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", 2, "Old", old, kind: 1), out _), [volume]);
        Assert.Equal((AliasResolvedBy.TargetId, "Old", "Disk: Old"), (folder.By, folder.Folder?.Name.ToMacRoman(), folder.ResolvedPath));
        Assert.Null(folder.File);
        var disk = AliasResolver.Resolve(AliasRecord.Read(AliasBuilder.Alias("Disk", 1, "Disk", 2, kind: 1), out _), [volume]);
        Assert.True(disk.Folder?.IsRoot);                                    // an alias of the volume itself
    }

    [Fact]
    public void An_alias_files_record_is_its_alis_0()
    {
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("Note alias"),
            FinderInfo = FinderInfo.Empty with { Flags = FinderFlags.IsAlias },
            ResourceFork = ForkData.FromBytes(AliasBuilder.Fork(AliasBuilder.Alias("Disk", 2, "Note", 30))),
        };
        Assert.Equal("Note", AliasResolver.ReadAlias(file)?.Name.ToMacRoman());
        Assert.Null(AliasResolver.ReadAlias(file with { ResourceFork = ForkData.Empty }));
        Assert.True(AliasResolver.IsAlias(file));
        Assert.False(AliasResolver.IsAlias(file with { FinderInfo = FinderInfo.Empty }));
    }

    [Fact]
    public void On_a_volume_of_another_date_a_target_found_by_name_must_match_the_aliass_dates_and_codes()
    {
        var (volume, _, @new, _) = Volume(created: 5);
        var byName = AliasRecord.Read(AliasBuilder.Alias("Disk", @new, "Note", 999), out _);
        Assert.False(AliasResolver.Resolve(byName, [volume]).Found);           // HfsBuilder's files have no creation date
    }

    // A volume with "Note" and two aliases: "Note alias" to it and "Alias alias" to that alias.
    [Fact]
    public void Following_goes_through_aliases_of_aliases_and_never_takes_the_alias_for_its_target()
    {
        var disk = new HfsBuilder();
        var next = disk.NextId;
        var aliasInfo = new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt"), Flags = FinderFlags.IsAlias };
        var note = disk.File(HfsBuilder.Root, "Note", [1], []);
        var noteAlias = disk.File(HfsBuilder.Root, "Note alias", [], AliasBuilder.Fork(AliasBuilder.Alias("Disk", 2, "Note", note)), info: aliasInfo);
        disk.File(HfsBuilder.Root, "Alias alias", [], AliasBuilder.Fork(AliasBuilder.Alias("Disk", 2, "Note alias", noteAlias)), info: aliasInfo);
        var self = disk.File(HfsBuilder.Root, "Self", [], AliasBuilder.Fork(AliasBuilder.Alias("Disk", 2, "Self", next + 3)), info: aliasInfo);
        var image = ForkData.FromBytes(disk.Build("Disk"));
        var files = HfsReader.Instance.Read(image, new ContainerContext());
        var volume = new AliasVolume(files, HfsReader.Instance.ReadFolders(image, new ContainerContext()), created: new MacDate(AliasBuilder.VolumeCreated));
        var followed = AliasResolver.Follow(files.Single(f => f.Name.ToMacRoman() == "Alias alias"), [volume]);
        Assert.Equal("Note", followed?.File?.Name.ToMacRoman());
        var selfFile = files.Single(f => f.CatalogId == self);
        Assert.False(AliasResolver.Resolve(AliasResolver.ReadAlias(selfFile)!, [volume], selfFile).Found);
        Assert.Null(AliasResolver.Follow(selfFile, [volume]));
    }
}
