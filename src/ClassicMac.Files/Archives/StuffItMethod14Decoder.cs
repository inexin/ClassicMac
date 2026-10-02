using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes a StuffIt method 14 fork.</summary>
/// <remarks>
/// Method 14 stores independently coded blocks. Each block describes literal/length and distance Huffman trees,
/// then encodes data with LZ references into a 256 KiB sliding window.
/// </remarks>
internal static class StuffItMethod14Decoder
{
    private const int WindowSize = 0x40000;
    private const int WindowMask = WindowSize - 1;
    private const int LiteralCount = 256;
    private const int LengthCount = 52;
    private const int DistanceCount = 75;
    private const int MaximumTreeDepth = 8;
    private const int MaximumCodeLength = 56;

    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        if (outputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        if (input.Length < 2)
        {
            throw new InvalidDataException("A StuffIt method 14 fork has no block count.");
        }

        int blockCount = input[0] | input[1] << 8;
        int inputOffset = 2;
        int outputOffset = 0;
        byte[] output = new byte[outputLength];
        byte[] window = new byte[WindowSize];
        int windowPosition = 0;

        for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            if (input.Length - inputOffset < 8)
            {
                throw new InvalidDataException("A StuffIt method 14 block header is truncated.");
            }

            uint compressedSize = ReadUInt32LittleEndian(input, inputOffset);
            uint expandedSize = ReadUInt32LittleEndian(input, inputOffset + 4);
            inputOffset += 8;
            if (compressedSize > input.Length - inputOffset)
            {
                throw new InvalidDataException("A StuffIt method 14 block exceeds its compressed fork.");
            }

            if (expandedSize > (uint)(output.Length - outputOffset))
            {
                throw new InvalidDataException("StuffIt method 14 output exceeds its declared fork length.");
            }

            var bits = new BitReader(input.Slice(inputOffset, checked((int)compressedSize)));
            HuffmanTree literalsAndLengths = ReadTree(ref bits, LiteralCount + LengthCount, 0);
            HuffmanTree distances = ReadTree(ref bits, DistanceCount, 0);
            int blockEnd = checked(outputOffset + (int)expandedSize);
            while (outputOffset < blockEnd)
            {
                int symbol = literalsAndLengths.ReadSymbol(ref bits);
                if (symbol < LiteralCount)
                {
                    Emit((byte)symbol, output, ref outputOffset, window, ref windowPosition);
                    continue;
                }

                int lengthIndex = symbol - LiteralCount;
                if ((uint)lengthIndex >= LengthCount)
                {
                    throw new InvalidDataException("A StuffIt method 14 block contains an invalid length symbol.");
                }

                int lengthExtraBits = LengthExtraBits(lengthIndex);
                int length = checked(LengthBase(lengthIndex) + 4 + (int)bits.ReadBits(lengthExtraBits));
                int distanceSymbol = distances.ReadSymbol(ref bits);
                if ((uint)distanceSymbol >= DistanceCount)
                {
                    throw new InvalidDataException("A StuffIt method 14 block contains an invalid distance symbol.");
                }

                int distanceExtraBits = DistanceExtraBits(distanceSymbol);
                int distance = checked(DistanceBase(distanceSymbol) + (int)bits.ReadBits(distanceExtraBits));
                if (distance > WindowSize)
                {
                    throw new InvalidDataException("A StuffIt method 14 back-reference exceeds its history window.");
                }

                if (length > blockEnd - outputOffset)
                {
                    throw new InvalidDataException("A StuffIt method 14 match exceeds its declared block length.");
                }

                for (int copied = 0; copied < length; copied++)
                {
                    byte value = window[(windowPosition - distance) & WindowMask];
                    Emit(value, output, ref outputOffset, window, ref windowPosition);
                }
            }

            bits.AlignToByte();
            inputOffset = checked(inputOffset + (int)compressedSize);
        }

        if (outputOffset != output.Length)
        {
            throw new InvalidDataException(
                $"StuffIt method 14 produced {outputOffset} of {output.Length} declared bytes.");
        }

