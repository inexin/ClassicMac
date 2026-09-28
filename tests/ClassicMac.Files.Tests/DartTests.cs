using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

public class DartTests
{
    private static byte[] Bytes(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * seed + seed)).ToArray();

    // An 800K HFS volume.
    private static byte[] Volume()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Read Me", Bytes(3000, 3), Bytes(300, 7));
        var volume = builder.Build("DART Disk");
        Array.Resize(ref volume, 800 * 1024);
        return volume;
    }

    // A DART image of an 800K disk: blocks of 40 sectors (data, then 480 zero tag bytes), stored or RLE-compressed
    // as one literal run of words.
    private static byte[] Dart(byte[] disk, byte compression)
    {
        var header = new byte[4 + 40 * 2];
        header[0] = compression;
        header[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 800);
        var body = new List<byte>();
        for (var i = 0; i < 40; i++)
        {
            byte[] block = [.. disk.AsSpan(i * 20480, 20480), .. new byte[480]];
            if (compression == 2)
            {
                BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(4 + i * 2), -1);
                body.AddRange(block);
            }
            else
            {
                var words = block.Length / 2;
                BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(4 + i * 2), (short)(words + 1));
                body.AddRange([(byte)(words >> 8), (byte)words, .. block]);
            }
        }
        return [.. header, .. body];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DART_images_read_as_their_disk(byte compression)
    {
        var volume = Volume();
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("Disk.image"),
            FinderInfo = new FinderInfo { Type = FourCC.FromString("DMd3"), Creator = FourCC.FromString("DART") },
            DataFork = ForkData.FromBytes(Dart(volume, compression)),
        };
        var diagnostics = new List<Diagnostic>();

        var root = ContainerUnwrapper.Default.Unwrap(file, "host file", new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(diagnostics);
        Assert.Equal("DART", Assert.Single(root.Children).Format);
        Assert.Equal(volume, root.Children[0].File.DataFork.ToArray());
        Assert.Equal(Bytes(3000, 3), Assert.Single(root.Leaves()).File.DataFork.ToArray());
    }

    [Fact]
    public void Other_data_is_not_DART()
    {
        Assert.False(DartReader.Instance.CanRead(ForkData.FromBytes(new byte[1000])));
        var image = Dart(Volume(), 2);
        image[3] = 0x21; // 801K: no such floppy
        Assert.False(DartReader.Instance.CanRead(ForkData.FromBytes(image)));
    }

    // DART 1.5.3's own files (CiderPress2 test data, in the user's MacROM_analysis/dart_samples): "fast" (RLE) and
    // "best" (LZH), one with random data and tags, each split into .data, .rsrc and .type. Each decodes to the source
    // disk's data, and the CKSM checksums match.
    [Fact]
    public void DART_153_files_decode_to_their_source_disks()
    {
        var samples = !CorpusFolders.Any ? []
            : CorpusFolders.EnumerateFiles("*-best.data", SearchOption.AllDirectories)
                .Concat(CorpusFolders.EnumerateFiles("*-fast.data", SearchOption.AllDirectories))
                .Where(f => !f.Contains("__MACOSX", StringComparison.Ordinal)).ToList();
        if (samples.Count == 0) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the dart_samples to run this.");

        foreach (var sample in samples)
        {
            var stem = sample[..^".data".Length];
            var typeCreator = File.ReadAllBytes(stem + ".type");
            var file = new MacFile
            {
                Name = MacString.FromMacRoman(Path.GetFileName(stem)),
                FinderInfo = new FinderInfo { Type = new FourCC(typeCreator.AsSpan(0, 4)), Creator = new FourCC(typeCreator.AsSpan(4, 4)) },
                DataFork = ForkData.FromBytes(File.ReadAllBytes(sample)),
                ResourceFork = ForkData.FromBytes(File.ReadAllBytes(stem + ".rsrc")),
            };
            var diagnostics = new List<Diagnostic>();
            Assert.True(DartReader.Instance.CanRead(file), sample);
            var disk = Assert.Single(DartReader.Instance.Read(file, new ContainerContext(diagnostics: diagnostics))).DataFork.ToArray();

            Assert.Empty(diagnostics);
            var source = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(sample)!, Path.GetFileName(stem).Split('-')[0] + "-source.img"));
            var sourceData = source.Length == 819200 ? source : source.AsSpan(84, 819200).ToArray(); // Disk Copy 4.2
            Assert.True(sourceData.AsSpan().SequenceEqual(disk), sample);
        }
    }
}
