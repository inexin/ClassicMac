using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

public class DiskCopyTests
{
    private static readonly byte[] Disk = Enumerable.Range(0, 2048).Select(i => (byte)(i * 7)).ToArray();

    private static (MacFile File, List<Diagnostic> Diagnostics) Read(byte[] image)
    {
        var input = ForkData.FromBytes(image);
        Assert.True(DiskCopy42Reader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        return (Assert.Single(DiskCopy42Reader.Instance.Read(input, new ContainerContext(diagnostics: diagnostics))), diagnostics);
    }

    [Fact]
    public void The_disk_becomes_one_file()
    {
        var (file, diagnostics) = Read(DiskCopy42("Utilities", Disk, withTags: true));

        Assert.Empty(diagnostics);
        Assert.Equal("Utilities", file.Name.ToMacRoman());
        Assert.Equal(Disk, file.DataFork.ToArray());
    }

    [Fact]
    public void A_wrong_checksum_is_a_warning()
    {
        var (file, diagnostics) = Read(DiskCopy42("Bad", Disk, corruptChecksum: true));
        Assert.Equal(Disk, file.DataFork.ToArray());
        Assert.Equal("diskcopy.checksum", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_truncated_image_keeps_its_whole_blocks()
    {
        var (file, diagnostics) = Read(DiskCopy42("Cut", Disk)[..(84 + 1100)]);
        Assert.Equal(1024, file.DataFork.Length);
        Assert.Equal("diskcopy.truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Other_data_is_not_Disk_Copy()
    {
        var image = DiskCopy42("Odd", Disk);
        image[82] = 0; // $0100 at 82 missing
        Assert.False(DiskCopy42Reader.Instance.CanRead(ForkData.FromBytes(image)));
        Assert.False(DiskCopy42Reader.Instance.CanRead(ForkData.FromBytes(new byte[2048])));
    }
}
