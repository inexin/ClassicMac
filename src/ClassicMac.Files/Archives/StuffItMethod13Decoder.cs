using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes a StuffIt method 13 LZ+Huffman fork.</summary>
/// <remarks>
/// The method 13 stream is LSB-first. Its literal/length symbols and offset bit-lengths use either one of five
/// predefined canonical Huffman sets or transmitted code lengths described by a fixed meta-code.
/// </remarks>
internal static class StuffItMethod13Decoder
{
    private const int LiteralLengthSymbolCount = 321;
    private const int EndOfStreamSymbol = 0x140;
    private const int MaximumCodeLength = 32;
    private static readonly byte[][] FirstCodeLengths =
    [
        StuffItMethod13Tables.SET1_FIRST, StuffItMethod13Tables.SET2_FIRST, StuffItMethod13Tables.SET3_FIRST,
        StuffItMethod13Tables.SET4_FIRST, StuffItMethod13Tables.SET5_FIRST,
    ];
    private static readonly byte[][] SecondCodeLengths =
    [
        StuffItMethod13Tables.SET1_SECOND, StuffItMethod13Tables.SET2_SECOND, StuffItMethod13Tables.SET3_SECOND,
        StuffItMethod13Tables.SET4_SECOND, StuffItMethod13Tables.SET5_SECOND,
    ];
    private static readonly byte[][] OffsetCodeLengths =
    [
        StuffItMethod13Tables.SET1_OFFSET, StuffItMethod13Tables.SET2_OFFSET, StuffItMethod13Tables.SET3_OFFSET,
        StuffItMethod13Tables.SET4_OFFSET, StuffItMethod13Tables.SET5_OFFSET,
    ];

    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        if (outputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        if (outputLength == 0)
        {
            return [];
        }

        if (input.IsEmpty)
        {
            throw new InvalidDataException("A nonempty StuffIt method 13 fork has no compressed data.");
        }

        byte control = input[0];
        int tableSet = control >> 4;
        var bits = new BitReader(input[1..]);
        (HuffmanTree codeA, HuffmanTree codeB, HuffmanTree offsetCode) = tableSet switch
        {
            0 => ReadDynamicCodes(control, ref bits),
            >= 1 and <= 5 => ReadPredefinedCodes(tableSet),
            _ => throw new InvalidDataException("A StuffIt method 13 control byte selects an invalid table."),
        };

        var output = new byte[outputLength];
        int written = 0;
        bool useCodeA = true;
        while (written < output.Length)
        {
            int symbol = (useCodeA ? codeA : codeB).ReadSymbol(ref bits);
            if (symbol <= 0xFF)
            {
                output[written++] = (byte)symbol;
                useCodeA = true;
                continue;
            }
            if (symbol == EndOfStreamSymbol)
            {
                throw new InvalidDataException("A StuffIt method 13 fork ended before its declared output length.");
            }

            int length = symbol switch
            {
                <= 0x13D => symbol - 0x100 + 3,
                0x13E => checked((int)bits.ReadBits(10) + 65),
                0x13F => checked((int)bits.ReadBits(15) + 65),
                _ => throw new InvalidDataException("A StuffIt method 13 fork contains an invalid token."),
            };

            int offsetBits = offsetCode.ReadSymbol(ref bits);
            int distance = offsetBits switch
            {
                0 => 1,
                1 => 2,
                <= 16 => checked((1 << (offsetBits - 1)) + (int)bits.ReadBits(offsetBits - 1) + 1),
                _ => throw new InvalidDataException("A StuffIt method 13 fork contains an invalid back-reference."),
            };
            if (distance > written)
            {
                throw new InvalidDataException("A StuffIt method 13 back-reference precedes the start of the fork.");
            }

            if (length > output.Length - written)
            {
                throw new InvalidDataException("StuffIt method 13 output exceeds its declared fork length.");
            }

            for (int index = 0; index < length; index++)
            {
                output[written + index] = output[written + index - distance];
            }

            written += length;
            useCodeA = false;
        }

        return output;
    }

    private static (HuffmanTree A, HuffmanTree B, HuffmanTree Offset) ReadPredefinedCodes(int tableSet)
    {
        int index = tableSet - 1;
        return (HuffmanTree.FromLengths(FirstCodeLengths[index]), HuffmanTree.FromLengths(SecondCodeLengths[index]),
            HuffmanTree.FromLengths(OffsetCodeLengths[index]));
    }

    private static (HuffmanTree A, HuffmanTree B, HuffmanTree Offset) ReadDynamicCodes(byte control,
        ref BitReader bits)
    {
        HuffmanTree metaCode = HuffmanTree.FromExplicitCodes(StuffItMethod13Tables.META_CODE_VALUES,
            StuffItMethod13Tables.META_CODE_LENGTHS);
        byte[] firstLengths = ReadCodeLengths(ref bits, metaCode, LiteralLengthSymbolCount);
        HuffmanTree codeA = HuffmanTree.FromLengths(firstLengths);
        HuffmanTree codeB = (control & 0x08) != 0
            ? codeA
            : HuffmanTree.FromLengths(ReadCodeLengths(ref bits, metaCode, LiteralLengthSymbolCount));
        int offsetCount = (control & 0x07) + 10;
        HuffmanTree offsetCode = HuffmanTree.FromLengths(ReadCodeLengths(ref bits, metaCode, offsetCount));
        return (codeA, codeB, offsetCode);
    }

