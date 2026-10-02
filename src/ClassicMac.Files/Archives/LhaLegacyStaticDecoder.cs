using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes the legacy static-Huffman LZSS method used by LHA <c>-lh3-</c> files.</summary>
internal static class LhaLegacyStaticDecoder
{
    private const int WindowSize = 8192;
    private const int LiteralLengthSymbolCount = 286;
    private const int PositionSymbolCount = WindowSize / 64;
    private const int MinimumMatchLength = 3;
    private const int MaximumMatchLength = 256;
    private const int MatchLengthBias = 256 - MinimumMatchLength;

    public static byte[] DecodeLh3(ReadOnlySpan<byte> packed, int expandedSize)
    {
        if (expandedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expandedSize));
        }

        var bits = new BitReader(packed);
        byte[] window = new byte[WindowSize];
        window.AsSpan().Fill((byte)' ');
        byte[] output = new byte[expandedSize];
        int outputOffset = 0;
        int blockCommands = 0;
        HuffmanTable literalLengthTree = null!;
        HuffmanTable positionTree = null!;

        while (outputOffset < output.Length)
        {
            if (blockCommands == 0)
            {
                blockCommands = bits.Read(16);
                if (blockCommands == 0)
                {
                    throw new InvalidDataException("An LH3 block has no commands.");
                }

                literalLengthTree = ReadLiteralLengthTree(ref bits);
                positionTree = bits.Read(1) != 0
                    ? ReadPositionTree(ref bits)
                    : BuildReadyMadePositionTree();
            }

            blockCommands--;
            int symbol = literalLengthTree.ReadSymbol(ref bits);
            if (symbol < 256)
            {
                byte value = (byte)symbol;
                output[outputOffset++] = value;
                window[(outputOffset - 1) & (WindowSize - 1)] = value;
                continue;
            }

            if (symbol >= LiteralLengthSymbolCount)
            {
                throw new InvalidDataException("An LH3 literal/length symbol is invalid.");
            }

            int lengthCode = symbol;
            if (symbol == LiteralLengthSymbolCount - 1)
            {
                lengthCode += bits.Read(8);
            }

            int matchLength = lengthCode - MatchLengthBias;
            if (matchLength is < MinimumMatchLength or > MaximumMatchLength)
            {
                throw new InvalidDataException("An LH3 match length is outside the method's range.");
            }

            if (matchLength > output.Length - outputOffset)
            {
                throw new InvalidDataException("An LH3 match exceeds the declared expanded size.");
            }

            int positionSymbol = positionTree.ReadSymbol(ref bits);
            int sourceOffset = (positionSymbol << 6) | bits.Read(6);
            for (int index = 0; index < matchLength; index++)
            {
                byte value = window[(sourceOffset + index) & (WindowSize - 1)];
                output[outputOffset++] = value;
                window[(outputOffset - 1) & (WindowSize - 1)] = value;
            }
        }

        return output;
    }

    private static HuffmanTable ReadLiteralLengthTree(ref BitReader bits)
    {
        byte[] lengths = new byte[LiteralLengthSymbolCount];
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            lengths[symbol] = bits.Read(1) == 0 ? (byte)0 : checked((byte)(bits.Read(4) + 1));
            if (symbol == 2 && lengths[0] == 1 && lengths[1] == 1 && lengths[2] == 1)
            {
                int onlySymbol = bits.Read(9);
                if (onlySymbol >= LiteralLengthSymbolCount)
                {
                    throw new InvalidDataException("An LH3 single-symbol tree names an invalid symbol.");
                }

                return HuffmanTable.SingleSymbol(onlySymbol);
            }
        }
        return HuffmanTable.FromLengths(lengths);
    }

    private static HuffmanTable ReadPositionTree(ref BitReader bits)
    {
        byte[] lengths = new byte[PositionSymbolCount];
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            lengths[symbol] = (byte)bits.Read(4);
            if (symbol == 2 && lengths[0] == 1 && lengths[1] == 1 && lengths[2] == 1)
            {
                int onlySymbol = bits.Read(7);
                if (onlySymbol >= PositionSymbolCount)
                {
                    throw new InvalidDataException("An LH3 single-symbol position tree is invalid.");
                }

                return HuffmanTable.SingleSymbol(onlySymbol);
            }
        }
        return HuffmanTable.FromLengths(lengths);
    }

    private static HuffmanTable BuildReadyMadePositionTree()
    {
        ReadOnlySpan<int> lengthBoundaries = [1, 1, 3, 6, 13, 31, 78];
        byte[] lengths = new byte[PositionSymbolCount];
        int boundary = 0;
        int codeLength = 2;
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            while (boundary < lengthBoundaries.Length && lengthBoundaries[boundary] == symbol)
            {
                codeLength++;
                boundary++;
            }
            lengths[symbol] = checked((byte)codeLength);
        }
        return HuffmanTable.FromLengths(lengths);
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _byteOffset;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data) => _data = data;

        public int Read(int count)
        {
            int value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                if (_byteOffset >= _data.Length)
                {
                    throw new InvalidDataException("An LH3 bitstream is truncated.");
                }

                value = (value << 1) | ((_data[_byteOffset] >> (7 - _bitOffset)) & 1);
                if (++_bitOffset == 8)
                {
                    _bitOffset = 0;
                    _byteOffset++;
                }
            }
            return value;
        }
    }

    private sealed class HuffmanTable
    {
        private readonly Dictionary<int, int> _symbolsByCode;
        private readonly int _singleSymbol;

        private HuffmanTable(Dictionary<int, int> symbolsByCode, int maximumCodeLength, int singleSymbol)
        {
            _symbolsByCode = symbolsByCode;
            MaximumCodeLength = maximumCodeLength;
            _singleSymbol = singleSymbol;
        }

        private int MaximumCodeLength { get; }

        public static HuffmanTable SingleSymbol(int symbol) => new([], 0, symbol);

        public static HuffmanTable FromLengths(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[17];
            counts.Clear();
            int maximumLength = 0;
            foreach (byte length in lengths)
            {
                if (length > 16)
                {
                    throw new InvalidDataException("An LH3 Huffman code is too long.");
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
                throw new InvalidDataException("An LH3 Huffman tree has no symbols.");
            }

            int remainingCodes = 1;
            for (int length = 1; length <= maximumLength; length++)
            {
                remainingCodes = (remainingCodes << 1) - counts[length];
                if (remainingCodes < 0)
                {
                    throw new InvalidDataException("An LH3 Huffman tree is oversubscribed.");
                }
            }

            Span<int> nextCode = stackalloc int[17];
            nextCode.Clear();
            int code = 0;
            for (int length = 1; length <= maximumLength; length++)
            {
                code = (code + counts[length - 1]) << 1;
                nextCode[length] = code;
            }

            var symbolsByCode = new Dictionary<int, int>();
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                int symbolCode = nextCode[length]++;
                symbolsByCode.Add((length << 16) | symbolCode, symbol);
            }
            return new HuffmanTable(symbolsByCode, maximumLength, -1);
        }

        public int ReadSymbol(ref BitReader bits)
        {
            if (_singleSymbol >= 0)
            {
                return _singleSymbol;
            }

            int code = 0;
            for (int length = 1; length <= MaximumCodeLength; length++)
            {
                code = (code << 1) | bits.Read(1);
                if (_symbolsByCode.TryGetValue((length << 16) | code, out int symbol))
                {
                    return symbol;
                }
            }
            throw new InvalidDataException("An LH3 Huffman code does not match its tree.");
        }
    }
}
