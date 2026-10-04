using ClassicMac.Files.Archives;
using ClassicMac.Files.Compression;

namespace ClassicMac.Files.Tests;

// StuffIt method 15 (Arsenic) streams built by ArsenicEncoder, each step of the decoder and each way it rejects a stream.
public class StuffItMethod15Tests
{
    private static byte[] Text(int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)"the quick brown fox jumps over the lazy dog "[i % 44])];

    public static TheoryData<string> Samples => ["empty", "text", "all bytes", "runs", "long run", "zeros", "random"];

    private static byte[] Sample(string name) => name switch
    {
        "empty" => [],
        "text" => Text(3000),
        "all bytes" => [.. Enumerable.Range(0, 1024).Select(i => (byte)(i * 37 % 256))],
        "runs" => [.. Enumerable.Range(0, 40).SelectMany(i => Enumerable.Repeat((byte)(i % 3), i))],
        "long run" => [1, .. Enumerable.Repeat((byte)7, 1000), 2],
        "zeros" => new byte[5000],
        _ => [.. new Random(15).GetItems(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), 4000)],
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Streams_decode_to_their_data(string name)
    {
        var data = Sample(name);
        Assert.Equal(data, StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode(data), data.Length));
    }

    // Blocks of 512 coded bytes (the smallest size): many blocks, each with its own models and BWT index.
    [Theory]
    [MemberData(nameof(Samples))]
    public void Small_blocks_decode_one_after_another(string name)
    {
        var data = Sample(name);
        Assert.Equal(data, StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode(data, blockBits: 9), data.Length));
    }

    [Fact]
    public void Randomized_blocks_are_derandomized()
    {
        var data = Text(3000);
        var stream = new ArsenicEncoder { BlockBits = 9, Randomized = block => block % 2 == 0 }.Run(data);
        Assert.Equal(data, StuffItMethod15Decoder.Decode(stream, data.Length));
    }

    private static void Rejects(Func<byte[]> decode, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => decode()).Message);

    [Fact]
    public void A_wrong_signature_is_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(new ArsenicEncoder { Signature = "Ax"u8.ToArray() }.Run(Text(10)), 10), "invalid signature");

    // The largest block size field, 15, gives 2^24-byte blocks.
    [Fact]
    public void The_largest_block_size_is_accepted() =>
        Assert.Equal(Text(10), StuffItMethod15Decoder.Decode(new ArsenicEncoder { BlockBits = 24 }.Run(Text(10)), 10));

    [Fact]
    public void A_BWT_index_past_the_block_is_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(
            new ArsenicEncoder { BlockBits = 9, PrimaryIndex = (_, _) => 400 }.Run(Text(100)), 100), "invalid BWT index");

    [Fact]
    public void A_wrong_CRC_is_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(new ArsenicEncoder { Crc = crc => crc ^ 1 }.Run(Text(100)), 100), "invalid CRC-32");

    [Fact]
    public void Fewer_bytes_than_declared_are_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode(Text(100)), 101), "produced 100 of 101");

    [Fact]
    public void More_bytes_than_declared_are_rejected()
    {
        Rejects(() => StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode(Text(100)), 99), "exceeds its declared fork length");
        Rejects(() => StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode([.. Enumerable.Repeat((byte)5, 100)]), 50), "exceeds its declared fork length");
    }

    [Fact]
    public void A_run_without_its_count_is_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(
            new ArsenicEncoder { DropLastRunCount = true }.Run([1, 2, 3, 9, 9, 9, 9, 9]), 8), "missing its repeat count");

    [Fact]
    public void A_negative_length_is_rejected() =>
        Rejects(() => StuffItMethod15Decoder.Decode(ArsenicEncoder.Encode([]), -1), "negative expanded length");

    [Fact]
    public void A_stream_cut_short_is_rejected()
    {
        var stream = ArsenicEncoder.Encode(Text(3000));
        Rejects(() => StuffItMethod15Decoder.Decode(stream.AsSpan(0, stream.Length / 2), 3000), "ends inside its arithmetic bitstream");
    }
}
