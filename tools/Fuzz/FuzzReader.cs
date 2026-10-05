namespace Fuzz;

/// <summary>Choices read from a fuzz input, a byte at a time: zeros once it runs out.</summary>
internal sealed class FuzzReader(ReadOnlyMemory<byte> input)
{
    private int at;

    public bool Done => at >= input.Length;

    public byte Byte() => at < input.Length ? input.Span[at++] : (byte)0;

    /// <summary>A number from 0 to <paramref name="count"/> − 1.</summary>
    public int Int(int count) => count <= 1 ? 0 : (Byte() << 8 | Byte()) % count;

    public bool Bool() => (Byte() & 1) != 0;

    /// <summary>Up to <paramref name="max"/> bytes: a length, then that many from the input.</summary>
    public byte[] Bytes(int max)
    {
        var length = Int(max + 1);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = Byte();
        }

        return bytes;
    }

    /// <summary>
    /// <paramref name="length"/> bytes for content: repeats of a short run from the input, so the data compresses and
    /// spans many allocation blocks without the input being as long.
    /// </summary>
    public byte[] Content(int length)
    {
        var run = Bytes(16);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = run.Length == 0 ? (byte)0 : run[i % run.Length];
        }

        return bytes;
    }
}
