using System;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Compression;

/// <summary>Decodes DiskDoubler's block-based DDn compression method.</summary>
/// <remarks>The block structure and token rules are fitted against XADMaster and checked against an original
/// DiskDoubler Pro 4.1.1 DD3 file in the feature tests.</remarks>
internal static class DiskDoublerMethod10Decoder
{
    private const int BlockHeaderLength = 22;
    private const int MaximumBlockLength = 65536;
    private const int WindowLength = 65536;

    public static byte[] Decode(ReadOnlyMemory<byte> encoded, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputLength);
        var reader = new BigEndianReader(encoded);
        ReadOnlySpan<byte> input = encoded.Span;

        byte[] output = new byte[outputLength];
        int inputOffset = 0;
        int outputOffset = 0;

        while (outputOffset < output.Length)
        {
            if (input.Length - inputOffset < BlockHeaderLength)
            {
                throw new InvalidDataException("A DiskDoubler method-10 block header is truncated.");
            }

            var header = reader.ReadSubReaderAt(inputOffset, BlockHeaderLength);
            int expectedHeaderXor = 0;
            for (int index = 0; index < BlockHeaderLength - 1; index++)
            {
                expectedHeaderXor ^= header.ReadByteAt(index);
            }

            if (expectedHeaderXor != header.ReadByteAt(BlockHeaderLength - 1))
            {
                throw new InvalidDataException("A DiskDoubler method-10 block header checksum is invalid.");
            }

            uint storedLength = header.ReadUInt32();
            int literalCount = header.ReadUInt16();
            int offsetCount = header.ReadUInt16();
            int lengthStreamLength = header.ReadUInt16();
            int literalStreamLength = header.ReadUInt16();
            int offsetStreamLength = header.ReadUInt16();
            byte flags = header.ReadByte();
            byte expectedBlockXor = header.ReadByteAt(19);
            // Checked before it is an int: a damaged length can pass int's range.
            if (storedLength is 0 or > MaximumBlockLength || storedLength > output.Length - outputOffset)
            {
                throw new InvalidDataException("A DiskDoubler method-10 block has an invalid expanded length.");
            }

            int blockLength = (int)storedLength;

            int dataOffset = checked(inputOffset + BlockHeaderLength);
            if ((flags & 0x40) != 0)
            {
                if (blockLength > input.Length - dataOffset)
                {
                    throw new InvalidDataException("A raw DiskDoubler method-10 block is truncated.");
                }

                input.Slice(dataOffset, blockLength).CopyTo(output.AsSpan(outputOffset));
                dataOffset += blockLength;
            }
            else
            {
                long compressedLength = (long)offsetStreamLength + literalStreamLength + lengthStreamLength;
                if (compressedLength > input.Length - dataOffset)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 block extends past its fork.");
                }

                if (literalCount > blockLength || offsetCount > blockLength ||
                    literalCount + 2L * offsetCount > MaximumBlockLength)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 block has invalid token counts.");
                }

