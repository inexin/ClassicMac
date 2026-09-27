namespace ClassicMac.Core.Tests;

public class MacDateTests
{
    [Fact]
    public void Counts_seconds_from_1904()
    {
        Assert.Equal(new DateTime(1904, 1, 1), new MacDate(0).ToDateTime());
        Assert.Equal(new DateTime(1984, 1, 24), new MacDate(2_526_595_200).ToDateTime());
        Assert.Equal(new DateTime(2040, 2, 6, 6, 28, 15), new MacDate(uint.MaxValue).ToDateTime());
    }

    [Fact]
    public void FromDateTime_round_trips_and_rejects_out_of_range()
    {
        var date = new DateTime(1991, 5, 13, 12, 34, 56);
        Assert.Equal(date, MacDate.FromDateTime(date).ToDateTime());
        Assert.Throws<ArgumentOutOfRangeException>(() => MacDate.FromDateTime(new DateTime(1903, 12, 31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacDate.FromDateTime(new DateTime(2040, 2, 6, 6, 28, 16)));
    }
}
