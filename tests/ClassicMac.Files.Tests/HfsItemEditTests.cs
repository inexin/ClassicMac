using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter's item edits (docs/formats/file-systems/hfs.md §3): renaming files and folders, setting a file's Finder
// info and a folder's Finder flags, and deleting a folder with everything in it.
public sealed class HfsItemEditTests
{
    private static IReadOnlyList<MacFile> Files(byte[] image) => HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(byte[] image) => HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext());

    private static byte[] Volume()
    {
        var builder = new HfsBuilder();
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        var deep = builder.Folder(docs, "Deep");
        builder.File(docs, "Letter", "data"u8.ToArray(), "rsrc"u8.ToArray(), thread: true);
        builder.File(deep, "Note", [1, 2, 3], []);
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        return builder.Build("Disk");
    }

    [Fact]
    public void A_file_is_renamed_keeping_its_forks_info_and_ID()
    {
        var source = Volume();
        var before = Files(source).Single(f => f.Name.ToMacRoman() == "Letter");

        var output = HfsWriter.Rename(ForkData.FromBytes(source), "Docs:Letter", "Reply");

        var after = Files(output).Single(f => f.Name.ToMacRoman() == "Reply");
        Assert.Equal(["Docs"], after.FolderPath.Select(n => n.ToMacRoman()));
        Assert.Equal(("data", "rsrc"), (System.Text.Encoding.ASCII.GetString(after.DataFork.ToArray()), System.Text.Encoding.ASCII.GetString(after.ResourceFork.ToArray())));
        Assert.Equal(before.FinderInfo.Type, after.FinderInfo.Type);
        Assert.DoesNotContain(Files(output), f => f.Name.ToMacRoman() == "Letter");
        Assert.Equal(before.Created, after.Created);
    }

    [Fact]
    public void A_folder_is_renamed_and_its_contents_follow()
    {
        var output = HfsWriter.Rename(ForkData.FromBytes(Volume()), "Docs", "Papers");

        Assert.Contains(Folders(output), f => f.MacPath == "Papers:Deep");
        Assert.Contains(Files(output), f => f.MacPath == "Papers:Deep:Note");
        Assert.DoesNotContain(Folders(output), f => f.MacPath == "Docs");
        // The renamed folder can be edited by its new path (its thread record names it).
        HfsWriter.CreateFolder(ForkData.FromBytes(output), "Papers:New");
    }

    [Fact]
    public void Renaming_refuses_a_taken_name_a_bad_name_and_a_missing_item()
    {
        var source = Volume();
        var original = source.ToArray();

        Assert.Throws<InvalidDataException>(() => HfsWriter.Rename(ForkData.FromBytes(source), "Read Me", "docs"));   // names ignore case
        Assert.Throws<ArgumentException>(() => HfsWriter.Rename(ForkData.FromBytes(source), "Read Me", "a:b"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Rename(ForkData.FromBytes(source), "Nothing", "Else"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void A_file_s_type_creator_and_flags_are_set()
    {
        var info = new FinderInfo { Type = FourCC.FromString("APPL"), Creator = FourCC.FromString("CMac"), Flags = FinderFlags.HasBundle | FinderFlags.IsInvisible };

        var output = HfsWriter.SetFinderInfo(ForkData.FromBytes(Volume()), "Read Me", info);

        var file = Files(output).Single(f => f.MacPath == "Read Me");
        Assert.Equal((info.Type, info.Creator, info.Flags), (file.FinderInfo.Type, file.FinderInfo.Creator, file.FinderInfo.Flags));
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(file.DataFork.ToArray()));
    }

    [Fact]
    public void A_folder_s_flags_are_set_and_a_file_path_is_refused()
    {
        var output = HfsWriter.SetFolderFlags(ForkData.FromBytes(Volume()), "Docs", FinderFlags.IsInvisible);

        Assert.Equal(FinderFlags.IsInvisible, Folders(output).Single(f => f.MacPath == "Docs").FinderInfo.Flags);
        Assert.Throws<InvalidDataException>(() => HfsWriter.SetFolderFlags(ForkData.FromBytes(Volume()), "Read Me", FinderFlags.IsInvisible));
        Assert.Throws<InvalidDataException>(() => HfsWriter.SetFinderInfo(ForkData.FromBytes(Volume()), "Docs", FinderInfo.Empty));
    }

    [Fact]
    public void A_folder_is_deleted_with_everything_in_it_only_when_asked()
    {
        var source = Volume();

        Assert.Throws<InvalidDataException>(() => HfsWriter.Delete(ForkData.FromBytes(source), "Docs", recursive: false));
        var output = HfsWriter.Delete(ForkData.FromBytes(source), "Docs", recursive: true);

        Assert.Equal(["Read Me"], Files(output).Select(f => f.MacPath));
        Assert.DoesNotContain(Folders(output), f => f.MacPath.StartsWith("Docs", StringComparison.Ordinal));
        var file = HfsWriter.Delete(ForkData.FromBytes(output), "Read Me", recursive: false);       // a file either way
        Assert.Empty(Files(file));
    }
}
