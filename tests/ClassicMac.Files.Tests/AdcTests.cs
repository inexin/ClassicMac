using ClassicMac.Files.Compression;

namespace ClassicMac.Files.Tests;

// The ADC encoder (docs/formats/codecs/adc.md §3): what it writes decodes to its input, with each token form where it
// fits, and repeats shrink.
public sealed class AdcTests
{
    private static byte[] RoundTrip(byte[] input)
    {
        var compressed = Adc.Compress(input);
        var output = new byte[input.Length];
        Assert.Equal(Adc.Result.Done, Adc.Decompress(compressed, output, out var written));
        Assert.Equal(input.Length, written);
        Assert.Equal(input, output);
        return compressed;
    }

    public static TheoryData<string> Inputs => ["empty", "one", "zeros", "random", "text", "pattern", "far repeat", "mixed"];

    private static byte[] Input(string name)
    {
        var random = new Random(name.Length);
        byte[] Random(int length)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }

        return name switch
        {
            "empty" => [],
            "one" => [42],
            "zeros" => new byte[512 * 512],
            "random" => Random(100_000),
            "text" => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"Line {i % 37} of the text. "))),
            "pattern" => [.. Enumerable.Range(0, 70_000).Select(i => (byte)(i % 7))],
            "far repeat" => [.. Random(30_000), .. Random(40_000), .. Random(30_000).Take(0), .. Random(10)],
            _ => [.. Random(3000), .. new byte[5000], .. Random(200), .. Enumerable.Repeat((byte)0xAB, 300)],
        };
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void What_it_writes_decodes_to_the_input(string name) => RoundTrip(Input(name));

    [Fact]
    public void Repeats_shrink_and_random_bytes_grow_only_by_their_run_headers()
    {
        Assert.InRange(RoundTrip(new byte[512 * 512]).Length, 1, 512 * 512 * 3 / 67 + 8);   // 67-byte matches, 3 bytes each
        var random = Input("random");
        Assert.InRange(RoundTrip(random).Length, random.Length, random.Length + (random.Length + 127) / 128);
    }

    // A repeat 2,000 bytes back needs the long form; one 10 back of up to 18 bytes the short form.
    [Fact]
    public void Each_token_form_is_used_where_it_fits()
    {
        var random = new Random(7);
        var block = new byte[2000];
        random.NextBytes(block);
        byte[] near = [.. block.Take(10), .. block.Take(10)];
        var compressed = RoundTrip(near);
        Assert.Equal([0x89, .. block.Take(10), (byte)(((10 - 3) << 2) | 0), 9], compressed);

        byte[] far = [.. block, .. block.Take(50)];
        compressed = RoundTrip(far);
        int tail = compressed.Length - 3;
        Assert.Equal([(byte)(0x40 | (50 - 4)), (2000 - 1) >> 8, (2000 - 1) & 0xFF], compressed[tail..]);
    }

    // A 2 MB volume ClassicMac made (format 2M, then a 1,500,000-byte file of byte i = (i * 7 + i / 251) & 0xFF), which
    // Disk Copy 6.3.3 saved as Read-Only Compressed ADC images; the chunks' SHA-256 are Disk Copy's.
    private static byte[] Source()
    {
        using var gzip = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "Adc", "src2m.dsk.gz")),
            System.IO.Compression.CompressionMode.Decompress);
        var disk = new MemoryStream();
        gzip.CopyTo(disk);
        return disk.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    // Disk Copy's default 512-sector chunks (two-byte prefix trees): every chunk byte for byte, each margin 4 (+$48 513).
    [Theory]
    [InlineData(4, 512, "99e60ef4061b5d38dd915b1089a910f6f7b2ea5f7079332054274e74904eb370")]
    [InlineData(516, 512, "803e33fde91d49fbfd622cbfc49b513d17df8b4b5b5cf696e04997c2d533d3ce")]
    [InlineData(1028, 512, "efb54b75c84dfb801360a4fe2b9ce0128563b605503f4d9e7f40cc6d25753561")]
    [InlineData(1540, 512, "20c38232bb6a77b660140215157de0b3832462e340e715d304ab530e542e13f3")]
    [InlineData(2052, 512, "7b4f31e47212894bc9049798bc44b86fa2abe04d0cc356853a5e9f6ce5eef1b7")]
    [InlineData(2564, 434, "e5a4b02533bec002f66233ed7c914c0481c7efd1638585b0ba13d3e0dcbc7dd5")]
    public void Large_chunks_are_Disk_Copy_s_bytes(int sector, int sectors, string sha256)
    {
        var chunk = Source().AsSpan(sector * 512, sectors * 512);

        var compressed = Adc.Compress(chunk, out int margin);

        Assert.Equal(sha256, Sha256(compressed));
        Assert.Equal(4, margin);
    }

    // Disk Copy's 7-sector chunks (one-byte prefix trees, under 4,000 bytes): sectors 4–2997 in 428 chunks, the last of
    // 5; the SHA-256 of their SHA-256 (lower-case hex, comma-separated) and the largest margin, 5 (+$48 8).
    [Fact]
    public void Small_chunks_are_Disk_Copy_s_bytes()
    {
        var source = Source();
        var hashes = new List<string>();
        var largest = 0;
        for (var sector = 4; sector <= 2997; sector += 7)
        {
            var sectors = Math.Min(7, 2998 - sector);
            hashes.Add(Sha256(Adc.Compress(source.AsSpan(sector * 512, sectors * 512), out int margin)));
            largest = Math.Max(largest, margin);
        }

        Assert.Equal(428, hashes.Count);
        Assert.Equal("c2511f94f6ec8cdf2a333e2d24b3d9f2816890a019e09346b864c3d8e7a1a6a5", Sha256(System.Text.Encoding.ASCII.GetBytes(string.Join(",", hashes))));
        Assert.Equal(5, largest);
    }

    // A match never reaches more than 65,536 bytes back.
    [Fact]
    public void Matches_stay_within_the_window()
    {
        var random = new Random(3);
        var block = new byte[1000];
        random.NextBytes(block);
        var filler = new byte[70_000];
        random.NextBytes(filler);
        byte[] input = [.. block, .. filler, .. block];

        var compressed = RoundTrip(input);
        Assert.True(compressed.Length > input.Length, "the second block, past the window, is stored as literals");
    }
}
