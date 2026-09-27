using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Tests;

public class HostWriteTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-write-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static MacFile Sample(string name, byte[]? resource = null) => new()
    {
        Name = new MacString(MacRoman.Encode(name)),
        FinderInfo = new FinderInfo
        {
            Type = FourCC.FromString("APPL"),
            Creator = FourCC.FromString("RLMZ"),
            Flags = FinderFlags.HasBundle | FinderFlags.HasBeenInited,
            Location = new MacPoint(40, 80),
            Folder = -3,
            Extended = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        },
        Created = MacDate.FromDateTime(new DateTime(1997, 3, 4, 5, 6, 8)),
        Modified = MacDate.FromDateTime(new DateTime(1998, 7, 8, 9, 10, 12)),
        DataFork = ForkData.FromBytes("data fork"u8.ToArray()),
        ResourceFork = ForkData.FromBytes(resource ?? Enumerable.Range(0, 700).Select(i => (byte)i).ToArray()),
    };

    [Theory]
    [InlineData("Read Me", "Read Me")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("100% done?", "100%25 done%3F")]
    [InlineData("Icon\r", "Icon%0D")]
    [InlineData("trail.", "trail%2E")]
    [InlineData("space ", "space%20")]
    [InlineData("COM1", "COM%31")]
    [InlineData("con.txt", "co%6E.txt")]
    [InlineData("Résumé ƒ", "Résumé ƒ")]
    [InlineData("Scenario:Data", "Scenario%3AData")]
    public void Mac_names_become_safe_host_names(string mac, string host)
    {
        Assert.Equal(host, HostNames.ToHostName(new MacString(MacRoman.Encode(mac))));
    }

    [Fact]
    public void Long_names_are_cut_before_the_extension()
    {
        var name = HostNames.ToHostName(MacString.FromMacRoman(new string('a', 40) + "?.sit"), maxLength: 20);
        Assert.Equal("aaaaaaaaaaaaaaaa.sit", name);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaa", HostNames.ToHostName(MacString.FromMacRoman(new string('a', 30)), maxLength: 20));
    }

    [Fact]
    public void Colliding_names_get_numbers()
    {
        var taken = new HashSet<string>();
        Assert.Equal("DUP.TXT", HostNames.MakeUnique("DUP.TXT", taken));
        Assert.Equal("DUP ~2.TXT", HostNames.MakeUnique("DUP.TXT", taken));
        Assert.Equal("dup ~3.txt", HostNames.MakeUnique("dup.txt", taken)); // case differs only: still taken
    }

    [Fact]
    public void Finder_info_round_trips()
    {
        var info = Sample("x").FinderInfo;
        var read = FinderInfo.Read(info.ToArray());
        Assert.Equal(info.ToArray(), read.ToArray());
        Assert.Equal(info with { Extended = read.Extended }, read);
    }

    [Theory]
    [InlineData(HostLayout.AppleDouble)]
    [InlineData(HostLayout.BasiliskII)]
    public void Written_files_read_back_the_same(HostLayout layout)
    {
        foreach (var name in new[] { "Read Me", "a/b: 100%", "Icon\r", "Résumé ƒ™", "COM1", "trail." })
        {
            var file = Sample(name);
            var paths = HostFiles.Write(file, folder, HostWriteOptions.Default with { Layout = layout });

            var host = HostFiles.Read(paths[0]);

            Assert.Equal(layout, host.Layout);
            Assert.Equal(file.Name, host.File.Name);
            Assert.Equal(file.FinderInfo.ToArray(), host.File.FinderInfo.ToArray());
            Assert.Equal(file.DataFork.ToArray(), host.File.DataFork.ToArray());
            Assert.Equal(file.ResourceFork.ToArray(), host.File.ResourceFork.ToArray());
            Assert.Equal(file.Modified!.Value.ToDateTime(), File.GetLastWriteTime(paths[0]));
            if (layout == HostLayout.AppleDouble)
                Assert.Equal((file.Created, file.Modified), (host.File.Created, host.File.Modified));
        }
    }

    [Fact]
    public void Basilisk_folders_get_no_resource_file_for_an_empty_fork()
    {
        var paths = HostFiles.Write(Sample("Plain", resource: []), folder, HostWriteOptions.Default with { Layout = HostLayout.BasiliskII });

        Assert.Equal([Path.Combine(folder, "Plain"), Path.Combine(folder, ".finf", "Plain")], paths);
        Assert.False(File.Exists(Path.Combine(folder, ".rsrc", "Plain")));
    }

    [Fact]
    public void Existing_files_are_kept_unless_overwriting()
    {
        HostFiles.Write(Sample("Twice"), folder);
        Assert.Throws<IOException>(() => HostFiles.Write(Sample("Twice"), folder));
        HostFiles.Write(Sample("Twice"), folder, HostWriteOptions.Default with { Overwrite = true });
    }

    [Fact]
    public void AppleDouble_headers_read_back()
    {
        var file = Sample("Header");
        var read = AppleSingleReader.AppleDouble.Read(ForkData.FromBytes(AppleDoubleWriter.ToArray(file)), new ContainerContext())[0];

        Assert.Equal(file.Name, read.Name);
        Assert.Equal((file.Created, file.Modified), (read.Created, read.Modified));
        Assert.Equal(file.ResourceFork.ToArray(), read.ResourceFork.ToArray());
    }
}
