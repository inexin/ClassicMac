using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Files.Compression;

/// <summary>Decodes the static-Huffman LZSS methods introduced by LHarc 2.</summary>
internal static class LhaNewDecoder
{
    private const int CopyThreshold = 3;
    private const int CodeCount = 510;
    private const int MaximumCodeLength = 16;

    public static byte[] Decode(ReadOnlySpan<byte> method, ReadOnlySpan<byte> packed, int expandedSize)
    {
        int windowBits = method[3] switch
        {
            (byte)'4' => 12,
            (byte)'5' => 13,
            (byte)'6' => 15,
            (byte)'7' => 16,
            _ => throw new InvalidDataException("The LHA compression method is not supported.")
        };
        int windowSize = 1 << windowBits;
        int offsetCodeBits = windowBits >= 15 ? 5 : 4;
        var reader = new BitReader(packed);
        var output = new byte[expandedSize];
        var window = new byte[windowSize];
        window.AsSpan().Fill((byte)' ');
        int windowPosition = 0;
        int outputPosition = 0;
        int commandsRemaining = 0;
        HuffmanTree codeTree = null!;
        HuffmanTree offsetTree = null!;

        while (outputPosition < output.Length)
        {
            if (commandsRemaining == 0)
            {
                commandsRemaining = reader.ReadBits(16);
                if (commandsRemaining == 0)
                {
                    throw new InvalidDataException("An LHA compressed block has no commands.");
                }

                HuffmanTree temporaryTree = ReadTemporaryTree(ref reader);
                codeTree = ReadCodeTree(ref reader, temporaryTree);
                offsetTree = ReadOffsetTree(ref reader, windowBits, offsetCodeBits);
            }

            commandsRemaining--;
            int code = codeTree.Decode(ref reader);
            if (code < 256)
            {
                Emit((byte)code, output, ref outputPosition, window, ref windowPosition);
                continue;
            }

            int matchLength = checked(code - 256 + CopyThreshold);
            if (matchLength > output.Length - outputPosition)
            {
                throw new InvalidDataException("An LHA match exceeds the declared expanded size.");
            }

            int offsetCode = offsetTree.Decode(ref reader);
            if (offsetCode > windowBits)
            {
                throw new InvalidDataException("An LHA match uses an invalid history offset.");
            }

            int offset = offsetCode == 0 ? 0 : checked((1 << (offsetCode - 1)) + reader.ReadBits(offsetCode - 1));
            int sourcePosition = (windowPosition - offset - 1 + windowSize) & (windowSize - 1);
            for (int index = 0; index < matchLength; index++)
            {
                byte value = window[sourcePosition];
                sourcePosition = (sourcePosition + 1) & (windowSize - 1);
                Emit(value, output, ref outputPosition, window, ref windowPosition);
            }
        }

        return output;
    }

    private static void Emit(byte value, byte[] output, ref int outputPosition, byte[] window,
        ref int windowPosition)
    {
        output[outputPosition++] = value;
        window[windowPosition] = value;
        windowPosition = (windowPosition + 1) & (window.Length - 1);
    }

    private static HuffmanTree ReadTemporaryTree(ref BitReader reader)
    {
        int count = reader.ReadBits(5);
        if (count == 0)
        {
            int code = reader.ReadBits(5);
            if (code >= 31)
            {
                throw new InvalidDataException("An LHA temporary table selects a code outside its alphabet.");
            }

            return HuffmanTree.Single(code);
        }
        if (count > 31)
        {
            throw new InvalidDataException("An LHA temporary Huffman table is too large.");
        }

        var lengths = new List<int>(count);
        for (int index = 0; index < count; index++)
        {
            lengths.Add(ReadLength(ref reader));
            if (index != 2)
            {
                continue;
            }

            int skipped = reader.ReadBits(2);
            if (skipped > count - lengths.Count)
            {
                throw new InvalidDataException("An LHA temporary Huffman table has too many skipped codes.");
            }

            for (int item = 0; item < skipped; item++)
            {
                lengths.Add(0);
            }

            index += skipped;
        }
        return HuffmanTree.FromLengths(lengths);
    }