                ReadOnlySpan<byte> offsetStream = input.Slice(dataOffset, offsetStreamLength);
                ReadOnlySpan<byte> literalStream = input.Slice(dataOffset + offsetStreamLength, literalStreamLength);
                ReadOnlySpan<byte> lengthStream = input.Slice(dataOffset + offsetStreamLength + literalStreamLength,
                    lengthStreamLength);
                DecodeCompressedBlock(offsetStream, literalStream, lengthStream, flags, literalCount, offsetCount,
                    output, outputOffset, blockLength);
                dataOffset = checked(dataOffset + (int)compressedLength);
            }

            int actualBlockXor = 0;
            for (int index = 0; index < blockLength; index++)
            {
                actualBlockXor ^= output[outputOffset + index];
            }

            if (actualBlockXor != expectedBlockXor)
            {
                throw new InvalidDataException("A DiskDoubler method-10 block checksum is invalid.");
            }

            outputOffset = checked(outputOffset + blockLength);
            inputOffset = dataOffset;
        }

        return output;
    }

    private static void DecodeCompressedBlock(ReadOnlySpan<byte> offsetStream, ReadOnlySpan<byte> literalStream,
        ReadOnlySpan<byte> lengthStream, byte flags, int literalCount, int offsetCount, byte[] output,
        int outputOffset, int blockLength)
    {
        int[] offsets = new int[offsetCount];
        if (offsetCount > 0)
        {
            var bits = new MsbBitReader(offsetStream);
            HuffmanCode code = ReadCode(ref bits);
            for (int index = 0; index < offsets.Length; index++)
            {
                int slot = code.ReadSymbol(ref bits);
                if (slot < 4)
                {
                    offsets[index] = slot + 1;
                    continue;
                }

                int extraBits = slot / 2 - 1;
                if (extraBits > 15)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 offset code is out of range.");
                }

                int start = ((2 + (slot & 1)) << extraBits) + 1;
                offsets[index] = checked(start + (int)bits.ReadBits(extraBits));
            }
        }

        byte[] literals = new byte[literalCount];
        if (literalCount > 0)
        {
            if ((flags & 0x80) != 0)
            {
                var bits = new MsbBitReader(literalStream);
                HuffmanCode code = ReadCode(ref bits);
                for (int index = 0; index < literals.Length; index++)
                {
                    literals[index] = checked((byte)code.ReadSymbol(ref bits));
                }
            }
            else
            {
                if (literalCount > literalStream.Length)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 literal stream is truncated.");
                }

                literalStream[..literalCount].CopyTo(literals);
            }
        }

        var lengthBits = new MsbBitReader(lengthStream);
        HuffmanCode lengthCode = ReadCode(ref lengthBits);
        int blockEnd = outputOffset + blockLength;
        int position = outputOffset;
        int literalIndex = 0;
        int offsetIndex = 0;
        while (position < blockEnd)
        {
            int code = lengthCode.ReadSymbol(ref lengthBits);
            if (code == 0)
            {
                if (literalIndex >= literals.Length)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 token references a missing literal.");
                }

                output[position++] = literals[literalIndex++];
            }
            else if (code < 128)
            {
                if (offsetIndex >= offsets.Length)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 token references a missing offset.");
                }

                int distance = offsets[offsetIndex++];
                int length = Math.Min(code + 2, blockEnd - position);
                if (distance <= 0 || distance > position)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 match reaches before its history window.");
                }

                for (int index = 0; index < length; index++)
                {
                    int source = position - distance;
                    if (position - source > WindowLength)
                    {
                        throw new InvalidDataException("A DiskDoubler method-10 match exceeds its history window.");
                    }

                    output[position++] = output[source];
                }
            }
            else
            {
                int exponent = code - 128;
                if (exponent > 16)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 literal run is too long.");
                }

                int length = Math.Min(1 << exponent, blockEnd - position);
                if (length > literals.Length - literalIndex)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 literal run is truncated.");
                }

                literals.AsSpan(literalIndex, length).CopyTo(output.AsSpan(position));
                literalIndex += length;
                position += length;
            }
        }
    }

    private static HuffmanCode ReadCode(ref MsbBitReader bits)
    {
        uint header = bits.ReadBits(32);
        int symbolCount = checked((int)((header >> 24) & 0xFF) + 1);
        int codeBytes = (int)((header >> 13) & 0x7FF);
        int maximumLength = (int)((header >> 8) & 0x1F);
        int lengthBits = (int)((header >> 3) & 0x1F);
        bool zeroCoded = (header & 0x04) != 0;
        int tableEnd = checked(bits.BytePosition + codeBytes);
        if (maximumLength is 0 or > 31 || lengthBits is 0 or > 31 || tableEnd > bits.Length)
        {
            throw new InvalidDataException("A DiskDoubler method-10 Huffman table header is invalid.");
        }

        byte[] lengths = new byte[symbolCount];
        for (int index = 0; index < lengths.Length; index++)
        {
            int length;
            if (zeroCoded && !bits.ReadBit())
            {
                lengths[index] = 0;
                continue;
            }
            length = checked((int)bits.ReadBits(lengthBits));
            if (length > maximumLength)
            {
                throw new InvalidDataException("A DiskDoubler method-10 Huffman code exceeds its maximum length.");
            }

            lengths[index] = (byte)length;
        }

        bits.SkipToByte(tableEnd);
        return HuffmanCode.Create(lengths, maximumLength);
    }

    private sealed class HuffmanCode
    {
        private readonly Node? _root;

        private HuffmanCode(Node? root) => _root = root;

        public static HuffmanCode Create(ReadOnlySpan<byte> lengths, int maximumLength)
        {
            int[] counts = new int[maximumLength + 1];
            foreach (byte length in lengths)
            {
                if (length != 0)
                {
                    counts[length]++;
                }
            }

            long remaining = 1;
            for (int length = 1; length <= maximumLength; length++)
            {
                remaining = checked((remaining << 1) - counts[length]);
                if (remaining < 0)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 Huffman tree is oversubscribed.");
                }
            }

            long[] nextCode = new long[maximumLength + 1];
            long code = 0;
            for (int length = 1; length <= maximumLength; length++)
            {
                code = checked((code + counts[length - 1]) << 1);
                nextCode[length] = code;
            }

            Node? root = null;
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                {
                    continue;
                }

                long symbolCode = nextCode[length]++;
                if (symbolCode >= 1L << length)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 Huffman code is invalid.");
                }

                Node node = root ??= new Node();
                for (int bit = length - 1; bit >= 0; bit--)
                {
                    if (node.Symbol >= 0)
                    {
                        throw new InvalidDataException("A DiskDoubler method-10 Huffman tree is not prefix-free.");
                    }

                    bool one = ((symbolCode >> bit) & 1) != 0;
                    Node child;
                    if (one)
                    {
                        child = node.One ??= new Node();
                    }
                    else
                    {
                        child = node.Zero ??= new Node();
                    }

                    node = child;
                }
                if (node.Symbol >= 0 || node.Zero is not null || node.One is not null)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 Huffman tree has duplicate codes.");
                }

                node.Symbol = symbol;
            }
            return new HuffmanCode(root);
        }

        public int ReadSymbol(ref MsbBitReader bits)
        {
            Node? node = _root;
            if (node is null)
            {
                throw new InvalidDataException("A DiskDoubler method-10 stream uses an empty Huffman tree.");
            }

            while (node.Symbol < 0)
            {
                bool one = bits.ReadBit();
                node = one ? node.One : node.Zero;
                if (node is null)
                {
                    throw new InvalidDataException("A DiskDoubler method-10 stream uses an undefined Huffman code.");
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
        private readonly ReadOnlySpan<byte> _input = input;
        private int _bitPosition;

        public readonly int Length => _input.Length;
        public readonly int BytePosition => checked((_bitPosition + 7) / 8);

        public bool ReadBit()
        {
            if (_bitPosition >= _input.Length * 8)
            {
                throw new InvalidDataException("A DiskDoubler method-10 Huffman stream is truncated.");
            }

            bool value = (_input[_bitPosition >> 3] & (0x80 >> (_bitPosition & 7))) != 0;
            _bitPosition++;
            return value;
        }

        public uint ReadBits(int count)
        {
            uint value = 0;
            for (int index = 0; index < count; index++)
            {
                value = (value << 1) | (ReadBit() ? 1u : 0u);
            }

            return value;
        }

        public void SkipToByte(int bytePosition)
        {
            if (bytePosition < BytePosition || bytePosition > _input.Length)
            {
                throw new InvalidDataException("A DiskDoubler method-10 Huffman table has an invalid byte length.");
            }

            _bitPosition = checked(bytePosition * 8);
        }
    }
}
