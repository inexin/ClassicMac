using System;
using System.IO;

namespace ClassicMac.Files.Compression;

/// <summary>Decodes the LArc <c>-lzs-</c> and <c>-lz5-</c> methods used in LHA archives.</summary>
internal static class LhaLarcDecoder
{
    private const int LzsWindowSize = 2048;
    private const int WindowSize = 4096;
    private const int InitialPosition = WindowSize - 18;

    public static byte[] DecodeLzs(ReadOnlySpan<byte> packed, int expandedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expandedSize);

        byte[] window = new byte[LzsWindowSize];
        window.AsSpan().Fill((byte)' ');
        byte[] output = new byte[expandedSize];
        var bits = new LzsBitReader(packed);
        int outputOffset = 0;
        int windowOffset = LzsWindowSize - 17;

        while (outputOffset < output.Length)
        {
            if (bits.Read(1) != 0)
            {
                byte value = (byte)bits.Read(8);
                output[outputOffset++] = value;
                window[windowOffset] = value;
                windowOffset = (windowOffset + 1) & (LzsWindowSize - 1);
                continue;
            }

            int sourceOffset = bits.Read(11);
            int length = bits.Read(4) + 2;
            if (length > output.Length - outputOffset)
            {
                throw new InvalidDataException("An LZS match exceeds the declared expanded size.");
            }

            for (int i = 0; i < length; i++)
            {
                byte value = window[(sourceOffset + i) & (LzsWindowSize - 1)];
                output[outputOffset++] = value;
                window[windowOffset] = value;
                windowOffset = (windowOffset + 1) & (LzsWindowSize - 1);
            }
        }

        return output;
    }

    public static byte[] DecodeLz5(ReadOnlySpan<byte> packed, int expandedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expandedSize);

        byte[] window = CreateInitialWindow();
        byte[] output = new byte[expandedSize];
        int inputOffset = 0;
        int outputOffset = 0;
        int windowOffset = InitialPosition;

        while (outputOffset < output.Length)
        {
            byte flags = ReadByte(packed, ref inputOffset);
            for (int bit = 0; bit < 8 && outputOffset < output.Length; bit++)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    byte value = ReadByte(packed, ref inputOffset);
                    output[outputOffset++] = value;
                    window[windowOffset] = value;
                    windowOffset = (windowOffset + 1) & (WindowSize - 1);
                    continue;
                }

                byte low = ReadByte(packed, ref inputOffset);
                byte highAndLength = ReadByte(packed, ref inputOffset);
                int sourceOffset = ((highAndLength & 0xF0) << 4) | low;
                int length = (highAndLength & 0x0F) + 3;
                if (length > output.Length - outputOffset)
                {
                    throw new InvalidDataException("An LZ5 match exceeds the declared expanded size.");
                }

                for (int i = 0; i < length; i++)
                {
                    byte value = window[(sourceOffset + i) & (WindowSize - 1)];
                    output[outputOffset++] = value;
                    window[windowOffset] = value;
                    windowOffset = (windowOffset + 1) & (WindowSize - 1);
                }
            }
        }

        return output;
    }

    private static byte[] CreateInitialWindow()
    {
        byte[] window = new byte[WindowSize];
        int offset = 0;
        for (int value = 0; value < 256; value++)
        {
            window.AsSpan(offset, 13).Fill((byte)value);
            offset += 13;
        }
        for (int value = 0; value < 256; value++)
        {
            window[offset++] = (byte)value;
        }

        for (int value = 255; value >= 0; value--)
        {
            window[offset++] = (byte)value;
        }

        window.AsSpan(offset, 128).Clear();
        offset += 128;
        window.AsSpan(offset, 110).Fill((byte)' ');
        offset += 110;
        window.AsSpan(offset, 18).Clear();
        return window;
    }

    private static byte ReadByte(ReadOnlySpan<byte> packed, ref int offset)
    {
        if ((uint)offset >= (uint)packed.Length)
        {
            throw new InvalidDataException("An LZ5 payload is truncated.");
        }

        return packed[offset++];
    }

    private ref struct LzsBitReader
    {
        private readonly ReadOnlySpan<byte> _packed;
        private int _byteOffset;
        private int _bitOffset;

        public LzsBitReader(ReadOnlySpan<byte> packed) => _packed = packed;

        public int Read(int count)
        {
            int value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                if (_byteOffset >= _packed.Length)
                {
                    throw new InvalidDataException("An LZS payload is truncated.");
                }

                value = (value << 1) | ((_packed[_byteOffset] >> (7 - _bitOffset)) & 1);
                if (++_bitOffset == 8)
                {
                    _bitOffset = 0;
                    _byteOffset++;
                }
            }
            return value;
        }
    }
}
