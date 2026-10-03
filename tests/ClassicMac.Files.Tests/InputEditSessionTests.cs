using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// An edit session on an opened input (the library side of the app's Volume menu and Save / Save As, and of the CLI's
// write commands): items and resources changed in memory, listed as planned changes, saved to a new file, or in place
// only when asked.
public sealed class InputEditSessionTests : IDisposable
{
    private static readonly FourCC Str = FourCC.FromString("STR ");

    private readonly string directory = Directory.CreateTempSubdirectory("cm-session").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Volume()
    {
        var builder = new HfsBuilder();
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        builder.File(docs, "Letter", "data"u8.ToArray(), fork.ToArray());
        builder.File(docs, "Empty", [], []);
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var path = Path.Combine(directory, "Disk.img");
        File.WriteAllBytes(path, WithFreeSpace(builder.Build("Disk")));
        return path;
    }

    private static IReadOnlyList<MacFile> Files(string image) => HfsReader.Instance.Read(ForkData.FromFile(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(string image) => HfsReader.Instance.ReadFolders(ForkData.FromFile(image), new ContainerContext());

    [Fact]
    public void A_volume_s_items_change_in_memory_and_save_to_a_new_image()
    {
        var path = Volume();
        var original = File.ReadAllBytes(path);
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);
        Assert.False(session.HasChanges);

        session.AddFolder("Docs:Old");
        session.AddFile("Docs:New", new MacFile
        {
            Name = MacString.FromMacRoman("New"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") },
            DataFork = ForkData.FromBytes("new"u8.ToArray()),
        });
        session.Rename("Read Me", "About");
        session.SetInfo("About", type: FourCC.FromString("ttro"), flags: FinderFlags.IsStationery);
        session.Delete("Docs:Empty");
        var target = Path.Combine(directory, "Out.img");
        var written = session.SaveAs(target);

        Assert.Equal([target], written);
        Assert.Equal(original, File.ReadAllBytes(path));                       // the original never changes
        var files = Files(target);
        Assert.Equal(["About", "Docs:Letter", "Docs:New"], files.Select(f => f.MacPath).Order());
        var about = files.Single(f => f.MacPath == "About");
        Assert.Equal((FourCC.FromString("ttro"), FourCC.FromString("ttxt"), FinderFlags.IsStationery),
            (about.FinderInfo.Type, about.FinderInfo.Creator, about.FinderInfo.Flags));
        Assert.Contains(Folders(target), f => f.MacPath == "Docs:Old");
        Assert.Equal(["mkdir", "add", "rename", "set", "delete"], session.Changes.Select(c => c.Action));
        Assert.Equal(("rename", "Read Me", "to About"), (session.Changes[2].Action, session.Changes[2].Path, session.Changes[2].Detail));
    }

    [Fact]
    public void Resources_are_added_replaced_and_deleted_in_a_volume_s_files()
    {
        var path = Volume();
        var session = InputEditSession.Open(path);

        session.SetResource("Docs:Letter", Str, 129, "two"u8.ToArray(), MacString.FromMacRoman("Second"));
        session.SetResource("Docs:Letter", Str, 128, "one"u8.ToArray());                  // replaces
        session.DeleteResource("Docs:Letter", Str, 129);
        session.SetResource("Read Me", Str, 200, "x"u8.ToArray());                         // a file with no resources yet
        session.Rename("Read Me", "Notes");                                              // its edits follow it
        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);

        var letter = ResourceFork.Read(Files(target).Single(f => f.MacPath == "Docs:Letter").ResourceFork.ToArray());
        Assert.Equal("one"u8.ToArray(), letter.Find(Str, 128)!.GetData().ToArray());
        Assert.Null(letter.Find(Str, 129));
        var notes = ResourceFork.Read(Files(target).Single(f => f.MacPath == "Notes").ResourceFork.ToArray());
        Assert.Equal("x"u8.ToArray(), notes.Find(Str, 200)!.GetData().ToArray());
        Assert.Equal(["res-set", "res-set", "res-delete", "res-set", "rename"], session.Changes.Select(c => c.Action));
        Assert.Throws<InvalidOperationException>(() => session.DeleteResource("Notes", Str, 999));
    }

    [Fact]
    public void An_item_moves_to_another_folder_and_its_resource_edits_follow()
    {
        var session = InputEditSession.Open(Volume());
        session.SetResource("Read Me", Str, 200, "x"u8.ToArray());
        session.Move("Read Me", "Docs");
        session.Move("Docs:Letter", "");
        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);

        Assert.Equal(["Docs:Empty", "Docs:Read Me", "Letter"], Files(target).Select(f => f.MacPath).Order());
        var moved = ResourceFork.Read(Files(target).Single(f => f.MacPath == "Docs:Read Me").ResourceFork.ToArray());
        Assert.Equal("x"u8.ToArray(), moved.Find(Str, 200)!.GetData().ToArray());
        Assert.Equal([("move", "Read Me", "to Docs"), ("move", "Docs:Letter", "to the volume's top level")],
            session.Changes.Skip(1).Select(c => (c.Action, c.Path, c.Detail)));
    }

