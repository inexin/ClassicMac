namespace ClassicMac.Core.Tests;

// Sizes as people type them (the CLI's --size, the app's Resize): plain bytes, or a number with a binary unit.
public sealed class ByteSizeTests
{
    [Theory]
    [InlineData("1024", 1024L)]
    [InlineData("64MiB", 64L << 20)]
    [InlineData("64 mib", 64L << 20)]
    [InlineData("2G", 2L << 30)]
    [InlineData("8KiB", 8L << 10)]
    public void Sizes_accept_bytes_and_binary_units(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out var size));
        Assert.Equal(expected, size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5MiB")]
    [InlineData("64MB")]
    [InlineData("9999999999GiB")]
    public void Sizes_reject_everything_else(string text)
    {
        Assert.False(ByteSize.TryParse(text, out _));
    }

    [Theory]
    [InlineData(819_200L, "800K")]
    [InlineData(1_474_560L, "1440K")]
    [InlineData(20L << 20, "20M")]
    [InlineData(2L << 30, "2G")]
    [InlineData(513L, "513")]
    public void A_size_is_written_in_its_largest_whole_unit_and_read_back(long size, string text)
    {
        Assert.Equal(text, ByteSize.Format(size));
        Assert.True(ByteSize.TryParse(text, out var back));
        Assert.Equal(size, back);
    }
}
