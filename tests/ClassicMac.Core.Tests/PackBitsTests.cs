namespace ClassicMac.Core.Tests;

// PackBits (Technical Note 1023; docs/formats/codecs/packbits.md): the one decoder and packer every format shares.
public class PackBitsTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // Technical Note 1023's example.
    [Fact]
    public void The_technote_example_unpacks()
    {
        var packed = Hex("FE AA 02 80 00 2A FD AA 03 80 00 2A 22 F7 AA");
        var output = new byte[24];

        var result = PackBits.Unpack(packed, output);

        Assert.Equal(Hex("AA AA AA 80 00 2A AA AA AA AA 80 00 2A 22 AA AA AA AA AA AA AA AA AA AA"), output);
        Assert.Equal(new PackBitsResult(15, 24, PackBitsEnd.InputUsed), result);
    }

    [Fact]
    public void Flag_80_is_a_no_op_or_a_run_of_129()
    {
        var packed = Hex("80 7F");
        var noOp = new byte[200];
        var run = new byte[200];

        var a = PackBits.Unpack(packed, noOp);
        var b = PackBits.Unpack(packed, run, new PackBitsOptions { Flag80IsRun = true });

        // As a no-op the 7F that follows is a literal of 128 bytes with none left: cut at the input's end.
        Assert.Equal(PackBitsEnd.LiteralPastInput, a.End);
        Assert.Equal(0, a.Written);
        // As a run, 129 copies of 7F.
        Assert.Equal(new PackBitsResult(2, 129, PackBitsEnd.InputUsed), b);
        Assert.All(run[..129], v => Assert.Equal(0x7F, v));
        Assert.Equal(0, run[129]);
    }

    [Fact]
    public void Units_can_be_words()
    {
        // FF: a word repeated twice; 01: two literal words.
        var packed = Hex("FF 12 34 01 AB CD EF 01");
        var output = new byte[8];

        var result = PackBits.Unpack(packed, output, new PackBitsOptions { UnitSize = 2 });

        Assert.Equal(Hex("12 34 12 34 AB CD EF 01"), output);
        Assert.Equal(new PackBitsResult(8, 8, PackBitsEnd.InputUsed), result);
    }

    [Fact]
    public void Decoding_stops_when_the_output_is_full()
    {
        var packed = Hex("01 11 22 01 33 44");
        var output = new byte[2];

        Assert.Equal(new PackBitsResult(3, 2, PackBitsEnd.OutputFull), PackBits.Unpack(packed, output));
        Assert.Equal(Hex("11 22"), output);
    }

    [Fact]
    public void A_literal_past_the_input_copies_what_there_is()
    {
        var output = new byte[8];
        Assert.Equal(new PackBitsResult(3, 2, PackBitsEnd.LiteralPastInput), PackBits.Unpack(Hex("03 11 22"), output));
        Assert.Equal(Hex("11 22 00 00 00 00 00 00"), output);
    }

    [Fact]
    public void A_repeat_with_no_unit_to_repeat_stops()
    {
        Assert.Equal(new PackBitsResult(1, 0, PackBitsEnd.RepeatPastInput), PackBits.Unpack(Hex("FE"), new byte[8]));
        Assert.Equal(new PackBitsResult(2, 0, PackBitsEnd.RepeatPastInput),
            PackBits.Unpack(Hex("FE 12"), new byte[8], new PackBitsOptions { UnitSize = 2 }));
    }

    [Fact]
    public void Runs_past_the_output_are_cut_to_it()
    {
        var literal = new byte[2];
        Assert.Equal(new PackBitsResult(3, 2, PackBitsEnd.LiteralPastOutput), PackBits.Unpack(Hex("03 11 22 33 44"), literal));
        Assert.Equal(Hex("11 22"), literal);

        var repeat = new byte[3];
        Assert.Equal(new PackBitsResult(2, 3, PackBitsEnd.RepeatPastOutput), PackBits.Unpack(Hex("FB 55"), repeat));
        Assert.Equal(Hex("55 55 55"), repeat);
    }

    [Fact]
    public void Empty_input_is_used_up_at_once() =>
        Assert.Equal(new PackBitsResult(0, 0, PackBitsEnd.InputUsed), PackBits.Unpack([], new byte[4]));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void The_unit_is_one_or_two_bytes(int unit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PackBits.Unpack(Hex("00 01"), new byte[4], new PackBitsOptions { UnitSize = unit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PackBits.Pack(Hex("00 01"), unit));
    }

    // The packer: runs of 3 or more equal bytes (2 or more words) as (1 − count, unit), literal blocks otherwise, at most
    // 128 units each, never the flag −128.
    [Fact]
    public void Packing_writes_runs_and_literal_blocks()
    {
        Assert.Equal(Hex("FE AA 01 11 22"), PackBits.Pack(Hex("AA AA AA 11 22")));
        Assert.Equal(Hex("01 AA AA"), PackBits.Pack(Hex("AA AA")));
        Assert.Equal(Hex("FF 12 34"), PackBits.Pack(Hex("12 34 12 34"), unitSize: 2));
        Assert.Empty(PackBits.Pack([]));
    }

    [Fact]
    public void Packing_caps_runs_and_blocks_at_128_units()
    {
        var run = PackBits.Pack(Enumerable.Repeat((byte)7, 300).ToArray());
        Assert.Equal(Hex("81 07 81 07 D5 07"), run);

        var literal = PackBits.Pack([.. Enumerable.Range(0, 130).Select(i => (byte)i)]);
        Assert.Equal(0x7F, literal[0]);
        Assert.Equal(0x01, literal[129]);
        Assert.Equal(132, literal.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Packed_data_unpacks_to_itself(int unit)
    {
        var random = new Random(23);
        for (int trial = 0; trial < 50; trial++)
        {
            var data = new byte[random.Next(0, 400) / unit * unit];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(random.Next(3) == 0 ? random.Next(256) : i / 20);
            }

            var packed = PackBits.Pack(data, unit);
            Assert.DoesNotContain((byte)0x80, Flags(packed, unit));
            var output = new byte[data.Length];
            var result = PackBits.Unpack(packed, output, new PackBitsOptions { UnitSize = unit });
            Assert.Equal(data, output);
            Assert.Equal(packed.Length, result.Read);
        }
    }

    // The flag bytes of packed data.
    private static IEnumerable<byte> Flags(byte[] packed, int unit)
    {
        int at = 0;
        while (at < packed.Length)
        {
            var flag = (sbyte)packed[at];
            yield return packed[at++];
            at += flag >= 0 ? (flag + 1) * unit : unit;
        }
    }
}
