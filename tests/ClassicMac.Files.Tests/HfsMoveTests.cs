using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// HfsWriter.Move (docs/formats/file-systems/hfs.md §3): a file or folder moved to another folder of its volume, as
// PBCatMove moves it: the record takes its new parent, its thread follows, and both folders' valences change.
public sealed class HfsMoveTests
{
    private const int Mdb = 2 * HfsBuilder.Block;

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

    // The parent ID a thread record holds (+10), found in the catalog's leaf nodes by its key (the item's ID, no name).
    private static uint ThreadParent(byte[] image, uint id)
    {
        var mdb = image.AsSpan(Mdb);
        int blockSize = BinaryPrimitives.ReadInt32BigEndian(mdb[0x14..]);
        int firstBlock = BinaryPrimitives.ReadUInt16BigEndian(mdb[0x1C..]) * HfsBuilder.Block;
        int start = BinaryPrimitives.ReadUInt16BigEndian(mdb[0x96..]), count = BinaryPrimitives.ReadUInt16BigEndian(mdb[0x98..]);
        var catalog = image.AsSpan(firstBlock + start * blockSize, count * blockSize);
        for (var node = 1; node * 512 < catalog.Length; node++)
        {
            var bytes = catalog.Slice(node * 512, 512);
            if (bytes[8] != 0xFF)
            {
                continue;                                                          // not a leaf node
            }

            for (var r = 0; r < BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]); r++)
            {
                int offset = BinaryPrimitives.ReadUInt16BigEndian(bytes[(510 - 2 * r)..]);
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 2)..]) == id && bytes[offset + 6] == 0)
                {
                    int data = offset + ((bytes[offset] + 2) & ~1);
                    return BinaryPrimitives.ReadUInt32BigEndian(bytes[(data + 10)..]);
                }
            }
        }

        throw new InvalidOperationException($"No thread for {id}.");
    }

    private static void AssertSound(byte[] image)
    {
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        var diagnostics = new List<Diagnostic>();
        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(diagnostics: diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_file_moves_to_another_folder_keeping_its_ID_forks_and_info_and_its_thread_follows()
    {
        var source = Volume();
        var before = Files(source).Single(f => f.Name.ToMacRoman() == "Letter");

        var output = HfsWriter.Move(ForkData.FromBytes(source), "Docs:Letter", "Docs:Deep");

        var after = Files(output).Single(f => f.Name.ToMacRoman() == "Letter");
        Assert.Equal("Docs:Deep:Letter", after.MacPath);
        Assert.Equal(before.CatalogId, after.CatalogId);
        Assert.Equal("data"u8.ToArray(), after.DataFork.ToArray());
        Assert.Equal("rsrc"u8.ToArray(), after.ResourceFork.ToArray());
        Assert.Equal(before.FinderInfo.ToArray(), after.FinderInfo.ToArray());
        Assert.Equal(before.Modified, after.Modified);
        var deep = Folders(output).Single(f => f.MacPath == "Docs:Deep");
        Assert.Equal(deep.CatalogId, ThreadParent(output, after.CatalogId!.Value));
        AssertSound(output);
    }

    [Fact]
    public void A_folder_moves_with_its_contents_and_its_thread_follows()
    {
        var output = HfsWriter.Move(ForkData.FromBytes(Volume()), "Docs:Deep", "");

        Assert.Contains(Folders(output), f => f.MacPath == "Deep");
        Assert.Contains(Files(output), f => f.MacPath == "Deep:Note");
        Assert.DoesNotContain(Folders(output), f => f.MacPath == "Docs:Deep");
        var deep = Folders(output).Single(f => f.MacPath == "Deep");
        Assert.Equal(2u, ThreadParent(output, deep.CatalogId!.Value));
        AssertSound(output);
    }

    [Fact]
    public void Moving_out_of_and_into_the_root_changes_the_volume_s_root_counts()
    {
        var source = Volume();
        int rootFiles = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(Mdb + 0x0C));

        var output = HfsWriter.Move(ForkData.FromBytes(source), "Read Me", "Docs");

        Assert.Contains(Files(output), f => f.MacPath == "Docs:Read Me");
        Assert.Equal(rootFiles - 1, BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(Mdb + 0x0C)));
        AssertSound(output);

        int rootFolders = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(Mdb + 0x52));
        output = HfsWriter.Move(ForkData.FromBytes(output), "Docs:Deep", "");
        Assert.Equal(rootFolders + 1, BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(Mdb + 0x52)));
        AssertSound(output);
    }

    [Fact]
    public void Moves_the_Mac_refuses_are_refused_and_the_source_is_untouched()
    {
        var source = Volume();
        var original = source.ToArray();

        // Into itself or a folder inside it (badMovErr); onto a name the folder has (dupFNErr); to no folder.
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Docs", "Docs"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Docs", "Docs:Deep"));
        var clash = HfsWriter.CreateFile(ForkData.FromBytes(source), "Docs:Deep:read me", default, default, FinderInfo.Empty);
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(clash), "Read Me", "Docs:Deep"));   // names ignore case
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Read Me", "Docs:Letter"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Read Me", "Nowhere"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Nothing", "Docs"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.Move(ForkData.FromBytes(source), "Read Me", ""));   // already there
        Assert.Equal(original, source);
    }
}
