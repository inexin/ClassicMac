namespace ClassicMac.Files.Tests;

// Builds StuffIt method 15 (Arsenic) streams for tests, the inverse of each step of StuffItMethod15Decoder: run-length
// coding (4 equal bytes, then a count of more), optional randomization, the Burrows-Wheeler transform, move-to-front with
// bijective base-2 zero runs, and the adaptive arithmetic coder. Options break single steps for the error tests.
internal sealed class ArsenicEncoder
{
    private const int One = 1 << 25;
    private const int Half = 1 << 24;
    private const int Window = 1 << 26;

    private static readonly (int First, int Last, int Increment)[] MtfModels =
        [(2, 3, 8), (4, 7, 4), (8, 15, 4), (16, 31, 4), (32, 63, 2), (64, 127, 2), (128, 255, 1)];

    private readonly List<int> bits = [];
    private readonly Model initial = new(0, 1, 1, 256);
    private long low;
    private int range = One;

    public int BlockBits { get; init; } = 16;

    // Which blocks (by index) are randomized before the transform.
    public Func<int, bool> Randomized { get; init; } = _ => false;

    // Overrides the signature, for the error tests.
    public byte[] Signature { get; init; } = "As"u8.ToArray();

    // Changes a block's primary index after the transform, and the stored CRC.
    public Func<int, int, int> PrimaryIndex { get; init; } = (_, index) => index;

    public Func<uint, uint> Crc { get; init; } = crc => crc;

    // Ends each block with its last run of 4 equal bytes and no count, when set.
    public bool DropLastRunCount { get; init; }

    public static byte[] Encode(byte[] data, int blockBits = 16) => new ArsenicEncoder { BlockBits = blockBits }.Run(data);

    public byte[] Run(byte[] data)
    {
        foreach (var b in Signature)
        {
            InitialBits(b, 8);
        }

        InitialBits((uint)(BlockBits - 9), 4);
        var blockLength = 1 << BlockBits;
        var rle = RunLengthBlocks(data, blockLength);
        for (var index = 0; index < rle.Count; index++)
        {
            var block = rle[index];
            InitialBits(0, 1);
            var randomized = Randomized(index);
            InitialBits(randomized ? 1u : 0, 1);
            if (randomized)
            {
                Randomize(block);
            }

            var (last, primary) = Transform(block);
            InitialBits((uint)PrimaryIndex(index, primary), BlockBits);
            Symbols(last);
        }
        InitialBits(1, 1);
        InitialBits(Crc(Crc32(data)), 32);
        return Finish();
    }

    // Run-length codes the data into blocks of at most blockLength coded bytes; a run never spans blocks.
    private List<byte[]> RunLengthBlocks(byte[] data, int blockLength)
    {
        var blocks = new List<byte[]>();
        var current = new List<byte>();
        var at = 0;
        while (at < data.Length)
        {
            var value = data[at];
            var run = 1;
            while (at + run < data.Length && data[at + run] == value && run < 4 + 255)
            {
                run++;
            }

            List<byte> coded = run >= 4 ? [value, value, value, value, (byte)(run - 4)] : [.. Enumerable.Repeat(value, run)];
            if (current.Count + coded.Count > blockLength)
            {
                blocks.Add([.. current]);
                current.Clear();
            }
            current.AddRange(coded);
            at += run;
        }
        if (current.Count > 0)
        {
            blocks.Add([.. current]);
        }

        if (DropLastRunCount)
        {
            foreach (var i in Enumerable.Range(0, blocks.Count))
            {
                blocks[i] = blocks[i][..^1];
            }
        }
        return blocks;
    }

