using System;
using System.IO;

namespace ClassicMac.Files.Compression;

/// <summary>Decodes AutoDoubler's block-based methods 6 (AD2) and 9 (AD1).</summary>
/// <remarks>The block structure is fitted against XADMaster and checked against original DiskDoubler Pro 4.1.1
/// AD1 and AD2 files in the feature tests.</remarks>
internal static class DiskDoublerAdnDecoder
{
    private const int BlockHeaderLength = 12;
    private const int MaximumBlockLength = 0x2000;

    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        if (outputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        }

        byte[] output = new byte[outputLength];
        int inputOffset = 0;
        int outputOffset = 0;

        while (outputOffset < output.Length)
        {
            if (input.Length - inputOffset < BlockHeaderLength)
            {
                throw new InvalidDataException("A DiskDoubler ADn block header is truncated.");
            }

            ReadOnlySpan<byte> header = input.Slice(inputOffset, BlockHeaderLength);
            byte headerXor = 0;
            for (int index = 0; index < BlockHeaderLength - 1; index++)
            {
                headerXor ^= header[index];
            }

            if (headerXor != header[^1])
            {
                throw new InvalidDataException("A DiskDoubler ADn block header checksum is invalid.");
            }

            int compressedLength = U16(header, 0);
            int blockLength = U16(header, 2);
            if (blockLength is 0 or > MaximumBlockLength || blockLength > output.Length - outputOffset)
            {
                throw new InvalidDataException("A DiskDoubler ADn block has an invalid expanded length.");
            }

            int dataOffset = checked(inputOffset + BlockHeaderLength);
            if (compressedLength > input.Length - dataOffset)
            {
                throw new InvalidDataException("A DiskDoubler ADn block extends past its fork.");
            }

            ReadOnlySpan<byte> compressed = input.Slice(dataOffset, compressedLength);
            if ((header[9] & 1) != 0)
            {
                if (compressedLength < blockLength)
                {
                    throw new InvalidDataException("A raw DiskDoubler ADn block is truncated.");
                }

                compressed[..blockLength].CopyTo(output.AsSpan(outputOffset));
            }
            else
            {
                DecodeCompressedBlock(compressed, output, outputOffset, blockLength);
            }

            outputOffset = checked(outputOffset + blockLength);
            inputOffset = checked(dataOffset + compressedLength);
        }

        return output;
    }

    private static void DecodeCompressedBlock(ReadOnlySpan<byte> input, byte[] output, int outputOffset,
        int outputLength)
    {
        var bits = new MsbBitReader(input);
        int position = outputOffset;
        int end = checked(outputOffset + outputLength);
        while (position < end)
        {
            if (!bits.ReadBit())
            {
                output[position++] = checked((byte)bits.ReadBits(8));
                continue;
            }

            bool farOffset = bits.ReadBit();
            int distance = bits.ReadBits(farOffset ? 12 : 8);
            if (distance <= 0 || distance > position - outputOffset)
            {
                throw new InvalidDataException("A DiskDoubler ADn match reaches before the current block.");
            }

            int length;
            if (!bits.ReadBit())
            {
                length = 2;
            }
            else if (!bits.ReadBit())
            {
                length = bits.ReadBit() ? 4 : 3;
            }
            else
            {
                length = bits.ReadBits(4) + 5;
            }

            length = Math.Min(length, end - position);
            if (length > distance)
            {
                throw new InvalidDataException("A DiskDoubler ADn match overlaps its source bytes.");
            }

            for (int index = 0; index < length; index++)
            {
                output[position + index] = output[position - distance + index];
            }

            position += length;
        }
    }

    private static int U16(ReadOnlySpan<byte> data, int offset) => (data[offset] << 8) | data[offset + 1];

    private ref struct MsbBitReader(ReadOnlySpan<byte> input)
    {
        private readonly ReadOnlySpan<byte> _input = input;
        private int _bitPosition;

        public bool ReadBit()
        {
            if (_bitPosition >= _input.Length * 8)
            {
                throw new InvalidDataException("A DiskDoubler ADn compressed block is truncated.");
            }

            bool value = (_input[_bitPosition >> 3] & (0x80 >> (_bitPosition & 7))) != 0;
            _bitPosition++;
            return value;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int index = 0; index < count; index++)
            {
                value = (value << 1) | (ReadBit() ? 1 : 0);
            }

            return value;
        }
    }
}
