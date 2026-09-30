using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassicMac.Files.Archives;

/// <summary>Decodes a StuffIt method 15 (Arsenic) fork.</summary>
/// <remarks>
/// This is an independent decoder based on Matthew T. Russotto's published Arsenic format description. The layout
/// remains fitted against original StuffIt archives because no vendor specification is available.
/// </remarks>
internal static class StuffItMethod15Decoder
{
    private static readonly (int First, int Last, int Increment)[] MtfModelParameters =
    [
        (2, 3, 8),
        (4, 7, 4),
        (8, 15, 4),
        (16, 31, 4),
        (32, 63, 2),
        (64, 127, 2),
        (128, 255, 1),
    ];

    private static readonly ushort[] RandomizationTable =
    [
        238, 86, 248, 195, 157, 159, 174, 44, 173, 205, 36, 157, 166, 257, 24, 185, 161, 130, 117, 233,
        159, 85, 102, 106, 134, 113, 220, 132, 86, 150, 86, 161, 132, 120, 183, 50, 106, 3, 227, 2, 17,
        257, 8, 68, 131, 256, 67, 227, 28, 240, 134, 106, 107, 15, 3, 45, 134, 23, 123, 16, 246, 128,
        120, 122, 161, 225, 239, 140, 246, 135, 75, 167, 226, 119, 250, 184, 129, 238, 119, 192, 157,
        41, 32, 39, 113, 18, 224, 107, 209, 124, 10, 137, 125, 135, 196, 257, 193, 49, 175, 56, 3, 104,
        27, 118, 121, 63, 219, 199, 27, 54, 123, 226, 99, 129, 238, 12, 99, 139, 120, 56, 151, 155,
        215, 143, 221, 242, 163, 119, 140, 195, 57, 32, 179, 18, 17, 14, 23, 66, 128, 44, 196, 146, 89,
        200, 219, 64, 118, 100, 180, 85, 26, 158, 254, 95, 6, 60, 65, 239, 212, 170, 152, 41, 205, 31,
        2, 168, 135, 210, 160, 147, 152, 239, 12, 67, 237, 157, 194, 235, 129, 233, 100, 35, 104, 30,
        37, 87, 222, 154, 207, 127, 229, 186, 65, 234, 234, 54, 26, 40, 121, 32, 94, 24, 78, 124, 142,
        88, 122, 239, 145, 2, 147, 187, 86, 161, 73, 27, 121, 146, 243, 88, 79, 82, 156, 2, 119, 175,
        42, 143, 73, 208, 153, 77, 152, 257, 96, 147, 256, 117, 49, 206, 73, 32, 86, 87, 226, 245, 38,
        43, 138, 191, 222, 208, 131, 52, 244, 23,
    ];

    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        if (outputLength < 0)
            throw new InvalidDataException("A StuffIt method 15 fork has a negative expanded length.");
        var reader = new ArithmeticReader(input);
        if ((byte)reader.ReadInitialBits(8) != (byte)'A' || (byte)reader.ReadInitialBits(8) != (byte)'s')
            throw new InvalidDataException("A StuffIt method 15 stream has an invalid signature.");

        int blockBits = 9 + (int)reader.ReadInitialBits(4);
        if (blockBits > 24)
            throw new InvalidDataException("A StuffIt method 15 stream declares an invalid block size.");
        int maximumBlockLength = 1 << blockBits;
        var output = new List<byte>(outputLength);

        while (reader.ReadInitialBit() == 0)
        {
            bool randomized = reader.ReadInitialBit() != 0;
            int primaryIndex = (int)reader.ReadInitialBits(blockBits);
            byte[] lastColumn = DecodeBlock(reader, maximumBlockLength);
            if (lastColumn.Length == 0 || primaryIndex >= lastColumn.Length)
                throw new InvalidDataException("A StuffIt method 15 block has an invalid BWT index.");

            byte[] block = InverseBurrowsWheeler(lastColumn, primaryIndex);
            if (randomized)
                Derandomize(block);
            AppendRunExpanded(block, output, outputLength);
        }

        uint storedCrc = reader.ReadInitialBits(32);
        if (storedCrc != Crc32(output))
            throw new InvalidDataException("A StuffIt method 15 fork has an invalid CRC-32.");
        if (output.Count != outputLength)
            throw new InvalidDataException($"StuffIt method 15 produced {output.Count} of {outputLength} declared bytes.");

