using System;

namespace ClassicMac.Resources.Decoders.Images.WebP;

/// <summary>VP8L's bit stream (RFC 9649 §3.3): values packed least significant bit first into bytes.</summary>
internal sealed class Vp8lBitWriter
{
    private byte[] bytes = new byte[4096];
    private int length;
    private ulong pending;
    private int used;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/> (at most 32).</summary>
    public void Write(uint value, int count)
    {
        if (count == 0)
        {
            return;
        }

        pending |= (ulong)(value & (uint)((1UL << count) - 1)) << used;
        used += count;
        while (used >= 8)
        {
            if (length == bytes.Length)
            {
                Array.Resize(ref bytes, bytes.Length * 2);
            }

            bytes[length++] = (byte)pending;
            pending >>= 8;
            used -= 8;
        }
    }

    /// <summary>The bytes written, the last one padded with zero bits.</summary>
    public byte[] ToArray()
    {
        var result = new byte[length + (used > 0 ? 1 : 0)];
        bytes.AsSpan(0, length).CopyTo(result);
        if (used > 0)
        {
            result[^1] = (byte)pending;
        }

        return result;
    }
}