        return output;
    }

    private static void Emit(byte value, byte[] output, ref int outputOffset, byte[] window,
        ref int windowPosition)
    {
        if (outputOffset >= output.Length)
        {
            throw new InvalidDataException("StuffIt method 14 output exceeds its declared fork length.");
        }

        output[outputOffset++] = value;
        window[windowPosition] = value;
        windowPosition = (windowPosition + 1) & WindowMask;
    }

    private static HuffmanTree ReadTree(ref BitReader bits, int symbolCount, int depth)
    {
        if (depth > MaximumTreeDepth)
        {
            throw new InvalidDataException("A StuffIt method 14 code tree is nested too deeply.");
        }

        bool hasZeroLengthCode = bits.ReadBits(1) != 0;
        int width = checked((int)bits.ReadBits(2) + 2);
        int bias = checked((int)bits.ReadBits(3) + 1);
        int valueCount = 1 << width;
        int repeatSymbol = valueCount - 1;
        int zeroLengthSymbol = hasZeroLengthCode ? repeatSymbol - 1 : -1;
        bool encodedLengths = (bits.ReadBits(2) & 1) != 0;
        var lengths = new byte[symbolCount];
        int position = 0;

        if (encodedLengths)
        {
            HuffmanTree lengthTree = ReadTree(ref bits, valueCount, depth + 1);
            while (position < lengths.Length)
            {
                int symbol;
                try
                { symbol = lengthTree.ReadSymbol(ref bits); }
                catch (InvalidDataException e)
                {
                    throw new InvalidDataException(
                        $"A StuffIt method 14 encoded code length is invalid at symbol {position}.", e);
                }
                if (symbol == zeroLengthSymbol)
                {
                    lengths[position++] = 0;
                }
                else if (symbol == repeatSymbol)
                {
                    int repeatCount = checked(lengthTree.ReadSymbol(ref bits) + 3);
                    RepeatPrevious(lengths, ref position, repeatCount);
                }
                else
                {
                    lengths[position++] = checked((byte)(symbol + bias));
                }
            }
        }
        else
        {
            while (position < lengths.Length)
            {
                int symbol = (int)bits.ReadBits(width);
                if (symbol == zeroLengthSymbol)
                {
                    lengths[position++] = 0;
                }
                else if (symbol == repeatSymbol)
                {
                    int repeatCount = checked((int)bits.ReadBits(width) + 3);
                    RepeatPrevious(lengths, ref position, repeatCount);
                }
                else
                {
                    lengths[position++] = checked((byte)(symbol + bias));
                }
            }
        }

        bits.AlignToByte();
        return HuffmanTree.FromLengths(lengths);
    }

    private static void RepeatPrevious(Span<byte> lengths, ref int position, int repeatCount)
    {
        if (position == 0 || repeatCount > lengths.Length - position)
        {
            throw new InvalidDataException("A StuffIt method 14 code-length repeat is invalid.");
        }

        byte previous = lengths[position - 1];
        lengths.Slice(position, repeatCount).Fill(previous);
        position += repeatCount;
    }

    private static int LengthExtraBits(int symbol) => symbol < 4 ? 0 : (symbol - 4) >> 2;

    private static int LengthBase(int symbol)
    {
        int result = 0;
        for (int index = 0; index < symbol; index++)
        {
            result = checked(result + (1 << LengthExtraBits(index)));
        }

        return result;
    }

    private static int DistanceExtraBits(int symbol) => symbol < 3 ? 0 : (symbol - 3) >> 2;

    private static int DistanceBase(int symbol)
    {
        int result = 1;
        for (int index = 0; index < symbol; index++)
        {
            result = checked(result + (1 << DistanceExtraBits(index)));
        }

        return result;
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> input, int offset) =>
        (uint)(input[offset] | input[offset + 1] << 8 | input[offset + 2] << 16 | input[offset + 3] << 24);

    private sealed class HuffmanTree
    {
        private readonly List<Node> _nodes;

        private HuffmanTree(List<Node> nodes) => _nodes = nodes;

        public static HuffmanTree FromLengths(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[MaximumCodeLength + 1];
            int maxLength = 0;
            foreach (byte length in lengths)
            {
                if (length > MaximumCodeLength)
                {
                    throw new InvalidDataException("A StuffIt method 14 Huffman code is too long.");
                }

                if (length == 0)
                {
                    continue;
                }

                counts[length]++;
                maxLength = Math.Max(maxLength, length);
            }
            if (maxLength == 0)
            {
                throw new InvalidDataException("A StuffIt method 14 Huffman tree has no symbols.");
            }

            var nextCodes = new ulong[MaximumCodeLength + 1];
            ulong code = 0;
            for (int length = 1; length <= maxLength; length++)
            {
                code = checked((code + (uint)counts[length - 1]) << 1);
                if (code + (uint)counts[length] > 1UL << length)
                {
                    throw new InvalidDataException("A StuffIt method 14 Huffman tree is oversubscribed.");
                }

                nextCodes[length] = code;
            }

            var tree = new HuffmanTree([new Node()]);
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                tree.Insert(ReverseBits(nextCodes[length]++, length), length, symbol);
            }
            return tree;
        }

        public int ReadSymbol(ref BitReader bits)
        {
            int node = 0;
            while (_nodes[node].Symbol < 0)
            {
                int branch = (int)bits.ReadBits(1);
                node = _nodes[node].Children[branch];
                if (node < 0)
                {
                    throw new InvalidDataException(
                $"A StuffIt method 14 Huffman code is invalid at compressed bit {bits.Position}.");
                }
            }
            return _nodes[node].Symbol;
        }

        private void Insert(ulong code, int length, int symbol)
        {
            int node = 0;
            for (int bit = 0; bit < length; bit++)
            {
                if (_nodes[node].Symbol >= 0)
                {
                    throw new InvalidDataException("A StuffIt method 14 Huffman tree is not prefix-free.");
                }

                int branch = (int)((code >> bit) & 1);
                int child = _nodes[node].Children[branch];
                if (child < 0)
                {
                    child = _nodes.Count;
                    _nodes[node].Children[branch] = child;
                    _nodes.Add(new Node());
                }
                node = child;
            }
            if (_nodes[node].Symbol >= 0 || _nodes[node].Children[0] >= 0 || _nodes[node].Children[1] >= 0)
            {
                throw new InvalidDataException("A StuffIt method 14 Huffman tree contains a code collision.");
            }

            _nodes[node].Symbol = symbol;
        }

        private static ulong ReverseBits(ulong value, int count)
        {
            ulong result = 0;
            for (int bit = 0; bit < count; bit++)
            {
                result |= ((value >> bit) & 1) << (count - bit - 1);
            }

            return result;
        }

        private sealed class Node
        {
            public int[] Children { get; } = [-1, -1];
            public int Symbol { get; set; } = -1;
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> _input = input;
        private int _position;

        public int Position => _position;

        public uint ReadBits(int count)
        {
            if (count is < 0 or > 32 || count > _input.Length * 8 - _position)
            {
                throw new InvalidDataException("A StuffIt method 14 block ends inside its compressed bitstream.");
            }

            uint value = 0;
            for (int bit = 0; bit < count; bit++, _position++)
            {
                value |= (uint)((_input[_position >> 3] >> (_position & 7)) & 1) << bit;
            }

            return value;
        }

        public void AlignToByte() => _position = checked((_position + 7) & ~7);
    }
}
