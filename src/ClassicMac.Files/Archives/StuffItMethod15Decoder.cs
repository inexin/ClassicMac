using System;
using System.IO;

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
        {
            throw new InvalidDataException("A StuffIt method 15 fork has a negative expanded length.");
        }

        var reader = new ArithmeticReader(input);
        if ((byte)reader.ReadInitialBits(8) != (byte)'A' || (byte)reader.ReadInitialBits(8) != (byte)'s')
        {
            throw new InvalidDataException("A StuffIt method 15 stream has an invalid signature.");
        }

        // 4 bits: blocks of 2^9 to 2^24 bytes.
        int blockBits = 9 + (int)reader.ReadInitialBits(4);
        int maximumBlockLength = 1 << blockBits;
        var output = new byte[outputLength];
        int written = 0;
        var state = new BlockState();

        while (reader.ReadInitialBit() == 0)
        {
            bool randomized = reader.ReadInitialBit() != 0;
            int primaryIndex = (int)reader.ReadInitialBits(blockBits);
            int length = DecodeBlock(reader, state, maximumBlockLength);
            if (length == 0 || primaryIndex >= length)
            {
                throw new InvalidDataException("A StuffIt method 15 block has an invalid BWT index.");
            }

            Span<byte> block = InverseBurrowsWheeler(state, length, primaryIndex);
            if (randomized)
            {
                Derandomize(block);
            }

            written = AppendRunExpanded(block, output, written);
        }

        uint storedCrc = reader.ReadInitialBits(32);
        if (storedCrc != Crc32.Compute(output.AsSpan(0, written)))
        {
            throw new InvalidDataException("A StuffIt method 15 fork has an invalid CRC-32.");
        }

        if (written != outputLength)
        {
            throw new InvalidDataException($"StuffIt method 15 produced {written} of {outputLength} declared bytes.");
        }

        return output;
    }

    // A stream's working buffers, grown as its blocks need and kept from one block to the next: the block's last
    // column, the inverse transform's links and the block it gives.
    private sealed class BlockState
    {
        public readonly AdaptiveModel Selector = new(0, 10, 8, 1024);
        public readonly AdaptiveModel[] Models = Array.ConvertAll(MtfModelParameters, p => new AdaptiveModel(p.First, p.Last, p.Increment, 1024));
        public readonly byte[] MoveToFront = new byte[256];
        public byte[] LastColumn = new byte[4096];
        public int[] Next = [];
        public byte[] Block = [];

        // Each block starts with fresh models and the identity order.
        public void Reset()
        {
            Selector.Reset();
            foreach (var model in Models)
            {
                model.Reset();
            }

            for (int index = 0; index < MoveToFront.Length; index++)
            {
                MoveToFront[index] = (byte)index;
            }
        }

        public void Reserve(int length, int maximumLength)
        {
            if (length <= LastColumn.Length)
            {
                return;
            }

            var larger = new byte[(int)Math.Min(Math.Max((long)LastColumn.Length * 2, length), maximumLength)];
            LastColumn.AsSpan().CopyTo(larger);
            LastColumn = larger;
        }
    }

    // Decodes one block's last column into state.LastColumn; returns its length.
    private static int DecodeBlock(ArithmeticReader reader, BlockState state, int maximumLength)
    {
        state.Reset();
        var moveToFront = state.MoveToFront;
        int count = 0;
        int zeroState = 0;
        long zeroCount = 0;
        while (true)
        {
            int symbol = reader.Decode(state.Selector);
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
                    {
                        throw new InvalidDataException("A StuffIt method 15 zero run exceeds its block size.");
                    }

                    zeroState *= 2;
                    zeroCount += symbol == 0 ? zeroState : 2L * zeroState;
                }
                if (zeroCount > maximumLength - count)
                {
                    throw new InvalidDataException("A StuffIt method 15 block exceeds its declared block size.");
                }

                continue;
            }

            if (zeroState != 0)
            {
                // A run of MTF index 0: the front byte, repeated.
                state.Reserve(count + (int)zeroCount, maximumLength);
                state.LastColumn.AsSpan(count, (int)zeroCount).Fill(moveToFront[0]);
                count += (int)zeroCount;
                zeroState = 0;
                zeroCount = 0;
            }

            if (symbol == 10)
            {
                return count;
            }

            int mtfIndex = symbol == 2 ? 1 : reader.Decode(state.Models[symbol - 3]);
            if (count == maximumLength)
            {
                throw new InvalidDataException("A StuffIt method 15 block exceeds its declared block size.");
            }

            byte value = moveToFront[mtfIndex];
            state.Reserve(count + 1, maximumLength);
            state.LastColumn[count++] = value;
            moveToFront.AsSpan(0, mtfIndex).CopyTo(moveToFront.AsSpan(1));
            moveToFront[0] = value;
        }
    }

    private static Span<byte> InverseBurrowsWheeler(BlockState state, int length, int primaryIndex)
    {
        ReadOnlySpan<byte> lastColumn = state.LastColumn.AsSpan(0, length);
        Span<int> nextPositions = stackalloc int[256];
        foreach (byte value in lastColumn)
        {
            nextPositions[value]++;
        }

        int cumulative = 0;
        for (int value = 0; value < 256; value++)
        {
            int count = nextPositions[value];
            nextPositions[value] = cumulative;
            cumulative += count;
        }

        if (state.Next.Length < length)
        {
            state.Next = new int[Math.Max(length, state.Next.Length * 2)];
        }

        if (state.Block.Length < length)
        {
            state.Block = new byte[Math.Max(length, state.Block.Length * 2)];
        }

        var next = state.Next;
        for (int index = 0; index < length; index++)
        {
            next[nextPositions[lastColumn[index]]++] = index;
        }

        var output = state.Block.AsSpan(0, length);
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

    // Expands the block's runs (4 equal bytes, then a count of more) into output after written; returns the new end.
    private static int AppendRunExpanded(ReadOnlySpan<byte> block, byte[] output, int written)
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
                {
                    throw new InvalidDataException("A StuffIt method 15 run is missing its repeat count.");
                }

                int repeatCount = block[index++];
                if (output.Length - written < 4)
                {
                    throw new InvalidDataException("StuffIt method 15 output exceeds its declared fork length.");
                }

                if (repeatCount > output.Length - written - 4)
                {
                    throw new InvalidDataException("A StuffIt method 15 run exceeds its declared fork length.");
                }

                output.AsSpan(written, 4 + repeatCount).Fill(value);
                written += 4 + repeatCount;
            }
            else
            {
                if (written == output.Length)
                {
                    throw new InvalidDataException("StuffIt method 15 output exceeds its declared fork length.");
                }

                output[written++] = value;
            }
        }
        return written;
    }

    private sealed class AdaptiveModel(int first, int last, int increment, int frequencyLimit)
    {
        private readonly int[] _frequencies = new int[last - first + 1];
        private int _total;

        public int Total => _total;

        public void Reset()
        {
            _frequencies.AsSpan().Fill(increment);
            _total = _frequencies.Length * increment;
        }

        // The symbol whose cumulative frequency range holds frequency, with that range.
        public int FindSymbol(int frequency, out int low, out int high)
        {
            int cumulative = 0;
            var frequencies = _frequencies;
            for (int index = 0; index < frequencies.Length; index++)
            {
                int next = cumulative + frequencies[index];
                if (frequency < next)
                {
                    low = cumulative;
                    high = next;
                    return index;
                }
                cumulative = next;
            }
            throw new InvalidDataException("A StuffIt method 15 arithmetic code is outside its model.");
        }

        public int First => first;

        // Counts the symbol at index; past the limit, every frequency is halved (rounding up).
        public void Update(int index)
        {
            _frequencies[index] += increment;
            _total += increment;
            if (_total <= frequencyLimit)
            {
                return;
            }

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
        private readonly long _bitLength;
        private readonly AdaptiveModel _initialModel = new(0, 1, 1, 256);
        private long _bitPosition;
        private int _range = One;
        private int _code;

        public ArithmeticReader(ReadOnlySpan<byte> input)
        {
            _input = input.ToArray();
            _bitLength = _input.LongLength * 8;
            _initialModel.Reset();
            for (int bit = 0; bit < 26; bit++)
            {
                _code = (_code << 1) | ReadBit();
            }
        }

        public int ReadInitialBit() => Decode(_initialModel);

        public uint ReadInitialBits(int count)
        {
            uint value = 0;
            for (int bit = 0; bit < count; bit++)
            {
                value |= (uint)Decode(_initialModel) << bit;
            }

            return value;
        }

        public int Decode(AdaptiveModel model)
        {
            int total = model.Total;
            int scale = _range / total;
            if (scale == 0)
            {
                throw new InvalidDataException("A StuffIt method 15 arithmetic model exceeds its range.");
            }
            // The last symbol owns the rest of the range (range - scale * Total, below), so a code past
            // scale * Total selects it rather than being invalid.
            int frequency = Math.Min(_code / scale, total - 1);
            if (frequency < 0)
            {
                throw new InvalidDataException("A StuffIt method 15 arithmetic code is invalid.");
            }

            int index = model.FindSymbol(frequency, out int low, out int high);
            int lowIncrement = scale * low;
            _code -= lowIncrement;
            _range = high == total ? _range - lowIncrement : (high - low) * scale;
            while (_range <= Half)
            {
                _range <<= 1;
                _code = (_code << 1) | ReadBit();
            }
            model.Update(index);
            return model.First + index;
        }

        private int ReadBit()
        {
            long position = _bitPosition;
            if (position >= _bitLength)
            {
                throw new InvalidDataException("A StuffIt method 15 fork ends inside its arithmetic bitstream.");
            }

            _bitPosition = position + 1;
            return (_input[position >> 3] >> (7 - (int)(position & 7))) & 1;
        }
    }
}
