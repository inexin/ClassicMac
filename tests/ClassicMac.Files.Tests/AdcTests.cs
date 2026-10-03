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
