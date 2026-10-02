using System;
using System.IO;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes a Compact Pro LZH+RLE fork.</summary>
/// <remarks>The bitstream and window rules are fitted against the independent format description in
/// docs/formats/codecs/compact-pro-rle-lzh.md §1 and §2.</remarks>
internal static class CompactProLzhDecoder
{
    private const int WindowSize = 0x2000;
    private const int WindowMask = WindowSize - 1;
    private const int BlockLimit = 0x1FFF0;

    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        if (outputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        var output = new RleOutput(outputLength);
        if (outputLength == 0)
        {
            return output.Result;
        }

        var bits = new MsbBitReader(input);
        var window = new byte[WindowSize];
        int windowPosition = 0;
        window.AsSpan(WindowSize - 3).Clear();

        while (!output.IsComplete)
        {
            HuffmanTree literals = ReadTree(ref bits, 256);
            HuffmanTree lengths = ReadTree(ref bits, 64);
            HuffmanTree displacements = ReadTree(ref bits, 128);
            int blockSize = 0;

            while (blockSize < BlockLimit && !output.IsComplete)
            {
                bool literalToken = bits.ReadBit();
                if (literalToken)
                {
                    byte value = checked((byte)literals.ReadSymbol(ref bits));
                    window[windowPosition] = value;
                    windowPosition = (windowPosition + 1) & WindowMask;
                    blockSize = checked(blockSize + 2);
                    output.Write(value);
                    continue;
                }

                int length = lengths.ReadSymbol(ref bits);
                int upperDisplacement = displacements.ReadSymbol(ref bits);
                int lowerDisplacement = bits.ReadBits(6);
                int distance = (upperDisplacement << 6) | lowerDisplacement;
                if (distance == 0)
                {
                    distance = WindowSize;
                }

                blockSize = checked(blockSize + 3);
                for (int index = 0; index < length && !output.IsComplete; index++)
                {
                    int source = (windowPosition - distance) & WindowMask;
                    byte value = window[source];
                    window[windowPosition] = value;
                    windowPosition = (windowPosition + 1) & WindowMask;
                    output.Write(value);
                }
            }

            if (!output.IsComplete)
            {
                bits.SkipBlockPadding();
            }
        }

        return output.Result;
    }

    private static HuffmanTree ReadTree(ref MsbBitReader bits, int maximumSymbols)
    {
        int symbolCount = bits.ReadBits(8) * 2;
        if (symbolCount > maximumSymbols)
        {
            throw new InvalidDataException("A Compact Pro LZH code tree describes too many symbols.");
        }

        var codeLengths = new byte[maximumSymbols];
        for (int symbol = 0; symbol < symbolCount; symbol++)
        {
            int length = bits.ReadBits(4);
            if (length > 15)
            {
                throw new InvalidDataException("A Compact Pro LZH code length is invalid.");
            }

            codeLengths[symbol] = (byte)length;
        }
        return HuffmanTree.Create(codeLengths);
    }

    internal sealed class RleOutput(int outputLength)
    {
        private readonly byte[] result = new byte[outputLength];
        private int written;
        private bool escapePending;
        private bool countPending;
        private byte previous;
        private bool hasPrevious;

        public byte[] Result => result;
        public bool IsComplete => written == result.Length;
        public int Written => written;

        public void Write(byte value)
        {
            if (countPending)
            {
                countPending = false;
                if (value == 0)
                {
                    Emit(0x81);
                    Emit(0x82);
                }
                else
                {
                    int repeatCount = value - 1;
                    if (repeatCount > 0 && !hasPrevious)
                    {
                        throw new InvalidDataException("A Compact Pro RLE run has no preceding byte.");
                    }

                    if (repeatCount > result.Length - written)
                    {
                        throw new InvalidDataException("Compact Pro RLE output exceeds its declared fork length.");
                    }

                    result.AsSpan(written, repeatCount).Fill(previous);
                    written += repeatCount;
                }
                return;
            }

            if (escapePending)
            {
                escapePending = false;
                if (value == 0x82)
                {
                    countPending = true;
                    return;
                }

                Emit(0x81);
                if (!IsComplete)
                {
                    Write(value);
                }

                return;
            }

            if (value == 0x81)
            {
                escapePending = true;
            }
            else
            {
                Emit(value);
            }
        }

