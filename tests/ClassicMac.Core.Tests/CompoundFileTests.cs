using System.Buffers.Binary;

namespace ClassicMac.Core.Tests;

// Compound files ([MS-CFB]; docs/formats/containers/compound-file.md), built byte by byte.
public class CompoundFileTests
{
    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + seed)).ToArray();

    [Fact]
    public void Streams_read_from_sectors_and_from_the_mini_stream()
    {
        var big = Bytes(9000, 1);                                   // ≥ 4096: in sectors
        var small = Bytes(300, 2);                                  // < 4096: in the mini stream
        var data = new CompoundFileBuilder().Stream("WordDocument", big).Stream("1Table", small).Stream("Empty", []).Build();
        var diagnostics = new List<Diagnostic>();

        var file = CompoundFile.Read(data, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal((3, 512), (file.MajorVersion, file.SectorSize));
        Assert.Equal(["WordDocument", "1Table", "Empty"], file.Entries.Where(e => e.Type == CompoundFileEntryType.Stream).Select(e => e.Path));
        Assert.Equal(big, file.ReadStream(file.Find("WordDocument")!).ToArray());
        Assert.Equal(small, file.ReadStream(file.Find("1table")!).ToArray());           // names compare ignoring case
        Assert.Empty(file.ReadStream(file.Find("Empty")!).ToArray());
        Assert.Equal(9000, file.Find("WordDocument")!.Size);
        Assert.Null(file.Find("Data"));
    }

    [Fact]
    public void Storages_hold_their_streams_by_path()
    {
        var data = new CompoundFileBuilder().Storage("ObjectPool").Stream("ObjectPool/_1234", Bytes(70, 3)).Stream("WordDocument", Bytes(5000, 4)).Build();

        var file = CompoundFile.Read(data);

        var storage = file.Find("ObjectPool")!;
        Assert.Equal(CompoundFileEntryType.Storage, storage.Type);
        Assert.Equal(Bytes(70, 3), file.ReadStream(file.Find("ObjectPool/_1234")!).ToArray());
        Assert.Equal(CompoundFileEntryType.Root, file.Entries[0].Type);
    }

    [Fact]
    public void Version_4_files_have_4096_byte_sectors()
    {
        var big = Bytes(10000, 5);
        var data = new CompoundFileBuilder { Version = 4 }.Stream("WordDocument", big).Stream("Small", Bytes(100, 6)).Build();

        var file = CompoundFile.Read(data);

        Assert.Equal((4, 4096), (file.MajorVersion, file.SectorSize));
        Assert.Equal(big, file.ReadStream(file.Find("WordDocument")!).ToArray());
        Assert.Equal(Bytes(100, 6), file.ReadStream(file.Find("Small")!).ToArray());
    }

    [Fact]
    public void FAT_sectors_past_the_header_s_109_are_found_through_DIFAT_sectors()
    {
        var big = Bytes(20000, 7);
        // Over 109 FAT sectors: 109 in the header, the rest in a DIFAT sector; each must be found marked as a FAT sector.
        var data = new CompoundFileBuilder { ExtraFatSectors = 120 }.Stream("WordDocument", big).Build();
        var diagnostics = new List<Diagnostic>();

        var file = CompoundFile.Read(data, diagnostics);

        Assert.True(file.FatSectorCount > 109);
        Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x2C)), file.FatSectorCount);
        Assert.Empty(diagnostics);
        Assert.Equal(big, file.ReadStream(file.Find("WordDocument")!).ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, true)]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE0 }, false)]
    [InlineData(new byte[] { 0xD0, 0xCF }, false)]
    public void The_signature_identifies_a_compound_file(byte[] start, bool expected) =>
        Assert.Equal(expected, CompoundFile.IsCompoundFile(start));

    [Fact]
    public void A_header_that_is_not_one_throws()
    {
        var data = new CompoundFileBuilder().Stream("A", Bytes(10, 1)).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x1C), 0xFEFF);    // byte order

        Assert.Throws<InvalidDataException>(() => CompoundFile.Read(data));
        Assert.Throws<InvalidDataException>(() => CompoundFile.Read(new byte[100]));
    }

    [Fact]
    public void A_chain_that_loops_or_leaves_the_file_is_cut_and_reported()
    {
        var builder = new CompoundFileBuilder
        {
            Damage = (bytes, layout) =>
            {
                // The stream's second sector points back to its first.
                var first = layout.FirstSectors["WordDocument"];
                var fat = 512 + layout.FatSectors[0] * 512;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fat + 4 * (first + 1)), (uint)first);
            },
        }.Stream("WordDocument", Bytes(6000, 8));
        var diagnostics = new List<Diagnostic>();

        var file = CompoundFile.Read(builder.Build(), diagnostics);
        var read = file.ReadStream(file.Find("WordDocument")!);

        Assert.Equal(6000, read.Length);                                           // padded with zeros to its size
        Assert.Equal(Bytes(1024, 8), read[..1024].ToArray());
        Assert.Equal(["cfb.bad-chain"], diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void A_directory_entry_pointing_outside_the_directory_is_left_out()
    {
        var builder = new CompoundFileBuilder
        {
            Damage = (bytes, layout) =>
            {
                // The second entry's right sibling: entry 900, past the directory.
                var entry = 512 + layout.FirstDirectorySector * 512 + 128;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 0x48), 900);
            },
        }.Stream("A", Bytes(10, 1));
        var diagnostics = new List<Diagnostic>();

        var file = CompoundFile.Read(builder.Build(), diagnostics);

        Assert.NotNull(file.Find("A"));
        Assert.Equal(["cfb.bad-directory"], diagnostics.Select(d => d.Code));
    }
}
