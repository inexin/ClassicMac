using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// Thread records only as long as their name, as hfsutils writes them and Mac OS 9 reads and keeps them (hfs.md §1.9): a
// volume holding them is checked and edited, a renamed item's thread is written at Mac OS's full length, and a moved
// item's thread takes its new parent.
public sealed class HfsShortThreadTests
{
    private const int Mdb = 1024;

    private static byte[] Volume()
    {
        var builder = new HfsBuilder { ShortThreads = true };
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        builder.File(docs, "Letter", "data"u8.ToArray(), [], thread: true);
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        return builder.Build("FLOP800");
    }

    // The thread record of an item (key: its ID, no name), from the catalog's leaf nodes.
    private static byte[] Thread(byte[] image, uint id)
    {
        int blockSize = BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(Mdb + 0x14));
        int firstBlock = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x1C)) * 512;
        int start = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x96)), count = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(Mdb + 0x98));
        var catalog = image.AsSpan(firstBlock + start * blockSize, count * blockSize);
        for (var node = 1; node * 512 < catalog.Length; node++)
        {
            var bytes = catalog.Slice(node * 512, 512);
            if (bytes[8] != 0xFF)
            {
                continue;
            }

            int records = BinaryPrimitives.ReadUInt16BigEndian(bytes[10..]);
            for (var r = 0; r < records; r++)
            {
                int offset = BinaryPrimitives.ReadUInt16BigEndian(bytes[(510 - 2 * r)..]), end = BinaryPrimitives.ReadUInt16BigEndian(bytes[(508 - 2 * r)..]);
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 2)..]) == id && bytes[offset + 6] == 0)
                {
                    return bytes[(offset + ((bytes[offset] + 2) & ~1))..end].ToArray();
                }
            }
        }

        throw new InvalidOperationException($"No thread for {id}.");
    }

    private static IReadOnlyList<MacFile> Files(byte[] image) => HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(byte[] image) => HfsReader.Instance.ReadFolders(ForkData.FromBytes(image), new ContainerContext());

    [Fact]
    public void A_volume_with_short_threads_passes_the_writer_s_checks()
    {
        var image = Volume();

        Assert.Equal(22, Thread(image, 2).Length);                                     // 15 + "FLOP800"
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
    }

    // Disk First Aid rejects short threads, so an edit session's first change writes them all at Mac OS's full length,
    // listed as a change of its own; the saved volume holds no short thread.
    [Fact]
    public void An_edit_session_writes_every_short_thread_at_full_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cm-threads-{Guid.NewGuid():N}.img");
        File.WriteAllBytes(path, Volume());
        var saved = path + ".out";
        try
        {
            var session = Editing.InputEditSession.Open(path);
            session.Delete("Read Me");
            session.SaveAs(saved);

            Assert.Equal(("repair", "", "3 thread records written at Mac OS's full length (Disk First Aid rejects shorter ones)"),
                (session.Changes[0].Action, session.Changes[0].Path, session.Changes[0].Detail));
            Assert.Equal("delete", session.Changes[1].Action);
            var image = File.ReadAllBytes(saved);
            foreach (var id in new uint[] { 2, Folders(image).Single(f => f.MacPath == "Docs").CatalogId!.Value, Files(image).Single(f => f.MacPath == "Docs:Letter").CatalogId!.Value })
            {
                Assert.Equal(46, Thread(image, id).Length);
            }

            Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        }
        finally
        {
            ForkData.CloseHostFile(path);
            ForkData.CloseHostFile(saved);
            File.Delete(path);
            File.Delete(saved);
        }
    }

    [Fact]
    public void A_renamed_folder_s_short_thread_is_written_at_full_length()
    {
        var image = Volume();
        var docs = Folders(image).Single(f => f.MacPath == "Docs").CatalogId!.Value;

        var renamed = HfsWriter.Rename(ForkData.FromBytes(image), "Docs", "Papers and Letters");

        var thread = Thread(renamed, docs);
        Assert.Equal(46, thread.Length);
        Assert.Equal("Papers and Letters", MacRoman.Decode(thread.AsSpan(15, thread[14])));
        Assert.Contains(Files(renamed), f => f.MacPath == "Papers and Letters:Letter");
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(renamed)));
    }

    [Fact]
    public void A_moved_file_s_short_thread_takes_its_new_parent()
    {
        var image = Volume();
        var letter = Files(image).Single(f => f.MacPath == "Docs:Letter").CatalogId!.Value;

        var moved = HfsWriter.Move(ForkData.FromBytes(image), "Docs:Letter", "");

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(Thread(moved, letter).AsSpan(10)));
        Assert.Contains(Files(moved), f => f.MacPath == "Letter");
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(moved)));
        Assert.DoesNotContain(Files(HfsWriter.Delete(ForkData.FromBytes(moved), "Docs", recursive: true)), f => f.MacPath.StartsWith("Docs", StringComparison.Ordinal));
    }
}
