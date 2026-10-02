using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;
using Run = ClassicMac.Files.Tests.UdifBuilder.Run;

namespace ClassicMac.Files.Tests;

public class UdifTests
{
    // 1024 bytes ((i * 7 + i / 256) & $FF), bzip2-compressed by Python's bz2 module: the content of a 2-sector run.
    private const string Bzip2Run = "QlpoOTFBWSZTWXem5HgAAAD/////////////////////////////////////////////wAIcAAEmAAmAAJgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkwAEwABMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEmAAmAAJgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFVVNQJgmjABMAAAAAJpppgAAAAAAAAAEwAAAAAAAAAAAAAAAARkwJkxk1PpBwH2gQEHAgQUNgoQ+5Cn4ERIkRMihFT8SLH5H5kXIwRkjRGyOEdI8R8kBIT9CREjJISUkxJyUEpJUSslhLSXEvJgTEmRMyaE1JsTcnBOSdE7J4T0nxPygFBKEUM/Uoh+xRT9yjH8FHKQUkpRSymFNKcU8qBUSpFTKoVUqxVysFZK0VsrhXSvFfLAWEsRYyyFlLMWctBaS1FrLYW0txby4FxLkXMuhdS7F3LwXkvRey+F9L8X8wBgTBGDMIfyYUwxhzEGJMUYsxhjTHGPMgZEyRkzKGVMsZczBmTNGbM4Z0zxnzQGhNEaM0hpTTGnNQak1RqzWGtNca82BsTZGzNobU2xtzcG5N0bs3hvTfG/OAcE4RwziHFOMcc5ByTlHLOYc05xzzoHROkdM6h1TrHXOwdk7R2zuHdO8d88B4TxHjPIeU8x5z0HpP6P7PUes/w9h7T3HvPgfE/0+R8z/j6H1PsQZCH3IU/8XckU4UJB3puR4A";

    // 20 sectors: text in 0–5, zeros in 6–9 (stored as a zero run and as free space), the bzip2 content in 10–11, and
    // more text in 12–19 (ADC and zlib).
    private static byte[] Device()
    {
        var device = new byte[20 * 512];
        for (var i = 0; i < 6 * 512; i++)
        {
            device[i] = (byte)"The quick brown fox jumps over the lazy dog. "[i % 45];
        }

        for (var i = 0; i < 1024; i++)
        {
            device[10 * 512 + i] = (byte)((i * 7 + i / 256) & 0xFF);
        }

        for (var i = 12 * 512; i < device.Length; i++)
        {
            device[i] = (byte)(i % 251);
        }

        return device;
    }

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] image, bool verify = true)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(UdifReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = verify }, diagnostics, MacString.FromMacRoman("Test.dmg"));
        return (UdifReader.Instance.Read(input, context), diagnostics);
    }

    private static readonly (int, Run, byte[]?)[] EveryRun =
    [
        (4, Run.Raw, null), (2, Run.Adc, null), (2, Run.Zero, null), (2, Run.Free, null),
        (2, Run.Bzip2, Convert.FromBase64String(Bzip2Run)), (4, Run.Adc, null), (4, Run.Zlib, null),
    ];

    [Theory]
    [InlineData(false, false)] // Disk Copy 6.5: tables in an embedded resource fork, CRC-32
    [InlineData(false, true)] // "entire device" style: MD5
    [InlineData(true, false)] // Mac OS X: tables in an XML property list
    public void Every_run_type_decodes_and_the_checksums_match(bool propertyList, bool md5)
    {
        var device = Device();

        var (files, diagnostics) = Read(UdifBuilder.Build(device, propertyList, md5, EveryRun));

        Assert.Empty(diagnostics);
        var disk = Assert.Single(files);
        Assert.Equal("Test", disk.Name.ToMacRoman());
        Assert.Equal(device, disk.DataFork.ToArray());
    }

    [Fact]
    public void Damaged_data_fails_its_checksums()
    {
        var image = UdifBuilder.Build(Device(), false, false, EveryRun);
        image[100] ^= 0xFF; // inside the first raw run

        var (_, diagnostics) = Read(image);

        Assert.Equal(3, diagnostics.Count(d => d.Code == "udif.bad-checksum")); // the table, the master and the data
        Assert.Empty(Read(image, verify: false).Diagnostics);
    }

    [Fact]
    public void LZFSE_runs_read_as_zeros_with_an_error()
    {
        var device = Device();
        var (files, diagnostics) = Read(UdifBuilder.Build(device, true, false, (10, Run.Raw, null), (10, Run.Lzfse, new byte[40])), verify: false);

        Assert.Equal("udif.unsupported-run", Assert.Single(diagnostics).Code);
        var disk = files[0].DataFork.ToArray();
        Assert.Equal(device[..(10 * 512)], disk[..(10 * 512)]);
        Assert.All(disk[(10 * 512)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Encrypted_images_are_recognised_and_refused()
    {
        byte[] image = [.. "encrcdsa"u8, .. new byte[1000]];
        var input = ForkData.FromBytes(image);

        Assert.True(UdifReader.Instance.CanRead(input));
        var e = Assert.Throws<InvalidDataException>(() => UdifReader.Instance.Read(input, new ContainerContext()));
        Assert.Contains("encrypted", e.Message, StringComparison.Ordinal);
        Assert.False(UdifReader.Instance.CanRead(ForkData.FromBytes(new byte[2048])));
    }

    // A .dmg of a partitioned disk unwraps through the partition map to the HFS volume and its files.
    [Fact]
    public void Images_unwrap_to_their_volumes()
    {
        var hfs = new HfsBuilder();
        hfs.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var volume = hfs.Build("Disk");
        var device = Fixtures.PartitionMap(("Disk", "Apple_HFS", volume));
        var image = UdifBuilder.Build(device, true, false, (device.Length / 512, Run.Zlib, null));

        var root = ContainerUnwrapper.Default.Unwrap(new MacFile { Name = MacString.FromMacRoman("Disk.dmg"), DataFork = ForkData.FromBytes(image) },
            "host file", new ContainerContext());

        Assert.Equal("Read Me", Assert.Single(root.Leaves()).File.Name.ToMacRoman());
    }

    // Disk Copy 6.5b13's own images of one device (read-only compressed, read-only, read-only "entire device" with MD5
    // checksums), which decode to that device exactly with every checksum matching.
    [Fact]
    public void Disk_Copy_6_5_images_decode_to_their_device()
    {
        var reference = !CorpusFolders.Any ? null
            : CorpusFolders.EnumerateFiles("dev800_ref.bin", SearchOption.AllDirectories).FirstOrDefault();
        if (reference is null)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to folders holding the harness's early UDIF samples to run this.");
        }

        var device = File.ReadAllBytes(reference);
        var images = CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is "uco" or "uro" or "ued" && !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.'))
            .ToList();
        Assert.NotEmpty(images);
        foreach (var path in images)
        {
            var (files, diagnostics) = Read(File.ReadAllBytes(path));
            Assert.Empty(diagnostics);
            Assert.Equal(device, files[0].DataFork.ToArray());
        }
    }
}
