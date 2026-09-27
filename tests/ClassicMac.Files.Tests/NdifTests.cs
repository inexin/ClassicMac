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

    private static MacFile Image(byte[] data, byte[] resource, string name = "Test.img") =>
        new() { Name = MacString.FromMacRoman(name), DataFork = ForkData.FromBytes(data), ResourceFork = ForkData.FromBytes(resource) };

    private static (byte[] Disk, List<Diagnostic> Diagnostics) Disk(MacFile image, Func<MacString, MacFile?>? siblings = null)
    {
        Assert.True(NdifReader.Instance.CanRead(image));
        var diagnostics = new List<Diagnostic>();
        var disk = Assert.Single(NdifReader.Instance.Read(image, new ContainerContext(diagnostics: diagnostics, siblings: siblings)));
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
    public void Segments_are_joined_from_their_siblings()
    {
        var volume = Volume();
        var (data, map) = NdifBuilder.Build(volume, "Test Disk", (300, Kind.Raw), (500, Kind.Zero));
        var third = data.Length / 3;
        var parts = new[] { data[..third], data[third..(2 * third)], data[(2 * third)..] };
        var bcem = ClassicMac.Resources.ResourceFork.Read(map).Resources[0].GetData().ToArray();
        MacFile Part(int n) => Image(parts[n - 1], n == 1
            ? NdifBuilder.Fork(("bcem", 128, bcem), ("bcm#", 128, NdifBuilder.PartResource(1, 3)))
            : NdifBuilder.Fork(("bcm#", 128, NdifBuilder.PartResource(n, 3))), $"Disk {n}of3");

        var (disk, diagnostics) = Disk(Part(1), name => name.ToMacRoman() switch { "Disk 2of3" => Part(2), "Disk 3of3" => Part(3), _ => null });
        Assert.Empty(diagnostics);
        Assert.Equal(volume, disk);

        (_, diagnostics) = Disk(Part(1), name => name.ToMacRoman() == "Disk 2of3" ? Part(2) : null);
        Assert.Contains(diagnostics, d => d.Code == "ndif.missing-segment");
        Assert.False(NdifReader.Instance.CanRead(Part(2))); // later parts have no map
    }

    [Fact]
    public void Unknown_chunks_read_as_zeros_and_are_reported()
    {
        var volume = Volume();
        var (data, resource) = NdifBuilder.Build(volume, "Test Disk", (6, Kind.Raw), (794, Kind.Unknown));

        var (disk, diagnostics) = Disk(Image(data, resource));

        Assert.Contains(diagnostics, d => d.Code == "ndif.unsupported-chunk");
        Assert.Equal(volume[..3072], disk[..3072]);
        Assert.All(disk[3072..], b => Assert.Equal(0, b));
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
        Assert.Contains(diagnostics, d => d.Code == "ndif.bad-adc");
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

        byte[] Decode(string path)
        {
            var diagnostics = new List<Diagnostic>();
            var host = HostFiles.Read(path, diagnostics: diagnostics);
            var context = new ContainerContext(diagnostics: diagnostics, siblings: HostFiles.Siblings(path));
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
}
