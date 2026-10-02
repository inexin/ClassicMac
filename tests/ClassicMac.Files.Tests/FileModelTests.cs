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

    public static TheoryData<string> AllKinds => ["memory", "file", "slice", "file slice", "extents", "file extents"];

    // Every kind of fork, each holding Bytes[10..60].
    private static ForkData Fifty(string kind) => kind switch
    {
        "memory" => ForkData.FromBytes(Bytes[10..60]),
        "file" => FileOf(Bytes[10..60]),
        "slice" => ForkData.FromBytes(Bytes).Slice(10, 50),
        "file slice" => Source("file").Slice(5, 60).Slice(5, 50),
        "extents" => new ExtentForkData(ForkData.FromBytes(Bytes), [(10, 7), (17, 30), (47, 20)], 50),
        _ => new ExtentForkData(Source("file"), [(10, 25), (35, 25)], 50),
    };

    private static ForkData FileOf(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, bytes);
        return ForkData.FromFile(path);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void ReadAt_reads_from_any_offset(string kind)
    {
        var fork = Fifty(kind);
        var buffer = new byte[12];
        Assert.Equal(12, fork.ReadAt(3, buffer));
        Assert.Equal(Bytes[13..25], buffer);
        Assert.Equal(Bytes[10..60], fork.ToArray());
        Assert.Equal(Bytes[30..34], fork.Slice(20, 10).ReadPrefix(4));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void ReadAt_stops_at_the_end(string kind)
    {
        var fork = Fifty(kind);
        var buffer = new byte[8];
        Assert.Equal(5, fork.ReadAt(45, buffer));
        Assert.Equal(Bytes[55..60], buffer[..5]);
        Assert.Equal(0, fork.ReadAt(50, buffer));
        Assert.Equal(0, fork.ReadAt(70, buffer));
        Assert.Equal(0, fork.ReadAt(0, Span<byte>.Empty));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void ReadAt_rejects_a_negative_offset(string kind) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Fifty(kind).ReadAt(-1, new byte[1]));

    // Reads keep the host file open between them (opening it costs more than a small read), and close it once idle.
    [Fact]
    public async Task A_host_file_stays_open_between_reads_and_closes_when_idle()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Bytes);
        var fork = ForkData.FromFile(path, idle: TimeSpan.FromMilliseconds(100));
        Assert.False(ForkData.IsHostFileOpen(fork));

        var buffer = new byte[4];
        fork.Slice(10, 50).ReadAt(2, buffer);
        Assert.Equal(Bytes[12..16], buffer);
        Assert.True(ForkData.IsHostFileOpen(fork));

        for (var waited = 0; ForkData.IsHostFileOpen(fork) && waited < 5000; waited += 50) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(ForkData.IsHostFileOpen(fork));
        fork.ReadAt(96, buffer);
        Assert.Equal(Bytes[96..100], buffer);
        File.Delete(path);
    }

    // Saving replaces the file: the handles reads keep on it are closed first, and the next read opens the new file.
    [Fact]
    public void Closing_a_host_file_lets_it_be_replaced()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Bytes);
        var fork = ForkData.FromFile(path);
        fork.ReadAt(0, new byte[1]);

        ForkData.CloseHostFile(path.ToUpperInvariant());
        Assert.False(ForkData.IsHostFileOpen(fork));
        var replacement = path + ".new";
        File.WriteAllBytes(replacement, [.. Bytes.Reverse()]);
        File.Replace(replacement, path, null);
        var buffer = new byte[1];
        fork.ReadAt(0, buffer);
        Assert.Equal(99, buffer[0]);
        ForkData.CloseHostFile(path);
        File.Delete(path);
    }
}

