using ClassicMac.Files.Compression;

namespace ClassicMac.Files.Tests;

// The KenCode encoder (docs/formats/codecs/kencode.md §3): what it writes decodes to its input, and it writes Disk Copy
// 6.3.3's bytes for the volume of AdcTests (TestData/Adc/src2m.dsk.gz), whose chunks' SHA-256 are Disk Copy's.
public sealed class KenCodeEncoderTests
{
    // Output longer than the input is stored raw (kencode.md §3 step 7), and the decoder reads at most 8 bits per byte.
    private static byte[] RoundTrip(byte[] input)
    {
        var compressed = KenCode.Compress(input);
        if (compressed.Length > input.Length)
        {
            return compressed;
        }

        var output = new byte[input.Length];
        Assert.Equal(KenCode.Result.Done, KenCode.Decompress(compressed, output, out var written));
        Assert.Equal(input.Length, written);
        Assert.Equal(input, output);
        return compressed;
    }

    public static TheoryData<int> Inputs => [0, 1, 2, 3, 4, 5, 6];

    private static byte[] Input(int which)
    {
        var random = new Random(which);
        byte[] Random(int length)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }

        return which switch
        {
            0 => [42],
            1 => new byte[512 * 512],
            2 => Random(30_000),
            3 => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"Line {i % 37} of the text. "))),
            4 => [.. Enumerable.Range(0, 70_000).Select(i => (byte)(i % 7))],
            5 => [.. Random(3000), .. new byte[5000], .. Random(200), .. Enumerable.Repeat((byte)0xAB, 300), .. Random(62), .. Random(63), .. Random(64)],
            _ => [.. Random(100), .. Random(150)],
        };
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void What_it_writes_decodes_to_the_input(int which) => RoundTrip(Input(which));

    [Fact]
    public void Repeats_shrink_and_random_bytes_grow()
    {
        Assert.InRange(RoundTrip(Input(1)).Length, 1, 512 * 512 / 20);               // 64-byte matches, 24 bits each
        Assert.True(RoundTrip(Input(2)).Length > 30_000);
        Assert.True(RoundTrip([42]).Length > 1);
    }

    private static byte[] Source()
    {
        using var gzip = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestData", "Adc", "src2m.dsk.gz")),
            System.IO.Compression.CompressionMode.Decompress);
        var disk = new MemoryStream();
        gzip.CopyTo(disk);
        return disk.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    // Disk Copy's KenCode save with 512-sector chunks: every chunk byte for byte, each margin 4 (+$48 513).
    [Theory]
    [InlineData(4, 512, "8a19ea725eee463ec397de5cd097638a34f03b367f45682d98772e4a88c9d6ca")]
    [InlineData(516, 512, "bf3cddac8a45f84824ad3e16086e6f3b64f6f43778aa24ff56a1a2b9ae0bbfee")]
    [InlineData(1028, 512, "eaf942524c9408913c3afb54b1769bcf2427f9e5f49eb9564b932de05097337a")]
    [InlineData(1540, 512, "75db3ae372af78a11b6ab96b799beeb999f4993394adf140f36eac3041b75ff8")]
    [InlineData(2052, 512, "2ea5dd04d71b884f87486ec315e794e0540da321015f09a27eb574b23bf8f978")]
    [InlineData(2564, 434, "b416ed5f12eb10be6c26059d8674ea6946e58d2eec2413394d30673d57728237")]
    public void Large_chunks_are_Disk_Copy_s_bytes(int sector, int sectors, string sha256)
    {
        var compressed = KenCode.Compress(Source().AsSpan(sector * 512, sectors * 512), out int margin);

        Assert.Equal(sha256, Sha256(compressed));
        Assert.Equal(4, margin);
    }

    // Disk Copy's 7-sector chunks: sectors 4–2997 in 428 chunks, the last of 5; the SHA-256 of their SHA-256 (lower-case
    // hex, comma-separated) and the largest margin, 5 (+$48 8).
    [Fact]
    public void Small_chunks_are_Disk_Copy_s_bytes()
    {
        var source = Source();
        var hashes = new List<string>();
        var largest = 0;
        for (var sector = 4; sector <= 2997; sector += 7)
        {
            var sectors = Math.Min(7, 2998 - sector);
            hashes.Add(Sha256(KenCode.Compress(source.AsSpan(sector * 512, sectors * 512), out int margin)));
            largest = Math.Max(largest, margin);
        }

        Assert.Equal("23e8fdda36599607a2aabf2c622e785f0afd72e4084f042a84447649495816ca", hashes[0]);
        Assert.Equal("dbea38423a9ac8f4cf5e52ecb6a4da9fec4fedff50f2da7bfd2c342b53d5912c", Sha256(System.Text.Encoding.ASCII.GetBytes(string.Join(",", hashes))));
        Assert.Equal(5, largest);
    }
}
