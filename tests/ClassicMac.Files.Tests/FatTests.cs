using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Fat;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class FatTests
{
    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] image)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(FatReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (FatReader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics)), diagnostics);
    }

    // A FINDER.DAT record (see PcExchangeTests).
    private static byte[] Record(string macName, string type, string creator, uint created, uint modified, string dosKey, ushort flags = 0)
    {
        var record = new byte[92];
        record[0] = (byte)macName.Length;
        MacRoman.Encode(macName).CopyTo(record, 1);
        FinderInfo(type, creator, flags).CopyTo(record, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x40), created);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x44), modified);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x4C), 0x7FFFFFF0);
        Encoding.ASCII.GetBytes(dosKey).CopyTo(record, 0x50);
        return record;
    }

    private static uint Seconds(DateTime local) => MacDate.FromDateTime(local).Seconds;

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void Files_and_folders_read_on_every_FAT_type(int bits)
    {
        var builder = new FatBuilder(bits);
        builder.File("NOTE.TXT", "NOTE.TXT", Bytes(9, 3));
        builder.File("LONG.BIN", "LONGNA~1.BIN", Bytes(1300, 5), longName: "A long name with ünïcode.bin", fragmented: true);
        builder.Folder("GAMES", "GAMES");
        builder.Folder("GAMES/REALMZ", "REALMZ~1", longName: "Realmz Folder");
        builder.File("GAMES/REALMZ/SCEN", "SCEN", Bytes(2000, 7));
        builder.File("EMPTY", "EMPTY", []);

        var (files, diagnostics) = Read(builder.Build());

        Assert.Empty(diagnostics);
        Assert.Equal(["A long name with ünïcode.bin", "EMPTY", "GAMES:Realmz Folder:SCEN", "NOTE.TXT"], files.Select(f => f.MacPath).Order());
        Assert.Equal(Bytes(1300, 5), files.Single(f => f.MacPath.StartsWith('A')).DataFork.ToArray());
        Assert.Equal(Bytes(2000, 7), files.Single(f => f.Name.ToMacRoman() == "SCEN").DataFork.ToArray());
        Assert.Empty(files.Single(f => f.Name.ToMacRoman() == "EMPTY").DataFork.ToArray());
        var note = files.Single(f => f.Name.ToMacRoman() == "NOTE.TXT");
        Assert.Equal(PcExchangePlaceholder, (note.FinderInfo.Type, note.FinderInfo.Creator)); // no record
        Assert.Equal(FatBuilder.DefaultTime, note.Created!.Value.ToDateTime());
    }

    private static readonly (FourCC, FourCC) PcExchangePlaceholder = (FourCC.FromString("TEXT"), FourCC.FromString("dosa"));

    [Fact]
    public void File_Exchange_data_gives_Mac_names_Finder_info_dates_and_forks()
    {
        var builder = new FatBuilder(12);
        var dos = new DateTime(2001, 2, 3, 4, 5, 6);
        builder.File("REPORT.DOC", "REPORT~1.DOC", Bytes(700, 3), longName: "report.doc", created: dos, modified: dos);
        builder.File("OLD.BIN", "OLD.BIN", Bytes(10, 5), created: dos, modified: dos);
        builder.File("Y2040.BIN", "Y2040.BIN", Bytes(5, 1), created: new DateTime(2040, 6, 1, 12, 0, 0));
        builder.Folder("FOLDER", "FOLDER~1");
        builder.File("FOLDER/INNER", "INNER", Bytes(3, 9));
        builder.File("HIDDEN.SYS", "HIDDEN.SYS", Bytes(4, 2), attributes: 0x22);
        builder.File("FINDER.DAT", "FINDER.DAT", [
            .. Record("Quarterly Report", "WDBN", "MSWD", Seconds(new DateTime(1999, 1, 1)), Seconds(new DateTime(2010, 1, 1)), "REPORT~1DOC", flags: 0x0100),
            .. Record("Old one", "BINA", "abcd", Seconds(new DateTime(1990, 1, 1)), Seconds(new DateTime(1990, 1, 1)), "OLD     BIN"),
            .. Record("My Folder", "\0\0\0\0", "\0\0\0\0", 0, 0, "FOLDER~1   "),
        ], attributes: 0x22);
        builder.Folder("RESOURCE.FRK", "RESOURCE.FRK", attributes: 0x12);
        builder.File("RESOURCE.FRK/REPORT", "REPORT~1.DOC", Bytes(600, 11));
        builder.File("FILEID.DAT", "FILEID.DAT", new byte[16], attributes: 0x22);

        var (files, diagnostics) = Read(builder.Build());

        Assert.Empty(diagnostics);
        Assert.Equal(["HIDDEN.SYS", "My Folder:INNER", "Old one", "Quarterly Report", "Y2040.BIN"], files.Select(f => f.MacPath).Order());

        var report = files.Single(f => f.Name.ToMacRoman() == "Quarterly Report");
        Assert.Equal(FourCC.FromString("WDBN"), report.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("MSWD"), report.FinderInfo.Creator);
        Assert.Equal(dos, report.Created!.Value.ToDateTime()); // from the DOS entry
        Assert.Equal(new DateTime(2010, 1, 1), report.Modified!.Value.ToDateTime()); // the record's, being later
        Assert.Equal(Bytes(700, 3), report.DataFork.ToArray());
        Assert.Equal(Bytes(600, 11), report.ResourceFork.ToArray());

        var old = files.Single(f => f.Name.ToMacRoman() == "Old one");
        Assert.Equal(dos, old.Modified!.Value.ToDateTime()); // the DOS entry's, being later
        Assert.Empty(old.ResourceFork.ToArray());

        Assert.Equal(new DateTime(1912, 6, 1, 12, 0, 0), files.Single(f => f.Name.ToMacRoman() == "Y2040.BIN").Created!.Value.ToDateTime());
        Assert.True(files.Single(f => f.Name.ToMacRoman() == "HIDDEN.SYS").FinderInfo.Flags.HasFlag(FinderFlags.IsInvisible));
    }

    [Fact]
    public void A_looping_chain_is_cut_and_reported()
    {
        var builder = new FatBuilder(16);
        builder.File("LOOP.BIN", "LOOP.BIN", Bytes(2000, 3)); // clusters 2–5
        var image = builder.Build();
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(builder.FatOffset + 3 * 2), 2); // 3 → 2

        var (files, diagnostics) = Read(image);

        Assert.Contains(diagnostics, d => d.Code == "fat.bad-chain");
        Assert.Contains(diagnostics, d => d.Code == "fat.short");
        Assert.Equal(Bytes(2000, 3)[..1024], Assert.Single(files).DataFork.ToArray());
    }

    [Fact]
    public void A_cluster_outside_the_volume_is_reported()
    {
        var builder = new FatBuilder(12);
        builder.File("FAR.BIN", "FAR.BIN", Bytes(1000, 3));
        var image = builder.Build();
        // Cluster 2's entry (the low 12 bits of the first pair) points far past the volume.
        image[builder.FatOffset + 3] = 0xF0;
        image[builder.FatOffset + 4] = (byte)((image[builder.FatOffset + 4] & 0xF0) | 0x0E);

        var (files, diagnostics) = Read(image);

        Assert.Contains(diagnostics, d => d.Code == "fat.bad-chain");
        Assert.Equal(512, Assert.Single(files).DataFork.Length);
    }

    [Fact]
    public void A_folder_pointing_at_its_parent_is_read_once()
    {
        var builder = new FatBuilder(16);
        builder.Folder("A", "A");
        builder.Folder("A/B", "B");
        builder.File("A/B/F", "F", Bytes(5, 1));
        var image = builder.Build();
        // B's entry in A (after ".", "..") points back at A, whose cluster A's root entry gives.
        var aCluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(builder.DataOffset - 512 * 32 + 26));
        var bEntry = builder.DataOffset + (aCluster - 2) * 512 + 2 * 32;
        Assert.Equal((byte)'B', image[bEntry]);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(bEntry + 26), aCluster);

        var (files, diagnostics) = Read(image);

        Assert.Empty(files);
        Assert.Contains(diagnostics, d => d.Code == "fat.folder-loop");
    }

    [Fact]
    public void Too_many_entries_stop_the_walk()
    {
        var builder = new FatBuilder(12);
        for (var i = 0; i < 10; i++) builder.File($"F{i}", $"F{i}", Bytes(3, i + 1));
        var diagnostics = new List<Diagnostic>();

        var files = FatReader.Instance.Read(ForkData.FromBytes(builder.Build()),
            new ContainerContext(ContainerReadOptions.Default with { MaxVolumeEntries = 4 }, diagnostics));

        Assert.Equal(4, files.Count);
        Assert.Contains(diagnostics, d => d.Code == "fat.too-many-entries");
    }

    [Fact]
    public void Non_FAT_boot_sectors_are_not_read()
    {
        var image = new FatBuilder(12).Build();
        image[13] = 3; // sectors per cluster not a power of two
        Assert.False(FatReader.Instance.CanRead(ForkData.FromBytes(image)));
        Assert.False(FatReader.Instance.CanRead(ForkData.FromBytes(new byte[2048])));
    }

    [Fact]
    public void FAT_volumes_nest_through_the_unwrapper()
    {
        var builder = new FatBuilder(12);
        builder.File("NOTE.TXT", "NOTE.TXT", Bytes(9, 3));
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("floppy.img"), DataFork = ForkData.FromBytes(builder.Build()) },
            "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal("FAT volume", Assert.Single(root.Children).Format);
        Assert.Equal("NOTE.TXT", Assert.Single(root.Leaves()).File.MacPath);
    }

    [Fact]
    public void Partitioned_disks_unwrap_to_their_FAT_volumes()
    {
        var builder = new FatBuilder(16);
        builder.File("NOTE.TXT", "NOTE.TXT", Bytes(9, 3));
        var volume = builder.Build();
        var disk = new byte[63 * 512 + volume.Length];
        volume.CopyTo(disk, 63 * 512);
        void Partition(int i, byte type, uint start, uint count)
        {
            var at = 446 + i * 16;
            disk[at + 4] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 8), start);
            BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(at + 12), count);
        }
        Partition(0, 0x06, 63, (uint)(volume.Length / 512));
        Partition(1, 0x83, 1, 1); // Linux: skipped
        disk[510] = 0x55;
        disk[511] = 0xAA;
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("disk.img"), DataFork = ForkData.FromBytes(disk) },
            "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Equal("DOS partition table", Assert.Single(root.Children).Format);
        Assert.Equal("NOTE.TXT", Assert.Single(root.Leaves()).File.MacPath);
        Assert.Contains(diagnostics, d => d.Code == "mbr.skipped");
        Assert.False(MbrReader.Instance.CanRead(ForkData.FromBytes(volume))); // a bare FAT volume has no table
    }

    [Theory]
    [InlineData(0x0000, 0x0000, null)]
    [InlineData(0x2E43, 0x6A83, "2003-02-03 13:20:06")] // (2003−1980)<<9 | 2<<5 | 3; 13<<11 | 20<<5 | 3
    [InlineData(0x2E5F, 0x0000, null)] // day 31 of February
    [InlineData(0x9021, 0x0000, "1924-01-01 00:00:00")] // 2052: years from 2032 read 128 earlier, as File Exchange reads them
    public void DOS_date_fields_decode(ushort date, ushort time, string? expected)
    {
        var decoded = DosTime.FromFields(date, time);
        Assert.Equal(expected is null ? null : DateTime.Parse(expected, CultureInfo.InvariantCulture), decoded?.ToDateTime());
    }

    // What OS 9 with File Exchange 3.0.2 reported for every file on a floppy it wrote (the user's SheepShaver harness:
    // run12\log.txt, last "F list" block), compared with our read of the same floppy. Two known differences: the
    // Finder updated the Desktop file after the listing, and File Exchange shows a placeholder record's type through
    // its extension map (display only; the disk keeps TEXT/dosa).
    [Fact]
    public void Floppy_written_by_File_Exchange_reads_as_OS_9_listed_it()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        var folder = string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) ? null
            : Directory.EnumerateFiles(corpus, "fxtest.img", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(d => d!.EndsWith("run12", StringComparison.Ordinal) && File.Exists(Path.Combine(d, "log.txt")));
        if (folder is null) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding run12/fxtest.img and run12/log.txt to run this.");

        var lines = File.ReadAllText(Path.Combine(folder, "log.txt")).Split('\r', '\n');
        var lastList = Array.FindLastIndex(lines, l => l.StartsWith("F list FXTEST: end", StringComparison.Ordinal));
        var first = Array.FindLastIndex(lines, lastList, l => l.StartsWith("F list FXTEST:#1 ", StringComparison.Ordinal));
        var pattern = new Regex(@"FILE '(?<name>.*)' num \d+ attrib \S+ type '(?<type>.{4})' creator '(?<creator>.{4})' flags (?<flags>[0-9A-F]{4}) .* cr (?<cr>[0-9A-F]{8}) md (?<md>[0-9A-F]{8}) .* data (?<data>\d+) rsrc (?<rsrc>\d+)");
        var listed = lines[first..lastList].Select(l => pattern.Match(l)).Where(m => m.Success).ToList();
        Assert.NotEmpty(listed);

        var (files, diagnostics) = Read(File.ReadAllBytes(Path.Combine(folder, "fxtest.img")));
        Assert.Empty(diagnostics);
        var root = files.Where(f => f.FolderPath.Count == 0).ToList();
        foreach (var m in listed)
        {
            var name = m.Groups["name"].Value;
            // The harness prints names up to a NUL; a garbage record name keeps its bytes past one.
            var file = root.Single(f => f.Name.ToMacRoman().Split('\0')[0] == name);
            var what = $"\"{name}\"";
            Assert.True(ulong.Parse(m.Groups["data"].Value) == (ulong)file.DataFork.Length, what);
            if (name == "Desktop") continue; // changed by the Finder after the listing
            Assert.True(ulong.Parse(m.Groups["rsrc"].Value) == (ulong)file.ResourceFork.Length, what);
            var type = (file.FinderInfo.Type.ToString(), file.FinderInfo.Creator.ToString());
            if (type != ("TEXT", "dosa")) Assert.Equal((m.Groups["type"].Value, m.Groups["creator"].Value), type);
            Assert.Equal(Convert.ToUInt16(m.Groups["flags"].Value, 16), (ushort)file.FinderInfo.Flags);
            Assert.Equal(Convert.ToUInt32(m.Groups["cr"].Value, 16), file.Created?.Seconds ?? 0);
            Assert.Equal(Convert.ToUInt32(m.Groups["md"].Value, 16), file.Modified?.Seconds ?? 0);
        }
    }
}