        return output.ToArray();
    }

    private static byte[] DecodeBlock(ArithmeticReader reader, int maximumLength)
    {
        var selector = new AdaptiveModel(0, 10, 8, 1024);
        var models = MtfModelParameters.Select(parameters =>
            new AdaptiveModel(parameters.First, parameters.Last, parameters.Increment, 1024)).ToArray();
        var moveToFront = new byte[256];
        for (int index = 0; index < moveToFront.Length; index++)
            moveToFront[index] = (byte)index;

        var lastColumn = new List<byte>(Math.Min(maximumLength, 4096));
        int zeroState = 0;
        long zeroCount = 0;
        while (true)
        {
            int symbol = reader.Decode(selector);
            if (symbol is 0 or 1)
            {
                if (zeroState == 0)
                {
                    zeroState = 1;
                    zeroCount = symbol == 0 ? 1 : 2;
                }
                else
                {
                    if (zeroState > maximumLength / 2)
                        throw new InvalidDataException("A StuffIt method 15 zero run exceeds its block size.");
                    zeroState *= 2;
                    zeroCount += symbol == 0 ? zeroState : 2L * zeroState;
                }
                if (zeroCount > maximumLength - lastColumn.Count)
                    throw new InvalidDataException("A StuffIt method 15 block exceeds its declared block size.");
                continue;
            }

            if (zeroState != 0)
            {
                AppendMoveToFrontZeros(zeroCount, moveToFront, lastColumn, maximumLength);
                zeroState = 0;
                zeroCount = 0;
            }

            if (symbol == 10)
                return lastColumn.ToArray();

            int mtfIndex = symbol == 2 ? 1 : reader.Decode(models[symbol - 3]);
            if ((uint)mtfIndex >= moveToFront.Length)
                throw new InvalidDataException("A StuffIt method 15 block contains an invalid MTF index.");
            if (lastColumn.Count == maximumLength)
                throw new InvalidDataException("A StuffIt method 15 block exceeds its declared block size.");

            byte value = moveToFront[mtfIndex];
            lastColumn.Add(value);
            if (mtfIndex > 0)
            {
                moveToFront.AsSpan(0, mtfIndex).CopyTo(moveToFront.AsSpan(1));
                moveToFront[0] = value;
            }
        }
    }

    private static void AppendMoveToFrontZeros(long count, byte[] moveToFront, List<byte> output, int maximumLength)
    {
        if (count > maximumLength - output.Count)
            throw new InvalidDataException("A StuffIt method 15 zero run exceeds its block size.");
        byte value = moveToFront[0];
        for (long index = 0; index < count; index++)
            output.Add(value);
    }

    private static byte[] InverseBurrowsWheeler(byte[] lastColumn, int primaryIndex)
    {
        var counts = new int[256];
        foreach (byte value in lastColumn)
            counts[value]++;

        var nextPositions = new int[256];
        int cumulative = 0;
        for (int value = 0; value < counts.Length; value++)
        {
            nextPositions[value] = cumulative;
            cumulative += counts[value];
        }

        var next = new int[lastColumn.Length];
        for (int index = 0; index < lastColumn.Length; index++)
            next[nextPositions[lastColumn[index]]++] = index;

        var output = new byte[lastColumn.Length];
        int row = next[primaryIndex];
        for (int index = 0; index < output.Length; index++)
        {
            output[index] = lastColumn[row];
            row = next[row];
        }
        return output;
    }

    private static void Derandomize(Span<byte> block)
    {
        int position = RandomizationTable[0];
        int index = 0;
        while (position < block.Length)
        {
            block[position] ^= 1;
            index = (index + 1) & 255;
            position += RandomizationTable[index];
        }
    }

    private static void AppendRunExpanded(ReadOnlySpan<byte> block, List<byte> output, int maximumLength)
    {
        int index = 0;
        while (index < block.Length)
        {
            byte value = block[index++];
            if (index + 2 < block.Length && block[index] == value && block[index + 1] == value &&
                block[index + 2] == value)
            {
                index += 3;
                if (index == block.Length)
                    throw new InvalidDataException("A StuffIt method 15 run is missing its repeat count.");
                int repeatCount = block[index++];
                AppendByte(value, output, maximumLength);
                AppendByte(value, output, maximumLength);
                AppendByte(value, output, maximumLength);
                AppendByte(value, output, maximumLength);
                if (repeatCount > maximumLength - output.Count)
                    throw new InvalidDataException("A StuffIt method 15 run exceeds its declared fork length.");
                for (int repeat = 0; repeat < repeatCount; repeat++)
                    output.Add(value);
            }
            else
            {
                AppendByte(value, output, maximumLength);
            }
        }
    }

    private static void AppendByte(byte value, List<byte> output, int maximumLength)
    {
        if (output.Count == maximumLength)
            throw new InvalidDataException("StuffIt method 15 output exceeds its declared fork length.");
        output.Add(value);
    }

    private static uint Crc32(IReadOnlyList<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return ~crc;
    }

    private sealed class AdaptiveModel(int first, int last, int increment, int frequencyLimit)
    {
        private readonly int[] _frequencies = Enumerable.Repeat(increment, last - first + 1).ToArray();
        private int _total = (last - first + 1) * increment;

        public int First { get; } = first;
        public int Last { get; } = last;
        public int Increment { get; } = increment;
        public int FrequencyLimit { get; } = frequencyLimit;
        public int Total => _total;

        public (int Symbol, int Low, int High) FindSymbol(int frequency)
        {
            int low = 0;
            for (int index = 0; index < _frequencies.Length; index++)
            {
                int high = low + _frequencies[index];
                if (frequency < high)
                    return (First + index, low, high);
                low = high;
            }
            throw new InvalidDataException("A StuffIt method 15 arithmetic code is outside its model.");
        }

        public void Update(int symbol)
        {
            int index = symbol - First;
            if ((uint)index >= _frequencies.Length || symbol > Last)
                throw new InvalidDataException("A StuffIt method 15 model produced an invalid symbol.");
            _frequencies[index] += Increment;
            _total += Increment;
            if (_total <= FrequencyLimit)
                return;

            _total = 0;
            for (int frequencyIndex = 0; frequencyIndex < _frequencies.Length; frequencyIndex++)
            {
                _frequencies[frequencyIndex] = (_frequencies[frequencyIndex] + 1) / 2;
                _total += _frequencies[frequencyIndex];
            }
        }
    }

    private sealed class ArithmeticReader
    {
        private const int One = 1 << 25;
        private const int Half = 1 << 24;
        private readonly byte[] _input;
        private readonly AdaptiveModel _initialModel = new(0, 1, 1, 256);
        private long _bitPosition;
        private int _range = One;
        private int _code;

        public ArithmeticReader(ReadOnlySpan<byte> input)
        {
            _input = input.ToArray();
            for (int bit = 0; bit < 26; bit++)
                _code = (_code << 1) | ReadBit();
        }

        public int ReadInitialBit() => DecodeInitialBit();

        public uint ReadInitialBits(int count)
        {
            uint value = 0;
            for (int bit = 0; bit < count; bit++)
                value |= (uint)DecodeInitialBit() << bit;
            return value;
        }

        public int Decode(AdaptiveModel model)
        {
            int scale = _range / model.Total;
            if (scale == 0)
                throw new InvalidDataException("A StuffIt method 15 arithmetic model exceeds its range.");
            int frequency = _code / scale;
            if ((uint)frequency >= model.Total)
                throw new InvalidDataException("A StuffIt method 15 arithmetic code is invalid.");

            (int symbol, int low, int high) = model.FindSymbol(frequency);
            int lowIncrement = scale * low;
            _code -= lowIncrement;
            _range = high == model.Total ? _range - lowIncrement : (high - low) * scale;
            while (_range <= Half)
            {
                _range <<= 1;
                _code = (_code << 1) | ReadBit();
            }
            model.Update(symbol);
            return symbol;
        }

        private int DecodeInitialBit()
        {
            return Decode(_initialModel);
        }

        private int ReadBit()
        {
            if (_bitPosition >= _input.LongLength * 8)
                throw new InvalidDataException("A StuffIt method 15 fork ends inside its arithmetic bitstream.");
            int position = checked((int)(_bitPosition >> 3));
            int bit = (_input[position] >> (7 - (int)(_bitPosition & 7))) & 1;
            _bitPosition++;
            return bit;
        }
    }
}
