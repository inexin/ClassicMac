using ClassicMac.Core;
using ClassicMac.Files.Archives;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// What telling a file's format costs: a volume of thousands of files is probed whole each time it is read, so a probe
// that is not its format reads a few bytes, not a fork (docs/formats/containers/unwrapping.md §2.1).
public sealed class ProbeCostTests
{
    // Counts the bytes read and the streams opened.
    private sealed class Counting(byte[] bytes) : ForkData
    {
        public long BytesRead { get; private set; }

        public int Opens { get; private set; }

        public override long Length => bytes.Length;

        public override Stream Open()
        {
            Opens++;
            return new MemoryStream(bytes, writable: false);
        }

        protected override void ReadAtCore(long offset, Span<byte> buffer)
        {
            BytesRead += buffer.Length;
            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        }
    }

    private static byte[] ResourceForkWith(string type, int length)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString(type), 128, new byte[length]));
        return fork.ToArray();
    }

    // An application's resource fork (a large one with no 'bcem') is told from an NDIF image by its map alone.
    [Fact]
    public void The_NDIF_probe_reads_a_resource_fork_s_map_not_the_fork()
    {
        var resources = new Counting(ResourceForkWith("CODE", 1_000_000));
        var file = new MacFile { Name = MacString.FromMacRoman("App"), DataFork = ForkData.FromBytes(new byte[] { 1 }), ResourceFork = resources };

        Assert.False(NdifReader.Instance.CanRead(file));
        Assert.InRange(resources.BytesRead, 1, 4096);
        Assert.Equal(0, resources.Opens);
    }

    // The map is read whole when it holds a 'bcem': an image is still recognised.
    [Fact]
    public void The_NDIF_probe_still_finds_a_bcem()
    {
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("Disk.img"),
            DataFork = ForkData.FromBytes(new byte[] { 1 }),
            ResourceFork = new Counting(ResourceForkWith("bcem", 0x58)),
        };

        Assert.True(NdifReader.Instance.CanRead(file));
        file = file with { ResourceFork = new Counting(ResourceForkWith("bcem", 0x57)) };
        Assert.False(NdifReader.Instance.CanRead(file));
    }

    // A Compact Pro archive starts with 1: a fork that does not is refused from its first bytes, with no stream opened.
    [Fact]
    public void The_Compact_Pro_probe_reads_the_first_bytes_only()
    {
        var data = new Counting(Enumerable.Range(0, 500_000).Select(i => (byte)(i % 200 + 2)).ToArray());

        Assert.False(CompactProReader.Instance.CanRead(data));
        Assert.InRange(data.BytesRead, 1, 64);
        Assert.Equal(0, data.Opens);
    }
}
