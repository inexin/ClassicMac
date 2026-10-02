using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Fat;
using ClassicMac.Tests;
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

    // File Exchange shows an 8.3 name's bytes as they are stored: each OEM byte as the Mac Roman byte of the same value
    // (CP437 $82 é shows as Mac Roman $82 Ç), no case change, each part cut at its first byte of $20 or less.
    [Fact]
    public void Short_names_are_shown_byte_for_byte()
    {
        var builder = new FatBuilder(12);
        builder.File("\u0082TE.TXT", "\u0082te.txt", Bytes(3, 1));

        var file = Assert.Single(Read(builder.Build()).Files);

        Assert.Equal([0x82, (byte)'t', (byte)'e', (byte)'.', (byte)'t', (byte)'x', (byte)'t'], file.Name.Bytes.ToArray());

        var spaced = new FatBuilder(12);
        spaced.File("X.A B", "X.A B", Bytes(3, 1)); // an extension keeps its inner space; only trailing ones go
        Assert.Equal("X.A B", Assert.Single(Read(spaced.Build()).Files).Name.ToMacRoman());
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
        for (var i = 0; i < 10; i++)
        {
            builder.File($"F{i}", $"F{i}", Bytes(3, i + 1));
        }

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

    // As File Exchange converts them through Mac OS 9's Date2Secs; the impossible dates are the ones checked in
    // SheepShaver (they roll over).
    [Theory]
    [InlineData(1999, 0, 1, "1998-12-02")]
    [InlineData(1999, 0, 0, "1998-12-01")]
    [InlineData(1999, 1, 0, "1998-12-31")]
    [InlineData(1999, 3, 0, "1999-02-28")]
    [InlineData(1999, 2, 29, "1999-03-01")]
    [InlineData(1999, 4, 31, "1999-05-01")]
    [InlineData(1999, 13, 1, "2000-01-01")]
    [InlineData(1999, 14, 1, "2000-02-01")]
    [InlineData(1999, 15, 1, "2000-03-02")]
    [InlineData(1999, 15, 31, "2000-04-01")]
    [InlineData(2000, 2, 29, "2000-02-29")]
    [InlineData(2003, 2, 3, "2003-02-03")]
    [InlineData(2052, 1, 1, "1924-01-01")] // years from 2032 read 128 earlier
    [InlineData(2032, 1, 0, "1947-04-29 17:31:44")] // 1904-01-00 wraps in 16-bit day arithmetic
    public void DOS_dates_convert_as_Date2Secs_does(int year, int month, int day, string expected)
    {
        var date = (ushort)((year - 1980) << 9 | month << 5 | day);
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), DosTime.FromFields(date, 0)!.Value.ToDateTime());
    }

    [Fact]
    public void DOS_times_and_zero_dates_convert_as_Date2Secs_does()
    {
        var date = (ushort)((1999 - 1980) << 9 | 1 << 5 | 1);
        Assert.Equal(new DateTime(1999, 1, 2, 8, 4, 2), DosTime.FromFields(date, (ushort)(31 << 11 | 63 << 5 | 31))!.Value.ToDateTime());
        Assert.Equal(new DateTime(2003, 2, 3, 13, 20, 6), DosTime.FromFields(0x2E43, 0x6A83)!.Value.ToDateTime());
        Assert.Equal(new DateTime(1979, 12, 1), DosTime.FromFields(0, 0)!.Value.ToDateTime()); // a zero modification date
        Assert.Null(DosTime.FromFields(0, 0x0800, zeroIsNull: true)); // a zero creation date: File Exchange shows "now"
    }

    // Decomposed (NFD) names are not composed: an accent takes the low-byte path (checked in SheepShaver).
    [Theory]
    [InlineData("Cafe\u0301.txt", new byte[] { 0x43, 0x61, 0x66, 0x65, 0x01, 0x2E, 0x74, 0x78, 0x74 })]
    [InlineData("Caf\u00E9.txt", new byte[] { 0x43, 0x61, 0x66, 0x8E, 0x2E, 0x74, 0x78, 0x74 })]
    public void Decomposed_long_names_are_not_composed(string longName, byte[] expected) =>
        Assert.Equal(expected, FatNames.FromLongName(longName).Bytes.ToArray());

    [Fact]
    public void Long_shortened_names_hash_the_stored_UTF16()
    {
        var composed = "Cr\u00E8me br\u00FBl\u00E9e with a very long name.txt";
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        Assert.Equal("Cr\u00E8me br\u00FBl\u00E9e with a ver#F28.txt", FatNames.FromLongName(composed).ToMacRoman());
        Assert.Equal([.. "Cre"u8, 0x00, .. "me bru"u8, 0x02, .. "le"u8, 0x01, .. "e with a #366.txt"u8],
            FatNames.FromLongName(decomposed).Bytes.ToArray());
    }

    [Theory]
    [InlineData("readme.txt", "readme.txt")]
    [InlineData("a:b c.txt", "a:b c.txt")] // ':' kept when the name converts
    [InlineData("R\u00E9sum\u00E9.doc", "R\u00E9sum\u00E9.doc")] // precomposed
    [InlineData("漢字 kanji.txt", "\"W kanji.txt")] // no Mac Roman: each unit's low byte
    [InlineData("漢:.txt", "\"_.txt")] // ... and ':' becomes '_'
    [InlineData("This is a very long Windows file name.txt", "This is a very long Win#7C7.txt")] // harness-confirmed
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcde", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcde")] // 31: fits
    public void Long_names_become_Mac_names_as_File_Exchange_shows_them(string longName, string macName)
    {
        Assert.Equal(macName, FatNames.FromLongName(longName).ToMacRoman());
    }

    [Fact]
    public void Shortened_names_keep_their_extension_and_a_CRC()
    {
        var name = FatNames.FromLongName("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef.jpeg").ToMacRoman();
        var crc = FatNames.Crc("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef.jpeg") & 0xFFF;
        Assert.Equal($"ABCDEFGHIJKLMNOPQRSTUV#{crc:X3}.jpeg", name); // 22 + 4 + 5 = 31
        // No '.' in the last six characters: no extension.
        var plain = FatNames.FromLongName("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh.eleven").ToMacRoman();
        Assert.Equal(31, plain.Length);
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZa#", plain[..28]);
    }

    // Internet Config entries the harness showed File Exchange using.
    private static readonly ExtensionMap HarnessMap = new(
    [
        new(".txt", (FourCC.FromString("TEXT"), FourCC.FromString("ttxt"))),
        new(".bin", (FourCC.FromString("BINA"), FourCC.FromString("SITx"))),
        new(".doc", (FourCC.FromString("WDBN"), FourCC.FromString("MSWD"))),
        new(".jpg", (FourCC.FromString("JPEG"), FourCC.FromString("ogle"))),
        new(".gif", (FourCC.FromString("GIFf"), FourCC.FromString("ogle"))),
    ]);

    [Fact]
    public void An_extension_map_applies_to_placeholders_only_and_only_when_given()
    {
        var builder = new FatBuilder(12);
        builder.File("PIC.JPG", "PIC.JPG", [1]);
        builder.File("NOTE.TXT", "NOTE.TXT", [1]);
        builder.File("KEEP.TXT", "KEEP.TXT", [1]);
        builder.File("FINDER.DAT", "FINDER.DAT", [
            .. Record("pic.jpg", "TEXT", "dosa", 0, 0, "PIC     JPG"),
            .. Record("keep.txt", "APPL", "abcd", 0, 0, "KEEP    TXT"),
            .. Record("stray", "XXXX", "XXXX", 0, 0, "\0\0\0\0\0\0\0\0\0\0\0"), // free: no 8.3 name
        ]);
        var image = builder.Build();
        string[] Types(ContainerReadOptions options) => FatReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext(options))
            .OrderBy(f => f.Name.ToMacRoman(), StringComparer.Ordinal).Select(f => $"{f.FinderInfo.Type}/{f.FinderInfo.Creator}").ToArray();

        // NOTE.TXT (no record), keep.txt, pic.jpg.
        Assert.Equal(["TEXT/dosa", "APPL/abcd", "TEXT/dosa"], Types(ContainerReadOptions.Default));
        Assert.Equal(["TEXT/ttxt", "APPL/abcd", "JPEG/ogle"], Types(ContainerReadOptions.Default with { ExtensionMap = HarnessMap }));
    }

    [Fact]
    public void The_longest_ending_wins_then_the_earlier_entry()
    {
        var map = new ExtensionMap(
        [
            new(".gz", (FourCC.FromString("Gzip"), FourCC.FromString("Gzip"))),
            new(".tar.gz", (FourCC.FromString("TARF"), FourCC.FromString("SITx"))),
            new(".GZ", (FourCC.FromString("XXXX"), FourCC.FromString("XXXX"))),
        ]);
        Assert.Equal(FourCC.FromString("TARF"), map.Apply(PcExchange.Placeholder, "a.TAR.GZ").Type);
        Assert.Equal(FourCC.FromString("Gzip"), map.Apply(PcExchange.Placeholder, "a.gz").Type);
    }

    // What OS 9 with File Exchange 9.0 listed for every file on floppies it wrote (the user's SheepShaver harness:
    // run12/fxtest.img and run13/fxtest13.img, each with the log.txt beside it), compared with our read of the same
    // floppies. Our read uses the file types the harness showed for the extension map, since OS 9 lists mapped types.
    // One known difference: the Finder updated the Desktop file after the listing.
    [Fact]
    public void Floppies_written_by_File_Exchange_read_as_OS_9_listed_them()
    {
        var images = !CorpusFolders.Any ? []
            : CorpusFolders.EnumerateFiles("fxtest*.img", SearchOption.AllDirectories)
                .Where(f => !f.Contains("_orig", StringComparison.Ordinal)
                    && !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.') // Basilisk II .rsrc/.finf companions
                    && File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "log.txt")))
                .ToList();
        if (images.Count == 0)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's fxtest*.img and log.txt to run this.");
        }

        var pattern = new Regex(@"^F list [^:]+:(?<folder>[^#]*)#\d+ FILE '(?<name>.*)' num \d+ attrib \S+ type '(?<type>.{4})' creator '(?<creator>.{4})' flags (?<flags>[0-9A-F]{4}) .* cr (?<cr>[0-9A-F]{8}) md (?<md>[0-9A-F]{8}) .* data (?<data>\d+) rsrc (?<rsrc>\d+)");
        var compared = 0;
        foreach (var image in images)
        {
            // The log is Mac Roman; the last listing of each file wins.
            var log = MacRoman.Decode(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(image)!, "log.txt")));
            var listed = new Dictionary<string, Match>();
            foreach (var line in log.Split('\r', '\n'))
            {
                if (pattern.Match(line) is { Success: true } m)
                {
                    listed[(m.Groups["folder"].Value.Length > 0 ? m.Groups["folder"].Value + ":" : "") + m.Groups["name"].Value] = m;
                }
            }

            var diagnostics = new List<Diagnostic>();
            var files = FatReader.Instance.Read(ForkData.FromBytes(File.ReadAllBytes(image)),
                new ContainerContext(ContainerReadOptions.Default with { ExtensionMap = HarnessMap }, diagnostics));
            Assert.All(diagnostics, d => Assert.Equal("fat.suspect-name", d.Code));
            foreach (var (path, m) in listed)
            {
                // The harness prints names up to a NUL; a garbage record name keeps its bytes past one.
                var file = files.SingleOrDefault(f => string.Join(':', [.. f.FolderPath.Select(p => p.ToMacRoman()), f.Name.ToMacRoman()]).Split('\0')[0] == path)
                    ?? throw new Xunit.Sdk.XunitException($"{Path.GetFileName(image)}: \"{path}\" is not among {string.Join(", ", files.Select(f => $"\"{f.MacPath}\""))}.");
                var what = $"{Path.GetFileName(image)}: \"{path}\"";
                Assert.True(ulong.Parse(m.Groups["data"].Value) == (ulong)file.DataFork.Length, what);
                compared++;
                if (path == "Desktop")
                {
                    continue; // changed by the Finder after the listing
                }

                Assert.True(ulong.Parse(m.Groups["rsrc"].Value) == (ulong)file.ResourceFork.Length, what);
                Assert.Equal((m.Groups["type"].Value, m.Groups["creator"].Value), (file.FinderInfo.Type.ToString(), file.FinderInfo.Creator.ToString()));
                Assert.Equal(Convert.ToUInt16(m.Groups["flags"].Value, 16), (ushort)file.FinderInfo.Flags);
                Assert.Equal(Convert.ToUInt32(m.Groups["cr"].Value, 16), file.Created?.Seconds ?? 0);
                Assert.Equal(Convert.ToUInt32(m.Groups["md"].Value, 16), file.Modified?.Seconds ?? 0);
            }
        }
        TestContext.Current.SendDiagnosticMessage($"{images.Count} floppies, {compared} files compared.");
    }
}