        private void Emit(byte value)
        {
            if (written == result.Length)
            {
                throw new InvalidDataException("Compact Pro RLE output exceeds its declared fork length.");
            }

            result[written++] = previous = value;
            hasPrevious = true;
        }
    }

    private sealed class HuffmanTree
    {
        private readonly Node? root;

        private HuffmanTree(Node? root) => this.root = root;

        public static HuffmanTree Create(ReadOnlySpan<byte> codeLengths)
        {
            Span<int> counts = stackalloc int[16];
            counts.Clear();
            foreach (byte length in codeLengths)
            {
                if (length != 0)
                {
                    counts[length]++;
                }
            }

            int remaining = 1;
            for (int length = 1; length <= 15; length++)
            {
                remaining = (remaining << 1) - counts[length];
                if (remaining < 0)
                {
                    throw new InvalidDataException("A Compact Pro LZH code tree is oversubscribed.");
                }
            }

            var nextCode = new int[16];
            int code = 0;
            for (int length = 1; length <= 15; length++)
            {
                code = (code + counts[length - 1]) << 1;
                nextCode[length] = code;
            }

            Node? root = null;
            for (int symbol = 0; symbol < codeLengths.Length; symbol++)
            {
                int length = codeLengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                int symbolCode = nextCode[length]++;
                if (symbolCode >= 1 << length)
                {
                    throw new InvalidDataException("A Compact Pro LZH code tree has an invalid canonical code.");
                }

                root ??= new Node();
                Node node = root;
                for (int bit = length - 1; bit >= 0; bit--)
                {
                    if (node.Symbol >= 0)
                    {
                        throw new InvalidDataException("A Compact Pro LZH code is a prefix of another code.");
                    }

                    bool one = ((symbolCode >> bit) & 1) != 0;
                    if (one)
                    {
                        node.One ??= new Node();
                    }
                    else
                    {
                        node.Zero ??= new Node();
                    }

                    node = (one ? node.One : node.Zero)!;
                }
                if (node.Symbol >= 0 || node.Zero is not null || node.One is not null)
                {
                    throw new InvalidDataException("A Compact Pro LZH code tree contains duplicate or prefix codes.");
                }

                node.Symbol = symbol;
            }
            return new HuffmanTree(root);
        }

        public int ReadSymbol(ref MsbBitReader bits)
        {
            Node? node = root;
            if (node is null)
            {
                throw new InvalidDataException("A Compact Pro LZH stream uses an empty code tree.");
            }

            while (node.Symbol < 0)
            {
                bool one = bits.ReadBit();
                node = one ? node.One : node.Zero;
                if (node is null)
                {
                    throw new InvalidDataException("A Compact Pro LZH stream follows an undefined Huffman code.");
                }
            }
            return node.Symbol;
        }

        private sealed class Node
        {
            public int Symbol { get; set; } = -1;
            public Node? Zero { get; set; }
            public Node? One { get; set; }
        }
    }

    private ref struct MsbBitReader(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> input = input;
        private int bitPosition;

        public bool ReadBit()
        {
            if (bitPosition >= input.Length * 8)
            {
                throw new InvalidDataException("A Compact Pro LZH stream ends inside a code tree or token.");
            }

            bool value = (input[bitPosition >> 3] & (0x80 >> (bitPosition & 7))) != 0;
            bitPosition++;
            return value;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                value = (value << 1) | (ReadBit() ? 1 : 0);
            }

            return value;
        }

        public void SkipBlockPadding()
        {
            int bytePosition = (bitPosition + 7) >> 3;
            bytePosition = checked(bytePosition + 2 + ((bytePosition & 1) == 0 ? 0 : 1));
            if (bytePosition > input.Length)
            {
                throw new InvalidDataException("A Compact Pro LZH block ends before its alignment padding.");
            }

            bitPosition = bytePosition << 3;
        }
    }
}
