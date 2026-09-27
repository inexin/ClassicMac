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

public class ForkSliceTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    public static TheoryData<string> Sources => ["memory", "file"];

    private static ForkData Source(string kind)
    {
        if (kind == "memory") return ForkData.FromBytes(Bytes);
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Bytes);
        return ForkData.FromFile(path);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Slices_read_their_range(string kind)
    {
        var slice = Source(kind).Slice(10, 20);
        Assert.Equal(20, slice.Length);
        Assert.Equal(Bytes[10..30], slice.ToArray());
        Assert.Equal(Bytes[10..14], slice.ReadPrefix(4));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Slices_of_slices_and_seeking_stay_in_range(string kind)
    {
        var inner = Source(kind).Slice(10, 50).Slice(5, 10);
        Assert.Equal(Bytes[15..25], inner.ToArray());

        using var stream = inner.Open();
        stream.Seek(-2, SeekOrigin.End);
        var tail = new byte[5];
        Assert.Equal(2, stream.Read(tail, 0, 5));
        Assert.Equal(Bytes[23..25], tail[..2]);
    }

    [Fact]
    public void Slices_outside_the_fork_throw()
    {
        var fork = ForkData.FromBytes(Bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => fork.Slice(90, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => fork.Slice(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fork.Slice(0, 10).Slice(5, 6));
        Assert.Throws<InvalidDataException>(() => fork.ToArray(maxLength: 99));
    }

    [Fact]
    public void ReadPrefix_stops_at_the_end()
    {
        Assert.Equal(Bytes[..3], ForkData.FromBytes(Bytes[..3]).ReadPrefix(128));
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
