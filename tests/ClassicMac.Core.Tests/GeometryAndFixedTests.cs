namespace ClassicMac.Core.Tests;

public class MacGeometryTests
{
    [Fact]
    public void Points_are_stored_vertical_first()
    {
        var point = MacPoint.Read([0x00, 0x0A, 0xFF, 0xFE]);
        Assert.Equal(new MacPoint(V: 10, H: -2), point);

        var bytes = new byte[MacPoint.Length];
        point.Write(bytes);
        Assert.Equal([0x00, 0x0A, 0xFF, 0xFE], bytes);
    }

    [Fact]
    public void Rects_are_top_left_bottom_right()
    {
        byte[] stored = [0x00, 0x28, 0x00, 0x14, 0x01, 0x2C, 0x01, 0xF4];
        var rect = MacRect.Read(stored);

        Assert.Equal(new MacRect(Top: 40, Left: 20, Bottom: 300, Right: 500), rect);
        Assert.Equal(new MacPoint(40, 20), rect.TopLeft);
        Assert.Equal(new MacPoint(300, 500), rect.BottomRight);
        Assert.Equal(new MacRect(rect.TopLeft, rect.BottomRight), rect);
        Assert.Equal(480, rect.Width);
        Assert.Equal(260, rect.Height);

        var bytes = new byte[MacRect.Length];
        rect.Write(bytes);
        Assert.Equal(stored, bytes);
    }

    [Fact]
    public void Empty_rects_enclose_no_pixels()
    {
        Assert.False(new MacRect(0, 0, 1, 1).IsEmpty);
        Assert.True(new MacRect(0, 0, 0, 10).IsEmpty);
        Assert.True(new MacRect(5, 5, 1, 10).IsEmpty);
        Assert.Equal(-32767 - 32767 - 1, new MacRect(0, short.MaxValue, 0, short.MinValue).Width);
    }
}

public class FixedTests
{
    [Theory]
    [InlineData(0x00010000, 1.0)]
    [InlineData(0x00008000, 0.5)]
    [InlineData(unchecked((int)0xFFFF0000), -1.0)]
    [InlineData(0x00480000, 72.0)]
    [InlineData(int.MaxValue, 32767.9999847412109375)]
    public void Fixed_is_16_16_signed(int raw, double value)
    {
        Assert.Equal(value, new Fixed(raw).ToDouble());
        Assert.Equal(new Fixed(raw), Fixed.FromDouble(value));
    }

    [Fact]
    public void Fixed_rounds_to_the_nearest_step_and_rejects_overflow()
    {
        Assert.Equal(new Fixed(1), Fixed.FromDouble(1.4 / 65536));
        Assert.Equal(new Fixed(2), Fixed.FromDouble(1.5 / 65536));
        Assert.Equal(new Fixed(-2), Fixed.FromDouble(-1.5 / 65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(32768));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(-32769));
    }

    [Fact]
    public void Fixed_reads_and_writes_big_endian()
    {
        var value = Fixed.Read([0x00, 0x48, 0x00, 0x00]);
        Assert.Equal(72.0, value.ToDouble());
        var bytes = new byte[4];
        value.Write(bytes);
        Assert.Equal([0x00, 0x48, 0x00, 0x00], bytes);
        Assert.Equal("72", value.ToString());
    }

    [Fact]
    public void UnsignedFixed_holds_CD_sample_rates()
    {
        // 44100 Hz: $AC440000, which would be negative as a signed Fixed.
        var rate = UnsignedFixed.Read([0xAC, 0x44, 0x00, 0x00]);
        Assert.Equal(44100.0, rate.ToDouble());
        Assert.Equal(rate, UnsignedFixed.FromDouble(44100));
        // The Sound Manager's 22 kHz rate, $56EE8BA3.
        Assert.Equal(22254.545, new UnsignedFixed(0x56EE8BA3).ToDouble(), 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => UnsignedFixed.FromDouble(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnsignedFixed.FromDouble(65536));
    }
}