// The cache probes read through: a file's head (up to 256 KB) and last 4 KB come from the underlying fork once, the
// rest as asked.
public class ProbeForkTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 600_000).Select(i => (byte)(i * 7 + i / 251)).ToArray();

    // Counts the reads that reach it.
    private sealed class Counting(byte[] bytes) : ForkData
    {
        public int Reads { get; private set; }

        public override long Length => bytes.Length;

        public override Stream Open() => new MemoryStream(bytes, writable: false);

        protected override void ReadAtCore(long offset, Span<byte> buffer)
        {
            Reads++;
            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        }
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(100, 65_536)]
    [InlineData(32_768, 2048)]
    [InlineData(250_000, 20_000)] // across the end of the head
    [InlineData(599_500, 500)] // the tail
    [InlineData(400_000, 1000)] // neither
    [InlineData(595_000, 5000)] // into the tail from before it
    public void Reads_give_the_forks_bytes(int offset, int length)
    {
        var probe = ForkData.ForProbing(new Counting(Bytes));
        var buffer = new byte[length];
        Assert.Equal(length, probe.ReadAt(offset, buffer));
        Assert.Equal(Bytes.AsSpan(offset, length).ToArray(), buffer);
        Assert.Equal(Bytes.AsSpan(offset, 100).ToArray(), probe.Slice(offset, 100).ReadPrefix(100));
        Assert.Equal(Bytes.Length, probe.Length);
    }

    [Fact]
    public void The_head_and_tail_are_read_once()
    {
        var fork = new Counting(Bytes);
        var probe = ForkData.ForProbing(fork);

        probe.ReadPrefix(4);
        probe.ReadPrefix(65_536);
        probe.Slice(32_768, 2048).ReadPrefix(2048);
        probe.Slice(Bytes.Length - 512, 512).ReadPrefix(512);
        probe.Slice(Bytes.Length - 8, 8).ReadPrefix(8);
        var reads = fork.Reads;
        probe.ReadPrefix(65_536);
        probe.Slice(Bytes.Length - 512, 512).ReadPrefix(512);

        Assert.True(reads <= 4, $"{reads} reads");
        Assert.Equal(reads, fork.Reads);
    }

    [Fact]
    public void Small_forks_are_read_whole_once()
    {
        var fork = new Counting(Bytes[..3000]);
        var probe = ForkData.ForProbing(fork);
        probe.ReadPrefix(4);
        probe.Slice(2990, 10).ReadPrefix(10);
        probe.ReadPrefix(3000);
        Assert.Equal(1, fork.Reads);
    }

    [Fact]
    public void Streams_open_the_fork_itself()
    {
        var probe = ForkData.ForProbing(ForkData.FromBytes(Bytes));
        using var stream = probe.Open();
        stream.Seek(400_000, SeekOrigin.Begin);
        Assert.Equal(Bytes[400_000], stream.ReadByte());
    }
}

public class ExtentForkTests
{
    private static readonly byte[] Image = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    [Fact]
    public void Extents_read_as_one_fork_cut_to_its_length()
    {
        var fork = new ExtentForkData(ForkData.FromBytes(Image), [(50, 10), (10, 10), (80, 10)], 25);

        Assert.Equal(25, fork.Length);
        Assert.Equal([.. Image[50..60], .. Image[10..20], .. Image[80..85]], fork.ToArray());
    }

    [Fact]
    public void Extent_forks_seek_across_ranges()
    {
        var fork = new ExtentForkData(ForkData.FromBytes(Image).Slice(0, 100), [(50, 10), (10, 10)], 20);
        using var stream = fork.Open();
        stream.Seek(8, SeekOrigin.Begin);
        var bytes = new byte[4];
        stream.ReadExactly(bytes);
        Assert.Equal([58, 59, 10, 11], bytes);
        Assert.Equal(0, stream.Read(new byte[4], 0, 0));
    }

    [Fact]
    public void Extents_outside_the_image_or_too_short_throw()
    {
        var image = ForkData.FromBytes(Image);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtentForkData(image, [(95, 10)], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtentForkData(image, [(0, 10)], 11));
    }
}

