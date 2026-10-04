using ClassicMac.Core;

namespace ClassicMac.Core.Tests;

// The ordering operators agree with CompareTo.
public class ComparisonOperatorTests
{
    [Fact]
    public void Fixed_orders_by_its_value()
    {
        var a = new Fixed(-0x8000);         // -0.5
        var b = new Fixed(0x10000);         // 1.0
        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.False(a > b);
        Assert.False(a >= b);
        Assert.True(b <= new Fixed(0x10000));
        Assert.True(b >= new Fixed(0x10000));
    }

    [Fact]
    public void UnsignedFixed_orders_by_its_value()
    {
        var a = new UnsignedFixed(0x8000);      // 0.5
        var b = new UnsignedFixed(0xFFFF0000);  // 65535.0, above every signed Fixed
        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.False(a > b);
        Assert.False(a >= b);
        Assert.True(b <= new UnsignedFixed(0xFFFF0000));
        Assert.True(b >= new UnsignedFixed(0xFFFF0000));
    }

    [Fact]
    public void FourCC_orders_by_its_code_as_an_unsigned_number()
    {
        var a = FourCC.FromString("ICN#");
        var b = FourCC.FromString("ics#");      // lower case sorts after upper case
        var high = new FourCC(0xA9706963);      // "©pic": MacRoman 0xA9 is above every ASCII code
        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.False(a > b);
        Assert.False(a >= b);
        Assert.True(b < high);
        Assert.True(a <= FourCC.FromString("ICN#"));
        Assert.True(a >= FourCC.FromString("ICN#"));
    }
}
