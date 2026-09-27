using ClassicMac.Core;
using ClassicMac.Files.Compression;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;
using Kind = ClassicMac.Files.Tests.NdifBuilder.Kind;

namespace ClassicMac.Files.Tests;

public class NdifTests
{
    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    // An 800-sector HFS volume with a file of varied bytes and room left free (zeros).
    private static byte[] Volume()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Read Me", Bytes(3000, 3), Bytes(300, 7));
        var volume = builder.Build("Test Disk");
        Array.Resize(ref volume, 800 * 512);
        return volume;
    }

    private static MacFile Image(byte[] data, byte[] resource, string name = "Test.img", string type = "rohd") =>
        new()
        {
            Name = MacString.FromMacRoman(name),
            FinderInfo = new FinderInfo { Type = FourCC.FromString(type), Creator = FourCC.FromString("ddsk") },
            DataFork = ForkData.FromBytes(data),
            ResourceFork = ForkData.FromBytes(resource),
        };

    // The bcem 128 of a built image, changed by the caller and put back.
    private static byte[] WithMap(byte[] resource, Action<byte[]> change, params (string Type, short Id, byte[] Data)[] more)
    {
        var map = ClassicMac.Resources.ResourceFork.Read(resource).Resources[0].GetData().ToArray();
        change(map);
        return NdifBuilder.Fork([("bcem", 128, map), .. more]);
    }

    private static (byte[] Disk, List<Diagnostic> Diagnostics) Disk(MacFile image, IEnumerable<MacFile>? siblings = null, ContainerReadOptions? options = null)
    {
        Assert.True(NdifReader.Instance.CanRead(image));
        var diagnostics = new List<Diagnostic>();
        var disk = Assert.Single(NdifReader.Instance.Read(image, new ContainerContext(options, diagnostics, siblings: siblings is null ? null : () => siblings)));
        return (disk.DataFork.ToArray(), diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chunks_decode_to_the_disk(bool compressed)
    {
        var kind = compressed ? Kind.Adc : Kind.Raw;
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (6, Kind.Raw), (200, kind), (94, kind), (500, Kind.Zero));

        var (disk, diagnostics) = Disk(Image(data, resource));

        Assert.Empty(diagnostics);
        Assert.Equal(volume, disk);
    }

    [Fact]
    public void Images_nest_through_the_unwrapper_to_the_volume()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (100, Kind.Adc), (700, Kind.Zero));
        var macBinary = MacBinary(2, "Test.img", data, resource, type: "rohd", creator: "ddsk");
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Test.img.bin"), DataFork = ForkData.FromBytes(macBinary) },
            "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(diagnostics);
        var leaf = Assert.Single(root.Leaves());
        Assert.Equal("Read Me", leaf.File.MacPath);
        Assert.Equal(Bytes(3000, 3), leaf.File.DataFork.ToArray());
        Assert.Equal(["host file", "MacBinary II", "NDIF (Disk Copy 6)", "HFS volume"], Chain(root));
    }

    private static List<string> Chain(ContainerNode node)
    {
        var formats = new List<string>();
        for (; ; node = Assert.Single(node.Children))
        {
            formats.Add(node.Format);
            if (node.Children.Count == 0) return formats;
        }
    }

    [Fact]
    public void Segments_are_found_by_their_part_resource_not_their_names()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (300, Kind.Raw), (500, Kind.Zero));
        var third = data.Length / 3 / 512 * 512;
        var parts = new[] { data[..third], data[third..(2 * third)], data[(2 * third)..] };
        var master = WithMap(resource, map =>
        {
            map[1] = 12; // version 12, segmented
            map[0x57] = 1;
        }, ("bcm#", 128, NdifBuilder.PartResource(1, 3, image: 7)));
        MacFile Part(int n, int image = 7, string? name = null) =>
            Image(parts[n - 1], NdifBuilder.Fork(("bcm#", 128, NdifBuilder.PartResource(n, 3, image))), name ?? $"x{n}", type: "dseg");
        var first = Image(parts[0], master, "Disk 1of3");

        // Renamed, out of order, and among another image's parts: joined by ID and number.
        var (disk, diagnostics) = Disk(first, [Part(3, name: "whatever"), Part(2, image: 8), Part(2, name: "Disk 9of9"), Image([1, 2], [], "Other")]);
        Assert.Empty(diagnostics);
        Assert.Equal(volume, disk);

        (_, diagnostics) = Disk(first, [Part(2)]);
        Assert.Contains(diagnostics, d => d.Code == "ndif.missing-segment");
        Assert.False(NdifReader.Instance.CanRead(Part(2))); // later parts have no map
    }

    [Fact]
    public void The_checksum_is_verified_only_when_asked()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (800, Kind.Adc));
        var right = NdifReader.Crc(ForkData.FromBytes(volume));
        byte[] Crc(uint crc) => WithMap(resource, map => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(0x50), crc));
        var verify = ContainerReadOptions.Default with { VerifyChecksums = true };

        Assert.Empty(Disk(Image(data, Crc(right)), options: verify).Diagnostics);
        Assert.Empty(Disk(Image(data, Crc(right ^ 1))).Diagnostics);
        Assert.Contains(Disk(Image(data, Crc(right ^ 1)), options: verify).Diagnostics, d => d.Code == "ndif.bad-checksum");
    }

    [Fact]
    public void Maps_Disk_Copy_refuses_are_not_read()
    {
        var (data, resource) = NdifBuilder.Build(Volume(), "Test Disk", (800, Kind.Raw));
        var diagnostics = new List<Diagnostic>();
        foreach (var change in new Action<byte[]>[] { m => m[1] = 13, m => m[1] = 9, m => m[0x7F] = 1, m => m[0x57] = 1 })
        {
            var image = Image(data, WithMap(resource, change));
            Assert.True(NdifReader.Instance.CanRead(image));
            Assert.Throws<InvalidDataException>(() => NdifReader.Instance.Read(image, new ContainerContext(diagnostics: diagnostics)));
        }
    }

    [Fact]
    public void Unknown_chunks_read_as_zeros_and_are_reported()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (6, Kind.Raw), (794, Kind.Unknown));

        var (disk, diagnostics) = Disk(Image(data, resource));

        Assert.Contains(diagnostics, d => d.Code == "ndif.unsupported-chunk"); // KenCode
        Assert.Equal(volume[..3072], disk[..3072]);
        Assert.All(disk[3072..], b => Assert.Equal(0, b));

        var odd = WithMap(resource, map => map[0x80 + 12 + 3] = 0x42);
        Assert.Contains(Disk(Image(data, odd)).Diagnostics, d => d.Code == "ndif.unknown-chunk");
    }

    [Fact]
    public void DART_RLE_chunks_decode()
    {
        // Two literal words, then one word repeated 254 times: 512 bytes.
        byte[] stored = [0, 2, 0xAB, 0xCD, 0x12, 0x34, 0xFF, 0x02, 0x55, 0xAA];
        var output = new byte[512];
        Assert.True(DartRle.Decompress(stored, output, out var written));
        Assert.Equal(512, written);
        Assert.Equal([0xAB, 0xCD, 0x12, 0x34, 0x55, 0xAA, 0x55, 0xAA], output[..8]);
        Assert.False(DartRle.Decompress(stored[..6], output, out _));
    }

    [Fact]
    public void Damaged_data_is_reported()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (100, Kind.Adc), (700, Kind.Raw));

        var (_, diagnostics) = Disk(Image(data[..(data.Length - 1000)], resource)); // the raw chunk runs past the end
        Assert.Contains(diagnostics, d => d.Code == "ndif.short");

        var adcOnly = NdifBuilder.Build(volume, "Test Disk", (800, Kind.Adc));
        var corrupt = adcOnly.Data.ToArray();
        corrupt[0] = 0x00; // a match before any output
        (_, diagnostics) = Disk(Image(corrupt, adcOnly.Resource));
        Assert.Contains(diagnostics, d => d.Code == "ndif.bad-chunk");
    }

    [Fact]
    public void Files_without_a_chunk_map_are_not_NDIF()
    {
        Assert.False(NdifReader.Instance.CanRead(Image(Volume(), [])));
        Assert.False(NdifReader.Instance.CanRead(Image(Volume(), NdifBuilder.Fork(("vers", 1, [1, 2, 3])))));
        Assert.False(NdifReader.Instance.CanRead(Image(Volume(), Bytes(1000, 5))));
    }

    [Fact]
    public void ADC_opcodes_decode()
    {
        // "abc" literal; short match: 3 bytes from 3 back; long match: 6 bytes from 1 back (overlapping).
        byte[] input = [0x82, (byte)'a', (byte)'b', (byte)'c', 0b0000_0000, 2, 0x40 | 2, 0, 0];
        var output = new byte[12];

        Assert.Equal(Adc.Result.Done, Adc.Decompress(input, output, out var written));
        Assert.Equal(12, written);
        Assert.Equal("abcabcccccc\0"u8.ToArray()[..11], output[..11]);

        Assert.Equal(Adc.Result.BadDistance, Adc.Decompress([0x80, 1, 0b0000_0000, 5], new byte[8], out _));
        Assert.Equal(Adc.Result.Truncated, Adc.Decompress([0x80, 1, 0x40], new byte[8], out _));
        // A token passing the end is an error, checked before it is written.
        Assert.Equal(Adc.Result.Overrun, Adc.Decompress([0x82, 1, 2, 3], new byte[2], out var partial));
        Assert.Equal(0, partial);
    }

    // Disk Copy 6.3.3's own images (the user's SheepShaver harness, run14/out): each NDIF variant decodes to the
    // sectors of the Read/Write image of the same volume, a plain byte copy.
    [Fact]
    public void Disk_Copy_images_decode_to_their_volumes()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        var folder = string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) ? null
            : Directory.EnumerateFiles(corpus, "S800 RW.img", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(d => !Path.GetFileName(d!).StartsWith('.'));
        if (folder is null) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's run14/out images to run this.");

        // With the checksum verified: Disk Copy's CRC-32 of each disk matches the one it stored.
        byte[] Decode(string path)
        {
            var diagnostics = new List<Diagnostic>();
            var host = HostFiles.Read(path, diagnostics: diagnostics);
            var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = true }, diagnostics, siblings: HostFiles.Siblings(path));
            Assert.True(NdifReader.Instance.CanRead(host.File), path);
            var disk = Assert.Single(NdifReader.Instance.Read(host.File, context)).DataFork.ToArray();
            Assert.Empty(diagnostics);
            return disk;
        }

        var s800 = File.ReadAllBytes(Path.Combine(folder, "S800 RW.img"));
        var s5m = File.ReadAllBytes(Path.Combine(folder, "S5M RW.img"));
        Assert.Equal(s800, Decode(Path.Combine(folder, "S800 RO.img")));
        Assert.Equal(s800, Decode(Path.Combine(folder, "S800 ADC.img")));
        Assert.Equal(s800, Decode(Path.Combine(folder, "S800 ADC.smi")));
        Assert.Equal(s5m, Decode(Path.Combine(folder, "S5M RO.img")));
        Assert.Equal(s5m, Decode(Path.Combine(folder, "S5M ADC.img")));
        Assert.Equal(s5m, Decode(Path.Combine(folder, "seg", "S5M RO seg 1of4")));
        Assert.Equal(Decode(Path.Combine(folder, "S5M RO del.img")), Decode(Path.Combine(folder, "S5M ADC del.img")));
    }

    // The harness's damaged images (run15/ndiftest) and what Disk Copy 6.3.3 did with them (run15/ndif_results.txt):
    // a bad CRC or damaged ADC data is refused only with "Verify checksum" on; a missing part, or a part of another
    // image, is -8821 "Part N is missing"; renamed parts mount.
    [Fact]
    public void Damaged_images_are_reported_as_Disk_Copy_refuses_them()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        var folder = string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) ? null
            : Directory.EnumerateDirectories(corpus, "ndiftest", SearchOption.AllDirectories).FirstOrDefault(d => File.Exists(Path.Combine(d, "control.img")));
        if (folder is null) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's run15/ndiftest images to run this.");

        List<Diagnostic> Read(string relative, bool verify)
        {
            var path = Path.Combine(folder, relative);
            var diagnostics = new List<Diagnostic>();
            var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = verify }, diagnostics, siblings: HostFiles.Siblings(path));
            var disk = Assert.Single(NdifReader.Instance.Read(HostFiles.Read(path).File, context));
            _ = disk.DataFork.ToArray(); // decode every chunk
            return diagnostics;
        }

        Assert.Empty(Read("control.img", verify: true));
        Assert.Empty(Read("badcrc.img", verify: false));
        Assert.Contains(Read("badcrc.img", verify: true), d => d.Code == "ndif.bad-checksum");
        foreach (var damaged in new[] { "adc_flip.img", "adc_garb.img" })
            Assert.Contains(Read(damaged, verify: true), d => d.Code is "ndif.bad-checksum" or "ndif.bad-chunk");
        Assert.Empty(Read(Path.Combine("seg_ok", "S5M RO seg 1of4"), verify: true));
        Assert.Empty(Read(Path.Combine("seg_renamed", "Apple one"), verify: true));
        Assert.Contains(Read(Path.Combine("seg_missing3", "S5M RO seg 1of4"), verify: false),
            d => d.Code == "ndif.missing-segment" && d.Message.StartsWith("Part 3 of 4", StringComparison.Ordinal));
        Assert.Contains(Read(Path.Combine("seg_foreign2", "S5M RO seg 1of4"), verify: false),
            d => d.Code == "ndif.missing-segment" && d.Message.StartsWith("Part 2 of 4", StringComparison.Ordinal));
    }
}
