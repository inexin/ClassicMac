using System.Buffers.Binary;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.Resources.Decoders.Tests;

// Text resources read as values and written back.
public class TextResourcesTests
{
    [Fact]
    public void Strings_and_lists_round_trip()
    {
        var str = TextResources.WriteString("Café\nnext");
        Assert.Equal([9, .. "Caf"u8, 0x8E, (byte)'\r', .. "next"u8], str);
        Assert.Equal("Café\nnext", TextResources.ReadString(str));

        var list = TextResources.WriteStringList(["one", "", "three"]);
        Assert.Equal([0, 3, 3, .. "one"u8, 0, 5, .. "three"u8], list);
        Assert.Equal(["one", "", "three"], TextResources.ReadStringList(list));

        Assert.Throws<ArgumentException>(() => TextResources.WriteString(new string('x', 256)));
        Assert.Throws<ArgumentException>(() => TextResources.WriteString("日本"));
    }

    [Fact]
    public void Versions_round_trip_in_BCD()
    {
        byte[] data = [0x12, 0x34, 0x60, 0x13, 0x00, 0x00, 5, .. "1.3.4"u8, 9, .. "1.3.4b13!"u8];
        var version = VersionResource.Read(data)!;
        Assert.Equal(new VersionResource(12, 3, 4, 0x60, 13, 0, "1.3.4", "1.3.4b13!"), version);
        Assert.Equal(data, version.Write());
        Assert.Throws<ArgumentException>(() => (version with { Minor = 16 }).Write());
    }

    private static byte[] Styl(params int[] starts)
    {
        var data = new byte[2 + 20 * starts.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)starts.Length);
        for (var i = 0; i < starts.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(2 + 20 * i), starts[i]);
            data[2 + 20 * i + 10] = (byte)(i + 1);               // a face per run, to tell them apart
        }
        return data;
    }

    private static (int Start, byte Face)[] Runs(byte[] styl) =>
        [.. Enumerable.Range(0, BinaryPrimitives.ReadUInt16BigEndian(styl)).Select(i => (BinaryPrimitives.ReadInt32BigEndian(styl.AsSpan(2 + 20 * i)), styl[2 + 20 * i + 10]))];

    [Fact]
    public void Style_runs_follow_the_text()
    {
        // "Hello world": run 1 "Hello ", run 2 "world".
        var old = "Hello world"u8.ToArray();
        var styl = Styl(0, 6);

        // Inserting inside run 1 grows it and moves run 2.
        var (text, grown) = TextResources.WriteText(old, styl, "Hello, big world", hasStyl: true);
        Assert.Equal("Hello, big world"u8.ToArray(), text);
        Assert.Equal([(0, 1), (11, 2)], Runs(grown!));

        // Replacing across the boundary: the new text takes run 1's style, what is left of "world" keeps run 2's.
        var (_, across) = TextResources.WriteText(old, styl, "Help!rld", hasStyl: true);
        Assert.Equal([(0, 1), (5, 2)], Runs(across!));

        // Deleting run 2's text drops it.
        var (_, cut) = TextResources.WriteText(old, styl, "Hello ", hasStyl: true);
        Assert.Equal([(0, 1)], Runs(cut!));

        // No styl: none is written.
        Assert.Null(TextResources.WriteText(old, [], "x", hasStyl: false).Styl);
    }
}