public class MacFileTests
{
    [Fact]
    public void Defaults_to_no_Finder_info_dates_or_forks()
    {
        var file = new MacFile { Name = new MacString("Read Me"u8) };

        Assert.Same(FinderInfo.Empty, file.FinderInfo);
        Assert.Empty(file.FolderPath);
        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("Games:Realmz:Read Me",
            (file with { FolderPath = [MacString.FromMacRoman("Games"), MacString.FromMacRoman("Realmz")] }).MacPath);
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

public class MacFolderTests
{
    [Fact]
    public void A_folder_s_path_is_its_parents_then_its_name()
    {
        var folder = new MacFolder
        {
            Name = MacString.FromMacRoman("Realmz"),
            FolderPath = [MacString.FromMacRoman("Games")],
        };

        Assert.False(folder.IsRoot);
        Assert.Same(FolderFinderInfo.Empty, folder.FinderInfo);
        Assert.Null(folder.Created);
        Assert.Null(folder.Modified);
        Assert.Equal(["Games", "Realmz"], folder.Path.Select(p => p.ToMacRoman()));
        Assert.Equal("Games:Realmz", folder.MacPath);
    }

    [Fact]
    public void The_root_folder_has_the_volume_s_name_and_an_empty_path()
    {
        var root = new MacFolder { Name = MacString.FromMacRoman("Macintosh HD"), IsRoot = true };

        Assert.Empty(root.Path);
        Assert.Equal("", root.MacPath);
    }
}

public class FolderFinderInfoTests
{
    // DInfo then DXInfo, every field a distinct value.
    internal static readonly byte[] Sample =
    [
        0x00, 0x28, 0x00, 0x0A, 0x01, 0x2C, 0x01, 0xF4, // frRect: top 40, left 10, bottom 300, right 500
        0x44, 0x00,                                     // frFlags: invisible, custom icon
        0x00, 0x14, 0x00, 0x1E,                         // frLocation: v 20, h 30
        0x01, 0x00,                                     // frView
        0xFF, 0xF6, 0x00, 0x05,                         // frScroll: v -10, h 5
        0x00, 0x00, 0x00, 0x07,                         // frOpenChain
        0x02,                                           // frScript
        0x81,                                           // frXFlags
        0x00, 0x03,                                     // frComment
        0x00, 0x00, 0x00, 0x63,                         // frPutAway
    ];

    [Fact]
    public void Reads_DInfo_and_DXInfo()
    {
        var info = FolderFinderInfo.Read(Sample);

        Assert.Equal(new MacRect(40, 10, 300, 500), info.WindowBounds);
        Assert.Equal(FinderFlags.IsInvisible | FinderFlags.HasCustomIcon, info.Flags);
        Assert.Equal(new MacPoint(20, 30), info.Location);
        Assert.Equal(0x0100, info.View);
        Assert.Equal(new MacPoint(-10, 5), info.ScrollPosition);
        Assert.Equal(7, info.OpenChain);
        Assert.Equal(2, info.Script);
        Assert.Equal(unchecked((sbyte)0x81), info.ExtendedFlags);
        Assert.Equal(3, info.Comment);
        Assert.Equal(0x63, info.PutAway);
    }

    [Fact]
    public void Writes_what_it_reads()
    {
        var info = FolderFinderInfo.Read(Sample);

        Assert.Equal(Sample, info.ToArray());
        var destination = new byte[40];
        info.Write(destination);
        Assert.Equal(Sample, destination[..32]);
        Assert.Throws<ArgumentException>(() => info.Write(new byte[31]));
    }

    [Fact]
    public void Short_input_is_padded_with_zeros()
    {
        var info = FolderFinderInfo.Read(Sample.AsSpan(0, 16));

        Assert.Equal(new MacRect(40, 10, 300, 500), info.WindowBounds);
        Assert.Equal(default, info.ScrollPosition);
        Assert.Equal(0, info.PutAway);
        Assert.Equal(FolderFinderInfo.Length, FolderFinderInfo.Empty.ToArray().Length);
        Assert.Equal(new byte[32], FolderFinderInfo.Empty.ToArray());
    }
}