    private static byte[] ReadCodeLengths(ref BitReader bits, HuffmanTree metaCode, int count)
    {
        var lengths = new byte[count];
        int position = 0;
        int accumulator = 0;
        while (position < lengths.Length)
        {
            int symbol = metaCode.ReadSymbol(ref bits);
            int repeat = 0;
            switch (symbol)
            {
                case >= 0 and <= 30:
                    accumulator = symbol + 1;
                    break;
                case 31:
                    accumulator = -1;
                    break;
                case 32:
                    accumulator = checked(accumulator + 1);
                    break;
                case 33:
                    accumulator = checked(accumulator - 1);
                    break;
                case 34:
                    repeat = (int)bits.ReadBits(1);
                    break;
                case 35:
                    repeat = checked((int)bits.ReadBits(3) + 2);
                    break;
                case 36:
                    repeat = checked((int)bits.ReadBits(6) + 10);
                    break;
                default:
                    throw new InvalidDataException("A StuffIt method 13 dynamic code-length symbol is invalid.");
            }

            if (accumulator > MaximumCodeLength)
            {
                throw new InvalidDataException("A StuffIt method 13 dynamic Huffman code length is too large.");
            }

            byte value = accumulator > 0 ? (byte)accumulator : (byte)0;
            for (int emitted = 0; emitted <= repeat && position < lengths.Length; emitted++)
            {
                lengths[position++] = value;
            }
        }
        return lengths;
    }

    private sealed class HuffmanTree
    {
        private readonly List<Node> _nodes;

        private HuffmanTree(List<Node> nodes) => _nodes = nodes;

        public static HuffmanTree FromLengths(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[MaximumCodeLength + 1];
            int maximumLength = 0;
            foreach (byte length in lengths)
            {
                if (length > MaximumCodeLength)
                {
                    throw new InvalidDataException("A StuffIt method 13 Huffman code is too long.");
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
                throw new InvalidDataException("A StuffIt method 13 Huffman tree has no symbols.");
            }

            var nextCode = BuildCanonicalCodes(counts, maximumLength);
            var tree = new HuffmanTree([new Node()]);
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                uint code = ReverseBits(nextCode[length]++, length);
                tree.Insert(code, length, symbol);
            }
            return tree;
        }

        public static HuffmanTree FromExplicitCodes(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> lengths)
        {
            if (codes.Length != lengths.Length)
            {
                throw new InvalidDataException("A StuffIt method 13 meta-code has mismatched tables.");
            }

            var tree = new HuffmanTree([new Node()]);
            for (int symbol = 0; symbol < codes.Length; symbol++)
            {
                if (lengths[symbol] == 0 || lengths[symbol] > MaximumCodeLength)
                {
                    throw new InvalidDataException("A StuffIt method 13 meta-code length is invalid.");
                }

                tree.Insert(codes[symbol], lengths[symbol], symbol);
            }
            return tree;
        }

        public int ReadSymbol(ref BitReader bits)
        {
            int nodeIndex = 0;
            while (_nodes[nodeIndex].Symbol < 0)
            {
                int branch = (int)bits.ReadBits(1);
                nodeIndex = _nodes[nodeIndex].Children[branch];
                if (nodeIndex < 0)
                {
                    throw new InvalidDataException("A StuffIt method 13 Huffman code is invalid.");
                }
            }
            return _nodes[nodeIndex].Symbol;
        }

        private void Insert(uint code, int length, int symbol)
        {
            int nodeIndex = 0;
            for (int bitIndex = 0; bitIndex < length; bitIndex++)
            {
                if (_nodes[nodeIndex].Symbol >= 0)
                {
                    throw new InvalidDataException("A StuffIt method 13 Huffman table is not prefix-free.");
                }

                int branch = (int)((code >> bitIndex) & 1);
                int childIndex = _nodes[nodeIndex].Children[branch];
                if (childIndex < 0)
                {
                    childIndex = _nodes.Count;
                    _nodes[nodeIndex].Children[branch] = childIndex;
                    _nodes.Add(new Node());
                }
                nodeIndex = childIndex;
            }
            if (_nodes[nodeIndex].Symbol >= 0 || _nodes[nodeIndex].Children[0] >= 0 ||
                _nodes[nodeIndex].Children[1] >= 0)
            {
                throw new InvalidDataException("A StuffIt method 13 Huffman table has a code collision.");
            }

            _nodes[nodeIndex].Symbol = symbol;
        }

        private static uint[] BuildCanonicalCodes(ReadOnlySpan<int> counts, int maximumLength)
        {
            var nextCode = new uint[MaximumCodeLength + 1];
            ulong code = 0;
            for (int bits = 1; bits <= maximumLength; bits++)
            {
                code = checked((code + (uint)counts[bits - 1]) << 1);
                if (code + (uint)counts[bits] > 1UL << bits)
                {
                    throw new InvalidDataException("A StuffIt method 13 Huffman table is oversubscribed.");
                }

                nextCode[bits] = checked((uint)code);
            }
            return nextCode;
        }

        private static uint ReverseBits(uint value, int count)
        {
            uint reversed = 0;
            for (int bit = 0; bit < count; bit++)
            {
                reversed |= ((value >> bit) & 1) << (count - bit - 1);
            }

            return reversed;
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

        public uint ReadBits(int count)
        {
            if (count is < 0 or > 32 || count > _input.Length * 8 - _position)
            {
                throw new InvalidDataException("A StuffIt method 13 fork ends inside its compressed bitstream.");
            }

            uint value = 0;
            for (int bit = 0; bit < count; bit++, _position++)
            {
                value |= (uint)((_input[_position >> 3] >> (_position & 7)) & 1) << bit;
            }

            return value;
        }
    }
}
