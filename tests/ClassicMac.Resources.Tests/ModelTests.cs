namespace ClassicMac.Resources.Tests;

public class FourCCTests
{
    [Fact]
    public void FromString_is_big_endian()
    {
        Assert.Equal(0x50494354u, FourCC.FromString("PICT").Value);
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        Assert.NotEqual(FourCC.FromString("PICT"), FourCC.FromString("pict"));
    }

    [Fact]
    public void ToString_escapes_bytes_outside_printable_ascii()
    {
        Assert.Equal("snd ", FourCC.FromString("snd ").ToString());
        Assert.Equal("ab\\x00\\xA5", new FourCC(0x616200A5).ToString());
        Assert.Equal("a\\x5Cbc", FourCC.FromString("a\\bc").ToString());
    }

    [Fact]
    public void FromString_rejects_wrong_lengths_and_wide_characters()
    {
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abc"));
        Assert.Throws<ArgumentException>(() => FourCC.FromString("abĀc"));
    }
}

public class MacStringTests
{
    [Fact]
    public void Compares_by_bytes()
    {
        Assert.Equal(new MacString([1, 2, 3]), new MacString([1, 2, 3]));
        Assert.NotEqual(new MacString([1, 2, 3]), new MacString([1, 2]));
        Assert.Equal(default, new MacString([]));
    }

    [Fact]
    public void Holds_at_most_255_bytes()
    {
        Assert.Equal(255, new MacString(new byte[255]).Length);
        Assert.Throws<ArgumentException>(() => new MacString(new byte[256]));
    }
}

public class MacDateTests
{
    [Fact]
    public void Counts_seconds_from_1904()
    {
        Assert.Equal(new DateTime(1904, 1, 1), new MacDate(0).ToDateTime());
        Assert.Equal(new DateTime(1984, 1, 24), new MacDate(2_526_595_200).ToDateTime());
        Assert.Equal(new DateTime(2040, 2, 6, 6, 28, 15), new MacDate(uint.MaxValue).ToDateTime());
    }

    [Fact]
    public void FromDateTime_round_trips_and_rejects_out_of_range()
    {
        var date = new DateTime(1991, 5, 13, 12, 34, 56);
        Assert.Equal(date, MacDate.FromDateTime(date).ToDateTime());
        Assert.Throws<ArgumentOutOfRangeException>(() => MacDate.FromDateTime(new DateTime(1903, 12, 31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacDate.FromDateTime(new DateTime(2040, 2, 6, 6, 28, 16)));
    }
}

public class ResourceForkTests
{
    private static readonly FourCC Pict = FourCC.FromString("PICT");
    private static readonly FourCC Snd = FourCC.FromString("snd ");

    [Fact]
    public void Keeps_order_and_finds_by_type_and_id()
    {
        var fork = new ResourceFork();
        var a = new Resource(Pict, 128, new byte[] { 1 });
        var b = new Resource(Snd, 128, new byte[] { 2 });
        var c = new Resource(Pict, 129, new byte[] { 3 });
        fork.Add(a);
        fork.Add(b);
        fork.Add(c);

        Assert.Equal([a, b, c], fork.Resources);
        Assert.Equal([Pict, Snd], fork.Types);
        Assert.Same(b, fork.Find(Snd, 128));
        Assert.Null(fork.Find(Snd, 129));
        Assert.Equal([a, c], fork.OfType(Pict));
    }

    [Fact]
    public void Rejects_duplicates_and_resources_owned_elsewhere()
    {
        var fork = new ResourceFork();
        var a = new Resource(Pict, 128, ReadOnlyMemory<byte>.Empty);
        fork.Add(a);

        Assert.Throws<InvalidOperationException>(() => fork.Add(new Resource(Pict, 128, ReadOnlyMemory<byte>.Empty)));
        Assert.Throws<InvalidOperationException>(() => new ResourceFork().Add(a));
    }

    [Fact]
    public void Remove_frees_the_id()
    {
        var fork = new ResourceFork();
        var a = new Resource(Pict, 128, ReadOnlyMemory<byte>.Empty);
        fork.Add(a);

        Assert.True(fork.Remove(a));
        Assert.False(fork.Remove(a));
        fork.Add(new Resource(Pict, 128, ReadOnlyMemory<byte>.Empty));
        new ResourceFork().Add(a);
    }

    [Fact]
    public void Renumber_moves_the_resource_to_a_free_id()
    {
        var fork = new ResourceFork();
        var a = new Resource(Pict, 128, ReadOnlyMemory<byte>.Empty);
        var b = new Resource(Pict, 129, ReadOnlyMemory<byte>.Empty);
        fork.Add(a);
        fork.Add(b);

        Assert.Throws<InvalidOperationException>(() => fork.Renumber(a, 129));
        fork.Renumber(a, 200);

        Assert.Equal(200, a.Id);
        Assert.Same(a, fork.Find(Pict, 200));
        Assert.Null(fork.Find(Pict, 128));
    }

    [Fact]
    public void SetData_replaces_data_and_length()
    {
        var a = new Resource(Pict, 128, new byte[] { 1, 2, 3 });
        a.SetData(new byte[] { 9 });

        Assert.Equal(1, a.Length);
        Assert.Equal([9], a.GetData().ToArray());
    }

    [Fact]
    public void Reserved_areas_must_keep_their_size()
    {
        var fork = new ResourceFork();
        Assert.Equal(ResourceFork.SystemDataLength, fork.SystemData.Length);
        Assert.Throws<ArgumentException>(() => fork.SystemData = new byte[10]);
        Assert.Throws<ArgumentException>(() => fork.ApplicationData = new byte[10]);
    }
}