    private static readonly ushort[] RandomizationTable = typeof(ClassicMac.Files.Archives.StuffItReader).Assembly
        .GetType("ClassicMac.Files.Archives.StuffItMethod15Decoder")!
        .GetField("RandomizationTable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null) as ushort[] ?? throw new InvalidOperationException();

    private static void Randomize(byte[] block)
    {
        int position = RandomizationTable[0], index = 0;
        while (position < block.Length)
        {
            block[position] ^= 1;
            index = (index + 1) & 255;
            position += RandomizationTable[index];
        }
    }

    // The last column of the sorted rotations, and the row of the unrotated block.
    private static (byte[] Last, int Primary) Transform(byte[] block)
    {
        var n = block.Length;
        var rows = Enumerable.Range(0, n).ToArray();
        Array.Sort(rows, (a, b) =>
        {
            for (var k = 0; k < n; k++)
            {
                var d = block[(a + k) % n].CompareTo(block[(b + k) % n]);
                if (d != 0)
                {
                    return d;
                }
            }
            return a.CompareTo(b);
        });
        return (rows.Select(r => block[(r + n - 1) % n]).ToArray(), Array.IndexOf(rows, 0));
    }

    private void Symbols(byte[] last)
    {
        var selector = new Model(0, 10, 8, 1024);
        var models = MtfModels.Select(m => new Model(m.First, m.Last, m.Increment, 1024)).ToArray();
        var order = Enumerable.Range(0, 256).Select(i => (byte)i).ToList();
        var zeros = 0;
        foreach (var value in last)
        {
            var index = order.IndexOf(value);
            if (index == 0)
            {
                zeros++;
                continue;
            }
            Zeros(selector, zeros);
            zeros = 0;
            if (index == 1)
            {
                Encode(selector, 2);
            }
            else
            {
                var m = Array.FindIndex(MtfModels, p => index >= p.First && index <= p.Last);
                Encode(selector, m + 3);
                Encode(models[m], index);
            }
            order.RemoveAt(index);
            order.Insert(0, value);
        }
        Zeros(selector, zeros);
        Encode(selector, 10);
    }

    // A zero run in bijective base 2, least significant digit first: symbol 0 is digit 1, symbol 1 digit 2.
    private void Zeros(Model selector, int count)
    {
        while (count > 0)
        {
            if ((count & 1) == 1)
            {
                Encode(selector, 0);
                count = (count - 1) / 2;
            }
            else
            {
                Encode(selector, 1);
                count = (count - 2) / 2;
            }
        }
    }

    private void InitialBits(uint value, int count)
    {
        for (var bit = 0; bit < count; bit++)
        {
            Encode(initial, (int)((value >> bit) & 1));
        }
    }

    private void Encode(Model model, int symbol)
    {
        var scale = range / model.Total;
        var (lowFrequency, highFrequency) = model.Range(symbol);
        low += (long)scale * lowFrequency;
        range = highFrequency == model.Total ? range - scale * lowFrequency : scale * (highFrequency - lowFrequency);
        if (low >= Window)
        {
            Carry();
        }

        while (range <= Half)
        {
            Emit((int)(low >> 25) & 1);
            low = (low << 1) & (Window - 1);
            range <<= 1;
        }
        model.Update(symbol);
    }

    private void Carry()
    {
        low -= Window;
        var i = bits.Count - 1;
        while (bits[i] == 1)
        {
            bits[i--] = 0;
        }

        bits[i] = 1;
    }

    private void Emit(int bit) => bits.Add(bit);

    private byte[] Finish()
    {
        for (var bit = 25; bit >= 0; bit--)
        {
            Emit((int)(low >> bit) & 1);
        }

        for (var pad = 0; pad < 32; pad++)
        {
            Emit(0);
        }

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            bytes[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        }

        return bytes;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }
        return ~crc;
    }

    private sealed class Model(int first, int last, int increment, int limit)
    {
        private readonly int[] frequencies = Enumerable.Repeat(increment, last - first + 1).ToArray();

        public int Total { get; private set; } = (last - first + 1) * increment;

        public (int Low, int High) Range(int symbol)
        {
            var lowFrequency = frequencies.Take(symbol - first).Sum();
            return (lowFrequency, lowFrequency + frequencies[symbol - first]);
        }

        public void Update(int symbol)
        {
            frequencies[symbol - first] += increment;
            Total += increment;
            if (Total <= limit)
            {
                return;
            }

            Total = 0;
            for (var i = 0; i < frequencies.Length; i++)
            {
                frequencies[i] = (frequencies[i] + 1) / 2;
                Total += frequencies[i];
            }
        }
    }
}
