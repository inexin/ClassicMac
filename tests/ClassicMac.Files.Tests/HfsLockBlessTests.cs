using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.SetLocked and Bless (docs/formats/file-systems/hfs.md §3): a file's locked flag (filFlags bit 0), and the
// blessed System Folder in the MDB's drFndrInfo.
public sealed class HfsLockBlessTests
{
    private const int Mdb = 2 * HfsBuilder.Block;

    private static IReadOnlyList<MacFile> Files(byte[] image) => HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(byte[] image) => HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext());

    private static byte[] Volume()
    {
        var builder = new HfsBuilder();
        var system = builder.Folder(HfsBuilder.Root, "System Folder");
        builder.File(system, "System", [], [], type: "zsys", creator: "MACS");
        builder.File(system, "Finder", [], [], type: "FNDR", creator: "MACS");
        builder.Folder(HfsBuilder.Root, "Docs");
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        return builder.Build("Disk");
    }

    [Fact]
    public void A_file_is_locked_and_unlocked()
    {
        var locked = HfsWriter.SetLocked(ForkData.FromBytes(Volume()), "Read Me", true);
        Assert.True(Files(locked).Single(f => f.MacPath == "Read Me").IsLocked);
        Assert.Throws<InvalidDataException>(() => HfsWriter.DeleteFile(ForkData.FromBytes(locked), "Read Me"));   // fLckdErr

        var unlocked = HfsWriter.SetLocked(ForkData.FromBytes(locked), "Read Me", false);
        Assert.False(Files(unlocked).Single(f => f.MacPath == "Read Me").IsLocked);
        Assert.DoesNotContain(Files(HfsWriter.DeleteFile(ForkData.FromBytes(unlocked), "Read Me")), f => f.MacPath == "Read Me");
    }

    [Fact]
    public void Only_a_file_that_exists_is_locked()
    {
        Assert.Throws<InvalidDataException>(() => HfsWriter.SetLocked(ForkData.FromBytes(Volume()), "Docs", true));       // HFS folders have no lock
        Assert.Throws<InvalidDataException>(() => HfsWriter.SetLocked(ForkData.FromBytes(Volume()), "Nothing", true));
    }

    [Fact]
    public void A_folder_with_a_System_file_is_blessed_in_the_MDB()
    {
        var source = Volume();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(Mdb + 0x5C)));

        var output = HfsWriter.Bless(ForkData.FromBytes(source), "System Folder");

        var system = Folders(output).Single(f => f.MacPath == "System Folder");
        Assert.Equal(system.CatalogId, BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(Mdb + 0x5C)));   // drFndrInfo[0]
        Assert.Equal(system.CatalogId, HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(output))!.BlessedFolderId);
        Assert.Equal(source.AsSpan(Mdb + 0x60, 28).ToArray(), output.AsSpan(Mdb + 0x60, 28).ToArray());     // the rest stays
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(output)));
    }

    [Fact]
    public void Only_a_folder_holding_a_System_file_is_blessed()
    {
        Assert.Throws<InvalidDataException>(() => HfsWriter.Bless(ForkData.FromBytes(Volume()), "Docs"));          // no System file
        Assert.Throws<InvalidDataException>(() => HfsWriter.Bless(ForkData.FromBytes(Volume()), "Read Me"));       // a file
        Assert.Throws<InvalidDataException>(() => HfsWriter.Bless(ForkData.FromBytes(Volume()), "Nowhere"));
        Assert.Null(HfsReader.Instance.ReadVolumeInfo(ForkData.FromBytes(Volume()))!.BlessedFolderId);                // none yet
    }
}
