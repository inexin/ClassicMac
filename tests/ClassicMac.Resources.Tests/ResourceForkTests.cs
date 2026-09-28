using ClassicMac.Core;

namespace ClassicMac.Resources.Tests;

public class ResourceForkTests
{
    private static readonly FourCC Pict = FourCC.FromString("PICT");
    private static readonly FourCC Snd = FourCC.FromString("snd ");

    // Another format's bytes read as a fork (an AppleDouble file named .rsrc in the corpus): a map of 0xFF promises
    // 65536 types of 65536 entries, each entry an error. Past a thousand errors the fork is refused, quickly.
    [Fact]
    public void Forks_too_damaged_to_read_are_refused()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 200_000).ToArray();
        byte[] header = [0, 0, 1, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // data at 256, map at 1024, both lengths 0
        header.CopyTo(bytes, 0);
        bytes[1024 + 24] = 0;
        bytes[1024 + 25] = 28; // type list right after the map header, promising 65535 types
        bytes[1024 + 28 + 1] = 0xFE;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<InvalidDataException>(() => ResourceFork.Read(bytes));

        Assert.Contains("too damaged", e.Message, StringComparison.Ordinal);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
    }

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