    private static HuffmanTree ReadCodeTree(ref BitReader reader, HuffmanTree temporaryTree)
    {
        int count = reader.ReadBits(9);
        if (count == 0)
        {
            int code = reader.ReadBits(9);
            if (code >= CodeCount)
            {
                throw new InvalidDataException("An LHA code table selects a code outside its alphabet.");
            }

            return HuffmanTree.Single(code);
        }
        if (count > CodeCount)
        {
            throw new InvalidDataException("An LHA code table has more entries than the method permits.");
        }

        var lengths = new List<int>(count);
        while (lengths.Count < count)
        {
            int code = temporaryTree.Decode(ref reader);
            if (code > 2)
            {
                lengths.Add(code - 2);
                continue;
            }

            int skipped = code switch
            {
                0 => 1,
                1 => reader.ReadBits(4) + 3,
                2 => reader.ReadBits(9) + 20,
                _ => throw new InvalidDataException("An LHA code table contains an invalid skip code.")
            };
            for (int item = 0; item < skipped && lengths.Count < count; item++)
            {
                lengths.Add(0);
            }
        }

        if (lengths.Count != count)
        {
            throw new InvalidDataException("An LHA code table is malformed.");
        }

        return HuffmanTree.FromLengths(lengths);
    }

    private static HuffmanTree ReadOffsetTree(ref BitReader reader, int windowBits, int offsetCodeBits)
    {
        int count = reader.ReadBits(offsetCodeBits);
        if (count == 0)
        {
            int code = reader.ReadBits(offsetCodeBits);
            if (code > windowBits)
            {
                throw new InvalidDataException("An LHA offset table selects a code outside its alphabet.");
            }

            return HuffmanTree.Single(code);
        }
        if (count > windowBits + 1)
        {
            throw new InvalidDataException("An LHA offset table is too large.");
        }

        var lengths = new int[count];
        for (int index = 0; index < count; index++)
        {
            lengths[index] = ReadLength(ref reader);
        }

        return HuffmanTree.FromLengths(lengths);
    }

    private static int ReadLength(ref BitReader reader)
    {
        int length = reader.ReadBits(3);
        if (length == 7)
        {
            while (reader.ReadBit() != 0)
            {
                if (++length > MaximumCodeLength)
                {
                    throw new InvalidDataException("An LHA Huffman code is too long.");
                }
            }
        }
        return length;
    }

    private sealed class HuffmanTree
    {
        private readonly Dictionary<int, int> _symbols;
        private readonly int _singleCode;

        private HuffmanTree(Dictionary<int, int> symbols, int singleCode)
        {
            _symbols = symbols;
            _singleCode = singleCode;
        }

        public static HuffmanTree Single(int code) => new([], code);

        public static HuffmanTree FromLengths(IReadOnlyList<int> lengths)
        {
            if (lengths.Count == 0)
            {
                throw new InvalidDataException("An LHA Huffman table is empty.");
            }

            int maximumLength = 0;
            var counts = new int[MaximumCodeLength + 1];
            for (int index = 0; index < lengths.Count; index++)
            {
                int length = lengths[index];
                if (length < 0 || length > MaximumCodeLength)
                {
                    throw new InvalidDataException("An LHA Huffman code has an invalid length.");
                }

                if (length == 0)
                {
                    continue;
                }

                counts[length]++;
                maximumLength = Math.Max(maximumLength, length);
            }
            if (maximumLength == 0)
            {
                throw new InvalidDataException("An LHA Huffman table has no usable codes.");
            }

            var nextCode = new int[MaximumCodeLength + 1];
            int code = 0;
            for (int bits = 1; bits <= MaximumCodeLength; bits++)
            {
                code = (code + counts[bits - 1]) << 1;
                if (code + counts[bits] > (1 << bits))
                {
                    throw new InvalidDataException("An LHA Huffman table is over-subscribed.");
                }

                nextCode[bits] = code;
            }

            var symbols = new Dictionary<int, int>();
            for (int symbol = 0; symbol < lengths.Count; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                symbols.Add((length << 16) | nextCode[length]++, symbol);
            }
            return new HuffmanTree(symbols, -1);
        }

        public int Decode(ref BitReader reader)
        {
            if (_singleCode >= 0)
            {
                return _singleCode;
            }

            int code = 0;
            for (int length = 1; length <= MaximumCodeLength; length++)
            {
                code = (code << 1) | reader.ReadBit();
                if (_symbols.TryGetValue((length << 16) | code, out int symbol))
                {
                    return symbol;
                }
            }
            throw new InvalidDataException("An LHA Huffman code is not present in its table.");
        }
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        public int ReadBit()
        {
            if (_bitOffset >= _data.Length * 8)
            {
                throw new InvalidDataException("An LHA compressed stream is truncated.");
            }

            int value = (_data[_bitOffset >> 3] >> (7 - (_bitOffset & 7))) & 1;
            _bitOffset++;
            return value;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }
    }
}
