using ClassicMac.Graphics.Pict;

namespace ClassicMac.Graphics.Tests;

// _PackBits as the pictures QuickDraw records pack their rows (docs/formats/codecs/packbits.md §4.2): runs of 3 to 128,
// literals of up to 128; the ROM peeks past the row and never ends a literal early.
public class QuickDrawPackBitsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rows_pack_as_both_QuickDraws_pack_them(bool rom)
    {
        Assert.Equal([0xFE, 0xAA, 0x03, 0x55, 0x55, 0x01, 0x02, 0xFD, 0x03],
            QuickDrawPackBits.Pack([0xAA, 0xAA, 0xAA, 0x55, 0x55, 0x01, 0x02, 0x03, 0x03, 0x03, 0x03], [], rom));
        Assert.Equal([0x01, 0x01, 0x02, 0xFE, 0x03, 0x00, 0x04], QuickDrawPackBits.Pack([1, 2, 3, 3, 3, 4], [], rom));
        Assert.Equal([0x81, 0x07, 0x01, 0x07, 0x07], QuickDrawPackBits.Pack(Enumerable.Repeat((byte)7, 130).ToArray(), [], rom));
        Assert.Equal([0x81, 0x07, 0x00, 0x07], QuickDrawPackBits.Pack(Enumerable.Repeat((byte)7, 129).ToArray(), [], rom));
    }

    [Fact]
    public void The_ROM_reads_past_the_row_where_its_last_two_bytes_repeat()
    {
        Assert.Equal([0xFF, 0x00], QuickDrawPackBits.Pack([0, 0], [0], rom: true));
        Assert.Equal([0x01, 0x00, 0x00], QuickDrawPackBits.Pack([0, 0], [0], rom: false));
        Assert.Equal([0x01, 0x01, 0x02, 0xFF, 0xAA], QuickDrawPackBits.Pack([1, 2, 0xAA, 0xAA], [0xAA], rom: true));
        Assert.Equal([0x03, 0x01, 0x02, 0xAA, 0xAA], QuickDrawPackBits.Pack([1, 2, 0xAA, 0xAA], [0xAA], rom: false));
    }

    [Fact]
    public void A_run_at_a_literals_128th_byte_ends_the_literal_on_Mac_OS_9_and_is_lost_in_the_ROM()
    {
        byte[] row = [.. Enumerable.Range(0, 127).Select(i => (byte)(i % 2 == 0 ? 1 : 2)), 9, 9, 9, 5];

        var native = QuickDrawPackBits.Pack(row, [], rom: false);
        Assert.Equal(0x7E, native[0]);                                              // 127 literal bytes
        Assert.Equal([0xFE, 0x09, 0x00, 0x05], native[128..]);

        var rom = QuickDrawPackBits.Pack(row, [], rom: true);
        Assert.Equal(0x7F, rom[0]);                                                 // 128, the run's first byte with them
        Assert.Equal([0x02, 0x09, 0x09, 0x05], rom[129..]);
    }
}
