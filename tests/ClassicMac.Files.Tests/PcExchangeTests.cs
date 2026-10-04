using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class PcExchangeTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-pcx-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A 92-byte FINDER.DAT record: Pascal name (garbage after it), FInfo/FXInfo, dates, file number, 8.3 key.
    private static byte[] Record(string macName, string type, string creator, uint created, uint modified, uint fileNumber, string dosKey)
    {
        var record = new byte[92];
        Array.Fill(record, (byte)0xA9, 1, 31); // garbage after the name, as real records have
        record[0] = (byte)macName.Length;
        MacRoman.Encode(macName).CopyTo(record, 1);
        FinderInfo(type, creator).CopyTo(record, 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x40), created);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x44), modified);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x4C), fileNumber);
        Encoding.ASCII.GetBytes(dosKey).CopyTo(record, 0x50);
        record[0x5B] = 1;
        return record;
    }

    [Fact]
    public void Records_decode()
    {
        byte[] data = [.. Record("Fantasoft news.eml", "TEXT", "MOSS", 0xBAED36D4, 0xBAED36D5, 0x7FFFF401, "FANTAS~1EML"), .. new byte[92]];

        var record = Assert.Single(PcExchange.ReadFinderData(data)); // the second record is free

        Assert.Equal("Fantasoft news.eml", record.MacName.ToMacRoman());
        Assert.Equal(FourCC.FromString("TEXT"), record.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("MOSS"), record.FinderInfo.Creator);
        Assert.Equal(new DateTime(2003, 5, 18, 13, 10, 44), record.Created!.Value.ToDateTime());
        Assert.Equal(0x7FFFF401u, record.FileNumber);
        Assert.Equal("FANTAS~1EML", record.DosName);
    }

    [Fact]
    public void Records_never_straddle_a_cluster()
    {
        // 512-byte clusters hold 5 records; the 52 bytes after them are junk, and the 6th record starts at 512.
        var data = new byte[1024];
        for (var i = 0; i < 6; i++)
        {
            var at = i < 5 ? i * 92 : 512;
            Record($"File {i}", "TEXT", "ttxt", 1, 1, (uint)(0x7FFFFFFF - i), $"FILE{i}   TXT").CopyTo(data, at);
        }
        Array.Fill(data, (byte)0x41, 460, 52);

        var records = PcExchange.ReadFinderData(data);

        Assert.Equal(["File 0", "File 1", "File 2", "File 3", "File 4", "File 5"], records.Select(r => r.MacName.ToMacRoman()));
    }

    [Theory]
    [InlineData("FANTAS~1.EML", "FANTAS~1EML")]
    [InlineData("readme", "README     ")]
    [InlineData("A.B", "A       B  ")]
    [InlineData("Fantasoft news.eml", null)]
    [InlineData("toolongname.txt", null)]
    [InlineData("a.b.c", null)]
    public void Host_names_give_8_3_keys(string hostName, string? key)
    {
        Assert.Equal(key, PcExchange.DosKey(hostName));
    }

    [Fact]
    public void A_folder_from_a_DOS_disk_joins_its_companions()
    {
        File.WriteAllText(Path.Combine(folder, "FANTAS~1.EML"), "Subject: news");
        Directory.CreateDirectory(Path.Combine(folder, "RESOURCE.FRK"));
        File.WriteAllBytes(Path.Combine(folder, "RESOURCE.FRK", "FANTAS~1.EML"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "FINDER.DAT"),
            Record("Fantasoft news.eml", "TEXT", "MOSS", 0xBAED36D4, 0xBAED36D4, 0x7FFFF401, "FANTAS~1EML"));

        var host = HostFiles.Read(Path.Combine(folder, "FANTAS~1.EML"));

        Assert.Equal(HostLayout.PcExchange, host.Layout);
        Assert.Equal(2, host.Companions.Count);
        Assert.Equal("Fantasoft news.eml", host.File.Name.ToMacRoman());
        Assert.Equal(FourCC.FromString("MOSS"), host.File.FinderInfo.Creator);
        Assert.Equal([1, 2, 3], host.File.ResourceFork.ToArray());
        // Creation comes from the DOS entry (File Exchange 9.0), not the record.
        Assert.Equal(DosTime.FromLocal(File.GetCreationTime(Path.Combine(folder, "FANTAS~1.EML"))), host.File.Created);
        Assert.True(host.File.Modified!.Value.Seconds >= 0xBAED36D4); // the later of the record's and the host's
    }

    [Theory]
    [InlineData("2003-05-18 13:10:45", "2003-05-18 13:10:44")] // DOS keeps even seconds
    [InlineData("2031-12-31 23:59:58", "2031-12-31 23:59:58")]
    [InlineData("2040-06-01 12:00:00", "1912-06-01 12:00:00")] // years from 2032 wrap back 128
    [InlineData("2078-01-01 00:00:00", "1950-01-01 00:00:00")]
    public void DOS_times_read_as_File_Exchange_reads_them(string dos, string mac)
    {
        Assert.Equal(DateTime.Parse(mac, CultureInfo.InvariantCulture), DosTime.FromLocal(DateTime.Parse(dos, CultureInfo.InvariantCulture))!.Value.ToDateTime());
    }

    [Fact]
    public void Long_host_names_match_the_Mac_name()
    {
        File.WriteAllText(Path.Combine(folder, "Fantasoft news.eml"), "x");
        File.WriteAllBytes(Path.Combine(folder, "finder.dat"), // any case: FAT names
            Record("Fantasoft news.eml", "TEXT", "MOSS", 1, 1, 0x7FFFF401, "FANTAS~1EML"));

        var host = HostFiles.Read(Path.Combine(folder, "Fantasoft news.eml"));

        Assert.Equal(HostLayout.PcExchange, host.Layout);
        Assert.Equal(FourCC.FromString("MOSS"), host.File.FinderInfo.Creator);
    }

    // A fork in RESOURCE.FRK but no FINDER.DAT record: File Exchange shows TEXT/dosa (the fork only gives lengths).
    [Fact]
    public void A_fork_without_a_record_gets_the_placeholder_Finder_info()
    {
        File.WriteAllText(Path.Combine(folder, "LETTER.TXT"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "RESOURCE.FRK"));
        File.WriteAllBytes(Path.Combine(folder, "RESOURCE.FRK", "LETTER.TXT"), [1, 2, 3]);

        var host = HostFiles.Read(Path.Combine(folder, "LETTER.TXT"));

        Assert.Equal(HostLayout.PcExchange, host.Layout);
        Assert.Equal(("TEXT", "dosa"), (host.File.FinderInfo.Type.ToString(), host.File.FinderInfo.Creator.ToString()));
        Assert.Equal(2, host.File.FinderInfo.Extended.Span[15]); // put-away folder 2, the root
        Assert.Equal([1, 2, 3], host.File.ResourceFork.ToArray());
    }

    // A Mac folder that holds a FINDER.DAT file (copied from a DOS disk), unpacked with per-file companions: each
    // file's own .finf/.rsrc or ._ file describes it, not the folder's FINDER.DAT. (RealmzClassicHD.img's Mails.)
    [Theory]
    [InlineData(HostLayout.BasiliskII)]
    [InlineData(HostLayout.AppleDouble)]
    public void Per_file_companions_win_over_a_FINDER_DAT_in_the_folder(HostLayout layout)
    {
        File.WriteAllBytes(Path.Combine(folder, "FINDER.DAT"),
            Record("Fantasoft news.eml", "TEXT", "MOSS", 1, 1, 0x7FFFF401, "FANTAS~1EML"));
        Directory.CreateDirectory(Path.Combine(folder, "RESOURCE.FRK"));
        File.WriteAllBytes(Path.Combine(folder, "RESOURCE.FRK", "Fantasoft news.eml"), [9, 9]);
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("Fantasoft news.eml"),
            DataFork = ForkData.FromBytes(new byte[] { 1 }),
            ResourceFork = ForkData.FromBytes(new byte[] { 1, 2, 3 }),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("ttro"), Creator = FourCC.FromString("ttxt") },
        };
        HostFiles.Write(file, folder, HostWriteOptions.Default with { Layout = layout });

        var host = HostFiles.Read(Path.Combine(folder, "Fantasoft news.eml"));

        Assert.Equal(layout, host.Layout);
        Assert.Equal(("ttro", "ttxt"), (host.File.FinderInfo.Type.ToString(), host.File.FinderInfo.Creator.ToString()));
        Assert.Equal([1, 2, 3], host.File.ResourceFork.ToArray());
    }

    [Fact]
    public void Files_without_a_record_or_fork_stay_plain()
    {
        File.WriteAllText(Path.Combine(folder, "OTHER.TXT"), "x");
        File.WriteAllBytes(Path.Combine(folder, "FINDER.DAT"),
            Record("Fantasoft news.eml", "TEXT", "MOSS", 1, 1, 0x7FFFF401, "FANTAS~1EML"));

        Assert.Equal(HostLayout.Plain, HostFiles.Read(Path.Combine(folder, "OTHER.TXT")).Layout);
        Assert.Equal(HostLayout.Plain, HostFiles.Read(Path.Combine(folder, "FINDER.DAT")).Layout);
    }
}
