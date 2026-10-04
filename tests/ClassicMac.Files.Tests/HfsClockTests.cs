using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The writer's dates come from one clock (TimeProvider), which a test sets for its own flow: what an edit writes can be
// pinned and reproduced.
public sealed class HfsClockTests
{
    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Edits_take_their_dates_from_the_clock()
    {
        var at = new DateTimeOffset(1999, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var seconds = MacDate.FromDateTime(at.DateTime).Seconds;
        byte[] first, second;
        using (HfsWriter.UseClock(new Fixed(at)))
        {
            var volume = HfsWriter.FormatVolume(800 * 1024, "Clock");
            first = HfsWriter.CreateFolder(volume, "Docs").ToArray();
            second = HfsWriter.CreateFolder(HfsWriter.FormatVolume(800 * 1024, "Clock"), "Docs").ToArray();
        }

        Assert.Equal(first, second);                                                    // the same edit, the same bytes
        Assert.Equal(seconds, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(1024 + 0x02)));   // drCrDate
        Assert.Equal(seconds, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(1024 + 0x06)));   // drLsMod
        var docs = HfsReader.Instance.ReadFolders(ForkData.FromBytes(first), new ContainerContext()).Single(f => f.MacPath == "Docs");
        Assert.Equal(seconds, docs.Created!.Value.Seconds);
    }
}
