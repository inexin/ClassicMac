using ClassicMac.Core;

namespace ClassicMac.Files.Tests;

public class ForkDataTests
{
    [Fact]
    public void Empty_has_no_bytes()
    {
        Assert.Equal(0, ForkData.Empty.Length);
        using var stream = ForkData.Empty.Open();
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void Each_open_is_a_fresh_read_only_stream()
    {
        var fork = ForkData.FromBytes(new byte[] { 1, 2, 3 });
        Assert.Equal(3, fork.Length);

        using var first = fork.Open();
        first.ReadByte();
        using var second = fork.Open();
        Assert.Equal(1, second.ReadByte());
        Assert.False(second.CanWrite);
    }
}

public class MacFileTests
{
    [Fact]
    public void Defaults_to_no_Finder_info_dates_or_forks()
    {
        var file = new MacFile { Name = new MacString("Read Me"u8) };

        Assert.Same(FinderInfo.Empty, file.FinderInfo);
        Assert.Null(file.Created);
        Assert.Null(file.Modified);
        Assert.Equal(0, file.DataFork.Length);
        Assert.Equal(0, file.ResourceFork.Length);
    }

    [Fact]
    public void Finder_info_keeps_its_extended_bytes()
    {
        var info = new FinderInfo
        {
            Type = FourCC.FromString("TEXT"),
            Creator = FourCC.FromString("ttxt"),
            Flags = FinderFlags.HasBundle | FinderFlags.IsInvisible,
        };

        Assert.Equal(16, info.Extended.Length);
        Assert.Equal(FinderFlags.HasBundle | FinderFlags.IsInvisible, info.Flags);
        Assert.Equal(info with { }, info);
    }
}
