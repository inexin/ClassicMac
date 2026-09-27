using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Iso;
using static ClassicMac.Files.Tests.IsoBuilder;

namespace ClassicMac.Files.Tests;

public class IsoTests
{
    private static byte[] Text(string s) => Encoding.ASCII.GetBytes(s);

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] image)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(IsoReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (IsoReader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics)), diagnostics);
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static MacFile Named(IReadOnlyList<MacFile> files, string name) => files.Single(f => f.Name.ToMacRoman() == name);

    [Fact]
    public void Names_are_cut_and_translated_as_the_Mac_shows_them()
    {
        var builder = new IsoBuilder();
        foreach (var name in new[] { "README.TXT;1", "NOEXT.;1", "LONGNAME12.;1", "mixed.Case;1", "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456;1" })
            builder.Add("", new Rec(name, Text("x")));
        builder.Folder("SUB");
        builder.Add("SUB", new Rec("INSIDE.TXT;1", Text("inside")));

        var (files, diagnostics) = Read(builder.Build());

        Assert.Empty(diagnostics);
        // 31 bytes are cut before ";1" is stripped: a 35-byte name keeps 31.
        Assert.Equal(["README.TXT", "NOEXT", "LONGNAME12.", "mixed.Case", "ABCDEFGHIJKLMNOPQRSTUVWXYZ01234", "SUB:INSIDE.TXT"],
            files.Select(f => f.MacPath));
        Assert.Equal(Text("inside"), files[^1].DataFork.ToArray());
    }

    [Fact]
    public void A_name_over_37_bytes_or_an_empty_file_ends_the_listing()
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("FIRST.TXT;1", Text("x")));
        builder.Add("", new Rec("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789AB;1", Text("x")));
        builder.Add("", new Rec("LATER.TXT;1", Text("x")));

        Assert.Equal(["FIRST.TXT"], Read(builder.Build()).Files.Select(f => f.Name.ToMacRoman()));
    }

    [Fact]
    public void Apple_system_use_fields_give_type_creator_and_masked_flags()
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("PLAIN.TXT;1", Text("a")));
        builder.Add("", new Rec("AA.BIN;1", Text("a"), SystemUse: AA("APPL", "ttxt", 0x6500))); // invisible, custom icon dropped
        builder.Add("", new Rec("BA3.BIN;1", Text("a"), SystemUse: BA(3, "APPL", "abcd", 0xFFFF)));
        builder.Add("", new Rec("BA6.BIN;1", Text("a"), SystemUse: BA(6, "PICT", "ttxt", 0x5000)));
        builder.Add("", new Rec("HIDDEN.TXT;1", Text("a"), Flags: 1));

        var (files, _) = Read(builder.Build());

        string Info(string name) { var f = Named(files, name).FinderInfo; return $"{f.Type}/{f.Creator} {(ushort)f.Flags:X4}"; }
        Assert.Equal("TEXT/hscd 0100", Info("PLAIN.TXT"));
        Assert.Equal("APPL/ttxt 2100", Info("AA.BIN"));
        Assert.Equal("APPL/abcd 2100", Info("BA3.BIN"));
        Assert.Equal("PICT/ttxt 1100", Info("BA6.BIN"));
        Assert.Equal("TEXT/hscd 4100", Info("HIDDEN.TXT"));
        // Icons in a six-column grid of 64-pixel cells, by listing order.
        Assert.Equal(new MacPoint(0, 256), Named(files, "HIDDEN.TXT").FinderInfo.Location);
    }

    [Fact]
    public void Associated_files_are_resource_forks_and_their_Finder_info_wins()
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("PAIR.BIN;1", Text("RSRC"), Flags: 4, SystemUse: AA("SIT!", "SIT!", 0)));
        builder.Add("", new Rec("PAIR.BIN;1", Text("DATA-FORK"), SystemUse: AA("XXXX", "XXXX", 0)));
        builder.Add("", new Rec("ONLYRSRC;1", Text("R"), Flags: 4));
        builder.Add("", new Rec("WRONG.BIN;1", Text("DATA")));
        builder.Add("", new Rec("WRONG.BIN;1", Text("RSRC"), Flags: 4)); // wrong order for ISO: two entries

        var (files, _) = Read(builder.Build());

        Assert.Equal(["PAIR.BIN", "ONLYRSRC", "WRONG.BIN", "WRONG.BIN"], files.Select(f => f.Name.ToMacRoman()));
        Assert.Equal(Text("DATA-FORK"), files[0].DataFork.ToArray());
        Assert.Equal(Text("RSRC"), files[0].ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("SIT!"), files[0].FinderInfo.Type);
        Assert.Equal(0, files[1].DataFork.Length);
        Assert.Equal((4, 0), (files[2].DataFork.Length, files[2].ResourceFork.Length));
        Assert.Equal((0, 4), (files[3].DataFork.Length, files[3].ResourceFork.Length));
    }

    [Theory]
    [InlineData(new byte[] { 99, 1, 1, 12, 0, 0, 36 }, "1999-01-01 12:00:00")] // GMT offset ignored
    [InlineData(new byte[] { 145, 1, 1, 0, 0, 0, 0 }, "2040-02-06 00:00:00")] // 2045
    [InlineData(new byte[] { 0, 3, 4, 5, 6, 7, 0 }, "1904-03-04 05:06:07")] // 1900
    [InlineData(new byte[] { 99, 13, 32, 2, 2, 1, 0 }, "1999-01-01 02:02:01")] // bad month and day
    public void Dates_read_as_local_time_within_Mac_range(byte[] date, string expected)
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("D.TXT;1", Text("x"), Date: date));
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), Read(builder.Build()).Files[0].Created!.Value.ToDateTime());
    }

    [Fact]
    public void Extended_attributes_and_interleaving_are_skipped()
    {
        var interleaved = Enumerable.Range(0, 3 * 2048).Select(i => (byte)(i / 2048 + 'A')).ToArray();
        var builder = new IsoBuilder();
        builder.Add("", new Rec("XAR.TXT;1", Text("after the XAR"), AttributeBlocks: 1));
        builder.Add("", new Rec("INTRLV.TXT;1", interleaved, Unit: 1, Gap: 1));

        var (files, diagnostics) = Read(builder.Build());

        Assert.Empty(diagnostics);
        Assert.Equal(Text("after the XAR"), files[0].DataFork.ToArray());
        Assert.Equal(interleaved, files[1].DataFork.ToArray());
    }

    [Fact]
    public void High_Sierra_puts_the_data_record_first()
    {
        var builder = new IsoBuilder(highSierra: true);
        builder.Add("", new Rec("PAIR.BIN;1", Text("DATA")));
        builder.Add("", new Rec("PAIR.BIN;1", Text("RSRC"), Flags: 4));

        var file = Assert.Single(Read(builder.Build()).Files);

        Assert.Equal(Text("DATA"), file.DataFork.ToArray());
        Assert.Equal(Text("RSRC"), file.ResourceFork.ToArray());
    }

    [Fact]
    public void Other_data_is_not_an_ISO_volume()
    {
        Assert.False(IsoReader.Instance.CanRead(ForkData.FromBytes(new byte[40000])));
        var image = new IsoBuilder().Build();
        image[16 * 2048 + 0x84] = 11; // path table sizes differ
        Assert.False(IsoReader.Instance.CanRead(ForkData.FromBytes(image)));
    }

    [Fact]
    public void ISO_volumes_nest_through_the_unwrapper()
    {
        var builder = new IsoBuilder();
        builder.Add("", new Rec("README.TXT;1", Text("x")));
        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("cd.iso"), DataFork = ForkData.FromBytes(builder.Build()) },
            "host file", new ContainerContext());
        Assert.Equal("ISO 9660 volume", Assert.Single(root.Children).Format);
    }

    // What OS 9's ISO 9660 File Access listed for the user's test disc (SheepShaver harness run13b: iso_tests.iso and
    // log.txt), compared with our read: names, order, type, creator, flags, icon positions, dates, fork sizes, and the
    // bytes the harness read back.
    [Fact]
    public void Test_disc_reads_as_OS_9_listed_it()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        var folder = string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) ? null
            : Directory.EnumerateFiles(corpus, "iso_tests.iso", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(d => !Path.GetFileName(d!).StartsWith('.') && File.Exists(Path.Combine(d!, "log.txt"))
                    && File.ReadAllText(Path.Combine(d!, "log.txt")).Contains(":#1 FILE", StringComparison.Ordinal));
        if (folder is null) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's iso_tests.iso and its log.txt to run this.");

        var log = MacRoman.Decode(File.ReadAllBytes(Path.Combine(folder, "log.txt"))).Split('\r', '\n');
        // The emulator may still hold the disc open; any copy of it will do.
        var disc = Directory.EnumerateFiles(corpus!, "iso_tests.iso", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetDirectoryName(f) == folder ? 0 : 1).Select(TryRead).FirstOrDefault(b => b is not null);
        if (disc is null) Assert.Skip("The harness's iso_tests.iso is in use.");
        var (files, diagnostics) = Read(disc);
        Assert.Empty(diagnostics);

        var pattern = new Regex(@"^F list =[^:]+:(?<folder>[^#]*)#\d+ FILE '(?<name>.*)' num \d+ attrib \S+ type '(?<type>.{4})' creator '(?<creator>.{4})' flags (?<flags>[0-9A-F]{4}) loc (?<h>-?\d+),(?<v>-?\d+) cr (?<cr>[0-9A-F]{8}) md (?<md>[0-9A-F]{8}) .* data (?<data>\d+) rsrc (?<rsrc>\d+)");
        var listed = log.Select(l => pattern.Match(l)).Where(m => m.Success).ToList();
        Assert.Equal(listed.Count, files.Count);
        foreach (var (m, file) in listed.Zip(files))
        {
            var path = (m.Groups["folder"].Value.Length > 0 ? m.Groups["folder"].Value + ":" : "") + m.Groups["name"].Value;
            Assert.Equal(path, string.Join(':', [.. file.FolderPath.Select(p => p.ToMacRoman()), file.Name.ToMacRoman()]));
            Assert.Equal(
                (m.Groups["type"].Value, m.Groups["creator"].Value, m.Groups["flags"].Value, short.Parse(m.Groups["v"].Value), short.Parse(m.Groups["h"].Value)),
                (file.FinderInfo.Type.ToString(), file.FinderInfo.Creator.ToString(), ((ushort)file.FinderInfo.Flags).ToString("X4"), file.FinderInfo.Location.V, file.FinderInfo.Location.H));
            Assert.Equal((Convert.ToUInt32(m.Groups["cr"].Value, 16), Convert.ToUInt32(m.Groups["md"].Value, 16)), (file.Created!.Value.Seconds, file.Modified!.Value.Seconds));
            Assert.Equal((long.Parse(m.Groups["data"].Value), long.Parse(m.Groups["rsrc"].Value)), (file.DataFork.Length, file.ResourceFork.Length));
        }

        // Fork contents: "F data|rsrc =VOL:NAME err 0 eof N [tail] HEX" (the last 32 bytes, or all of a short fork).
        var content = new Regex(@"^F (?<fork>data|rsrc) =[^:]+:(?<name>\S+) err 0 eof (?<eof>\d+) (?:tail )?(?<hex>[0-9A-F]+)$");
        foreach (var m in log.Select(l => content.Match(l)).Where(m => m.Success))
        {
            var file = files.First(f => f.Name.ToMacRoman() == m.Groups["name"].Value && f.FolderPath.Count == 0);
            var bytes = (m.Groups["fork"].Value == "data" ? file.DataFork : file.ResourceFork).ToArray();
            var expected = Convert.FromHexString(m.Groups["hex"].Value);
            Assert.Equal(long.Parse(m.Groups["eof"].Value), bytes.Length);
            Assert.Equal(expected, bytes[^expected.Length..]);
        }
    }
}