    [Fact]
    public void A_file_is_locked_and_unlocked_and_a_System_Folder_blessed()
    {
        var builder = new HfsBuilder();
        var system = builder.Folder(HfsBuilder.Root, "System Folder");
        builder.File(system, "System", [], [], type: "zsys", creator: "MACS");
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var path = Path.Combine(directory, "System.img");
        File.WriteAllBytes(path, builder.Build("Disk"));
        var session = InputEditSession.Open(path);

        session.SetLocked("Read Me", true);
        session.SetLocked("System Folder:System", true);
        session.SetLocked("System Folder:System", false);
        session.Bless("System Folder");
        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);

        Assert.True(Files(target).Single(f => f.MacPath == "Read Me").IsLocked);
        Assert.False(Files(target).Single(f => f.MacPath == "System Folder:System").IsLocked);
        Assert.NotNull(HfsReader.Instance.ReadVolumeInfo(ForkData.FromFile(target))!.BlessedFolderId);
        Assert.Equal([("lock", "Read Me", ""), ("lock", "System Folder:System", ""), ("unlock", "System Folder:System", ""),
            ("bless", "System Folder", "as the System Folder")], session.Changes.Select(c => (c.Action, c.Path, c.Detail)));
    }

    [Fact]
    public void An_empty_volume_is_a_volume_to_edit()
    {
        var path = Path.Combine(directory, "Blank.img");
        File.WriteAllBytes(path, HfsWriter.Format(800 * 1024, "Blank"));
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);

        session.AddFolder("Docs");
        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);

        Assert.Contains(Folders(target), f => f.MacPath == "Docs");
    }

    [Fact]
    public void A_volume_is_grown_and_further_changes_use_the_new_space()
    {
        var path = Path.Combine(directory, "Small.img");
        File.WriteAllBytes(path, HfsWriter.Format(800 * 1024, "Small"));
        var session = InputEditSession.Open(path);

        session.Resize(4 * 1024 * 1024);
        session.AddFile("Big", new MacFile { Name = MacString.FromMacRoman("Big"), DataFork = ForkData.FromBytes(new byte[2 * 1024 * 1024]) });
        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);

        Assert.Equal(4 * 1024 * 1024, new FileInfo(target).Length);
        Assert.Contains(Files(target), f => f.MacPath == "Big");
        Assert.Equal(("resize", "", "to 4,194,304 bytes"), (session.Changes[0].Action, session.Changes[0].Path, session.Changes[0].Detail));
    }

    [Fact]
    public void A_folder_with_contents_is_deleted_only_when_asked()
    {
        var session = InputEditSession.Open(Volume());

        Assert.Throws<InvalidDataException>(() => session.Delete("Docs"));
        Assert.False(session.HasChanges);
        session.Delete("Docs", recursive: true);

        var target = Path.Combine(directory, "Out.img");
        session.SaveAs(target);
        Assert.Equal(["Read Me"], Files(target).Select(f => f.MacPath));
    }

    [Fact]
    public void Saving_in_place_keeps_the_original_as_orig()
    {
        var path = Volume();
        var original = File.ReadAllBytes(path);
        var session = InputEditSession.Open(path);
        session.AddFolder("Fresh");

        session.SaveInPlace();

        Assert.Equal(original, File.ReadAllBytes(path + ".orig"));
        Assert.Contains(Folders(path), f => f.MacPath == "Fresh");
        Assert.Throws<InvalidOperationException>(() => InputEditSession.Open(path).SaveAs(path));   // Save As never overwrites the input
    }

    [Fact]
    public void A_host_file_is_imported_with_its_forks_and_Finder_info()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'z' }));
        var mac = new MacFile
        {
            Name = MacString.FromMacRoman("Packed"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("APPL"), Creator = FourCC.FromString("Pkd1") },
            DataFork = ForkData.FromBytes("d"u8.ToArray()),
            ResourceFork = ForkData.FromBytes(fork.ToArray()),
        };
        var binary = Path.Combine(directory, "Packed.bin");
        File.WriteAllBytes(binary, MacBinaryWriter.ToArray(mac));
        var plain = Path.Combine(directory, "plain.txt");
        File.WriteAllText(plain, "text");

        var imported = HostImport.Read(binary);
        var raw = HostImport.Read(plain);

        Assert.Equal(("Packed", FourCC.FromString("APPL")), (imported.Name.ToMacRoman(), imported.FinderInfo.Type));
        Assert.Equal(fork.ToArray(), imported.ResourceFork.ToArray());
        Assert.Equal(("plain.txt", "text"), (raw.Name.ToMacRoman(), System.Text.Encoding.ASCII.GetString(raw.DataFork.ToArray())));
        Assert.True(InputEditSession.TryParseCode("TXT", out var padded));
        Assert.Equal(FourCC.FromString("TXT "), padded);
        Assert.False(InputEditSession.TryParseCode("TOOLONG", out _));
    }

    [Fact]
    public void A_single_file_s_resources_and_info_change_and_save_in_its_own_form()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        var path = Path.Combine(directory, "One.bin");
        File.WriteAllBytes(path, MacBinaryWriter.ToArray(new MacFile
        {
            Name = MacString.FromMacRoman("One"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") },
            ResourceFork = ForkData.FromBytes(fork.ToArray()),
        }));
        var session = InputEditSession.Open(path);
        Assert.Equal(InputEditKind.SingleFile, session.Kind);

        session.SetResource("", Str, 130, "new"u8.ToArray());
        session.SetInfo("", creator: FourCC.FromString("CMac"));
        Assert.Throws<InvalidOperationException>(() => session.AddFolder("Nope"));
        var target = Path.Combine(directory, "Two.bin");
        session.SaveAs(target);

        var back = HostImport.Read(target);
        Assert.Equal(FourCC.FromString("CMac"), back.FinderInfo.Creator);
        Assert.Equal("new"u8.ToArray(), ResourceFork.Read(back.ResourceFork.ToArray()).Find(Str, 130)!.GetData().ToArray());
    }

    [Fact]
    public void A_raw_resource_fork_file_takes_resource_edits()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        var path = Path.Combine(directory, "Prefs.rsrc");
        File.WriteAllBytes(path, fork.ToArray());
        var session = InputEditSession.Open(path);

        session.DeleteResource("", Str, 128);
        session.SetResource("", Str, 131, "b"u8.ToArray());
        session.SaveInPlace();

        var back = ResourceFork.Read(File.ReadAllBytes(path));
        Assert.Null(back.Find(Str, 128));
        Assert.NotNull(back.Find(Str, 131));
        Assert.True(File.Exists(path + ".orig"));
        Assert.Throws<InvalidOperationException>(() => session.SetInfo("", type: Str));      // a raw fork has no Finder info
    }

    private static byte[] WithFreeSpace(byte[] image)
    {
        const int allocationBlocks = 1600;
        int oldBlocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12));
        int oldFree = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22));
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), allocationBlocks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22),
            checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }
}
