using System.Text;
using ClassicMac.Files.Compression;

namespace ClassicMac.Files.Tests;

// bzip2 streams made by Python's bz2 module (libbzip2 1.0): a short one with long runs, one over three 100 kB blocks,
// and two streams back to back.
public class BZip2Tests
{
    private const string SmallBz2 = "QlpoOTFBWSZTWX+uQlUAAEX/////////////////////////////////////////////sADVWkBMCYAJgAAGgCYAAAAAAAAJgAAAAAAJgaAAAAAAEwAEwAAAAAAAVKaDQamAJpgCYmABMJgAIwmTAACYAIzUMAAAAATTIYAAAAAAACNGmTBMJkYAAABJgAJgACYAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAdQXcGAABsKBAFhcYDjI0NjgjwDo8PkBCREZIS+Em8ROUFJUVlhaXF4kIwMTLyGZoWAGQuAF5gYmRmafs1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWmBhYmNkZfWzM6EEzpQYDQKAaWoS2OAWw3uA2AX9gx7XEOE5CQADe4MEIVvf8HN0dnd4eXp7fH1+f4CBgoOEhYaHiImKi4yNjo+QkZKTlP7Ky0vMTM0Bm5ydnp+goaKjpKWmp6ipqqusra6vsLGys7S1tre4ubq7vL2+v8DBwsPExcbHyMnKy8zNzs/Q0dLT1NXW19bX2Nna29zd3t/g4eLj4uSE5ebn6Onq6+zt7u/w8fLz9PX2FwWY9wPx8/X2LuSKcKEg/1yEqg=";
    private const string MultiBz2 = "QlpoMTFBWSZTWRJib4cAnRrZgAAQQAB/8B9ptsBAAjuACAoABoAACgAGgAAE1VI0TAjADFApVPSpGf6qCNqDJobRQlOkhKeVCU9KEp3FSLslSLvoWkXXiReRFglVwi6oXCLrhF0JVcIuEWULkV110gOEXCLCLCLCLCLCLCLCLCLCLCLCLCLCLCLCLJFhFhFlC8qF26Iu4i7iL6EXoRfYi+5F6kXYi2hbtCzCLCLSLSLSLSLSLSLSLaF4eFCzSLhFxK0i0i0i0i0i2hbQuu/xipF7RUi9EEp9kJT2oSmEhKYYqEplISmahKfFCU0UJTCQlNqhKZKEptUJTOQlN+ShKZqEpjISmihKbJQlP8xQVkmU1m3CSpRABkyWYAAEEAAf/AfabbAQAI8AABQADQAAFAANAAAUAA0AAAUqp4qQ/9VQyeUGh4nmKIusURfyKIveKIun6iiLtFEX3IvUi9iLRFoi0ReyTZFsi2RfZRU4RcIuEXCLglVwi4RcIuUTglVwi4RcIt0KtkWyLZFoi0RaItEWiLRFoi0RaIvZJsi2RbIvQi/BF1IuxF1IuxFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFhFlE+sURe6UResURf+KIv7FEXSKIuRRF3iiLwlEX+SiLzFEXSKIvSKIu8URfJKIvEURd4oi8RRFuKIvKURcRRF8GKCskymss/1diQAE6yzAAAggAD/4D7TbYCAA/cAgFAANAAAUAA0AAAmqpAAAAMgUqmVPUP1Q02UABlEhXRJCvOfCkhXqJCvWSVHMkqOpFwRbEWxFsRbEWxFsRcQwRYIsEWCLBF4SAyRZIskWRKrJFkiyRZEqskWSLMiwRYIsULniHJFuRbkW5FuReZF1IuCLki0RaItEWiLRFoi0RaItEWiLRFoi0RaIbSptKmG6JCvSSFcUkK+pIV7SQrBJCtESFfkkKxSQr4khWUSFYJIVpiQr9EhWpJCsUkK+JIVjEhWdJCskkK0USFfxdyRThQkEBhUa0A==";
    private const string Concatenated = "QlpoOTFBWSZTWZ2tPQ4AAAIRgEAAIyIcACAAIgMEIMmIMDCRZMrxdyRThQkJ2tPQ4EJaaDkxQVkmU1nwk5f7AAAFEYBAAC4DnAAgADEA000EAaMlWw4g8kh4u5IpwoSHhJy/2A==";

    private static byte[] Small() =>
        [.. "hello hello hello "u8, .. Enumerable.Repeat((byte)'a', 300), .. Enumerable.Range(0, 256).Select(i => (byte)i), .. new byte[1000], .. "end"u8];

    private static byte[] Multi() =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 9000).Select(i => $"record {i / 100}: the quick brown fox\n")));

    private static (BZip2.Result Result, byte[] Output) Decode(string base64, int size)
    {
        var output = new byte[size];
        var result = BZip2.Decompress(Convert.FromBase64String(base64), output, out var written);
        return (result, output[..written]);
    }

    [Fact]
    public void Streams_decode()
    {
        foreach (var (base64, expected) in new[] { (SmallBz2, Small()), (MultiBz2, Multi()), (Concatenated, "first stream second stream"u8.ToArray()) })
        {
            var (result, output) = Decode(base64, expected.Length + 10);
            Assert.Equal(BZip2.Result.Done, result);
            Assert.Equal(expected, output);
        }
    }

    [Fact]
    public void Damage_is_detected()
    {
        var damaged = Convert.FromBase64String(SmallBz2);
        damaged[^8] ^= 0x40; // inside the data or the stream CRC
        Assert.NotEqual(BZip2.Result.Done, BZip2.Decompress(damaged, new byte[4096], out _));
        Assert.Equal(BZip2.Result.OutputFull, BZip2.Decompress(Convert.FromBase64String(SmallBz2), new byte[100], out _));
        Assert.Equal(BZip2.Result.BadData, BZip2.Decompress("not bzip2"u8, new byte[100], out _));
    }
}
