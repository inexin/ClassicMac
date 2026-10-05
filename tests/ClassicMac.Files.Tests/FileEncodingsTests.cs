using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// The encoding a volume says its files are in (docs/formats/codecs/text-encodings.md §2.1, §5): an HFS Plus file's
// text encoding hint, else the volume's System file's region.
public sealed class FileEncodingsTests
{
    private static ContainerNode Unwrap(byte[] volume, string name = "disk.img") =>
        ContainerUnwrapper.Default.Unwrap(new MacFile { Name = MacString.FromMacRoman(name), DataFork = ForkData.FromBytes(volume) }, "host file",
            new ContainerContext());

    private static IEnumerable<ContainerNode> All(ContainerNode node) => [node, .. node.Children.SelectMany(All)];

    private static ContainerNode Node(ContainerNode root, string name) => All(root).Single(n => n.File.Name.ToMacRoman() == name);

    // A System file's resource fork with a 'vers' 1 of the given region.
    private static byte[] System(int region)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("vers"), 1, new byte[] { 7, 0x50, 0x80, 0, (byte)(region >> 8), (byte)region, 3, (byte)'7', (byte)'.', (byte)'5', 0 }));
        return fork.ToArray();
    }

    [Fact]
    public void An_HFS_Plus_hint_is_the_file_s_encoding()
    {
        var builder = new HfsPlusBuilder();
        var japanese = builder.File(HfsPlusBuilder.Root, "Japanese", "x"u8.ToArray(), []);
        builder.TextEncoding(japanese, 1);
        var ukrainian = builder.File(HfsPlusBuilder.Root, "Ukrainian", "x"u8.ToArray(), []);
        builder.TextEncoding(ukrainian, 152);
        var farsi = builder.File(HfsPlusBuilder.Root, "Farsi", "x"u8.ToArray(), []);
        builder.TextEncoding(farsi, 140);                                        // not one ClassicMac reads
        builder.File(HfsPlusBuilder.Root, "Roman", "x"u8.ToArray(), []);              // 0: says nothing

        var root = Unwrap(builder.Build("Plus"));

        Assert.Equal(MacTextEncoding.Japanese, Node(root, "Japanese").File.TextEncoding);
        Assert.Equal(MacTextEncoding.Ukrainian, Node(root, "Ukrainian").File.TextEncoding);
        Assert.Null(Node(root, "Farsi").File.TextEncoding);
        Assert.Null(Node(root, "Roman").File.TextEncoding);
        Assert.Equal(MacTextEncoding.Japanese, FileEncodings.Of(root)(Node(root, "Japanese")));
    }

    [Fact]
    public void A_System_file_s_region_is_its_volume_s_encoding()
    {
        var disk = new HfsBuilder();
        var folder = disk.Folder(HfsBuilder.Root, "System Folder");
        disk.File(folder, "System", [], System(14), "zsys", "MACS");                       // verJapan
        disk.File(HfsBuilder.Root, "Read Me", "x"u8.ToArray(), [], "TEXT", "ttxt");
        var root = Unwrap(disk.Build("Disk"));

        var of = FileEncodings.Of(root);

        Assert.Equal(MacTextEncoding.Japanese, of(Node(root, "Read Me")));
        Assert.Null(of(root));                                                                  // not on a volume
        Assert.Equal(MacTextEncoding.Japanese, FileEncodings.OfSystem(All(root).Select(n => n.File), null));
    }

    [Fact]
    public void A_Roman_System_or_none_or_a_damaged_one_says_nothing()
    {
        MacFile System(byte[] fork) => new()
        {
            Name = MacString.FromMacRoman("System"),
            FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("zsys"), Creator = FourCC.FromString("MACS") },
            ResourceFork = ForkData.FromBytes(fork),
        };

        Assert.Null(FileEncodings.OfSystem([System(FileEncodingsTests.System(0))], null));                // verUS
        Assert.Equal(MacTextEncoding.Greek, FileEncodings.OfSystem([System(FileEncodingsTests.System(20))], null));
        Assert.Null(FileEncodings.OfSystem([], null));
        Assert.Null(FileEncodings.OfSystem([System([1, 2, 3])], null));
    }

    // With a blessed folder, the System file in it is the one that counts.
    [Fact]
    public void The_blessed_folder_s_System_is_chosen()
    {
        MacFile System(int region, uint parent) => new()
        {
            Name = MacString.FromMacRoman("System"),
            FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("zsys"), Creator = FourCC.FromString("MACS") },
            ResourceFork = ForkData.FromBytes(FileEncodingsTests.System(region)),
            ParentId = parent,
        };

        Assert.Equal(MacTextEncoding.Greek, FileEncodings.OfSystem([System(14, 20), System(20, 21)], 21));
    }
}
